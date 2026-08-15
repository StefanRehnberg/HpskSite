namespace HpskSite.Models
{
    /// <summary>
    /// The shooter's wish for an early or late start, expressed per class entry on the
    /// registration (<see cref="ShootingClassEntry.StartPreference"/>).
    ///
    /// ⚠ The stored vocabulary DRIFTED for years while nothing consumed the field, so six
    /// spellings are in the wild and a plain string comparison silently matches none of them:
    ///   "Inget" (RegistrationAdminController + the public modal) · "No Preference"
    ///   (ShootingClassEntry's own default, so it lands on every entry nobody touched) ·
    ///   "" (UmbracoStartListRepository's legacy single-class fallback) · "Tidig Start" /
    ///   "Sen Start" (the pickers) · "Early" / "Late" (the deprecated display switch and the
    ///   AddLateRegistration API example).
    /// Everything that reads the field goes through <see cref="Normalize"/> or
    /// <see cref="Rank"/> — never compare the raw string.
    /// </summary>
    public static class StartPreference
    {
        public const string None = "Inget";
        public const string Early = "Tidig Start";
        public const string Late = "Sen Start";

        /// <summary>Sort key: early first, no-preference in the middle, late last.</summary>
        public const int RankEarly = 0;
        public const int RankNone = 1;
        public const int RankLate = 2;

        /// <summary>
        /// Maps any of the historical spellings onto one of the three canonical values.
        /// Anything unrecognised is treated as "no preference" — a wish we cannot read must
        /// never reorder a start list.
        /// </summary>
        public static string Normalize(string? value)
        {
            var v = (value ?? "").Trim();
            if (v.Length == 0) return None;

            if (v.Equals("Tidig Start", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("Tidig", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("Early", StringComparison.OrdinalIgnoreCase))
                return Early;

            if (v.Equals("Sen Start", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("Sen", StringComparison.OrdinalIgnoreCase) ||
                v.Equals("Late", StringComparison.OrdinalIgnoreCase))
                return Late;

            return None;
        }

        /// <summary>Sort key for start-list generators. Unreadable values sort as neutral.</summary>
        public static int Rank(string? value) => Normalize(value) switch
        {
            Early => RankEarly,
            Late => RankLate,
            _ => RankNone
        };

        /// <summary>True when the shooter actually asked for something.</summary>
        public static bool HasWish(string? value) => Normalize(value) != None;

        /// <summary>Swedish label for display.</summary>
        public static string Display(string? value) => Normalize(value) switch
        {
            Early => "Tidig start",
            Late => "Sen start",
            _ => "Ingen preferens"
        };
    }
}
