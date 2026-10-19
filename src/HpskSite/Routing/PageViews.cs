namespace HpskSite.Routing
{
    /// <summary>
    /// <b>Vyerna för sidorna som inte längre är noder</b> (C3-planen §5.5 punkt 6). Namnen får ALDRIG vara samma som en
    /// Umbraco-mall: att ta bort en mall raderar vyfilen med mallens namn. Filerna med de gamla namnen
    /// (<c>Competition.cshtml</c> …) är mellanled som visar de här, för Umbracos mallar i nodläget.
    /// <c>PageViewsTests</c> fäller bygget om ett namn krockar med en mall eller filen saknas.
    /// </summary>
    public static class PageViews
    {
        public const string Competition = "CompetitionPage";
        public const string Series = "CompetitionSeriesPage";
        public const string Result = "CompetitionResultPage";
        public const string StartList = "PrecisionStartListPage";
        public const string FinalsStartList = "PrecisionFinalsStartListPage";
        public const string Event = "ClubSimpleEventPage";

        public static readonly string[] All = { Competition, Series, Result, StartList, FinalsStartList, Event };

        /// <summary>Vyn för en nods mall: sidans egen vy när mallen är en av de gamla, annars mallen själv.</summary>
        public static string ForTemplate(string templateAlias) => templateAlias switch
        {
            "Competition" => Competition,
            "CompetitionSeries" => Series,
            "CompetitionResult" => Result,
            "PrecisionStartList" => StartList, // startliststore-ok: mallens namn, inte doktypen
            "PrecisionFinalsStartList" or "precisionFinalsStartList" => FinalsStartList,
            "ClubSimpleEvent" => Event,
            _ => templateAlias,
        };

        /// <summary>Mallarna som hör till dokumenttyperna som tas bort, med sina gamla vyer (nu mellanled).</summary>
        public static readonly string[] LegacyTemplates =
        {
            "Competition", "CompetitionSeries", "CompetitionResult", "PrecisionStartList", "PrecisionFinalsStartList", // startliststore-ok: resultstore-ok: competitionstore-ok: mallarnas namn
            "ClubSimpleEvent", "CompetitionSeason", "CompetitionRegistration", "CompetitionRegistrationsHub",
            "CompetitionStartListsHub", "CompetitionTeamRegistration",
        };
    }
}
