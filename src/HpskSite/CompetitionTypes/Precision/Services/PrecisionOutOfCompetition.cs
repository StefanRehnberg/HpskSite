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
    }
}
