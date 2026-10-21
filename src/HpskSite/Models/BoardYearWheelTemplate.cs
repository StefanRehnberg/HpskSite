namespace HpskSite.Models
{
    /// <summary>
    /// Standard annual governance cycle for a Swedish ideell förening. Since 2026-10-21 each item becomes a
    /// yearly <see cref="BoardWheelRule"/> per club/region the first time the Årshjul is read (one time per
    /// owner). Dates are sensible defaults the club can edit, and clubs add their own items freely.
    ///
    /// <para><b>⚠️ The Key is stored on the rule (<c>TemplateKey</c>) and is what the kallelse ticks on.</b>
    /// Never rename a key — a club's rule would stop matching.</para>
    /// </summary>
    public static class BoardYearWheelTemplate
    {
        public const string KallelseKey = "kallelse";

        public static readonly (string Key, int Month, int Day, string Title)[] Items = new[]
        {
            ("bokslut", 1, 31, "Bokslut och årsredovisning klar"),
            ("map", 1, 31, "Årsredovisning i MAP"),
            ("verksamhetsberattelse", 2, 15, "Verksamhetsberättelse klar"),
            ("revision", 2, 28, "Revisorernas granskning klar"),
            (KallelseKey, 3, 15, "Kallelse till årsmöte utskickad"),
            ("arsmote", 3, 31, "Årsmöte"),
            ("konstituering", 4, 15, "Konstituerande styrelsemöte"),
            ("budget", 11, 30, "Budget och verksamhetsplan för nästa år"),
            ("medlemsrapportering", 12, 31, "Medlemsrapportering till IdrottOnline/SISU"),
        };
    }
}
