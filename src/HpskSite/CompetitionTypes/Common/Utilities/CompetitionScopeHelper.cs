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
        /// True for all four mästerskap values (SM, Landsdel, Krets, Klubb).
        /// Championship rules apply: Särskjutning must resolve tied medal positions 1–3.
        /// </summary>
        public static bool IsChampionshipScope(string? scope) =>
            scope is SvensktMasterskap or Landsdelsmasterskap
                  or Kretsmasterskap or Klubbmasterskap;
    }
}
