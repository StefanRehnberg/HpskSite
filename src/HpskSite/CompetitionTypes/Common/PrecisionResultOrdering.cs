using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// I vilken ordning skyttarna i en klass står — ETT ställe, för hela precisionsfamiljen.
    ///
    /// Grundregeln är poäng, sedan innertior, sedan grenens egen återräkning. Men
    /// <b>Nationell Helmatch läser delmomenten FÖRE innertiorna</b>: SHB:s särskiljning för grenen
    /// är delmoment C och därefter B, och nämner inte innertior alls. Ordningen mellan de två
    /// stegen är hela skillnaden — rapporterat efter klubbmästerskapet 2026-09-19 (tävling 7075)
    /// stod skytten med högst fältresultat på plats 6 i stället för 5, eftersom innertiorna lästes
    /// först och avgjorde innan delmomentet ens kom på tal.
    ///
    /// Ligger här och inte som ett uttryck inne i resultatberäkningen därför att tre ytor behöver
    /// samma svar: resultatlistans klasstabeller, medaljkategorierna som namnger medaljörerna, och
    /// särskiljningen i vapengruppsvyn. En kopia som glider blir en lista där två ytor sätter olika
    /// personer på samma plats.
    /// </summary>
    public static class PrecisionResultOrdering
    {
        /// <summary>Läser grenen delmoment före innertior? Sant bara för Nationell Helmatch.</summary>
        public static bool DelmomentBeforeXCount(string? typeId) =>
            SeriesSegments.Delmoment(typeId, "C") != null;

        /// <summary>
        /// Skyttarna i placeringsordning. <paramref name="comparer"/> är grenens återräkning —
        /// <c>NationellHelmatchTieBreaker</c>, <c>MilsnabbTieBreaker</c> eller
        /// <c>SeriesCountBackComparer</c> — och jämför STIGANDE, alltså gjord för
        /// <c>ThenByDescending</c>.
        /// </summary>
        public static List<PrecisionShooterResult> Order(
            IEnumerable<PrecisionShooterResult> shooters,
            string? typeId,
            IComparer<PrecisionShooterResult> comparer)
        {
            return DelmomentBeforeXCount(typeId)
                ? shooters.OrderByDescending(s => s.TotalScore)
                          .ThenByDescending(s => s, comparer)
                          // Innertiorna ligger KVAR, men sist: de är inget kriterium i SHB:s
                          // särskiljning för grenen, och nästa steg i regeln är särskjutning eller
                          // lottning. Att låta dem ge en sista icke-slumpmässig ordning är bättre
                          // än att lämna två skyttar i godtycklig inbördes ordning.
                          .ThenByDescending(s => s.TotalXCount)
                          .ToList()
                : shooters.OrderByDescending(s => s.TotalScore)
                          .ThenByDescending(s => s.TotalXCount)
                          .ThenByDescending(s => s, comparer)
                          .ToList();
        }
    }
}
