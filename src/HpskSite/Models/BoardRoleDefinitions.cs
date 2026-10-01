namespace HpskSite.Models
{
    public static class BoardRoleDefinitions
    {
        // IsBoardMember distinguishes who actually sits on the styrelse (and is therefore seeded into
        // meeting attendance, counted toward quorum, and gates /styrelse access) from other elected
        // functionaries (revisor, valberedning) who are chosen at the årsmöte but are NOT board members.
        // Valberedning roles are managed on the Valberedning tab; Revisor/Revisorssuppleant show under
        // "Övriga förtroendevalda" on the Styrelsen tab.
        //
        // Owners = which owner types the role may be given to (BoardRoleOwners). The krets
        // assignments (kretsgranskning fas 1) only exist for a region: a club has no results to
        // review for others, and offering "Resultatgranskare" on a club board would invite a
        // role that grants nothing anywhere.
        public static readonly (string Key, string Label, int DefaultSort, bool IsBoardMember, BoardRoleOwners Owners)[] AllRoles = new[]
        {
            ("Ordforande",                "Ordförande",                   1, true,  BoardRoleOwners.Both),
            ("ViceOrdforande",            "Vice ordförande",              2, true,  BoardRoleOwners.Both),
            ("Sekreterare",               "Sekreterare",                  3, true,  BoardRoleOwners.Both),
            ("Kassor",                    "Kassör",                        4, true,  BoardRoleOwners.Both),
            ("Ledamot",                   "Ledamot",                       5, true,  BoardRoleOwners.Both),
            ("Suppleant",                 "Suppleant",                     6, true,  BoardRoleOwners.Both),
            ("Revisor",                   "Revisor",                       7, false, BoardRoleOwners.Both),
            ("Revisorssuppleant",         "Revisorssuppleant",             8, false, BoardRoleOwners.Both),
            ("ValberedningSammankallande","Valberedning (sammankallande)", 9, false, BoardRoleOwners.Both),
            ("Valberedning",              "Valberedning",                 10, false, BoardRoleOwners.Both),
            (RoleResultatgranskare,       "Resultatgranskare",            11, false, BoardRoleOwners.Region),
            (RoleBangranskare,            "Bangranskare (fält)",          12, false, BoardRoleOwners.Region),
            (RoleTavlingsansvarig,        "Tävlingsansvarig",             13, false, BoardRoleOwners.Region),
        };

        /// <summary>Role keys that belong to the valberedning (managed on the Valberedning tab, never board members).</summary>
        public static readonly string[] ValberedningRoleKeys = { "Valberedning", "ValberedningSammankallande" };

        // ---- Kretsens uppdrag (kretsgranskning fas 1) --------------------------------------
        // Assignments, not board seats: IsBoardMember = false, so they never touch attendance or
        // quorum. A reviewer is usually already a ledamot through another row. Who HOLDS one is
        // answered only by KretsUppdragService — never query these keys directly elsewhere.

        /// <summary>Kontrollerar och godkänner resultatlistor (SHB C.4.3.1.10, C.4.2.1).</summary>
        public const string RoleResultatgranskare = "Resultatgranskare";

        /// <summary>Granskar målförutsättningar och stationsbeskrivningar i fält (SHB C.3.5.2.2).</summary>
        public const string RoleBangranskare = "Bangranskare";

        /// <summary>Tar emot ansökningar och skriver kretsens yttrande (SHB C.3.5.3.2, C.3.5.4).</summary>
        public const string RoleTavlingsansvarig = "Tavlingsansvarig";

        /// <summary>The three krets assignments, in display order.</summary>
        public static readonly string[] KretsUppdragRoleKeys =
            { RoleResultatgranskare, RoleBangranskare, RoleTavlingsansvarig };

        /// <summary>
        /// May this role be given to this owner type? Custom roles may go anywhere; an unknown key
        /// may not (it would be a role nothing reads).
        /// </summary>
        public static bool AppliesTo(string roleKey, int ownerType)
        {
            if (roleKey == "Custom") return true;
            var match = AllRoles.FirstOrDefault(r => r.Key == roleKey);
            if (match.Key == null) return false;
            return ownerType switch
            {
                DocumentOwnerType.Club => match.Owners.HasFlag(BoardRoleOwners.Club),
                DocumentOwnerType.Region => match.Owners.HasFlag(BoardRoleOwners.Region),
                _ => false
            };
        }

        /// <summary>What the assignment does, in the words shown under its heading.</summary>
        public static string KretsUppdragTask(string roleKey) => roleKey switch
        {
            RoleResultatgranskare => "Kontrollerar och godkänner resultatlistor, och godkänner utökad stickprovskontroll av vapen.",
            RoleBangranskare => "Granskar målförutsättningar och stationsbeskrivningar i fält, och godkänner mål utanför SHB:s förteckning.",
            RoleTavlingsansvarig => "Tar emot klubbarnas tävlingsansökningar, skriver kretsens yttrande och skickar vidare till Förbundet.",
            _ => ""
        };

        /// <summary>True for the krets assignments (Resultatgranskare, Bangranskare, Tävlingsansvarig).</summary>
        public static bool IsKretsUppdrag(string? roleKey) =>
            roleKey != null && KretsUppdragRoleKeys.Contains(roleKey);

        /// <summary>
        /// The chairman's role key. Named because three places now ask "is this the ordförande?" —
        /// the two meeting-attendance seeders and the föreningsintyg signatory proposal — and a
        /// misspelled literal in any of them fails silently: nobody is chairman, no quorum flag, no
        /// name on the certificate. The other keys stay literals until something needs them.
        /// </summary>
        public const string RoleOrdforande = "Ordforande";

        /// <summary>
        /// The treasurer's role key. Named because the ledger's write access follows it
        /// (LedgerAccessService, 2026-09-24): a misspelled literal there would silently give NO ONE
        /// the treasurer's rights and fall back to the club admin for every association.
        /// </summary>
        public const string RoleKassor = "Kassor";

        /// <summary>
        /// Elected auditors — READ access to the ledger via the auditor branch, never write, never
        /// board membership (IsBoardMember = false: they must not count toward quorum on the board
        /// they audit). The suppleant is included: elected, and must be able to step in unprepared.
        /// </summary>
        public static readonly string[] AuditorRoleKeys = { "Revisor", "Revisorssuppleant" };

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

    /// <summary>Which owner types a predefined board role may be given to.</summary>
    [Flags]
    public enum BoardRoleOwners
    {
        Club = 1,
        Region = 2,
        Both = Club | Region
    }
}
