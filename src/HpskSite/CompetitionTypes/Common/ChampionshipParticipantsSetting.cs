namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// De två små inställningarna på tävlingen som styr utanför-mästerskapet-beteendet: "vilka deltar" och växeln per tävling.
    /// Lagrade värden normaliseras vid skrivning så att databasen bara kan innehålla de kända formerna.
    /// </summary>
    public static class ChampionshipParticipantsSetting
    {
        public const string All = "all";
        public const string Own = "own";
        public const string OwnPlusNeighbours = "own+neighbours";

        public const string ModeOn = "on";

        /// <summary>null/tomt/"all"/okänt = null (alla deltar, dagens beteende); annars "own" eller "own+neighbours".</summary>
        public static string? Normalize(string? value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return v switch
            {
                Own => Own,
                OwnPlusNeighbours => OwnPlusNeighbours,
                _ => null
            };
        }

        /// <summary>null/tomt/"off"/okänt = null (av, dagens beteende); "on" = på.</summary>
        public static string? NormalizeMode(string? value) =>
            string.Equals((value ?? "").Trim(), ModeOn, StringComparison.OrdinalIgnoreCase) ? ModeOn : null;
    }
}
