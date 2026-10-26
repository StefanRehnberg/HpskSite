namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Skjutledare och stationschef får "Hitta ersättare" <b>för sin del</b> (steg 4–6, "Inte gjort" 1): det är de som står på
    /// banan kl 06:30 när någon uteblir, och de kan inte be en sammankallande om en rättighet de inte har. Ren funktion över fakta
    /// som kontrollern hämtar; svaret är texten till den som frågar, eller null = får.
    /// <list type="bullet">
    /// <item><b>Del</b> = planeringen uppdraget ligger i. Ledarrollen ska finnas i just den, och den som har den får inte ha tackat nej.</item>
    /// <item>Rader med behörighet och Tävlingsledning lämnas aldrig över här — en överlämning släcker behörigheten, och den som delar ut
    /// den är en sammankallande (<see cref="StaffAdminGrantRules"/>).</item>
    /// <item>Den som lämnar över sig själv säger "Jag kan inte" i Mitt schema; det ger sammankallande besked och går vidare.</item>
    /// <item>Den som tar över ska ha anmält sig till dagen (reservpoolen). Annars kunde vilken medlem som helst läggas på en rad.</item>
    /// </list>
    /// </summary>
    public static class ReplacementAccessRules
    {
        public const string SkjutledareRole = "skjutledare";
        public const string StationschefRole = "stationschef";
        public const string TavlingsledningRole = "tavlingsledning";

        public const string NotLeader = "Bara den som leder delen, eller bemannar den, kan lämna över uppdrag här.";
        public const string NeedsConvener = "Uppdraget har behörighet i tävlingen. Det lämnas över av den som bemannar.";
        public const string Yourself = "Du kan inte lämna över ditt eget uppdrag här. Tryck Jag kan inte i Mitt schema.";
        public const string NotInPool = "Den personen har inte anmält sig till dagen.";

        /// <summary>Ledarrollerna som ger rätten. Stationsmarkör, markör och övriga roller gör det inte.</summary>
        public static bool IsLeadRole(string? roleKey) =>
            string.Equals(roleKey, SkjutledareRole, System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(roleKey, StationschefRole, System.StringComparison.OrdinalIgnoreCase);

        /// <param name="callerLeadsPart">Har den som frågar en ledarroll (ej avböjd) i uppdragets planering?</param>
        /// <param name="candidateIsInPool">Null vid läsning av listan; vid överlämning om den nya personen finns i poolen.</param>
        public static string? Refusal(bool callerLeadsPart, string? targetRoleKey, bool targetHasAdminAccess, bool targetIsCaller, bool? candidateIsInPool)
        {
            if (!callerLeadsPart) return NotLeader;
            if (targetIsCaller) return Yourself;
            if (targetHasAdminAccess || string.Equals(targetRoleKey, TavlingsledningRole, System.StringComparison.OrdinalIgnoreCase)) return NeedsConvener;
            if (candidateIsInPool == false) return NotInPool;
            return null;
        }
    }
}
