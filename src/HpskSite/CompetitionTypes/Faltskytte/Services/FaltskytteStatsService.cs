using System.Text.Json;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Aggregates a member's Fältskytte results across all comps for use on
    /// /user-profile-page (dashboard cards + Resultat-tab table).
    ///
    /// Two row sources:
    ///   - Hosted   — rows in FaltskytteResultEntry for comps run on pistol.nu
    ///   - External — rows in TrainingScores with Discipline='Faltskytte'
    ///                (self-entered from off-site comps)
    ///
    /// For hosted rows the placement is computed by re-running the comp's class
    /// grouping + tiebreaker — same code path the official result list uses, so the
    /// numbers match exactly. The standard medal is read from the materialized
    /// StandardMedalAward ledger (Source='OnSite'), the single source of truth shared
    /// with the club-secretary view and SPSF reporting (it is NOT recomputed here).
    /// </summary>
    public class FaltskytteStatsService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly ILogger<FaltskytteStatsService> _logger;

        public FaltskytteStatsService(
            IUmbracoDatabaseFactory databaseFactory,
            IContentService contentService,
            ILogger<FaltskytteStatsService> logger)
        {
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _logger = logger;
        }

        /// <summary>
        /// Return all of a member's Fältskytte comp entries (hosted + external),
        /// sorted by date descending. Pass <paramref name="year"/> to filter to
        /// a single year, or null to return everything.
        /// </summary>
        public async Task<List<FaltskytteSeasonEntry>> GetMemberSeasonAsync(int memberId, int? year)
        {
            var entries = new List<FaltskytteSeasonEntry>();
            entries.AddRange(await LoadHostedAsync(memberId, year));
            entries.AddRange(LoadExternal(memberId, year));
            return entries.OrderByDescending(e => e.Date).ToList();
        }

        // ── Hosted (FaltskytteResultEntry + comp content + class merging) ──

        private async Task<List<FaltskytteSeasonEntry>> LoadHostedAsync(int memberId, int? year)
        {
            using var db = _databaseFactory.CreateDatabase();

            // Distinct comps the member has any rows in.
            var competitionIds = (await db.FetchAsync<int>(
                "SELECT DISTINCT CompetitionId FROM FaltskytteResultEntry WHERE MemberId = @0",
                memberId)).ToList();

            if (!competitionIds.Any())
                return new List<FaltskytteSeasonEntry>();

            // Won Standard medals at our OWN comps are read from the materialized ledger
            // (Source='OnSite'), exactly like the Precision path in MemberController — NOT
            // recomputed here. The ledger is the single source of truth: it's written (gated on
            // isAwardingStandardMedals && !isClubOnly) when results are published, drives the
            // club-secretary view + SPSF reporting, and survives recompute. Keyed CompetitionId|Class.
            var onSiteMedalLookup = new Dictionary<string, string>();
            foreach (var m in await db.FetchAsync<dynamic>(
                "SELECT CompetitionId, ShootingClass, MedalType FROM StandardMedalAward WHERE MemberId = @0 AND Source = @1",
                memberId, StandardMedals.SourceOnSite))
            {
                onSiteMedalLookup[(int)m.CompetitionId + "|" + ((string)m.ShootingClass ?? "")] = (string)m.MedalType;
            }

            var results = new List<FaltskytteSeasonEntry>();

            foreach (var competitionId in competitionIds)
            {
                try
                {
                    var competition = _contentService.GetById(competitionId);
                    if (competition == null) continue;

                    var competitionDate = competition.GetValue<DateTime?>("competitionDate") ?? DateTime.MinValue;
                    if (year.HasValue && competitionDate.Year != year.Value) continue;

                    var competitionType = competition.GetValue<string>("competitionType") ?? "Faltskytte";
                    if (competitionType != "Faltskytte" && competitionType != "MagnumFalt") continue;

                    var competitionConfig = FaltskytteConfigParser.Parse(competition.GetValue<string>("stationConfig"));
                    // Config's own _scoringMode wins — the competition property is a mirror
                    // that only syncs at Anslut time. See FaltskytteScoringMode.
                    var scoringMode = FaltskytteScoringMode.Resolve(competitionConfig, competition.GetValue<string>("scoringMode"));
                    var firstWcConfig = competitionConfig.WeaponConfigs.Values.FirstOrDefault();
                    var stationCount = firstWcConfig?.Stations.Count ?? 0;

                    // Load EVERY shooter's entries for the comp so we can rank.
                    var allRows = await db.FetchAsync<FaltskytteResultEntry>(
                        "WHERE CompetitionId = @0 ORDER BY MemberId, StationNumber", competitionId);
                    if (!allRows.Any()) continue;

                    // Build shooter aggregates with totals (same shape GetFaltskytteResults uses).
                    var shooters = allRows
                        .GroupBy(r => new { r.MemberId, r.ShootingClass })
                        .Select(g =>
                        {
                            var stationResults = g.OrderBy(r => r.StationNumber)
                                .Select(r => new FaltskytteStationResult
                                {
                                    StationNumber = r.StationNumber,
                                    Hits = r.Hits,
                                    Figures = r.Figures,
                                    TiebreakerScore = r.TiebreakerScore
                                }).ToList();

                            return new FaltskytteShooterResult
                            {
                                MemberId = g.Key.MemberId,
                                ShootingClass = g.Key.ShootingClass,
                                Stations = stationResults,
                                TotalHits = stationResults.Sum(s => s.Hits),
                                TotalFigures = stationResults.Sum(s => s.Figures),
                                TotalPoints = stationResults.Sum(s => s.Points),
                                TotalTiebreakerScore = stationResults
                                    .Where(s => s.TiebreakerScore.HasValue)
                                    .Sum(s => s.TiebreakerScore!.Value)
                            };
                        }).ToList();

                    // Apply class merging config so placement reflects what the published
                    // result list shows.
                    var mergeLookup = new Dictionary<string, string>();
                    var mergeJson = competition.HasProperty("mergeConfig") ? competition.GetValue<string>("mergeConfig") ?? "" : "";
                    if (!string.IsNullOrEmpty(mergeJson))
                    {
                        try
                        {
                            var actions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeJson);
                            if (actions != null)
                            {
                                foreach (var a in actions)
                                {
                                    var combined = ClassMergingService.GetCombinedClassName(a.SourceClass, a.TargetClass);
                                    mergeLookup[a.SourceClass] = combined;
                                    if (!mergeLookup.ContainsKey(a.TargetClass))
                                        mergeLookup[a.TargetClass] = combined;
                                }
                            }
                        }
                        catch { /* ignore invalid merge config */ }
                    }

                    // Rank inside class group with the official tiebreaker.
                    var isPoang = scoringMode.Equals("Poang", StringComparison.OrdinalIgnoreCase);
                    var tieBreaker = new FaltskylteTieBreaker(isPoang);
                    var classGroups = shooters
                        .GroupBy(s => mergeLookup.GetValueOrDefault(s.ShootingClass, s.ShootingClass))
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s, tieBreaker).ToList());

                    // Find the member's row(s) — a member can register in more than one class.
                    var memberRows = shooters.Where(s => s.MemberId == memberId).ToList();
                    foreach (var mine in memberRows)
                    {
                        var classKey = mergeLookup.GetValueOrDefault(mine.ShootingClass, mine.ShootingClass);
                        if (!classGroups.TryGetValue(classKey, out var ranked)) continue;

                        var placement = ranked.FindIndex(s => s.MemberId == memberId && s.ShootingClass == mine.ShootingClass) + 1;
                        var participants = ranked.Count;

                        // Medal comes from the materialized ledger, keyed by the shooter's raw class
                        // (the same key the publish-time materialization stores). null when the comp
                        // didn't award medals or results aren't published yet.
                        var hostedMedal = onSiteMedalLookup.GetValueOrDefault(competitionId + "|" + mine.ShootingClass);

                        // Per-weapon-class station config: use the shooter's class to
                        // get the right denominator. Different weapon groups in the
                        // same comp can have different figure counts per station.
                        var wcConfig = competitionConfig.GetForWeaponClass(mine.ShootingClass);
                        var perClassStationCount = wcConfig?.Stations.Count ?? stationCount;
                        var maxHits = perClassStationCount * 6;
                        // Use TotalTargets (scoring slots) not TotalFigures (figure objects):
                        // a multi-target figure like "3 silhouettes" counts as 3 slots in
                        // the saved result (HitsPerFigure.Count(h => h > 0)), so the
                        // denominator must match that or we get >100 %.
                        var maxFigures = wcConfig?.Stations.Sum(s => s.TotalTargets) ?? 0;

                        results.Add(new FaltskytteSeasonEntry
                        {
                            Source = "Hosted",
                            CompetitionId = competitionId,
                            Date = competitionDate,
                            CompetitionName = competition.Name ?? $"Tävling #{competitionId}",
                            ShootingClass = mine.ShootingClass,
                            ShootingClassName = ShootingClasses.GetById(mine.ShootingClass)?.Name ?? mine.ShootingClass,
                            WeaponGroup = ShootingClasses.GetWeaponClassCode(mine.ShootingClass) ?? "?",
                            Mode = isPoang ? "Poangfalt" : "Normalfalt",
                            StationCount = perClassStationCount,
                            TotalHits = mine.TotalHits,
                            TotalFigures = mine.TotalFigures,
                            MaxHits = maxHits,
                            MaxFigures = maxFigures,
                            HitPercent = maxHits > 0 ? Math.Round(100.0 * mine.TotalHits / maxHits, 1) : 0,
                            FigurePercent = maxFigures > 0 ? Math.Round(100.0 * mine.TotalFigures / maxFigures, 1) : 0,
                            Placement = placement > 0 ? placement : null,
                            Participants = participants,
                            StandardMedal = hostedMedal,
                            // pistol.nu-hosted medals are backed by the competition's own result
                            // list, so no member-uploaded proof is needed (same as Precision OnSite).
                            ProofStatus = string.IsNullOrEmpty(hostedMedal) ? null : "has",
                            Notes = null
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error building Fältskytte stats for competition {CompetitionId}", competitionId);
                }
            }

            return results;
        }

        // ── External (self-entered TrainingScores rows) ──

        private List<FaltskytteSeasonEntry> LoadExternal(int memberId, int? year)
        {
            using var db = _databaseFactory.CreateDatabase();

            // PracticeType IS NULL: exclude non-scoring practice rows (fri övning) — their
            // SeriesScores hold practice groups, not a FaltskytteExternalPayload, so they must
            // never reach the season/scoring aggregates. They surface in the practice card instead.
            var sql = year.HasValue
                ? "SELECT Id, TrainingDate, WeaponClass, CompetitionShootingClass, CompetitionStdMedal, SeriesScores, Notes " +
                  "FROM TrainingScores WHERE MemberId = @0 AND Discipline = 'Faltskytte' AND PracticeType IS NULL AND YEAR(TrainingDate) = @1"
                : "SELECT Id, TrainingDate, WeaponClass, CompetitionShootingClass, CompetitionStdMedal, SeriesScores, Notes " +
                  "FROM TrainingScores WHERE MemberId = @0 AND Discipline = 'Faltskytte' AND PracticeType IS NULL";

            var rows = year.HasValue
                ? db.Fetch<dynamic>(sql, memberId, year.Value)
                : db.Fetch<dynamic>(sql, memberId);

            // Whether each self-entered medal has an uploaded proof file (keyed by TrainingScoreId),
            // so each row can show a proof cue (has proof / missing) like the Precision path.
            var proofByScore = new Dictionary<int, bool>();
            foreach (var a in db.Fetch<dynamic>(
                "SELECT TrainingScoreId, ProofType, ProofFileRef FROM StandardMedalAward WHERE MemberId = @0 AND TrainingScoreId IS NOT NULL",
                memberId))
            {
                if (a.TrainingScoreId == null) continue;
                proofByScore[(int)a.TrainingScoreId] = ((string)a.ProofType == "File") && !string.IsNullOrEmpty((string)a.ProofFileRef);
            }

            var results = new List<FaltskytteSeasonEntry>();
            foreach (var r in rows)
            {
                try
                {
                    var payloadJson = (string?)r.SeriesScores ?? "";
                    if (string.IsNullOrWhiteSpace(payloadJson)) continue;

                    var payload = JsonSerializer.Deserialize<FaltskytteExternalPayload>(payloadJson);
                    if (payload == null) continue;

                    var shootingClass = (string?)r.CompetitionShootingClass ?? "";
                    var weaponGroup = (string?)r.WeaponClass ?? ShootingClasses.GetWeaponClassCode(shootingClass) ?? "?";
                    var maxHits = payload.StationCount * 6;
                    var maxFigures = payload.FiguresMax;

                    var medal = NormalizeMedal((string?)r.CompetitionStdMedal);
                    string? proofStatus = (medal == "S" || medal == "B")
                        ? (proofByScore.TryGetValue((int)r.Id, out var hasFile) && hasFile ? "has" : "missing")
                        : null;

                    results.Add(new FaltskytteSeasonEntry
                    {
                        Source = "External",
                        TrainingScoreId = (int)r.Id,
                        Date = (DateTime)r.TrainingDate,
                        CompetitionName = string.IsNullOrWhiteSpace(payload.CompetitionName) ? "Extern tävling" : payload.CompetitionName,
                        ShootingClass = shootingClass,
                        ShootingClassName = ShootingClasses.GetById(shootingClass)?.Name ?? shootingClass,
                        WeaponGroup = weaponGroup,
                        Mode = string.IsNullOrEmpty(payload.Mode) ? "Normalfalt" : payload.Mode,
                        StationCount = payload.StationCount,
                        TotalHits = payload.Hits,
                        TotalFigures = payload.Figures,
                        MaxHits = maxHits,
                        MaxFigures = maxFigures,
                        HitPercent = maxHits > 0 ? Math.Round(100.0 * payload.Hits / maxHits, 1) : 0,
                        FigurePercent = maxFigures > 0 ? Math.Round(100.0 * payload.Figures / maxFigures, 1) : 0,
                        Placement = payload.Placement,
                        Participants = payload.Participants,
                        StandardMedal = medal,
                        ProofStatus = proofStatus,
                        Notes = (string?)r.Notes
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping malformed Fältskytte external row for member {MemberId}", memberId);
                }
            }
            return results;
        }

        private static string? NormalizeMedal(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var v = raw.Trim().ToUpperInvariant();
            if (v == "S" || v == "SILVER") return "S";
            if (v == "B" || v == "BRONS" || v == "BRONZE") return "B";
            return null;
        }

        // ── Dashboard payload (cards + chart data) ──

        /// <summary>
        /// Build the Fältskytte-specific dashboard payload for a member. Aggregates
        /// one season's worth of <see cref="FaltskytteSeasonEntry"/> rows into the
        /// cards + chart data the frontend renders for the Fältskytte view.
        /// </summary>
        public async Task<object> BuildMemberDashboardAsync(int memberId)
        {
            // Whole-history pull; the frontend filters by year client-side.
            var all = await GetMemberSeasonAsync(memberId, null);
            var availableYears = all.Select(e => e.Date.Year).Distinct().OrderByDescending(y => y).ToList();
            if (!availableYears.Any()) availableYears.Add(DateTime.Now.Year);

            var currentYear = DateTime.Now.Year;
            var defaultYear = availableYears.Contains(currentYear) ? currentYear : availableYears.First();
            var seasonForDefault = all.Where(e => e.Date.Year == defaultYear).ToList();

            // Per-year stats so the year dropdown can switch without a server round-trip.
            var statsByYear = availableYears.ToDictionary(y => y.ToString(), y => SummarizeSeason(all.Where(e => e.Date.Year == y).ToList()));

            // Year-over-year delta on overall träff %
            double? yoYDelta = null;
            if (availableYears.Contains(defaultYear - 1))
            {
                var prev = all.Where(e => e.Date.Year == defaultYear - 1).ToList();
                if (prev.Any() && seasonForDefault.Any())
                {
                    yoYDelta = Math.Round(seasonForDefault.Average(e => e.HitPercent) - prev.Average(e => e.HitPercent), 1);
                }
            }

            return new
            {
                competitionType = "Faltskytte",
                availableYears,
                defaultYear,
                statsByYear,
                hitPercentYoYDelta = yoYDelta,
                // Chart data covers all years; the frontend filters by selectedYear.
                chartData = all.Select(e => new
                {
                    date = e.Date,
                    year = e.Date.Year,
                    weaponGroup = e.WeaponGroup,
                    mode = e.Mode,
                    source = e.Source,
                    hitPercent = e.HitPercent,
                    figurePercent = e.FigurePercent,
                    placementPercent = e.Placement.HasValue && e.Participants.HasValue && e.Participants.Value > 1
                        ? Math.Round(100.0 * (1.0 - ((e.Placement.Value - 1.0) / (e.Participants.Value - 1.0))), 1)
                        : (double?)null,
                    headline = e.Mode == "Poangfalt" ? $"{e.TotalHits + e.TotalFigures} p" : $"{e.TotalHits}/{e.TotalFigures}",
                    competitionName = e.CompetitionName
                }).OrderBy(x => x.date).ToList()
            };
        }

        private static object SummarizeSeason(List<FaltskytteSeasonEntry> season)
        {
            if (!season.Any())
            {
                return new
                {
                    totalCompetitions = 0,
                    activityByWeaponGroup = new List<object>(),
                    hitPercentAvg = 0.0,
                    hitPercentByWeaponGroup = new List<object>(),
                    figurePercentAvg = 0.0,
                    medalStats = new { silverCount = 0, bronzeCount = 0, totalPoints = 0 },
                    placement = new
                    {
                        best = (object?)null,
                        median = (object?)null,
                        podiums = 0,
                        topPercent = (double?)null
                    }
                };
            }

            var activityByWeaponGroup = season
                .GroupBy(e => e.WeaponGroup)
                .OrderBy(g => g.Key)
                .Select(g => new { weaponGroup = g.Key, count = g.Count() })
                .ToList<object>();

            var hitPercentByWeaponGroup = season
                .GroupBy(e => e.WeaponGroup)
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    weaponGroup = g.Key,
                    hitPercent = Math.Round(g.Average(e => e.HitPercent), 1),
                    figurePercent = Math.Round(g.Average(e => e.FigurePercent), 1),
                    count = g.Count()
                })
                .ToList<object>();

            var silverCount = season.Count(e => e.StandardMedal == "S");
            var bronzeCount = season.Count(e => e.StandardMedal == "B");

            // Placement metrics (only entries with known placement+participants).
            var placed = season
                .Where(e => e.Placement.HasValue && e.Participants.HasValue && e.Participants.Value > 0)
                .Select(e => new { e.CompetitionName, e.Placement, e.Participants, percent = 100.0 * e.Placement!.Value / e.Participants!.Value })
                .OrderBy(p => p.percent)
                .ToList();

            object? best = null;
            object? median = null;
            double? topPercent = null;
            int podiums = 0;
            if (placed.Any())
            {
                var b = placed.First();
                best = new { placement = b.Placement, participants = b.Participants, competitionName = b.CompetitionName };

                var midIdx = placed.Count / 2;
                var m = placed.ElementAt(midIdx);
                median = new { placement = m.Placement, participants = m.Participants };

                topPercent = Math.Round(placed.Average(p => p.percent), 1);
                podiums = placed.Count(p => p.Placement <= 3);
            }

            return new
            {
                totalCompetitions = season.Count,
                activityByWeaponGroup,
                hitPercentAvg = Math.Round(season.Average(e => e.HitPercent), 1),
                hitPercentByWeaponGroup,
                figurePercentAvg = Math.Round(season.Average(e => e.FigurePercent), 1),
                medalStats = new
                {
                    silverCount,
                    bronzeCount,
                    totalPoints = silverCount * 2 + bronzeCount
                },
                placement = new
                {
                    best,
                    median,
                    podiums,
                    topPercent
                }
            };
        }
    }

    /// <summary>One Fältskytte comp entry as the user sees it on /user-profile-page.</summary>
    public class FaltskytteSeasonEntry
    {
        public string Source { get; set; } = "";        // "Hosted" | "External"
        public int? CompetitionId { get; set; }         // null for External
        public int? TrainingScoreId { get; set; }       // null for Hosted
        public DateTime Date { get; set; }
        public string CompetitionName { get; set; } = "";
        public string ShootingClass { get; set; } = "";    // e.g. "C2"
        public string ShootingClassName { get; set; } = "";// display name from ShootingClasses registry
        public string WeaponGroup { get; set; } = "";      // "C", "A", "A_Opt", "R", "B", "M"
        public string Mode { get; set; } = "Normalfalt";   // "Normalfalt" | "Poangfalt"
        public int StationCount { get; set; }
        public int TotalHits { get; set; }
        public int TotalFigures { get; set; }
        public int MaxHits { get; set; }      // stationCount × 6 for the shooter's weapon class
        public int MaxFigures { get; set; }
        public double HitPercent { get; set; }
        public double FigurePercent { get; set; }
        public int? Placement { get; set; }
        public int? Participants { get; set; }
        public string? StandardMedal { get; set; }   // "S" | "B" | null
        // Proof-of-placement cue for the medal: "has" (proof on file / pistol.nu-verified),
        // "missing" (self-reported medal without an uploaded proof), or null (no medal).
        public string? ProofStatus { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// JSON payload stored in TrainingScores.SeriesScores for self-entered
    /// Fältskytte rows. Keeps Fältskytte-specific fields out of the table schema.
    /// CompetitionName lives here because the existing TrainingScores schema has
    /// no comp-name column (CompetitionPlace is the integer placement, not the name).
    /// </summary>
    public class FaltskytteExternalPayload
    {
        public string CompetitionName { get; set; } = "";
        public string Mode { get; set; } = "Normalfalt";  // "Normalfalt" | "Poangfalt"
        public int StationCount { get; set; }
        public int Hits { get; set; }
        public int Figures { get; set; }
        public int FiguresMax { get; set; }
        public int? Placement { get; set; }
        public int? Participants { get; set; }
    }
}
