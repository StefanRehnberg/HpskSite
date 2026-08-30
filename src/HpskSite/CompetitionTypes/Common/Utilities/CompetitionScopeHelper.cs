namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Helpers for interpreting the <c>competitionScope</c> property.
    /// </summary>
    public static class CompetitionScopeHelper
    {
        public const string SvensktMasterskap = "Svenskt Mästerskap";
        public const string Landsdelsmasterskap = "Landsdelsmästerskap";
        public const string Kretsmasterskap = "Kretsmästerskap";
        public const string Klubbmasterskap = "Klubbmästerskap";

        /// <summary>
        /// De fyra värdena i visningsordning — och den ENDA källan varje
        /// omfattnings-dropdown ska renderas ur.
        ///
        /// ⚠️ Varför den finns: värdena var handskrivna på nytt i varje modal, och de
        /// hann drifta. Springskyttemodalen hade <c>value="Kretsmasterskap"</c> UTAN
        /// diakriter medan konstanterna och precisionsmodalen har med. Jämförelserna
        /// här och i <c>CompetitionUrlProvider</c> är <c>StringComparison.Ordinal</c>,
        /// så en springskyttetävling satt till mästerskap via sin egen modal räknades
        /// inte som mästerskap alls: fel URL-form för SM och Landsdel, och
        /// <see cref="IsChampionshipScope"/> svarade false.
        ///
        /// Rendera alltid dropdownen ur den här listan. Skriv aldrig värdena för hand.
        /// </summary>
        public static readonly string[] All =
        {
            SvensktMasterskap, Landsdelsmasterskap, Kretsmasterskap, Klubbmasterskap
        };

        /// <summary>
        /// True for all four mästerskap values (SM, Landsdel, Krets, Klubb).
        /// Championship rules apply: Särskjutning must resolve tied medal positions 1–3.
        /// </summary>
        public static bool IsChampionshipScope(string? scope) =>
            scope is SvensktMasterskap or Landsdelsmasterskap
                  or Kretsmasterskap or Klubbmasterskap;
    }
}
