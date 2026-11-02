namespace HpskSite.Services.Security
{
    /// <summary>
    /// Vilka konton som är PRIVILEGIERADE (säkerhetsplanen 2026-11-02, H2/H2b/H4).
    ///
    /// <para>Regeln: <b>en länk i en inkorg eller en QR-kod får aldrig ge en session till ett privilegierat konto
    /// utan lösenord</b>, och en roll av den här sorten förs aldrig över till ett annat konto av en
    /// sammanslagning. Rollnamnen är samma som <c>AdminAuthorizationService</c> använder.</para>
    /// </summary>
    public static class PrivilegedAccount
    {
        public const string SiteAdminRole = "Administrators";
        public const string RegionalAdminPrefix = "RegionalAdmin_";
        public const string ClubAdminPrefix = "ClubAdmin_";

        /// <summary>Sajtadmin, kretsadmin eller klubbadmin (för någon klubb/krets).</summary>
        public static bool IsPrivilegedRole(string? role)
        {
            if (string.IsNullOrEmpty(role)) return false;
            return string.Equals(role, SiteAdminRole, StringComparison.OrdinalIgnoreCase)
                || role.StartsWith(RegionalAdminPrefix, StringComparison.OrdinalIgnoreCase)
                || role.StartsWith(ClubAdminPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Har kontot minst en privilegierad roll?</summary>
        public static bool HasPrivilegedRole(IEnumerable<string>? roles) =>
            roles != null && roles.Any(IsPrivilegedRole);

        /// <summary>
        /// Roller som en sammanslagning ALDRIG för över från förloraren till det konto som behålls:
        /// sajtadmin och kretsadmin alltid; klubbadmin för klubb X bara om den som slår ihop själv
        /// administrerar X (<paramref name="actorAdministersClub"/>).
        /// </summary>
        public static bool MayTransferInMerge(string? role, Func<int, bool> actorAdministersClub)
        {
            if (string.IsNullOrEmpty(role)) return true;
            if (string.Equals(role, SiteAdminRole, StringComparison.OrdinalIgnoreCase)) return false;
            if (role.StartsWith(RegionalAdminPrefix, StringComparison.OrdinalIgnoreCase)) return false;
            if (role.StartsWith(ClubAdminPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var tail = role.Substring(ClubAdminPrefix.Length);
                return int.TryParse(tail, out var clubId) && actorAdministersClub(clubId);
            }
            return true;
        }
    }
}
