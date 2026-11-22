using HpskSite.CompetitionTypes.Faltskytte.Models;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Magnumfält shoot-off round comparator.
    /// SHB E.6.11.3: särskjutningen sker på en särskild station där alla figurer är poängmål och högst en träff per figur räknas;
    /// "reglerna för särskiljning ... gäller", alltså träff först, sedan poäng. Eftersom en träff per figur är max är antalet
    /// träffade figurer lika med antalet träff. Rundans träff räknas ur <see cref="FaltskytteShootOffEntry.PoangmalScores"/>
    /// (poäng över 0 = träff) när <see cref="FaltskytteShootOffEntry.Hits"/> saknas; poängen är
    /// <see cref="FaltskytteShootOffEntry.TiebreakerScore"/> (summan av PoangmalScores).
    /// Display: "{träff} träff, {poäng}p" (e.g. "5 träff, 23p"), eller "{poäng}p" när träffen inte går att räkna ut.
    /// </summary>
    public class MagnumRoundComparer : IShootOffRoundComparer
    {
        /// <summary>Antal träffade figurer i rundan, eller null när det inte går att avgöra.</summary>
        public static int? HitsOf(FaltskytteShootOffEntry e)
        {
            if (e.Hits.HasValue) return e.Hits.Value;
            if (string.IsNullOrWhiteSpace(e.PoangmalScores)) return null;
            try
            {
                var scores = JsonConvert.DeserializeObject<List<int?>>(e.PoangmalScores);
                return scores?.Count(s => s.GetValueOrDefault() > 0);
            }
            catch { return null; }
        }

        public int Compare(FaltskytteShootOffEntry a, FaltskytteShootOffEntry b)
        {
            var hitDiff = (HitsOf(b) ?? 0).CompareTo(HitsOf(a) ?? 0);
            if (hitDiff != 0) return hitDiff;
            return (b.TiebreakerScore ?? 0).CompareTo(a.TiebreakerScore ?? 0);
        }

        public string FormatRound(FaltskytteShootOffEntry e)
        {
            var hits = HitsOf(e);
            return hits.HasValue ? $"{hits.Value} träff, {e.TiebreakerScore ?? 0}p" : $"{e.TiebreakerScore ?? 0}p";
        }
    }
}
