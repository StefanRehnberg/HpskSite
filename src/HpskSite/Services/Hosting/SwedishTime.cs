namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Allt på sajten ska visas i svensk tid. Prod-servern kör i svensk tid, så lokal tid = svensk tid.
    ///
    /// <see cref="FromUtc(DateTime)"/> är för ett värde som är UTC — en kolumn i
    /// <see cref="StoredTimeBasis.UtcColumns"/>, eller DateTime.UtcNow — när det ska visas eller jämföras
    /// med svensk tid. Värdet behandlas som UTC oavsett märkning, eftersom ett värde som läses med SQL direkt
    /// (ExecuteScalar, en anonym DTO) saknar märkning. Ett värde som redan är lokalt (Kind = Local) rörs inte.
    ///
    /// ⚠️ Använd den INTE på en kolumn med svensk tid — då flyttas den två timmar.
    /// </summary>
    public static class SwedishTime
    {
        private static readonly TimeZoneInfo? Sweden = Find();

        private static TimeZoneInfo? Find()
        {
            foreach (var id in new[] { "Europe/Stockholm", "W. Europe Standard Time" })
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* prova nästa */ }
            return null;
        }

        /// <summary>
        /// Klockan i Sverige just nu — oberoende av serverns tidszon. För det som styrs av klockslag på dygnet (kvällsmejlet kl 17–21);
        /// prod kör i svensk tid idag, men vid flytt till servrar i UTC (fas D) hade <see cref="DateTime.Now"/> flyttat fönstret.
        /// Faller tillbaka på lokal tid om tidszonen saknas.
        /// </summary>
        public static DateTime Now => Sweden == null ? DateTime.Now : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Sweden);

        public static DateTime FromUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local || value == DateTime.MinValue || value == DateTime.MaxValue) return value;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
        }

        public static DateTime? FromUtc(DateTime? value) => value.HasValue ? FromUtc(value.Value) : null;
    }
}
