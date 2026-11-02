namespace HpskSite.Services.Security
{
    /// <summary>
    /// Vad en sammanslagning av två medlemmar får göra med ROLLER (säkerhetsplanen 2026-11-02, A5/H4).
    ///
    /// <para>Sammanslagningen förde förut över ALLA förlorarens roller till kontot som behålls och raderade
    /// förloraren. En klubbadmin kunde därför välja sitt eget konto som "behålls" och en sajtadmin som "tas bort"
    /// — och fick sajtadminrollen medan sajtadmin raderades.</para>
    ///
    /// <para>Reglerna: <b>sajtadmin</b> (<c>Administrators</c>) och <b>kretsadmin</b> (<c>RegionalAdmin_*</c>) förs
    /// aldrig över av någon som inte själv är sajtadmin; <b>klubbadmin</b> för klubb X förs bara över av någon som
    /// administrerar X. Ett konto med en roll som inte får föras över får inte heller raderas av en sammanslagning
    /// (annars kan en klubbadmin ta bort en annan klubbs eller kretsens administratör). Sajtadmin får allt.</para>
    /// </summary>
    public static class MergeRoleRules
    {
        /// <summary>Får <paramref name="role"/> föras över från förloraren till det konto som behålls?</summary>
        public static bool MayTransfer(string? role, bool actorIsSiteAdmin, Func<int, bool> actorAdministersClub) =>
            actorIsSiteAdmin || PrivilegedAccount.MayTransferInMerge(role, actorAdministersClub);

        /// <summary>
        /// Null om sammanslagningen får göras; annars en svensk förklaring till varför den nekas — förloraren har en
        /// roll som aktören inte får föra över (och därmed inte heller ta bort kontot för).
        /// </summary>
        public static string? LoserRefusal(string loserName, IEnumerable<string> loserRoles, bool actorIsSiteAdmin, Func<int, bool> actorAdministersClub)
        {
            if (actorIsSiteAdmin) return null;
            foreach (var role in loserRoles)
            {
                if (MayTransfer(role, false, actorAdministersClub)) continue;
                var what = Describe(role);
                return $"{loserName} är {what}, så kontot kan inte slås ihop härifrån. Be en sajtadministratör om hjälp.";
            }
            return null;
        }

        private static string Describe(string role)
        {
            if (string.Equals(role, PrivilegedAccount.SiteAdminRole, StringComparison.OrdinalIgnoreCase))
                return "sajtadministratör";
            if (role.StartsWith(PrivilegedAccount.RegionalAdminPrefix, StringComparison.OrdinalIgnoreCase))
                return "kretsadministratör";
            if (role.StartsWith(PrivilegedAccount.ClubAdminPrefix, StringComparison.OrdinalIgnoreCase))
                return "administratör för en annan klubb";
            return "administratör";
        }
    }
}
