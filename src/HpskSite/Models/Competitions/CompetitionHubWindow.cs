namespace HpskSite.Models.Competitions
{
    /// <summary>
    /// <b>Vad tävlingssidan (/tavlingar/) visar från start</b> (skalplanen, 2026-10-19, Stefans beslut): kommande
    /// tävlingar och de som avslutats de senaste <see cref="WindowMonths"/> månaderna. Äldre nås per år
    /// (<c>/tavlingar/?ar=2024</c>) eller med sökningen i alla år.
    ///
    /// <para><b>15 månader, inte "innevarande år"</b>: förra årets motsvarande tävling kan ha gått vid en annan tid
    /// på året (ett kretsmästerskap i juni förra året, i september i år), och ett kalenderår hade tömt sidan på
    /// höstens resultat den 1 januari — just inför årsmötena.</para>
    ///
    /// <para>Sidan växte annars med varje tävling som någonsin hållits: 4,2 MB HTML med 783 tävlingar på
    /// 20 000-kopian. Reglerna bor här och används av både sidan och sökningen, så de inte kan glida isär.</para>
    /// </summary>
    public static class CompetitionHubWindow
    {
        public const int WindowMonths = 15;

        /// <summary>Den äldsta sista tävlingsdag som visas från start.</summary>
        public static DateTime From(DateTime today) => today.Date.AddMonths(-WindowMonths);

        /// <summary>
        /// Visas från start: sista tävlingsdagen är inom fönstret eller senare. En tävling utan datum visas alltid —
        /// den går inte att placera i tiden, och att gömma den vore att tappa den helt.
        /// </summary>
        public static bool InWindow(CompetitionInfo c, DateTime today) =>
            c.LastDay is not DateTime last || last >= From(today);

        /// <summary>Hör till ett valt år: startdatumets år (samma som kortets säsong).</summary>
        public static bool InYear(CompetitionInfo c, int year) => c.CompetitionDate?.Year == year;

        /// <summary>Ett år i adressen (<c>?ar=</c>), eller null.</summary>
        public static int? ParseYear(string? value) =>
            int.TryParse(value, out var y) && y >= 1990 && y <= 2200 ? y : null;

        /// <summary>
        /// Varje ord i frågan måste finnas någonstans i texten, utan hänsyn till skiftläge. "km fält 2025" hittar
        /// alltså "Kretsmästerskap ... fält" bara om alla tre finns — samma som sidans egen sökning gör för en fras,
        /// men tolerant mot ordföljd.
        /// </summary>
        public static bool Matches(string haystack, string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            var text = haystack.ToLowerInvariant();
            return query.ToLowerInvariant()
                .Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .All(t => text.Contains(t));
        }
    }
}
