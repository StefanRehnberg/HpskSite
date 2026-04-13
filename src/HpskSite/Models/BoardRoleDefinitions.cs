namespace HpskSite.Models
{
    public static class BoardRoleDefinitions
    {
        public static readonly (string Key, string Label, int DefaultSort, bool IsBoardMember)[] AllRoles = new[]
        {
            ("Ordforande",      "Ordförande",        1, true),
            ("ViceOrdforande",  "Vice ordförande",   2, true),
            ("Sekreterare",     "Sekreterare",       3, true),
            ("Kassor",          "Kassör",             4, true),
            ("Ledamot",         "Ledamot",            5, true),
            ("Suppleant",       "Suppleant",          6, true),
            ("Revisor",         "Revisor",            7, true),
            ("Valberedning",    "Valberedning",       8, true),
        };

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
