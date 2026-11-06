namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Inställningen på tävlingen "vilka deltar i mästerskapet", som styr förslagen om vem som skjuter utanför. Lagrade värden
    /// normaliseras vid skrivning så att databasen bara kan innehålla de kända formerna. (Växeln per tävling är borttagen.)
    /// </summary>
    public static class ChampionshipParticipantsSetting
    {
        public const string All = "all";
        public const string Own = "own";
        public const string OwnPlusNeighbours = "own+neighbours";

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
    }
}
