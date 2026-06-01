using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;
using NPoco;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>One competition result counting toward a competition-driven märke.</summary>
    public class MarkenCompEvidence
    {
        public int? CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public int Year { get; set; }
        public string WeaponGroup { get; set; } = "";
        public int Dim { get; set; }          // series count (precision-shape) or station count (Fält)
        public int Total { get; set; }        // points or hits
        public string? ReachedLevel { get; set; }
        public string Source { get; set; } = ""; // "Hosted" | "SelfReported"
    }

    /// <summary>Per-family analysis across all the member's years.</summary>
    public class CompFamilyAnalysis
    {
        public string Family { get; set; } = "";
        public string? EarnedLevel { get; set; }       // highest valör supported across all years
        public List<int> GuldMetYears { get; set; } = new();
        public List<MarkenCompEvidence> ThisYear { get; set; } = new();
        public string? ThisYearLevel { get; set; }     // valör supported by this year's results
        public bool ThisYearGuldMet { get; set; }
        public int CompetitionsRequired { get; set; }
    }

    /// <summary>
    /// Engine for competition-driven märken (Precision / Fält / Milsnabb / Nationell helmatch).
    /// Harvests the member's results live from hosted pistol.nu competitions, merges verified
    /// self-reported external results, and evaluates each family's valör + årtalsmärke progression.
    /// Read-only except the self-reported-result CRUD.
    ///
    /// Progression: SHB awards one valör/year and a higher valör only to a prior holder of the next
    /// lower. We award the <b>highest valör the results support</b> across years (a small lenience —
    /// historical hosted data is often incomplete). Årtalsmärke years count guld-fulfilled years
    /// AFTER the first (the first guld-year = earning the märke; later ones = "ånyo").
    /// </summary>
    public class MarkenCompetitionService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public MarkenCompetitionService(IUmbracoDatabaseFactory databaseFactory, IUmbracoContextAccessor umbracoContextAccessor)
        {
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        // ── Self-reported result CRUD ─────────────────────────────────
        public async Task<MarkenCompetitionResult?> GetSelfReportedAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<MarkenCompetitionResult>("WHERE Id = @0", id);
        }

        public async Task<int> InsertSelfReportedAsync(MarkenCompetitionResult r)
        {
            r.CreatedAt = DateTime.Now; r.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(r);
            return r.Id;
        }

        public async Task<(bool, string?)> SetSelfReportedStatusAsync(int id, string status, int validatorId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var r = await db.SingleOrDefaultAsync<MarkenCompetitionResult>("WHERE Id = @0", id);
            if (r == null) return (false, "Resultatet hittades inte.");
            r.Status = status;
            if (status == Marken.StatusVerified) { r.ValidatedByMemberId = validatorId; r.ValidatedDate = DateTime.Now; }
            r.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(r);
            return (true, null);
        }

        public async Task<List<MarkenCompetitionResult>> GetPendingSelfReportedAsync(IEnumerable<int>? clubIds)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (clubIds == null)
                return await db.FetchAsync<MarkenCompetitionResult>("WHERE Status = @0 ORDER BY CreatedAt", Marken.SeriesStatusPending);
            var ids = clubIds.Distinct().ToList();
            if (ids.Count == 0) return new();
            return await db.FetchAsync<MarkenCompetitionResult>(
                $"WHERE Status = @0 AND ClubId IN ({string.Join(",", ids)}) ORDER BY CreatedAt", Marken.SeriesStatusPending);
        }

        public async Task<List<MarkenCompetitionResult>> GetSelfReportedForMemberAsync(int memberId, string family, int? year = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("WHERE MemberId = @0 AND BadgeFamily = @1", memberId, family);
            if (year.HasValue) sql.Append("AND [Year] = @0", year.Value);
            sql.Append("ORDER BY CompetitionDate DESC");
            return await db.FetchAsync<MarkenCompetitionResult>(sql);
        }

        // ── Analysis ──────────────────────────────────────────────────

        public async Task<CompFamilyAnalysis> AnalyzeAsync(int memberId, string familyKey, int displayYear)
        {
            var def = MarkenFamilies.Get(familyKey);
            var result = new CompFamilyAnalysis { Family = familyKey };
            if (def == null || def.Pattern != MarkenPattern.CompetitionAchievement) return result;
            result.CompetitionsRequired = def.CompetitionsRequired;

            var evidence = HarvestHosted(memberId, def);
            // Verified self-reported (all years)
            foreach (var r in await GetSelfReportedForMemberAsync(memberId, familyKey))
            {
                if (r.Status != Marken.StatusVerified) continue;
                evidence.Add(new MarkenCompEvidence
                {
                    CompetitionName = r.CompetitionName,
                    Year = r.Year,
                    WeaponGroup = r.WeaponGroup,
                    Dim = r.Dim,
                    Total = r.Total,
                    ReachedLevel = def.LevelForCompetition(r.WeaponGroup, r.Dim, r.Total),
                    Source = "SelfReported"
                });
            }

            // Per-year supported valör + guld-met set.
            string? highest = null;
            foreach (var yearGroup in evidence.GroupBy(e => e.Year))
            {
                var lvl = SupportedLevel(yearGroup.ToList(), def.CompetitionsRequired);
                if (Marken.LevelOrdinal(lvl) > Marken.LevelOrdinal(highest)) highest = lvl;
                if (lvl == Marken.LevelGuld) result.GuldMetYears.Add(yearGroup.Key);
            }
            result.GuldMetYears.Sort();
            result.EarnedLevel = highest;

            result.ThisYear = evidence.Where(e => e.Year == displayYear)
                .OrderByDescending(e => Marken.LevelOrdinal(e.ReachedLevel)).ThenByDescending(e => e.Total).ToList();
            result.ThisYearLevel = SupportedLevel(result.ThisYear, def.CompetitionsRequired);
            result.ThisYearGuldMet = result.ThisYearLevel == Marken.LevelGuld;

            return result;
        }

        /// <summary>Highest valör reached at ≥ required competitions (a comp counts for level L if its own level ≥ L).</summary>
        private static string? SupportedLevel(List<MarkenCompEvidence> comps, int required)
        {
            int guld = comps.Count(c => c.ReachedLevel == Marken.LevelGuld);
            int silverPlus = comps.Count(c => Marken.LevelOrdinal(c.ReachedLevel) >= Marken.LevelOrdinal(Marken.LevelSilver));
            int bronsPlus = comps.Count(c => Marken.LevelOrdinal(c.ReachedLevel) >= Marken.LevelOrdinal(Marken.LevelBrons));
            if (guld >= required) return Marken.LevelGuld;
            if (silverPlus >= required) return Marken.LevelSilver;
            if (bronsPlus >= required) return Marken.LevelBrons;
            return null;
        }

        /// <summary>
        /// Year-badge count for the family's årtalsmärke ladder: guld-fulfilled years after the first
        /// (the first guld-year is the märke-earning year; subsequent ones are "ånyo").
        /// </summary>
        public static int ArtalsmarkeYears(CompFamilyAnalysis a) => Math.Max(0, a.GuldMetYears.Count - 1);

        // ── Hosted harvest ────────────────────────────────────────────

        private List<MarkenCompEvidence> HarvestHosted(int memberId, MarkenFamilyDef def)
        {
            var list = new List<MarkenCompEvidence>();
            if (string.IsNullOrEmpty(def.ResultTable)) return list;

            List<dynamic> rows;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                rows = def.HitBased
                    ? db.Fetch<dynamic>($"SELECT CompetitionId, ShootingClass, Hits, StationNumber FROM {def.ResultTable} WHERE MemberId = @0", memberId)
                    : db.Fetch<dynamic>($"SELECT CompetitionId, ShootingClass, Shots FROM {def.ResultTable} WHERE MemberId = @0", memberId);
            }
            catch { return list; }
            if (rows.Count == 0) return list;

            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return list;

            // Resolve each competition's year + name + märke-eligibility once. Eligibility (SHB):
            // competition-driven märken that require krets level only count hosted comps whose scope
            // is Kretsmästerskap / Landsdelsmästerskap / Svenskt Mästerskap. Club comps (and scope
            // "Ingen") must be self-reported (a functionary confirms the level). NatHelmatch counts
            // any level (RequiresKretsScope = false).
            var compInfo = new Dictionary<int, (int Year, string Name, bool Eligible)>();
            (int Year, string Name, bool Eligible) CompInfo(int compId)
            {
                if (!compInfo.TryGetValue(compId, out var ci))
                {
                    var comp = ctx.Content.GetById(compId);
                    if (comp == null || comp.ContentType.Alias != "competition")
                        ci = (0, "", false);
                    else
                    {
                        // Untyped read — competitionScope is a FlexibleDropdown that throws on Value<string>().
                        var scope = comp.Value("competitionScope")?.ToString();
                        bool eligible = !def.RequiresKretsScope || IsKretsOrAbove(scope);
                        ci = (comp.Value<DateTime?>("competitionDate")?.Year ?? 0, comp.Name ?? "Tävling", eligible);
                    }
                    compInfo[compId] = ci;
                }
                return ci;
            }

            foreach (var byComp in rows.GroupBy(r => (int)r.CompetitionId))
            {
                int compId = byComp.Key;
                var ci = CompInfo(compId);
                int year = ci.Year;
                if (year == 0 || !ci.Eligible) continue;
                var compRows = byComp.ToList();
                var group = Marken.WeaponGroup((string?)compRows[0].ShootingClass);
                if (group == null) continue;

                int total, dim;
                if (def.HitBased)
                {
                    total = compRows.Sum(r => (int)r.Hits);
                    dim = compRows.Select(r => (int)r.StationNumber).Distinct().Count();
                }
                else
                {
                    total = compRows.Sum(r => SumShots((string?)r.Shots));
                    dim = compRows.Count; // one row per series
                }

                list.Add(new MarkenCompEvidence
                {
                    CompetitionId = compId,
                    CompetitionName = ci.Name,
                    Year = year,
                    WeaponGroup = group,
                    Dim = dim,
                    Total = total,
                    ReachedLevel = def.LevelForCompetition(group, dim, total),
                    Source = "Hosted"
                });
            }
            return list;
        }

        /// <summary>Krets level or above (Kretsmästerskap / Landsdelsmästerskap / Svenskt Mästerskap).</summary>
        private static bool IsKretsOrAbove(string? scope) =>
            scope is CompetitionScopeHelper.Kretsmasterskap
                  or CompetitionScopeHelper.Landsdelsmasterskap
                  or CompetitionScopeHelper.SvensktMasterskap;

        private static int SumShots(string? shotsJson)
        {
            if (string.IsNullOrWhiteSpace(shotsJson)) return 0;
            try
            {
                var shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shotsJson);
                if (shots == null) return 0;
                int t = 0;
                foreach (var s in shots)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (s.Trim().Equals("X", StringComparison.OrdinalIgnoreCase)) t += 10;
                    else if (int.TryParse(s.Trim(), out var v)) t += v;
                }
                return t;
            }
            catch { return 0; }
        }
    }
}
