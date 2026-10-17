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
        public static DateTime FromUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local || value == DateTime.MinValue || value == DateTime.MaxValue) return value;
            return DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
        }

        public static DateTime? FromUtc(DateTime? value) => value.HasValue ? FromUtc(value.Value) : null;
    }
}
