using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.NationellHelmatch.Services
{
    /// <summary>
    /// Särskiljning i Nationell Helmatch, ordagrant enligt SHB:
    ///
    /// <i>"Vid särskiljning går den som har högst poäng i delmoment C före, därefter i
    /// delmoment B. Kan särskiljning ändå inte erhållas, skall antingen särskjutning ske tills
    /// särskiljning erhållits, eller då särskjutning inte kan eller inte anses böra ske,
    /// lottning utföras."</i>
    ///
    /// Delmoment C är fält-/figurskjutningen (serie 9–12) och delmoment B duellen (serie 5–8);
    /// gränserna hämtas ur <see cref="SeriesSegments"/> så remsan i inmatningsskärmen, rubrikerna
    /// i resultatlistan och den här regeln inte kan rita tre olika delmoment.
    ///
    /// ⚠️ INNERTIOR ÄR INTE ETT KRITERIUM HÄR, och därför får den här jämförelsen inte läggas
    /// EFTER innertiorna i sorteringen. Grenen använde tidigare Milsnabbs återräkning på
    /// tiopoängspar, som körde efter <c>ThenByDescending(TotalXCount)</c> — rapporterat efter
    /// klubbmästerskapet 2026-09-19 (tävling 7075): skytten på plats 6 hade högst poäng i fält
    /// och skulle ha stått på plats 5, men förlorade på innertior innan delmomentet ens lästes.
    ///
    /// Går det inte att särskilja på C och B är skyttarna genuint lika (delmoment A är då givet,
    /// eftersom totalen är lika) och jämförelsen svarar 0 — då gäller särskjutning eller lottning,
    /// vilket är en människas beslut och inte sorteringens.
    ///
    /// Jämförelsen är STIGANDE (större poäng ger positivt svar), alltså gjord för
    /// <c>ThenByDescending(s =&gt; s, comparer)</c> precis som <c>MilsnabbTieBreaker</c>.
    /// </summary>
    public class NationellHelmatchTieBreaker : IComparer<PrecisionShooterResult>
    {
        private const string TypeId = "NationellHelmatch";

        public int Compare(PrecisionShooterResult? x, PrecisionShooterResult? y)
        {
            if (x == null || y == null) return 0;

            foreach (var letter in new[] { "C", "B" })
            {
                var seg = SeriesSegments.Delmoment(TypeId, letter);
                if (seg == null) continue;

                var xs = SegmentScore(x, seg);
                var ys = SegmentScore(y, seg);
                if (xs != ys) return xs.CompareTo(ys);
            }

            return 0;
        }

        /// <summary>Poängsumman för ett delmoment. Serier som inte matats in räknas som 0 — samma
        /// försiktighet som <c>TotalScore</c>: en utebliven serie är inga poäng, inte ett hål.</summary>
        private static int SegmentScore(PrecisionShooterResult shooter, SeriesSegments.Segment seg)
        {
            var sum = 0;
            foreach (var r in shooter.Results)
            {
                if (r.SeriesNumber < seg.FirstSeries || r.SeriesNumber > seg.LastSeries) continue;
                sum += ScoreOf(r.Shots);
            }
            return sum;
        }

        private static int ScoreOf(string? shotsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(shotsJson)) return 0;
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(shotsJson);
                if (shots == null || shots.Count == 0) return 0;
                return (int)ScoringUtilities.CalculateTotal(shots);
            }
            catch
            {
                return 0;
            }
        }
    }
}
