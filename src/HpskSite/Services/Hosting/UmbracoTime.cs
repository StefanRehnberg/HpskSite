namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Umbraco 17 lagrar och lämnar ut sina systemdatum i UTC — nodernas och medlemmarnas
    /// <c>CreateDate</c>/<c>UpdateDate</c>/<c>LastLoginDate</c> och kolumnerna i <c>umbracoNode</c>,
    /// <c>cmsMember</c>, <c>umbracoContentVersion</c> m.fl. (migreringen <c>MigrateSystemDatesToUtc</c>).
    /// Resten av sajten räknar i lokal tid (<c>DateTime.Now</c>, tävlingsdatum, våra egna tabeller), som
    /// Umbraco 16 gjorde.
    ///
    /// Ett systemdatum som visas eller jämförs med en lokal tid går därför genom de här metoderna, som ger
    /// tillbaka exakt det värde Umbraco 16 lämnade ut: lokal tid med <see cref="DateTimeKind.Unspecified"/>.
    ///
    /// ⚠️ Gäller BARA Umbracos systemdatum. Datumegenskaper på dokumenttyperna (t.ex. <c>competitionDate</c>)
    /// och våra egna kolumner migrerades inte och är fortfarande lokal tid — de ska INTE gå hit.
    /// </summary>
    public static class UmbracoTime
    {
        /// <summary>Ett systemdatum ur Umbracos API (Kind = Utc) som lokal tid.</summary>
        public static DateTime Local(DateTime value)
            => value.Kind == DateTimeKind.Utc
                ? DateTime.SpecifyKind(value.ToLocalTime(), DateTimeKind.Unspecified)
                : value;

        public static DateTime? Local(DateTime? value) => value.HasValue ? Local(value.Value) : null;

        /// <summary>
        /// Ett systemdatum läst med SQL direkt ur en Umbraco-tabell. Värdet är UTC men kommer ut med
        /// Kind = Unspecified, så det måste märkas som UTC innan det räknas om.
        /// </summary>
        public static DateTime FromDbUtc(DateTime value)
            => value == DateTime.MinValue
                ? value
                : DateTime.SpecifyKind(DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime(), DateTimeKind.Unspecified);
    }
}
