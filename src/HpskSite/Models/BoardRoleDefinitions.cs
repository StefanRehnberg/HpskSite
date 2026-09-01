namespace HpskSite.Models
{
    public static class BoardRoleDefinitions
    {
        // IsBoardMember distinguishes who actually sits on the styrelse (and is therefore seeded into
        // meeting attendance, counted toward quorum, and gates /styrelse access) from other elected
        // functionaries (revisor, valberedning) who are chosen at the årsmöte but are NOT board members.
        // Valberedning roles are managed on the Valberedning tab; Revisor/Revisorssuppleant show under
        // "Övriga förtroendevalda" on the Styrelsen tab.
        public static readonly (string Key, string Label, int DefaultSort, bool IsBoardMember)[] AllRoles = new[]
        {
            ("Ordforande",                "Ordförande",                   1, true),
            ("ViceOrdforande",            "Vice ordförande",              2, true),
            ("Sekreterare",               "Sekreterare",                  3, true),
            ("Kassor",                    "Kassör",                        4, true),
            ("Ledamot",                   "Ledamot",                       5, true),
            ("Suppleant",                 "Suppleant",                     6, true),
            ("Revisor",                   "Revisor",                       7, false),
            ("Revisorssuppleant",         "Revisorssuppleant",             8, false),
            ("ValberedningSammankallande","Valberedning (sammankallande)", 9, false),
            ("Valberedning",              "Valberedning",                 10, false),
        };

        /// <summary>Role keys that belong to the valberedning (managed on the Valberedning tab, never board members).</summary>
        public static readonly string[] ValberedningRoleKeys = { "Valberedning", "ValberedningSammankallande" };

        /// <summary>
        /// The chairman's role key. Named because three places now ask "is this the ordförande?" —
        /// the two meeting-attendance seeders and the föreningsintyg signatory proposal — and a
        /// misspelled literal in any of them fails silently: nobody is chairman, no quorum flag, no
        /// name on the certificate. The other keys stay literals until something needs them.
        /// </summary>
        public const string RoleOrdforande = "Ordforande";

        public static string GetLabel(string roleKey)
        {
            var match = AllRoles.FirstOrDefault(r => r.Key == roleKey);
            return match.Label ?? roleKey;
        }

        public static int GetDefaultSort(string roleKey)
        {
            if (roleKey == "Custom") return 99;
            var match = AllRoles.FirstOrDefault(r => r.Key == roleKey);
            return match.DefaultSort > 0 ? match.DefaultSort : 99;
        }

        public static bool GetDefaultIsBoardMember(string roleKey)
        {
            if (roleKey == "Custom") return false;
            var match = AllRoles.FirstOrDefault(r => r.Key == roleKey);
            return match.IsBoardMember;
        }
    }
}
