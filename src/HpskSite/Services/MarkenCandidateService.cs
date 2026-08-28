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
    ///   • Part 1 (precision): ≥ 3 qualifying Guld series this year. A qualifying series is a Verified
    ///     <see cref="MarkenSeries"/> Guldserie that still counts (see
    ///     <see cref="MarkenSeries.CountsTowardGuldfodring"/>) — <b>including</b> the ones
    ///     <see cref="MarkenCompetitionSeriesSync"/> materialised from hosted competition results.
    ///     ⚠️ Competition series used to be read live from PrecisionResultEntry HERE, on top of the
    ///     ledger and with no dedup, which double-counted a series that had also been submitted by
    ///     hand. There is exactly one source now; callers reconcile before analysing.
    ///   • Part 2 (speed): a Verified Guld Snabbserie, OR a held Standardmedalj i fältskjutning, OR
    ///     a manual on-site attestation at sign-off.
    /// </summary>
    public class MarkenCandidateService
    {
        private readonly IMemberService _memberService;
        private readonly MarkenLedgerService _ledger;
        private readonly StandardMedalLedgerService _standardMedals;

        public MarkenCandidateService(
            IMemberService memberService,
            MarkenLedgerService ledger,
            StandardMedalLedgerService standardMedals)
        {
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

            // ── ONE SOURCE: every qualifying guldserie is a MarkenSeries row ──
            // Competition series used to be read live from PrecisionResultEntry here, IN ADDITION to the
            // ledger and with no dedup — so a series both shot in a klubbtävling and submitted by hand
            // counted twice, while the club Guldserie-ligan (which reads the ledger) never saw it at all.
            // MarkenCompetitionSeriesSync now materialises them, keyed to the result row, and this
            // analyser reads the ledger alone. Callers reconcile BEFORE analysing.
            foreach (var s in await _ledger.GetVerifiedQualifyingPrecisionAsync(memberId, year))
            {
                // An excluded series stays in the record but stops counting — that is how a duplicate or
                // a mis-entered series is resolved without deleting something that was really shot.
                if (!s.CountsTowardGuldfodring) continue;

                qualifying.Add(new QualifyingSeries
                {
                    Id = s.Id,
                    Date = s.SeriesDate,
                    WeaponGroup = s.WeaponGroup,
                    Score = s.Total,
                    Threshold = s.Threshold,
                    Source = s.IsFromCompetition ? "Tävling" : "Guldserie",
                    Label = s.IsFromCompetition ? (s.Notes ?? "Tävling") : "Guldserie"
                });
            }

            result.QualifyingSeries = qualifying.OrderByDescending(q => q.Score).ThenBy(q => q.Date).ToList();
            result.Part1Met = result.QualifyingSeries.Count >= Marken.GuldfodringPrecisionSeriesRequired;

            // All of the year's series (one query) — pending counts + verified speed count.
            var allSeries = await _ledger.GetSeriesForMemberAsync(memberId, year);
            result.PendingPrecisionCount = allSeries.Count(s =>
                s.SeriesType == Marken.SeriesTypePrecision && s.Status == Marken.SeriesStatusPending);
            // Scoped the same way as Part2SeriesCount below — "väntar på validering" on this card is
            // about the part it can complete, and a pending snabbpistol series completes nothing here.
            result.PendingSpeedCount = allSeries.Count(s =>
                Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplineTillampning
                && s.Status == Marken.SeriesStatusPending);
            // ⚠️ TILLÄMPNINGSSERIER ONLY, not every Speed series.
            // SHB 5.1.1.1 pt 2 defines the speed part as 3 tillämpningsserier against B 100 (50 m) or
            // 1/6 C 30 (25 m). A snabbpistol series (snabbpistoltavla, 25 m, 3 s/shot) is Elit's speed
            // evidence, NOT this — the codebase said so in Marken.SeriesDiscipline's own comment while
            // this count read `SeriesType == Speed` and quietly accepted both.
            // Pre-existing, and it would have gone from a corner case to systematic the moment Duell
            // competition series began materialising as snabbpistol series (2026-08-28).
            result.Part2SeriesCount = allSeries.Count(s =>
                Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplineTillampning
                && s.Status == Marken.StatusVerified
                && string.Equals(s.ClaimedLevel, Marken.LevelGuld, StringComparison.OrdinalIgnoreCase));

            // ── Part 2 (SHB 5.1.1.1 pt 2): a held Standardmedalj i fält satisfies the whole part;
            //    otherwise 3 verified Guld Snabbserier are required. ──
            var awards = await _standardMedals.GetAwardsForMemberAsync(memberId, year, includeRejected: false);
            var faltMedal = awards.FirstOrDefault(a =>
                string.Equals(a.Discipline, StandardMedals.Faltskytte, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Discipline, StandardMedals.MagnumFalt, StringComparison.OrdinalIgnoreCase));
            if (faltMedal != null)
            {
                result.Part2Met = true;
                result.Part2ViaFalt = true;
                result.Part2Source = Marken.PartSourceStandardMedal;
                result.Part2Detail = $"{StandardMedals.MedalDisplayName(faltMedal.MedalType)} – {faltMedal.CompetitionName ?? "fältskjutning"}";
            }
            else if (result.Part2SeriesCount >= Marken.GuldfodringSpeedSeriesRequired)
            {
                result.Part2Met = true;
                result.Part2Source = Marken.SeriesTypeSpeed;
                result.Part2Detail = $"{result.Part2SeriesCount}/{Marken.GuldfodringSpeedSeriesRequired} snabbserier";
            }

            return result;
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
        public bool Part2ViaFalt { get; set; }
        public int Part2SeriesCount { get; set; }
        public int PendingSpeedCount { get; set; }
        public int RequiredSpeedSeries => Marken.GuldfodringSpeedSeriesRequired;

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
