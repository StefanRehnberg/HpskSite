namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Umbraco 17 lagrar och lämnar ut sina systemdatum i UTC — nodernas och medlemmarnas
    /// <c>CreateDate</c>/<c>UpdateDate</c>/<c>LastLoginDate</c> och kolumnerna i <c>umbracoNode</c>,
    /// <c>cmsMember</c>, <c>umbracoContentVersion</c> m.fl. (migreringen <c>MigrateSystemDatesToUtc</c>).
    /// Umbraco 16 lämnade ut samma datum som serverns lokala tid med <see cref="DateTimeKind.Local"/>
    /// (mätt i fas E 2026-10-17), och resten av sajten räknar så.
    ///
    /// Ett systemdatum som visas eller jämförs med en lokal tid går därför genom de här metoderna, som ger
    /// tillbaka exakt det värde Umbraco 16 lämnade ut.
    ///
    /// ⚠️ Gäller BARA Umbracos systemdatum. Datumegenskaper på dokumenttyperna (t.ex. <c>competitionDate</c>)
    /// och våra egna kolumner migrerades inte och ska INTE gå hit.
    /// ⚠️ Prod-servern kör i UTC: där är lokal tid = UTC, och omräkningen gör ingenting.
    /// </summary>
    public static class UmbracoTime
    {
        /// <summary>Ett systemdatum ur Umbracos API (Kind = Utc) som lokal tid (Kind = Local), som i 16.</summary>
        public static DateTime Local(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;

        public static DateTime? Local(DateTime? value) => value.HasValue ? Local(value.Value) : null;

        /// <summary>
        /// Ett systemdatum läst med SQL direkt ur en Umbraco-tabell. Värdet är UTC men kommer ut med
        /// Kind = Unspecified; i 16 var det lokal tid med Kind = Unspecified, och det är vad som ges tillbaka.
        /// </summary>
        public static DateTime FromDbUtc(DateTime value)
            => value == DateTime.MinValue
                ? value
                : DateTime.SpecifyKind(DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime(), DateTimeKind.Unspecified);
    }
}
