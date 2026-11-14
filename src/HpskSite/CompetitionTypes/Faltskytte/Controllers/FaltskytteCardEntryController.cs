using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.CompetitionTypes.Faltskytte.Controllers
{
    /// <summary>
    /// Sekretariatets kortinmatning (Njudungsmästerskapet 2026-11-15): när stationerna inte är
    /// uppkopplade bär patrullen ett papperskort, och sekretariatet matar in det efteråt, flera
    /// personer samtidigt.
    ///
    /// ⚠️ Skriver till SAMMA tabell och med SAMMA upsert-nyckel som <c>FaltskytteController.SaveStationResult</c>
    /// (CompetitionId + StationNumber + MemberId + ShootingClass), men är en egen väg: stationsinmatningen
    /// är medvetet orörd. Skillnader mot stationsvägen:
    ///  - Klass och patrullnummer hämtas på servern ur patrullmedlemmen — klienten skickar dem aldrig.
    ///  - Servern kontrollerar figurantal, träffar per figur (max 6 per station) och poäng mot konfigurationen.
    ///  - En rad bär sin version (LastModified.Ticks). Har någon annan ändrat raden svarar servern "conflict"
    ///    i stället för att tyst skriva över.
    ///  - Skrivningen sker under lås per patrull.
    ///  - Två former: träffar per figur ("2+1+2", sparar fördelningen) eller bara total ("5/3" = 5 träff i 3
    ///    figurer, HitDistribution lämnas NULL — vi gissar aldrig en fördelning). Resultatlistan läser bara
    ///    Hits, Figures och TiebreakerScore, så båda formerna ger identiskt resultat i listan.
    /// </summary>
    public class FaltskytteCardEntryController : SurfaceController
    {
        private const int MaxHitsPerStation = 6;
        private const int MaxPoangmalScore = 50;

        private readonly IUmbracoDatabaseFactory _dbFactory;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ILogger<FaltskytteCardEntryController> _logger;
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Hosting.DatabaseLock _dbLock;
        private readonly HpskSite.Services.SiteSettings.SiteSettingService _siteSettings;

        public FaltskytteCardEntryController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            ILogger<FaltskytteCardEntryController> logger,
            AdminAuthorizationService auth,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Hosting.DatabaseLock dbLock,
            HpskSite.Services.SiteSettings.SiteSettingService siteSettings)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _dbFactory = umbracoDatabaseFactory;
            _memberManager = memberManager;
            _memberService = memberService;
            _logger = logger;
            _auth = auth;
            _competitions = competitions;
            _dbLock = dbLock;
            _siteSettings = siteSettings;
        }

        // ── Spärren: stäng stationsinmatningen när sekretariatet matar in korten ──

        public class LockRequest { public int CompetitionId { get; set; } public bool Locked { get; set; } }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStationEntryLock([FromBody] LockRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Saknar parametrar." });
            if (!await IsStaff(request.CompetitionId)) return Json(new { success = false, message = "Du har inte behörighet." });
            var by = await GetCurrentMemberIdAsync();
            try
            {
                _siteSettings.Set(FaltskytteCardEntryOnly.Key(request.CompetitionId), request.Locked ? "1" : "0", by > 0 ? by : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte sätta spärren för tävling {Comp}", request.CompetitionId);
                return Json(new { success = false, message = "Det gick inte att spara inställningen." });
            }
            return Json(new { success = true, locked = FaltskytteCardEntryOnly.IsOn(_siteSettings, request.CompetitionId) });
        }

        // Samma grind som övriga skjutledarytor (arrangören, tävlingsansvariga, bemanning med appåtkomst,
        // klubbadmin/skjutledare, kretsvärd, sajtadmin). Ingen självservice: en skytt får aldrig skriva via kortet.
        private Task<bool> IsStaff(int competitionId) => _auth.HasCompetitionStaffAccessAsync(competitionId);

        public static string LockName(int competitionId, int patrolNumber) => $"falt-kort:{competitionId}:{patrolNumber}";

        private static bool IsDns(FaltskyttePatrolMember m) => !string.IsNullOrEmpty(m.Status);

        private static string VersionOf(FaltskytteResultEntry e) => e.LastModified.Ticks.ToString();

        /// <summary>Konfigurationsnyckeln (A, A_Opt, C, R …) för en skytt: klassens vapengrupp först, patrullens sedan.</summary>
        private static string? ResolveKey(FaltskytteCompetitionConfig config, FaltskyttePatrol patrol, FaltskyttePatrolMember member)
        {
            var byClass = ShootingClasses.GetWeaponClassCode(member.ShootingClass);
            if (!string.IsNullOrEmpty(byClass) && config.WeaponConfigs.ContainsKey(byClass)) return byClass;
            if (!string.IsNullOrEmpty(patrol.WeaponGroup) && config.WeaponConfigs.ContainsKey(patrol.WeaponGroup!)) return patrol.WeaponGroup;
            return null;
        }

        private static List<FaltskytteStationConfig> ScoredStations(FaltskytteCompetitionConfig config, string key) =>
            config.WeaponConfigs[key].Stations.Where(s => !s.IsShootOffOnly).OrderBy(s => s.Station).ToList();

        private static object RowView(FaltskytteResultEntry e, Dictionary<int, string> names)
        {
            int[]? dist = null;
            if (!string.IsNullOrWhiteSpace(e.HitDistribution))
            {
                try { dist = JsonConvert.DeserializeObject<int[]>(e.HitDistribution); } catch { dist = null; }
            }
            int?[]? pm = null;
            if (!string.IsNullOrWhiteSpace(e.PoangmalScores))
            {
                try { pm = JsonConvert.DeserializeObject<int?[]>(e.PoangmalScores); } catch { pm = null; }
            }
            return new
            {
                memberId = e.MemberId,
                station = e.StationNumber,
                shootingClass = e.ShootingClass,
                hits = e.Hits,
                figures = e.Figures,
                hitsPerFigure = dist,          // null = bara total inmatad (5/3)
                poangmalScores = pm,
                version = VersionOf(e),
                enteredBy = names.TryGetValue(e.EnteredBy, out var n) ? n : null,
                enteredById = e.EnteredBy,
                modified = e.LastModified
            };
        }

        private Dictionary<int, string> NamesFor(IEnumerable<int> ids)
        {
            var d = new Dictionary<int, string>();
            foreach (var id in ids.Where(i => i > 0).Distinct())
            {
                try { var m = _memberService.GetById(id); if (m != null) d[id] = m.Name ?? ""; } catch { /* namn är bara visning */ }
            }
            return d;
        }

        // ── Status: alla patruller ───────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Status(int competitionId)
        {
            if (!await IsStaff(competitionId)) return Json(new { success = false, message = "Du har inte behörighet." });
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);

            using var db = _dbFactory.CreateDatabase();
            var patrols = await db.FetchAsync<FaltskyttePatrol>("WHERE CompetitionId = @0 ORDER BY PatrolNumber", competitionId);
            var patrolIds = patrols.Select(p => p.Id).ToList();
            var members = patrolIds.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>($"WHERE PatrolId IN ({string.Join(",", patrolIds)}) ORDER BY Position")
                : new List<FaltskyttePatrolMember>();
            var results = await db.FetchAsync<FaltskytteResultEntry>("WHERE CompetitionId = @0", competitionId);
            var names = NamesFor(results.Select(r => r.EnteredBy));
            var keyed = results.ToDictionary(r => $"{r.MemberId}|{r.StationNumber}|{r.ShootingClass}", r => r);

            var rows = patrols.Select(p =>
            {
                var pm = members.Where(m => m.PatrolId == p.Id).ToList();
                var shooters = pm.Where(m => !IsDns(m)).ToList();
                var firstKey = shooters.Select(m => ResolveKey(config, p, m)).FirstOrDefault(k => k != null)
                               ?? (config.WeaponConfigs.ContainsKey(p.WeaponGroup ?? "") ? p.WeaponGroup : config.WeaponConfigs.Keys.FirstOrDefault());
                var stationNumbers = firstKey == null ? new List<int>() : ScoredStations(config, firstKey).Select(s => s.Station).ToList();

                int stationsDone = 0, cellsDone = 0;
                foreach (var sn in stationNumbers)
                {
                    var have = shooters.Count(m => keyed.ContainsKey($"{m.MemberId}|{sn}|{m.ShootingClass}"));
                    cellsDone += have;
                    if (shooters.Count > 0 && have == shooters.Count) stationsDone++;
                }
                var mine = results.Where(r => r.PatrolNumber == p.PatrolNumber).OrderByDescending(r => r.LastModified).FirstOrDefault();
                var state = cellsDone == 0 ? "none"
                    : (shooters.Count > 0 && stationsDone == stationNumbers.Count ? "done" : "prog");
                return new
                {
                    patrolNumber = p.PatrolNumber,
                    weaponGroup = p.WeaponGroup,
                    members = pm.Count,
                    shooters = shooters.Count,
                    dns = pm.Count - shooters.Count,
                    stationsTotal = stationNumbers.Count,
                    stationsDone,
                    cellsDone,
                    cellsTotal = shooters.Count * stationNumbers.Count,
                    state,
                    lastBy = mine != null && names.TryGetValue(mine.EnteredBy, out var nm) ? nm : null,
                    lastModified = mine?.LastModified
                };
            }).ToList();

            return Json(new
            {
                success = true,
                competitionName = competition.CompetitionName ?? competition.Name ?? "",
                patrols = rows,
                stationEntryLocked = FaltskytteCardEntryOnly.IsOn(_siteSettings, competitionId),
                serverNow = DateTime.UtcNow
            });
        }

        // ── Kortet: en patrull ───────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> Card(int competitionId, int patrolNumber)
        {
            if (!await IsStaff(competitionId)) return Json(new { success = false, message = "Du har inte behörighet." });
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            var scoring = FaltskytteScoringMode.Resolve(config, competition.ScoringMode);

            using var db = _dbFactory.CreateDatabase();
            var patrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>("WHERE CompetitionId = @0 AND PatrolNumber = @1", competitionId, patrolNumber);
            if (patrol == null) return Json(new { success = false, message = "Patrullen hittades inte." });
            var members = await db.FetchAsync<FaltskyttePatrolMember>("WHERE PatrolId = @0 ORDER BY Position", patrol.Id);
            var ids = members.Select(m => m.MemberId).Distinct().ToList();
            var existing = ids.Any()
                ? await db.FetchAsync<FaltskytteResultEntry>($"WHERE CompetitionId = @0 AND MemberId IN ({string.Join(",", ids)})", competitionId)
                : new List<FaltskytteResultEntry>();
            var names = NamesFor(existing.Select(r => r.EnteredBy));

            var shooterViews = members.Select(m =>
            {
                var key = ResolveKey(config, patrol, m);
                var stations = key == null ? new List<object>() : ScoredStations(config, key).Select(s => (object)new
                {
                    station = s.Station,
                    name = s.Name,
                    slots = s.TotalTargets,
                    maxPerFigure = scoring == FaltskytteScoringMode.Normal ? Math.Min(MaxHitsPerStation, Math.Max(1, s.MaxShotsPerFigure)) : MaxHitsPerStation,
                    poangmal = s.TotalPoangmal
                }).ToList();
                return new
                {
                    memberId = m.MemberId,
                    name = m.MemberName,
                    club = HpskSite.Helpers.ClubNameHelper.Shorten(m.ClubName),
                    shootingClass = m.ShootingClass,
                    dns = IsDns(m),
                    configMissing = key == null,
                    stations,
                    rows = existing.Where(r => r.MemberId == m.MemberId && r.ShootingClass == m.ShootingClass)
                        .OrderBy(r => r.StationNumber).Select(r => RowView(r, names)).ToList(),
                    otherClassRows = existing.Count(r => r.MemberId == m.MemberId && r.ShootingClass != m.ShootingClass)
                };
            }).ToList();

            var meId = await GetCurrentMemberIdAsync();
            return Json(new
            {
                success = true,
                competitionName = competition.CompetitionName ?? competition.Name ?? "",
                patrolNumber,
                weaponGroup = patrol.WeaponGroup,
                scoringMode = scoring,
                me = meId,
                shooters = shooterViews
            });
        }

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            return _memberService.GetByEmail(current.Email ?? "")?.Id ?? 0;
        }

        // ── Spara kortet ─────────────────────────────────────────────

        public class CardRow
        {
            public int MemberId { get; set; }
            public int Station { get; set; }
            /// <summary>"fig" = träffar per figur, "tot" = bara total (träffar/figurer).</summary>
            public string Mode { get; set; } = "fig";
            public int[]? HitsPerFigure { get; set; }
            public int? Hits { get; set; }
            public int? Figures { get; set; }
            public int?[]? PoangmalScores { get; set; }
            /// <summary>Versionen klienten läste (LastModified.Ticks). Null = klienten trodde att raden saknades.</summary>
            public string? ExpectedVersion { get; set; }
        }

        public class SaveCardRequest
        {
            public int CompetitionId { get; set; }
            public int PatrolNumber { get; set; }
            public List<CardRow> Rows { get; set; } = new();
        }

        /// <summary>Resultatet av valideringen: antingen ett fel eller de färdiga fälten.</summary>
        private sealed class Computed
        {
            public string? Error;
            public int Hits, Figures;
            public string? HitDistribution;
            public int? Tiebreaker;
            public string? PoangmalJson;
        }

        private static Computed Compute(CardRow r, FaltskytteStationConfig st, int maxPerFigure)
        {
            var c = new Computed();
            var slots = st.TotalTargets;
            if (r.Mode == "tot")
            {
                if (r.Hits == null || r.Figures == null) { c.Error = "Träffar och antal figurer måste anges."; return c; }
                int h = r.Hits.Value, f = r.Figures.Value;
                if (h < 0 || f < 0) { c.Error = "Värdena kan inte vara negativa."; return c; }
                if (h > MaxHitsPerStation) { c.Error = $"Högst {MaxHitsPerStation} träff per station."; return c; }
                if (f > slots) { c.Error = $"Stationen har {slots} figurer, men {f} anges."; return c; }
                if (f > h) { c.Error = "Fler träffade figurer än träffar."; return c; }
                if (h > 0 && f == 0) { c.Error = "Träffar anges men ingen träffad figur."; return c; }
                if (h > f * maxPerFigure) { c.Error = $"Högst {maxPerFigure} träff per figur: {h} träff i {f} figurer går inte."; return c; }
                c.Hits = h; c.Figures = f; c.HitDistribution = null;
            }
            else
            {
                var arr = r.HitsPerFigure;
                if (arr == null || arr.Length != slots) { c.Error = $"Stationen har {slots} figurer, men {(arr?.Length ?? 0)} värden angavs."; return c; }
                if (arr.Any(v => v < 0 || v > maxPerFigure)) { c.Error = $"Träffar per figur måste vara 0–{maxPerFigure}."; return c; }
                var sum = arr.Sum();
                if (sum > MaxHitsPerStation) { c.Error = $"Högst {MaxHitsPerStation} träff per station."; return c; }
                c.Hits = sum; c.Figures = arr.Count(v => v > 0); c.HitDistribution = JsonConvert.SerializeObject(arr);
            }

            var pmCount = st.TotalPoangmal;
            if (r.PoangmalScores != null && r.PoangmalScores.Any(v => v != null))
            {
                if (pmCount == 0) { c.Error = "Stationen har ingen poängräkning."; return c; }
                if (r.PoangmalScores.Length != pmCount) { c.Error = $"Stationen har {pmCount} poängmål."; return c; }
                if (r.PoangmalScores.Any(v => v != null && (v < 0 || v > MaxPoangmalScore))) { c.Error = $"Poäng måste vara 0–{MaxPoangmalScore}."; return c; }
                c.Tiebreaker = r.PoangmalScores.Sum(v => v ?? 0);
                c.PoangmalJson = JsonConvert.SerializeObject(r.PoangmalScores);
            }
            return c;
        }

        private static bool SameValues(FaltskytteResultEntry e, Computed c) =>
            e.Hits == c.Hits && e.Figures == c.Figures
            && (e.HitDistribution ?? "") == (c.HitDistribution ?? "")
            && e.TiebreakerScore == c.Tiebreaker
            && (e.PoangmalScores ?? "") == (c.PoangmalJson ?? "");

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCard([FromBody] SaveCardRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.PatrolNumber <= 0 || request.Rows == null)
                return Json(new { success = false, message = "Saknar parametrar." });
            if (!await IsStaff(request.CompetitionId))
                return Json(new { success = false, message = "Du har inte behörighet." });
            if (request.Rows.Count > 200)
                return Json(new { success = false, message = "För många rader i ett anrop." });

            var competition = _competitions.GetSaved(request.CompetitionId);
            if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });
            var config = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            var scoring = FaltskytteScoringMode.Resolve(config, competition.ScoringMode);
            var enteredBy = await GetCurrentMemberIdAsync();
            if (enteredBy <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            using var lease = await _dbLock.TryAcquireAsync(LockName(request.CompetitionId, request.PatrolNumber), TimeSpan.FromSeconds(5));
            if (lease == null)
                return Json(new { success = false, message = "Någon annan sparar samma patrull just nu. Försök igen om en stund." });

            using var db = _dbFactory.CreateDatabase();
            var patrol = await db.FirstOrDefaultAsync<FaltskyttePatrol>("WHERE CompetitionId = @0 AND PatrolNumber = @1", request.CompetitionId, request.PatrolNumber);
            if (patrol == null) return Json(new { success = false, message = "Patrullen hittades inte." });
            var members = await db.FetchAsync<FaltskyttePatrolMember>("WHERE PatrolId = @0", patrol.Id);

            var outcomes = new List<object>();
            var names = new Dictionary<int, string>();
            foreach (var row in request.Rows)
            {
                object Fail(string msg) => new { memberId = row.MemberId, station = row.Station, status = "rejected", message = msg };

                var member = members.FirstOrDefault(m => m.MemberId == row.MemberId);
                if (member == null) { outcomes.Add(Fail("Skytten står inte i patrullen.")); continue; }
                if (IsDns(member)) { outcomes.Add(Fail("Skytten är markerad som ej start.")); continue; }
                var key = ResolveKey(config, patrol, member);
                if (key == null) { outcomes.Add(Fail("Stationskonfiguration saknas för skyttens klass.")); continue; }
                var st = config.WeaponConfigs[key].Stations.FirstOrDefault(s => s.Station == row.Station && !s.IsShootOffOnly);
                if (st == null) { outcomes.Add(Fail($"Station {row.Station} finns inte för klassen.")); continue; }
                var maxPer = scoring == FaltskytteScoringMode.Normal ? Math.Min(MaxHitsPerStation, Math.Max(1, st.MaxShotsPerFigure)) : MaxHitsPerStation;

                var c = Compute(row, st, maxPer);
                if (c.Error != null) { outcomes.Add(Fail(c.Error)); continue; }

                try
                {
                    var existing = await db.FirstOrDefaultAsync<FaltskytteResultEntry>(
                        "WHERE CompetitionId = @0 AND StationNumber = @1 AND MemberId = @2 AND ShootingClass = @3",
                        request.CompetitionId, row.Station, row.MemberId, member.ShootingClass);

                    if (existing != null)
                    {
                        var versionOk = row.ExpectedVersion != null && row.ExpectedVersion == VersionOf(existing);
                        if (!versionOk)
                        {
                            if (SameValues(existing, c))
                            {
                                // Samma värden redan sparade (till exempel en omsändning efter ett tappat svar).
                                names = NamesFor(new[] { existing.EnteredBy });
                                outcomes.Add(new { memberId = row.MemberId, station = row.Station, status = "saved", version = VersionOf(existing), row = RowView(existing, names), unchanged = true });
                                continue;
                            }
                            names = NamesFor(new[] { existing.EnteredBy });
                            outcomes.Add(new
                            {
                                memberId = row.MemberId, station = row.Station, status = "conflict",
                                message = "Raden har ändrats av någon annan.",
                                current = RowView(existing, names)
                            });
                            continue;
                        }

                        existing.Hits = c.Hits;
                        existing.Figures = c.Figures;
                        existing.HitDistribution = c.HitDistribution;
                        existing.TiebreakerScore = c.Tiebreaker;
                        existing.PoangmalScores = c.PoangmalJson;
                        existing.PatrolNumber = request.PatrolNumber;
                        existing.EnteredBy = enteredBy;
                        existing.LastModified = DateTime.UtcNow;
                        await db.UpdateAsync(existing);
                        _logger.LogInformation("Kortinmatning: uppdaterade tävling {Comp} patrull {Patrol} station {Station} skytt {Member} av {By} ({Hits}/{Figs})",
                            request.CompetitionId, request.PatrolNumber, row.Station, row.MemberId, enteredBy, c.Hits, c.Figures);
                        names = NamesFor(new[] { enteredBy });
                        outcomes.Add(new { memberId = row.MemberId, station = row.Station, status = "saved", version = VersionOf(existing), row = RowView(existing, names) });
                    }
                    else
                    {
                        if (row.ExpectedVersion != null)
                        {
                            // Klienten såg en rad som nu är borta (raderad av någon annan): behandla som krock.
                            outcomes.Add(new { memberId = row.MemberId, station = row.Station, status = "conflict", message = "Raden finns inte längre.", current = (object?)null });
                            continue;
                        }
                        var now = DateTime.UtcNow;
                        var entry = new FaltskytteResultEntry
                        {
                            CompetitionId = request.CompetitionId,
                            StationNumber = row.Station,
                            MemberId = row.MemberId,
                            PatrolNumber = request.PatrolNumber,
                            ShootingClass = member.ShootingClass,
                            Hits = c.Hits,
                            Figures = c.Figures,
                            HitDistribution = c.HitDistribution,
                            TiebreakerScore = c.Tiebreaker,
                            PoangmalScores = c.PoangmalJson,
                            Reshoots = 0,
                            EnteredBy = enteredBy,
                            EnteredAt = now,
                            LastModified = now
                        };
                        await db.InsertAsync(entry);
                        _logger.LogInformation("Kortinmatning: ny rad tävling {Comp} patrull {Patrol} station {Station} skytt {Member} av {By} ({Hits}/{Figs})",
                            request.CompetitionId, request.PatrolNumber, row.Station, row.MemberId, enteredBy, c.Hits, c.Figures);
                        names = NamesFor(new[] { enteredBy });
                        outcomes.Add(new { memberId = row.MemberId, station = row.Station, status = "saved", version = VersionOf(entry), row = RowView(entry, names) });
                    }
                }
                catch (SqlException ex) when (ex.Number is 2627 or 2601)
                {
                    // Unikt index (tävling, station, medlem, klass): en annan skrivning hann före. Skriv aldrig över tyst.
                    outcomes.Add(new { memberId = row.MemberId, station = row.Station, status = "conflict", message = "Raden skapades av någon annan precis nu." });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kortinmatning: fel vid sparning (tävling {Comp}, patrull {Patrol}, station {Station}, skytt {Member})",
                        request.CompetitionId, request.PatrolNumber, row.Station, row.MemberId);
                    outcomes.Add(Fail("Serverfel vid sparning. Försök igen."));
                }
            }

            return Json(new { success = true, results = outcomes, serverNow = DateTime.UtcNow });
        }
    }
}
