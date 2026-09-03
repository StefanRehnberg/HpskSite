using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Models;
using HpskSite.Models.Ranking;
using HpskSite.Services.Notifications;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Ranking
{
    /// <summary>
    /// Builds one day's Träningsmatch ranking snapshot: for every shooter with recent
    /// Träningsmatch activity, computes their handicap index per (discipline, weapon group),
    /// folds in club/region membership + denormalised identity, and the 30-day / season
    /// improvement deltas. Persists to the RankingSnapshot table (one row set per day).
    ///
    /// All member/stats data is bulk-loaded once (never looked up in a loop) per the
    /// project performance rules.
    /// </summary>
    public class RankingSnapshotService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly IHandicapCalculator _handicapCalculator;
        private readonly IShooterStatisticsService _statsService;
        private readonly IConfiguration _config;
        private readonly WebPushService _webPush;
        private readonly ILogger<RankingSnapshotService> _logger;

        // Tunables (see Documentation/TRANINGSMATCH_RANKING_SYSTEM.md §13).
        // Overridable via the "RankingSettings" config section (handy to lower in dev).
        public const int MinRecentSessions = 3;
        public const int RecencyDays = 120;
        public const int ImprovementWindowDays = 30;

        /// <summary>Minsta indexförbättring (i poäng per serie) som är värd en push.</summary>
        public const decimal ImprovementPushThreshold = 0.25m;

        public static readonly string[] Disciplines =
            { "Precision", "Milsnabb", "Duell", "NationellHelmatch", "MagnumPrecision" };

        public RankingSnapshotService(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            IContentService contentService,
            ClubService clubService,
            IHandicapCalculator handicapCalculator,
            IShooterStatisticsService statsService,
            IConfiguration config,
            WebPushService webPush,
            ILogger<RankingSnapshotService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _contentService = contentService;
            _clubService = clubService;
            _handicapCalculator = handicapCalculator;
            _statsService = statsService;
            _config = config;
            _webPush = webPush;
            _logger = logger;
        }

        /// <summary>
        /// Behålls som publik ingång — flera anropare går hit — men kartan bor i
        /// <see cref="HpskSite.CompetitionTypes.Common.PrecisionFamily"/>. Den här metoden VAR en
        /// egen kopia av samma switch, alltså ett andra ställe att glömma vid en ny gren.
        /// </summary>
        public static string ShooterClassProperty(string discipline) =>
            HpskSite.CompetitionTypes.Common.PrecisionFamily.ShooterClassProperty(discipline);

        /// <summary>A | B | C | R | M | L. A-family (A/A Opt/AM/AP/AG) folds to "A".</summary>
        public static string NormalizeWeaponGroup(string? weaponClass)
        {
            if (string.IsNullOrWhiteSpace(weaponClass)) return "";
            var c = char.ToUpperInvariant(weaponClass.Trim()[0]);
            return c switch
            {
                'A' => "A",
                'B' => "B",
                'C' => "C",
                'R' => "R",
                'M' => "M",
                'L' => "L",
                _ => weaponClass.Trim().ToUpperInvariant()
            };
        }

        public async Task<int> BuildSnapshotAsync(CancellationToken ct = default, bool sendNotifications = false)
        {
            var today = DateTime.Today;
            var minSessions = _config.GetValue("RankingSettings:MinRecentSessions", MinRecentSessions);
            var recencyDays = _config.GetValue("RankingSettings:RecencyDays", RecencyDays);
            var recencyCutoff = today.AddDays(-recencyDays);

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // 1. Recent Träningsmatch session counts per (member, raw weapon class, discipline)
            var sessionRows = await db.FetchAsync<SessionCountRow>(
                @"SELECT ts.MemberId,
                         tm.WeaponClass AS WeaponClass,
                         COALESCE(tm.Discipline, 'Precision') AS Discipline,
                         COUNT(DISTINCT tm.Id) AS Sessions
                  FROM TrainingScores ts
                  INNER JOIN TrainingMatches tm ON ts.TrainingMatchId = tm.Id
                  WHERE tm.Status = 'Completed'
                    AND tm.CompletedDate >= @0
                    AND ts.TrainingMatchId IS NOT NULL
                  GROUP BY ts.MemberId, tm.WeaponClass, COALESCE(tm.Discipline, 'Precision')",
                recencyCutoff);

            var candidates = sessionRows.Where(r => r.Sessions >= minSessions).ToList();
            if (candidates.Count == 0)
            {
                _logger.LogInformation("RankingSnapshot: no candidates with >= {Min} recent sessions (last {Days} d); nothing to build.", minSessions, recencyDays);
                scope.Complete();
                return 0;
            }

            // 2. Bulk-load all shooter statistics (one query) -> dict keyed (memberId, discipline, weaponClass)
            var statRows = await db.FetchAsync<StatRow>(
                @"SELECT MemberId, Discipline, WeaponClass, CompletedMatches,
                         TotalSeriesCount, TotalSeriesPoints, AveragePerSeries
                  FROM ShooterStatistics");
            var statLookup = new Dictionary<(int, string, string), StatRow>();
            foreach (var s in statRows)
                statLookup[(s.MemberId, s.Discipline ?? "Precision", s.WeaponClass ?? "")] = s;

            // 3. Members involved — load once into a dict
            var memberIds = candidates.Select(c => c.MemberId).Distinct().ToList();
            var members = new Dictionary<int, Umbraco.Cms.Core.Models.IMember>();
            foreach (var id in memberIds)
            {
                var m = _memberService.GetById(id);
                if (m == null) continue;
                // GDPR objection (Art. 21): an excluded member never gets a row on any board.
                if (m.HasProperty("rankingExcluded") && m.GetValue<bool>("rankingExcluded")) continue;
                members[id] = m;
            }

            // club -> region code cache
            var regionByClub = new Dictionary<int, string>();
            string? RegionForClub(int clubId)
            {
                if (regionByClub.TryGetValue(clubId, out var cached)) return string.IsNullOrEmpty(cached) ? null : cached;
                var node = _contentService.GetById(clubId);
                var rc = node?.ContentType.Alias == "club" ? (node.GetValue<string>("regionalFederation") ?? "") : "";
                regionByClub[clubId] = rc;
                return string.IsNullOrEmpty(rc) ? null : rc;
            }

            // 4. Accumulate one row per (member, discipline, weaponGroup) — best (lowest) index across folded classes
            var acc = new Dictionary<(int memberId, string discipline, string group), AccRow>();

            foreach (var cand in candidates)
            {
                if (!members.TryGetValue(cand.MemberId, out var member)) continue;

                var prop = ShooterClassProperty(cand.Discipline);
                var competenceClass = member.HasProperty(prop) ? member.GetValue<string>(prop) : null;
                if (string.IsNullOrEmpty(competenceClass)) continue; // calculator requires a class

                if (!statLookup.TryGetValue((cand.MemberId, cand.Discipline, cand.WeaponClass ?? ""), out var sr))
                    continue; // no stats row -> can't compute a meaningful index

                var stats = new ShooterStatistics
                {
                    MemberId = cand.MemberId,
                    Discipline = cand.Discipline,
                    WeaponClass = cand.WeaponClass ?? "",
                    CompletedMatches = sr.CompletedMatches,
                    TotalSeriesCount = sr.TotalSeriesCount,
                    TotalSeriesPoints = sr.TotalSeriesPoints,
                    AveragePerSeries = sr.AveragePerSeries,
                    LastCalculated = DateTime.Now
                };

                HandicapProfile profile;
                try
                {
                    profile = _handicapCalculator.CalculateHandicap(stats, competenceClass);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "RankingSnapshot: skipping member {MemberId} {Discipline}/{Class} (handicap calc failed)",
                        cand.MemberId, cand.Discipline, competenceClass);
                    continue;
                }

                var group = NormalizeWeaponGroup(cand.WeaponClass);
                if (string.IsNullOrEmpty(group)) continue;

                var key = (cand.MemberId, cand.Discipline, group);
                if (!acc.TryGetValue(key, out var row))
                {
                    row = new AccRow { Index = profile.HandicapPerSeries, IsProvisional = profile.IsProvisional, Sessions = 0 };
                    acc[key] = row;
                }
                // keep the best (lowest) index for the folded group
                if (profile.HandicapPerSeries < row.Index)
                {
                    row.Index = profile.HandicapPerSeries;
                    row.IsProvisional = profile.IsProvisional;
                }
                row.Sessions += cand.Sessions;
            }

            if (acc.Count == 0)
            {
                _logger.LogInformation("RankingSnapshot: no rankable rows after handicap computation.");
                scope.Complete();
                return 0;
            }

            // 5. Improvement baselines — one baseline date for 30d, one for season (snapshots are daily & uniform)
            var cut30 = today.AddDays(-ImprovementWindowDays);
            var seasonStart = new DateTime(today.Year, 1, 1);

            var baseline30Date = await db.ExecuteScalarAsync<DateTime?>(
                "SELECT MAX(SnapshotDate) FROM RankingSnapshot WHERE SnapshotDate <= @0", cut30);

            var baseline30 = await LoadBaselineAsync(db, baseline30Date);
            // Season baseline is PER-MEMBER: each shooter's earliest snapshot this year, so shooters who
            // started at different points in the season each get a real baseline (not just those active on
            // a single global earliest date).
            var baselineSeason = await LoadSeasonBaselineAsync(db, seasonStart, today);

            // Prior snapshot (most recent before today) — used to detect improvement for the push.
            var priorDate = await db.ExecuteScalarAsync<DateTime?>(
                "SELECT MAX(SnapshotDate) FROM RankingSnapshot WHERE SnapshotDate < @0", today);
            var priorIndex = await LoadBaselineAsync(db, priorDate);

            // 6. Build identity + membership per member, then materialise insert rows
            var inserts = new List<object>(acc.Count);
            foreach (var kvp in acc)
            {
                var (memberId, discipline, group) = kvp.Key;
                var row = kvp.Value;
                if (!members.TryGetValue(memberId, out var member)) continue;

                var (clubIds, regionCodes, primaryClubId) = ResolveMembership(member, RegionForClub);
                var (fullName, initials) = ResolveNames(member);
                var clubName = primaryClubId > 0 ? _clubService.GetClubNameById(primaryClubId) : null;
                var avatar = member.HasProperty("profilePictureUrl") ? member.GetValue<string>("profilePictureUrl") : null;
                var visibility = NormalizeVisibility(member.HasProperty("identityVisibility") ? member.GetValue<string>("identityVisibility") : null);
                var showClub = ReadShowClubOnBoard(member);

                decimal? imp30 = null, impSeason = null;
                if (baseline30.TryGetValue((memberId, discipline, group), out var b30)) imp30 = b30 - row.Index;
                if (baselineSeason.TryGetValue((memberId, discipline, group), out var bs)) impSeason = bs - row.Index;

                inserts.Add(new
                {
                    SnapshotDate = today,
                    MemberId = memberId,
                    Discipline = discipline,
                    WeaponGroup = group,
                    HandicapIndex = row.Index,
                    IsProvisional = row.IsProvisional,
                    SessionCount = row.Sessions,
                    ClubIds = clubIds,
                    RegionCodes = regionCodes,
                    ImprovementDelta30 = imp30,
                    ImprovementDeltaSeason = impSeason,
                    FullName = fullName,
                    Initials = initials,
                    ClubName = clubName,
                    AvatarUrl = avatar,
                    IdentityVisibility = visibility,
                    ShowClub = showClub,
                    CreatedAt = DateTime.UtcNow
                });
            }

            // 7. Persist — replace today's rows
            db.Execute("DELETE FROM RankingSnapshot WHERE SnapshotDate = @0", today);
            foreach (var ins in inserts)
                db.Insert("RankingSnapshot", "Id", true, ins);

            // 8. Improvement push candidates — best improvement per member.
            //
            // ⚠️ Baselinen är det index medlemmen SENAST fick en notis om (RankingPushLog), inte
            // gårdagens snapshot. Med gårdagens snapshot som baseline skickades samma förbättring
            // om igen vid varje ny körning samma dag — och hosted servicen kör inte bara 03:00 utan
            // också ~2 min efter varje appstart, så varje deploy/recycle blev en ny notis om exakt
            // samma sak. Gårdagens snapshot används nu bara som fallback för en (skytt, gren) vi
            // aldrig har notifierat om, så att den allra första förbättringen fortfarande hörs.
            var improvements = new Dictionary<int, (string disc, string group, decimal newIdx, decimal delta)>();
            var notifiedKeys = new List<(int memberId, string discipline, string group, decimal index)>();
            if (sendNotifications && _webPush.IsConfigured)
            {
                var lastNotified = await LoadLastNotifiedAsync(db);
                foreach (var kvp in acc)
                {
                    var (memberId, discipline, group) = kvp.Key;
                    decimal baseline;
                    if (lastNotified.TryGetValue(kvp.Key, out var alreadyToldAbout))
                    {
                        // Nivån är redan annonserad. En försämring följd av en återgång till samma
                        // index är därmed ingen nyhet och får inte pusha igen.
                        baseline = alreadyToldAbout;
                    }
                    else if (priorIndex.TryGetValue(kvp.Key, out var old))
                    {
                        baseline = old;
                    }
                    else continue;

                    var delta = baseline - kvp.Value.Index; // positive = improved (lower index is better)
                    if (delta < ImprovementPushThreshold) continue;

                    // Varje kvalificerad gren loggas, inte bara den vi formulerar notisen kring:
                    // annars skulle nästa körning samma dag pusha om medlemmens övriga grenar.
                    notifiedKeys.Add((memberId, discipline, group, kvp.Value.Index));
                    if (!improvements.TryGetValue(memberId, out var ex) || delta > ex.delta)
                        improvements[memberId] = (discipline, group, kvp.Value.Index, delta);
                }
            }

            scope.Complete();
            _logger.LogInformation("RankingSnapshot built for {Date:yyyy-MM-dd}: {Count} rows across {Members} shooters.",
                today, inserts.Count, members.Count);

            // 9. Send the "din träningsform förbättrades" push (one per member, biggest improvement).
            if (improvements.Count > 0)
            {
                var sentTo = new HashSet<int>();
                foreach (var kv in improvements)
                {
                    var (disc, group, newIdx, _) = kv.Value;
                    var body = $"Index {newIdx:0.00} i {DisciplineLabel(disc)} {group} — se var du ligger på topplistan.";
                    try
                    {
                        await _webPush.SendToMemberAsync(kv.Key, "Din träningsform förbättrades 🎯", body, "/traningsmatch/#topplista", "ranking", onlyRanking: true);
                        sentTo.Add(kv.Key);
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Ranking push failed for member {Member}", kv.Key); }
                }

                // Loggas oavsett hur många enheter som faktiskt nåddes — även en medlem utan
                // aktiv prenumeration ska räknas som "informerad", annars står förbättringen
                // kvar som ny och pushas vid nästa körning. Bara ett kastat undantag hoppas över,
                // så ett tillfälligt fel kan göra ett nytt försök i morgon.
                await MarkNotifiedAsync(notifiedKeys.Where(k => sentTo.Contains(k.memberId)));

                _logger.LogInformation("RankingSnapshot: sent improvement push to {Count} members.", sentTo.Count);
            }

            return inserts.Count;
        }

        /// <summary>
        /// Builds a snapshot AS OF a past date using reconstructed indices — used only as a baseline for
        /// the improvement boards / movement. No notifications, no deltas. Skips (returns -1) if a snapshot
        /// for that date already exists, so it never overwrites a live snapshot. Returns rows inserted.
        /// </summary>
        public async Task<int> BuildHistoricalSnapshotAsync(DateTime asOf, CancellationToken ct = default)
        {
            asOf = asOf.Date;
            var recencyDays = _config.GetValue("RankingSettings:RecencyDays", RecencyDays);
            var minSessions = _config.GetValue("RankingSettings:MinRecentSessions", MinRecentSessions);
            var recencyStart = asOf.AddDays(-recencyDays);

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var existing = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RankingSnapshot WHERE SnapshotDate = @0", asOf);
            if (existing > 0) { scope.Complete(); return -1; }

            var sessionRows = await db.FetchAsync<SessionCountRow>(
                @"SELECT ts.MemberId, tm.WeaponClass AS WeaponClass, COALESCE(tm.Discipline, 'Precision') AS Discipline,
                         COUNT(DISTINCT tm.Id) AS Sessions
                  FROM TrainingScores ts INNER JOIN TrainingMatches tm ON ts.TrainingMatchId = tm.Id
                  WHERE tm.Status = 'Completed' AND tm.CompletedDate <= @0 AND tm.CompletedDate >= @1 AND ts.TrainingMatchId IS NOT NULL
                  GROUP BY ts.MemberId, tm.WeaponClass, COALESCE(tm.Discipline, 'Precision')",
                asOf, recencyStart);

            var candidates = sessionRows.Where(r => r.Sessions >= minSessions).ToList();
            if (candidates.Count == 0) { scope.Complete(); return 0; }

            var members = new Dictionary<int, Umbraco.Cms.Core.Models.IMember>();
            foreach (var id in candidates.Select(c => c.MemberId).Distinct())
            {
                var m = _memberService.GetById(id);
                if (m == null) continue;
                if (m.HasProperty("rankingExcluded") && m.GetValue<bool>("rankingExcluded")) continue;
                members[id] = m;
            }

            var regionByClub = new Dictionary<int, string>();
            string? RegionForClub(int clubId)
            {
                if (regionByClub.TryGetValue(clubId, out var cached)) return string.IsNullOrEmpty(cached) ? null : cached;
                var node = _contentService.GetById(clubId);
                var rc = node?.ContentType.Alias == "club" ? (node.GetValue<string>("regionalFederation") ?? "") : "";
                regionByClub[clubId] = rc;
                return string.IsNullOrEmpty(rc) ? null : rc;
            }

            var acc = new Dictionary<(int, string, string), AccRow>();
            foreach (var cand in candidates)
            {
                if (ct.IsCancellationRequested) break;
                if (!members.TryGetValue(cand.MemberId, out var member)) continue;
                var prop = ShooterClassProperty(cand.Discipline);
                var competenceClass = member.HasProperty(prop) ? member.GetValue<string>(prop) : null;
                if (string.IsNullOrEmpty(competenceClass)) continue;

                var stats = await _statsService.ComputeAsOfAsync(cand.MemberId, cand.WeaponClass ?? "", asOf, cand.Discipline);
                if (stats == null) continue;

                HandicapProfile profile;
                try { profile = _handicapCalculator.CalculateHandicap(stats, competenceClass); }
                catch { continue; }

                var group = NormalizeWeaponGroup(cand.WeaponClass);
                if (string.IsNullOrEmpty(group)) continue;
                var key = (cand.MemberId, cand.Discipline, group);
                if (!acc.TryGetValue(key, out var row))
                {
                    row = new AccRow { Index = profile.HandicapPerSeries, IsProvisional = profile.IsProvisional, Sessions = 0 };
                    acc[key] = row;
                }
                if (profile.HandicapPerSeries < row.Index) { row.Index = profile.HandicapPerSeries; row.IsProvisional = profile.IsProvisional; }
                row.Sessions += cand.Sessions;
            }

            if (acc.Count == 0) { scope.Complete(); return 0; }

            var inserts = new List<object>(acc.Count);
            foreach (var kvp in acc)
            {
                var (memberId, discipline, group) = kvp.Key;
                var row = kvp.Value;
                if (!members.TryGetValue(memberId, out var member)) continue;
                var (clubIds, regionCodes, primaryClubId) = ResolveMembership(member, RegionForClub);
                var (fullName, initials) = ResolveNames(member);
                var clubName = primaryClubId > 0 ? _clubService.GetClubNameById(primaryClubId) : null;
                var avatar = member.HasProperty("profilePictureUrl") ? member.GetValue<string>("profilePictureUrl") : null;
                var visibility = NormalizeVisibility(member.HasProperty("identityVisibility") ? member.GetValue<string>("identityVisibility") : null);
                var showClub = ReadShowClubOnBoard(member);

                inserts.Add(new
                {
                    SnapshotDate = asOf,
                    MemberId = memberId,
                    Discipline = discipline,
                    WeaponGroup = group,
                    HandicapIndex = row.Index,
                    IsProvisional = row.IsProvisional,
                    SessionCount = row.Sessions,
                    ClubIds = clubIds,
                    RegionCodes = regionCodes,
                    ImprovementDelta30 = (decimal?)null,
                    ImprovementDeltaSeason = (decimal?)null,
                    FullName = fullName,
                    Initials = initials,
                    ClubName = clubName,
                    AvatarUrl = avatar,
                    IdentityVisibility = visibility,
                    ShowClub = showClub,
                    CreatedAt = DateTime.UtcNow
                });
            }

            foreach (var ins in inserts) db.Insert("RankingSnapshot", "Id", true, ins);
            scope.Complete();
            _logger.LogInformation("RankingSnapshot historical build for {Date:yyyy-MM-dd}: {Count} rows.", asOf, inserts.Count);
            return inserts.Count;
        }

        /// <summary>
        /// One-off backfill: reconstruct baseline snapshots (season start, ~30 days ago, ~7 days ago) from
        /// historical match data, then rebuild today's snapshot so the improvement boards + movement get real
        /// baselines immediately instead of waiting for the season to accrue.
        /// </summary>
        public async Task<object> BackfillAsync(CancellationToken ct = default)
        {
            var today = DateTime.Today;
            var seasonStart = new DateTime(today.Year, 1, 1);

            DateTime? earliest;
            using (var scope = _scopeProvider.CreateScope())
            {
                earliest = await scope.Database.ExecuteScalarAsync<DateTime?>(
                    "SELECT MIN(tm.CompletedDate) FROM TrainingMatches tm WHERE tm.Status='Completed' AND tm.CompletedDate >= @0", seasonStart);
                scope.Complete();
            }
            var seasonBaseline = (earliest.HasValue && earliest.Value.Date > seasonStart) ? earliest.Value.Date : seasonStart;

            // Monthly points across the season + the ~30-day and ~7-day baselines, so per-member season
            // baselines reach back to roughly when each shooter started.
            var dates = new List<DateTime>();
            for (var d = seasonBaseline; d <= today.AddDays(-7); d = d.AddDays(30)) dates.Add(d.Date);
            dates.Add(today.AddDays(-30).Date);
            dates.Add(today.AddDays(-7).Date);
            dates = dates.Where(d => d < today && d >= seasonBaseline).Distinct().OrderBy(d => d).ToList();

            var report = new List<object>();
            foreach (var d in dates)
            {
                if (ct.IsCancellationRequested) break;
                var n = await BuildHistoricalSnapshotAsync(d, ct);
                report.Add(new { date = d.ToString("yyyy-MM-dd"), rows = n < 0 ? 0 : n, skipped = n < 0 });
            }

            var todayRows = await BuildSnapshotAsync(ct, sendNotifications: false);
            return new { seasonBaseline = seasonBaseline.ToString("yyyy-MM-dd"), backfilled = report, todayRows };
        }

        private static string DisciplineLabel(string discipline) => discipline switch
        {
            "Milsnabb" => "Milsnabb",
            "Duell" => "Duell",
            "NationellHelmatch" => "Nationell helmatch",
            "MagnumPrecision" => "Magnum precision",
            _ => "Precision"
        };

        /// <summary>Per-member season baseline: each (member, discipline, group)'s index at their EARLIEST
        /// snapshot on/after Jan 1 (and before today). Handles shooters who joined the season at different times.</summary>
        private static async Task<Dictionary<(int, string, string), decimal>> LoadSeasonBaselineAsync(NPoco.IDatabase db, DateTime seasonStart, DateTime today)
        {
            var map = new Dictionary<(int, string, string), decimal>();
            var rows = await db.FetchAsync<RankingSnapshotRow>(
                @"SELECT r.MemberId, r.Discipline, r.WeaponGroup, r.HandicapIndex
                  FROM RankingSnapshot r
                  INNER JOIN (
                      SELECT MemberId, Discipline, WeaponGroup, MIN(SnapshotDate) AS FirstDate
                      FROM RankingSnapshot
                      WHERE SnapshotDate >= @0 AND SnapshotDate < @1
                      GROUP BY MemberId, Discipline, WeaponGroup
                  ) f ON r.MemberId = f.MemberId AND r.Discipline = f.Discipline
                       AND r.WeaponGroup = f.WeaponGroup AND r.SnapshotDate = f.FirstDate",
                seasonStart, today);
            foreach (var r in rows) map[(r.MemberId, r.Discipline, r.WeaponGroup)] = r.HandicapIndex;
            return map;
        }

        /// <summary>
        /// Det index vi senast SKICKADE en förbättringsnotis om, per (skytt, gren, vapengrupp).
        /// Det här — inte gårdagens snapshot — är baselinen för pushen, så att en omkörning eller
        /// en appstart samma dag inte kan annonsera samma förbättring en andra gång.
        /// </summary>
        private static async Task<Dictionary<(int, string, string), decimal>> LoadLastNotifiedAsync(NPoco.IDatabase db)
        {
            var map = new Dictionary<(int, string, string), decimal>();
            var rows = await db.FetchAsync<RankingPushLogRow>(
                "SELECT MemberId, Discipline, WeaponGroup, NotifiedIndex FROM RankingPushLog");
            foreach (var r in rows)
                map[(r.MemberId, r.Discipline, r.WeaponGroup)] = r.NotifiedIndex;
            return map;
        }

        /// <summary>Skriver (eller uppdaterar) push-loggen för de grenar notisen omfattade.</summary>
        private async Task MarkNotifiedAsync(IEnumerable<(int memberId, string discipline, string group, decimal index)> keys)
        {
            var list = keys.ToList();
            if (list.Count == 0) return;

            try
            {
                using var scope = _scopeProvider.CreateScope();
                foreach (var k in list)
                {
                    await scope.Database.ExecuteAsync(
                        @"UPDATE RankingPushLog
                             SET NotifiedIndex = @3, NotifiedAt = GETUTCDATE()
                           WHERE MemberId = @0 AND Discipline = @1 AND WeaponGroup = @2;
                          IF @@ROWCOUNT = 0
                             INSERT INTO RankingPushLog (MemberId, Discipline, WeaponGroup, NotifiedIndex)
                             VALUES (@0, @1, @2, @3);",
                        k.memberId, k.discipline, k.group, k.index);
                }
                scope.Complete();
            }
            catch (Exception ex)
            {
                // Misslyckas den här skrivningen kan samma förbättring pushas igen nästa körning,
                // så det är värt en varning i loggen och inte bara tystnad.
                _logger.LogWarning(ex, "RankingSnapshot: could not update RankingPushLog for {Count} rows.", list.Count);
            }
        }

        private static async Task<Dictionary<(int, string, string), decimal>> LoadBaselineAsync(NPoco.IDatabase db, DateTime? date)
        {
            var map = new Dictionary<(int, string, string), decimal>();
            if (date == null) return map;
            var rows = await db.FetchAsync<RankingSnapshotRow>(
                "SELECT MemberId, Discipline, WeaponGroup, HandicapIndex FROM RankingSnapshot WHERE SnapshotDate = @0", date.Value);
            foreach (var r in rows)
                map[(r.MemberId, r.Discipline, r.WeaponGroup)] = r.HandicapIndex;
            return map;
        }

        private (string clubIds, string regionCodes, int primaryClubId) ResolveMembership(
            Umbraco.Cms.Core.Models.IMember member, Func<int, string?> regionForClub)
        {
            var clubIds = new List<int>();
            int primaryClubId = 0;

            var primaryStr = member.GetValue<string>("primaryClubId");
            if (!string.IsNullOrEmpty(primaryStr) && int.TryParse(primaryStr, out var pc) && pc > 0)
            {
                primaryClubId = pc;
                clubIds.Add(pc);
            }

            var additional = member.GetValue<string>("memberClubIds") ?? "";
            foreach (var part in additional.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part.Trim(), out var cid) && cid > 0 && !clubIds.Contains(cid))
                    clubIds.Add(cid);
            }

            var regions = new List<string>();
            foreach (var cid in clubIds)
            {
                var rc = regionForClub(cid);
                if (!string.IsNullOrEmpty(rc) && !regions.Contains(rc)) regions.Add(rc);
            }

            return (string.Join(",", clubIds), string.Join(",", regions), primaryClubId);
        }

        private static (string fullName, string initials) ResolveNames(Umbraco.Cms.Core.Models.IMember member)
        {
            var first = (member.GetValue<string>("firstName") ?? "").Trim();
            var last = (member.GetValue<string>("lastName") ?? "").Trim();
            var full = ($"{first} {last}").Trim();
            if (string.IsNullOrEmpty(full)) full = member.Name ?? "Skytt";

            string initials;
            if (!string.IsNullOrEmpty(first) || !string.IsNullOrEmpty(last))
                initials = $"{(first.Length > 0 ? first[0].ToString() : "")}{(last.Length > 0 ? last[0].ToString() : "")}".ToUpperInvariant();
            else
                initials = (full.Length > 0 ? full[0].ToString() : "S").ToUpperInvariant();

            return (full, string.IsNullOrEmpty(initials) ? "S" : initials);
        }

        public static string NormalizeVisibility(string? v)
        {
            return v switch
            {
                "Halv" => "Halv",
                "Anonym" => "Anonym",
                _ => "Full"
            };
        }

        /// <summary>
        /// Reads showClubOnBoard treating "unset/empty" as the intended default (true).
        /// Umbraco's GetValue&lt;bool&gt; can't distinguish unset from false, so we parse the raw value.
        /// </summary>
        public static bool ReadShowClubOnBoard(Umbraco.Cms.Core.Models.IMember member)
        {
            if (!member.HasProperty("showClubOnBoard")) return true;
            var raw = member.GetValue<string>("showClubOnBoard");
            if (string.IsNullOrWhiteSpace(raw)) return true;
            return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private class SessionCountRow
        {
            public int MemberId { get; set; }
            public string? WeaponClass { get; set; }
            public string Discipline { get; set; } = "Precision";
            public int Sessions { get; set; }
        }

        private class StatRow
        {
            public int MemberId { get; set; }
            public string? Discipline { get; set; }
            public string? WeaponClass { get; set; }
            public int CompletedMatches { get; set; }
            public int TotalSeriesCount { get; set; }
            public decimal TotalSeriesPoints { get; set; }
            public decimal AveragePerSeries { get; set; }
        }

        private class RankingPushLogRow
        {
            public int MemberId { get; set; }
            public string Discipline { get; set; } = "";
            public string WeaponGroup { get; set; } = "";
            public decimal NotifiedIndex { get; set; }
        }

        private class AccRow
        {
            public decimal Index { get; set; }
            public bool IsProvisional { get; set; }
            public int Sessions { get; set; }
        }
    }
}
