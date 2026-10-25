using System.Globalization;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Personens nyckel i bemanningen — EN funktion (Expertgruppen 1c/steg 3). Förut skrevs regeln ut på sex ställen, ett av
    /// dem utan <c>Trim</c>, så samma person kunde bli två. Medlem: <c>m:{id}</c>. Utan konto: <c>n:{namnet, trimmat, i gemener}</c>.
    /// </summary>
    public static class StaffPersonKey
    {
        public static string For(int? memberId, string? displayName) =>
            memberId is > 0
                ? "m:" + memberId.Value.ToString(CultureInfo.InvariantCulture)
                : "n:" + (displayName ?? "").Trim().ToLowerInvariant();

        /// <summary>Medlemmens id ur en nyckel, eller 0 när nyckeln inte är en medlems.</summary>
        public static int MemberIdOf(string? key) =>
            key != null && key.StartsWith("m:", System.StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;
    }
}
