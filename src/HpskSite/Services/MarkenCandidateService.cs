using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Computes a member's yearly Guldfodring candidacy from <b>validated</b> evidence only — never
    /// from self-entered training logs. Read-only.
    ///
    /// Phase 1 = Pistolskyttemärket:
    ///   • Part 1 (precision): ≥ 3 qualifying Guld series this year, where a qualifying series is a
    ///     Verified <see cref="MarkenSeries"/> Guldserie OR a hosted pistol.nu competition precision
    ///     series ≥ the age-adjusted Guld threshold (read live from PrecisionResultEntry).
    ///   • Part 2 (speed): a Verified Guld Snabbserie, OR a held Standardmedalj i fältskjutning, OR
    ///     a manual on-site attestation at sign-off.
    /// </summary>
    public class MarkenCandidateService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberService _memberService;
        private readonly MarkenLedgerService _ledger;
        private readonly StandardMedalLedgerService _standardMedals;

        public MarkenCandidateService(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberService memberService,
            MarkenLedgerService ledger,
            StandardMedalLedgerService standardMedals)
        {
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberService = memberService;
            _ledger = ledger;
            _standardMedals = standardMedals;
        }

        public int GetBirthYear(int memberId, int year)
        {
            var member = _memberService.GetById(memberId);
            var pn = member?.GetValue("personNumber")?.ToString();
            return Marken.BirthYearFromPersonNumber(pn, year);
        }

        public async Task<GuldfodringCandidate> AnalyzePistolskytteAsync(int memberId, int year)
        {
            int birthYear = GetBirthYear(memberId, year);
            var result = new GuldfodringCandidate
            {
                Year = year,
                BirthYear = birthYear,
                AgeThisYear = birthYear > 0 ? year - birthYear : 0,
                Part1ThresholdNote = BuildThresholdNote(year, birthYear, birthYear > 0 ? year - birthYear : 0)
            };

            var qualifying = new List<QualifyingSeries>();

            // (a) Verified Guld Guldserier (validated single-series submissions)
            foreach (var s in await _ledger.GetVerifiedQualifyingPrecisionAsync(memberId, year))
            {
                qualifying.Add(new QualifyingSeries
                {
                    Id = s.Id,
                    Date = s.SeriesDate,
                    WeaponGroup = s.WeaponGroup,
                    Score = s.Total,
                    Threshold = s.Threshold,
                    Source = "Guldserie",
                    Label = "Guldserie"
                });
            }

            // (b) Qualifying precision series from hosted pistol.nu competitions
            qualifying.AddRange(GetHostedCompQualifyingSeries(memberId, year, birthYear));

            result.QualifyingSeries = qualifying.OrderByDescending(q => q.Score).ThenBy(q => q.Date).ToList();
            result.Part1Met = result.QualifyingSeries.Count >= Marken.GuldfodringPrecisionSeriesRequired;

            // Pending Guldserier (for the member's progress view)
            var allSeries = await _ledger.GetSeriesForMemberAsync(memberId, year);
            result.PendingPrecisionCount = allSeries.Count(s =>
                s.SeriesType == Marken.SeriesTypePrecision && s.Status == Marken.SeriesStatusPending);

            // ── Part 2: Verified Guld Snabbserie → else Standardmedalj i fält ──
            var speed = await _ledger.GetVerifiedSpeedAsync(memberId, year, Marken.LevelGuld);
            if (speed != null)
            {
                result.Part2Met = true;
                result.Part2Source = Marken.SeriesTypeSpeed;
                result.Part2Detail = $"Snabbserie ({Marken.SpeedTargetDisplay(speed.Target)})";
            }
            else
            {
                var awards = await _standardMedals.GetAwardsForMemberAsync(memberId, year, includeRejected: false);
                var faltMedal = awards.FirstOrDefault(a =>
                    string.Equals(a.Discipline, StandardMedals.Faltskytte, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Discipline, StandardMedals.MagnumFalt, StringComparison.OrdinalIgnoreCase));
                if (faltMedal != null)
                {
                    result.Part2Met = true;
                    result.Part2Source = Marken.PartSourceStandardMedal;
                    result.Part2Detail = $"{StandardMedals.MedalDisplayName(faltMedal.MedalType)} – {faltMedal.CompetitionName ?? "fältskjutning"}";
                }
            }

            return result;
        }

        /// <summary>
        /// Qualifying precision series from the member's hosted pistol.nu competitions in a year.
        /// Each PrecisionResultEntry row is one series; a series qualifies when its 5-shot total ≥
        /// the age-adjusted Guld threshold for its weapon group. Competition year + name come from
        /// the competition content node.
        /// </summary>
        private List<QualifyingSeries> GetHostedCompQualifyingSeries(int memberId, int year, int birthYear)
        {
            var result = new List<QualifyingSeries>();
            List<dynamic> rows;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                rows = db.Fetch<dynamic>(
                    "SELECT CompetitionId, ShootingClass, Shots FROM PrecisionResultEntry WHERE MemberId = @0", memberId);
            }
            catch { return result; }
            if (rows.Count == 0) return result;

            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return result;

            // Resolve each competition's year + name once.
            var compYear = new Dictionary<int, (int Year, string Name)>();
            foreach (var r in rows)
            {
                int compId = (int)r.CompetitionId;
                if (compYear.ContainsKey(compId)) continue;
                var comp = ctx.Content.GetById(compId);
                if (comp == null || comp.ContentType.Alias != "competition") { compYear[compId] = (0, ""); continue; }
                var date = comp.Value<DateTime?>("competitionDate");
                compYear[compId] = (date?.Year ?? 0, comp.Name ?? "Tävling");
            }

            foreach (var r in rows)
            {
                int compId = (int)r.CompetitionId;
                if (!compYear.TryGetValue(compId, out var ci) || ci.Year != year) continue;

                var group = Marken.WeaponGroup((string?)r.ShootingClass);
                if (group == null) continue;

                int total = SumShots((string?)r.Shots);
                int threshold = Marken.PrecisionThreshold(group, year, birthYear);
                if (total < threshold) continue;

                result.Add(new QualifyingSeries
                {
                    Id = 0,
                    Date = new DateTime(year, 1, 1),
                    WeaponGroup = group,
                    Score = total,
                    Threshold = threshold,
                    Source = "Tävling",
                    Label = ci.Name
                });
            }
            return result;
        }

        private static int SumShots(string? shotsJson)
        {
            if (string.IsNullOrWhiteSpace(shotsJson)) return 0;
            try
            {
                var shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shotsJson);
                if (shots == null) return 0;
                int total = 0;
                foreach (var s in shots)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (s.Trim().Equals("X", StringComparison.OrdinalIgnoreCase)) total += 10;
                    else if (int.TryParse(s.Trim(), out var v)) total += v;
                }
                return total;
            }
            catch { return 0; }
        }

        private static string BuildThresholdNote(int year, int birthYear, int ageThisYear)
        {
            string baseNote = "Guldkrav per serie (A 43 / B 45 / C 46)";
            if (birthYear <= 0)
                return baseNote + " — födelseår saknas, inga åldersavdrag.";
            if (ageThisYear >= 66)
                return "Silverkrav per serie (A 38 / B 39 / C 40) – fyllde 65 år föregående år (SHB 5.1.2.2).";
            if (ageThisYear >= 56)
                return "Guldkrav − 1 poäng/serie (A 42 / B 44 / C 45) – fyllde 55 år föregående år.";
            return baseNote + ".";
        }
    }

    /// <summary>Live Guldfodring candidacy for one member/year (read-only analysis).</summary>
    public class GuldfodringCandidate
    {
        public int Year { get; set; }
        public int BirthYear { get; set; }
        public int AgeThisYear { get; set; }

        public bool Part1Met { get; set; }
        public string Part1ThresholdNote { get; set; } = "";
        public List<QualifyingSeries> QualifyingSeries { get; set; } = new();
        public int PendingPrecisionCount { get; set; }
        public int RequiredSeries => Marken.GuldfodringPrecisionSeriesRequired;

        public bool Part2Met { get; set; }
        public string? Part2Source { get; set; }
        public string? Part2Detail { get; set; }

        public bool BothPartsMet => Part1Met && Part2Met;
    }

    public class QualifyingSeries
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string WeaponGroup { get; set; } = "";
        public int Score { get; set; }
        public int Threshold { get; set; }
        public string Source { get; set; } = "";   // "Guldserie" | "Tävling"
        public string Label { get; set; } = "";
    }
}
