using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// "Utanför mästerskapet" i precisionsfamiljens resultatlista — rena funktioner så att regeln kan prövas utan byggaren och dess åtta tjänster.
    /// Samma regler som fältets <c>FaltskytteResultsBuilder</c> (S6): en skytt utanför får resultatet och står kvar i klassgruppen med sin
    /// standardmedalj, men får ingen plats, ingen mästerskapskategori, ingen särskjutning och aldrig en finalplats. Platsen räknas över de
    /// tävlande i klassgruppen (visningsgruppen — den sammanslagna om klasserna slagits ihop).
    /// </summary>
    public static class PrecisionOutOfCompetition
    {
        /// <summary>Sätter <see cref="PrecisionShooterResult.OutOfCompetition"/> ur mängden. Nyckeln är (skytt, klass), klassen i visningsnamn eller id.</summary>
        public static void Mark(IEnumerable<PrecisionShooterResult> shooters, OutOfCompetitionSet outside)
        {
            foreach (var s in shooters)
                s.OutOfCompetition = outside.Contains(s.MemberId, s.ShootingClass);
        }

        /// <summary>
        /// Plats per klassgrupp, räknad över de tävlande (1, 2, 3 …); en skytt utanför får ingen. MÅSTE anropas EFTER att särskjutningen
        /// omsorterat klassgrupperna — platsen ska vara den som visas, och särskjutningen flyttar om skyttar med lika poäng.
        /// </summary>
        public static void AssignPlacements(IEnumerable<PrecisionClassGroup> classGroups)
        {
            foreach (var cg in classGroups)
            {
                var place = 0;
                foreach (var s in cg.Shooters)
                    s.Placement = s.OutOfCompetition ? null : ++place;
            }
        }

        /// <summary>
        /// De skyttar som tävlar om mästerskapet: inte utanför, och i en vapengrupp mästerskapet gäller. ENDA filtret för medaljkandidater, särskjutning
        /// och antalet medaljer — en skytt utanför får aldrig en finalplats eller en mästerskapsmedalj, oavsett vad en finalstartlista innehåller.
        /// </summary>
        public static IEnumerable<PrecisionShooterResult> Competing(IEnumerable<PrecisionShooterResult> shooters, Func<string, bool> appliesToClass) =>
            shooters.Where(s => !s.OutOfCompetition && appliesToClass(s.ShootingClass));

        /// <summary>
        /// Artefaktens skyttar uppdelade i de som är med i tävlingen och en flagga för om någon står utanför. Hederspriserna räknar deltagare och
        /// kandidater ur de tävlande (Stefan 2026-11-06: "en gäst är inte med i tävlingen så ska inte räknas in"); utan någon gäst är allt som förut.
        /// </summary>
        public static (List<PrecisionShooterResult> Competing, bool AnyOutside) SplitByOutside(IEnumerable<PrecisionClassGroup> classGroups)
        {
            var all = classGroups.SelectMany(cg => cg.Shooters).ToList();
            return (all.Where(s => !s.OutOfCompetition).ToList(), all.Any(s => s.OutOfCompetition));
        }

        // ── Finalen: en gäst står aldrig i en finalstartlista (Stefan 2026-11-06: "Gäst får inte skjuta final") ────────────────────────────

        /// <summary>
        /// Resultatrader utan de skyttar som står utanför mästerskapet (per skytt OCH klass). Matas till kvalrankingen FÖRE finalistgränsen
        /// räknas, så att 1/6-gränsen, "X av N går vidare", kvalplatserna och "alla skjuter final" bara gäller de tävlande — en gäst får inte
        /// ta en plats och inte flytta gränsen. Tom mängd = ingen förändring (samma lista tillbaka).
        /// </summary>
        public static List<PrecisionResultEntry> ExcludeGuests(List<PrecisionResultEntry> results, OutOfCompetitionSet outside) =>
            outside.Count == 0 ? results : results.Where(r => !outside.Contains(r.MemberId, r.ShootingClass)).ToList();

        /// <summary>Motsatsen till <see cref="ExcludeGuests(List{PrecisionResultEntry}, OutOfCompetitionSet)"/>: bara gästernas rader (för att kunna säga hur många som står utanför).</summary>
        public static List<PrecisionResultEntry> OnlyGuests(List<PrecisionResultEntry> results, OutOfCompetitionSet outside) =>
            outside.Count == 0 ? new List<PrecisionResultEntry>() : results.Where(r => outside.Contains(r.MemberId, r.ShootingClass)).ToList();

        /// <summary>
        /// Tar bort skyttar som står utanför mästerskapet ur en startlista som ska bli finalstartlista ("fortsätt i samma ordning" kopierar
        /// kvalstartlistan). Skjutlagsnumren BEHÅLLS (löftet är samma skjutlag och plats; platsen en gäst lämnar blir en lucka), tomma skjutlag
        /// faller bort. Returnerar antalet borttagna.
        /// </summary>
        public static int ExcludeGuests(StartListConfiguration config, OutOfCompetitionSet outside)
        {
            if (outside.Count == 0 || config.Teams == null) return 0;
            var removed = 0;
            foreach (var team in config.Teams)
            {
                var kept = (team.Shooters ?? new List<StartListShooter>())
                    .Where(sh => !outside.Contains(sh.MemberId, sh.WeaponClass)).ToList();
                removed += (team.Shooters?.Count ?? 0) - kept.Count;
                team.Shooters = kept;
                team.ShooterCount = kept.Count;
                team.WeaponClasses = kept.Select(sh => sh.WeaponClass).Distinct().OrderBy(c => c).ToList();
            }
            config.Teams = config.Teams.Where(t => (t.Shooters?.Count ?? 0) > 0).ToList();
            return removed;
        }

        /// <summary>
        /// Skälet att vägra när någon läggs in på en FINALstartlista för hand, eller null. Utan klass avgör medlemsnivån (skytten står utanför i
        /// någon klass) — hellre ett nej som går att förklara än en gäst i finalen.
        /// </summary>
        public static string? FinalsAddRefusal(OutOfCompetitionSet outside, int memberId, string? weaponClass, string name)
        {
            var isGuest = string.IsNullOrWhiteSpace(weaponClass) ? outside.HasMember(memberId) : outside.Contains(memberId, weaponClass);
            return isGuest ? $"{name} skjuter utanför mästerskapet och får inte stå i finalen." : null;
        }
    }
}
