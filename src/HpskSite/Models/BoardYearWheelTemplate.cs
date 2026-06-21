namespace HpskSite.Models
{
    /// <summary>
    /// Standard annual governance cycle for a Swedish ideell förening. Seeded per club/region per year
    /// the first time the Årshjul is opened (mirrors BoardMeetingTemplates). Dates are sensible defaults
    /// the club can edit; LOK-stöd deadlines (25 feb / 25 aug) are the fixed RF dates.
    /// </summary>
    public static class BoardYearWheelTemplate
    {
        // (month, day, title) — TargetDate is built for the requested year.
        public static readonly (int Month, int Day, string Title)[] Items = new[]
        {
            (1, 31, "Bokslut och årsredovisning klar"),
            (2, 15, "Verksamhetsberättelse klar"),
            (2, 25, "LOK-stöd: ansökan för höstterminen (deadline 25 feb)"),
            (2, 28, "Revisorernas granskning klar"),
            (3, 15, "Kallelse till årsmöte utskickad"),
            (3, 31, "Årsmöte"),
            (4, 15, "Konstituerande styrelsemöte"),
            (8, 25, "LOK-stöd: ansökan för vårterminen (deadline 25 aug)"),
            (11, 30, "Budget och verksamhetsplan för nästa år"),
            (12, 31, "Medlemsrapportering till IdrottOnline/SISU"),
        };
    }
}
