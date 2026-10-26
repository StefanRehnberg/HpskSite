using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// A role the arrangör named themselves. See <c>Migrations/create-staff-role-table.sql</c> for the
    /// reasoning: the built-in <see cref="FunctionaryRoles"/> catalog is a closed set, and clubs use their
    /// own vocabulary for the same job (and sometimes the same word for a different job). Forcing them onto
    /// our word doesn't just annoy — it makes the data wrong.
    ///
    /// <para>A row whose <see cref="RoleKey"/> matches a built-in key <b>overrides</b> that built-in's
    /// display name for its owner scope. A new key is a new role. Merge order + resolution lives in
    /// <c>Services/Staffing/RoleCatalogService.cs</c>, which is the ONE place roles are read from — never
    /// call <see cref="FunctionaryRoles"/> directly from a surface again.</para>
    /// </summary>
    [TableName("StaffRole")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class StaffRole
    {
        public int Id { get; set; }

        /// <summary>System | Region | Club | Competition — see <see cref="RoleOwnerType"/>.</summary>
        public string OwnerType { get; set; } = RoleOwnerType.Competition;
        public int OwnerId { get; set; }

        public string RoleKey { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? PluralName { get; set; }
        public string? DefaultScopeType { get; set; }
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string? Description { get; set; }
        public string? NeedsJson { get; set; }
        public string? Disciplines { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;

        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    public static class RoleOwnerType
    {
        public const string System = "System";
        public const string Region = "Region";
        public const string Club = "Club";
        public const string Competition = "Competition";
        /// <summary>P2b: rollerna hör till planeringen och följer med när den kopplas loss.</summary>
        public const string Planning = "Planning";
    }

    // --- Request DTOs ---

    /// <summary>
    /// Create or rename a role. <see cref="RoleKey"/> empty = create (the key is generated from the name);
    /// set = update/override that key for this competition, which is how a built-in gets renamed.
    /// </summary>
    public class SaveStaffRoleRequest
    {
        public int CompetitionId { get; set; }
        public string? RoleKey { get; set; }
        public string DisplayName { get; set; } = "";
        public string? PluralName { get; set; }
        public string? DefaultScopeType { get; set; }
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string? Description { get; set; }
        public int SortOrder { get; set; }
    }

    public class DeleteStaffRoleRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";

        /// <summary>
        /// Second call. The first one reports how many people stand on the row and writes nothing; the
        /// arrangör confirms, and only then are the assignments removed with it.
        /// </summary>
        public bool DeleteAssignments { get; set; }
    }

    /// <summary>Clone a whole competition day's crew onto another day ("fyll höger" in the grid).</summary>
    public class CopyDayRequest
    {
        public int CompetitionId { get; set; }
        public string? FromDate { get; set; }   // "yyyy-MM-dd"
        public string? ToDate { get; set; }
    }

    // --- Grid DTOs (Bemanning → rutnätsvyn) ---

    /// <summary>One person in one grid cell. A thin projection of <see cref="StaffAssignmentView"/> —
    /// the grid shows name + club + time and nothing else; detail lives in the row editor.</summary>
    public class GridEntry
    {
        public int Id { get; set; }
        public int? MemberId { get; set; }
        public string DisplayName { get; set; } = "";
        public string? ClubName { get; set; }
        public string? TimeLabel { get; set; }      // "08–09"; null = heldag (shown as bare name)
        public string? ScopeLabel { get; set; }
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
        public bool IsResponsible { get; set; }
        public bool IsExternal { get; set; }        // no member account → no schedule, no push
        /// <summary>What the organiser first typed, when an identity change has since replaced it.
        /// Shown so a silent rename is visible and reversible.</summary>
        public string? OriginalName { get; set; }
        public string? Email { get; set; }
        /// <summary>Reachable at all: has an account, or at least an e-mail for a tokened invite.</summary>
        public bool HasContact { get; set; }
        public bool ReadOnly { get; set; }
        public string? Note { get; set; }
        /// <summary>Omgång 3 (3.2): passet uppdraget ligger i, om det ligger i ett.</summary>
        public int? PassId { get; set; }
        /// <summary>Expertgruppen K6: planeringen raden ägs av. Klienten skickar den med varje ändring av raden —
        /// i familjens rutnät är det inte sidans egen planering.</summary>
        public int PlanningId { get; set; }
    }

    /// <summary>Omgång 3 (3.2): ett pass i en ruta — hur många som är där samtidigt under passet, mot behovet.</summary>
    public class GridPassCover
    {
        public int PassId { get; set; }
        public string Label { get; set; } = "";
        public int Filled { get; set; }
        public int Needed { get; set; }
    }

    /// <summary>A column: one day of the plan (see <see cref="StaffDay"/>). Passes on that day are the
    /// drill-in level (not built yet).</summary>
    public class GridColumn
    {
        public string Key { get; set; } = "";        // "yyyy-MM-dd", or "" for the undated bucket
        public string Label { get; set; } = "";      // "fre 21 aug"
        /// <summary>The arrangör's own word for the day — "Iordningställande", "Vapengrupp C".</summary>
        public string? DayLabel { get; set; }
        public string Kind { get; set; } = StaffDayKind.Competition;
        public string KindLabel { get; set; } = "";
        /// <summary>False = the day carries work but is not in the plan's day list (offer to add it).</summary>
        public bool IsPlanned { get; set; }
        public int DayId { get; set; }
        public bool HasAssignments { get; set; }
        public string? TimeLabel { get; set; }       // "09:00–19:00" from the day's passes
        public List<int> PassIds { get; set; } = new();
        /// <summary>Omgång 3 (3.2): dagens pass med tider (namn), för behovet per pass. Antalen är inte satta här.</summary>
        public List<GridPassCover> Passes { get; set; } = new();
    }

    /// <summary>A row: one role (optionally narrowed to one scope, e.g. "Skjutledare · Bana 3").</summary>
    public class GridRow
    {
        /// <summary>Expertgruppen K6: planeringen raden (rollen) hör till.</summary>
        public int PlanningId { get; set; }
        public string RoleKey { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public string? ScopeLabel { get; set; }
        public bool IsCustom { get; set; }
        /// <summary>The arrangör's own running order (drag the rows). Roles they have never placed
        /// carry <see cref="FunctionaryRoles.BuiltInSortBase"/> + catalog index and trail behind.</summary>
        public int SortOrder { get; set; }
        public bool SupportsTargetRange { get; set; }
        public bool SupportsFunctionTitle { get; set; }
        public string? DefaultScopeType { get; set; }
        /// <summary>Column key → the people in that cell.</summary>
        public Dictionary<string, List<GridEntry>> Cells { get; set; } = new();
        public int Filled { get; set; }
        /// <summary>Uncovered stretches between this row's own shifts, per day.</summary>
        public List<GridGap> Gaps { get; set; } = new();
        /// <summary>How many of this role are needed on EVERY day (the role's own number). 0 = none stated.</summary>
        public int Needed { get; set; }
        /// <summary>Antalet per dag (kolumnens nyckel → antal), med dagarnas avvikelser inräknade. Bär en
        /// nyckel per daterad kolumn så fort rollen har ett behov någon dag.</summary>
        public Dictionary<string, int> NeededByDay { get; set; } = new();
        /// <summary>
        /// Omgång 3 (3.2, "tiden är sanningen"): flest som är på plats samtidigt per dag. Tio markörer i två skift om fem
        /// är fem på plats — antalet uppdrag (<see cref="Cells"/>) och antalet samtidigt är olika saker.
        /// </summary>
        public Dictionary<string, int> FilledByDay { get; set; } = new();
        /// <summary>Omgång 3: per dag, passen med flest samtidigt under passet och behovet för passet.</summary>
        public Dictionary<string, List<GridPassCover>> PassCover { get; set; } = new();
        /// <summary>Omgång 3: behovet per pass (passets id → antal) — förifyllning i "Antal per dag".</summary>
        public Dictionary<int, int> NeededByPass { get; set; } = new();
        /// <summary>Omgång 3: raden är en station (stationsroll × station). Behovet är då "per station".</summary>
        public bool IsStation => string.Equals(ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase);
        /// <summary>Omgång 3: just den här stationen har ett eget behov (annars gäller "varje station").</summary>
        public bool HasOwnStationNeed { get; set; }
        /// <summary>Behövs rollen någon dag alls?</summary>
        public bool HasNeed => Needed > 0 || NeededByDay.Values.Any(v => v > 0) || NeededByPass.Values.Any(v => v > 0);
        /// <summary>Antalet som behövs i kolumnen.</summary>
        public int NeedFor(string columnKey) =>
            NeededByDay.TryGetValue(columnKey ?? "", out var v) ? v : (string.IsNullOrEmpty(columnKey) ? 0 : Needed);
    }

    /// <summary>A member who volunteered but holds no assignment — the only people the grid cannot show,
    /// because they are not in it.</summary>
    public class GridVolunteer
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Summary { get; set; }
    }

    /// <summary>Behovet för en roll: alla dagars antal och avvikande dagar ("yyyy-MM-dd").</summary>
    public class SetRoleNeedsRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
        public int All { get; set; }
        public List<RoleNeedDay> Days { get; set; } = new();
        /// <summary>Omgång 3 (3.2): "Station" = behovet per station; annars hela tävlingen.</summary>
        public string? ScopeKind { get; set; }
        /// <summary>Omgång 3: en bestämd station; tomt = varje station.</summary>
        public string? ScopeKey { get; set; }
        /// <summary>Omgång 3: passen (bara de som avviker från sin dag lagras).</summary>
        public List<RoleNeedPass> Passes { get; set; } = new();
    }

    public class RoleNeedDay
    {
        public string Date { get; set; } = "";
        public int Count { get; set; }
    }

    public class RoleNeedPass
    {
        public int PassId { get; set; }
        public int Count { get; set; }
    }

    public class SetCrewNeedRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
        public int Count { get; set; }
    }

    public class GridResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        public bool CanEdit { get; set; }
        public string Discipline { get; set; } = "";
        public List<GridColumn> Columns { get; set; } = new();
        public List<GridRow> Rows { get; set; } = new();
        public int TotalAssigned { get; set; }
        /// <summary>Rows with no member account — they get no Mitt schema and no push.</summary>
        public int ExternalCount { get; set; }
        /// <summary>No account AND no e-mail: these people cannot be told anything at all. A list, not a
        /// count, because the organiser has to act on each name.</summary>
        public List<string> Unreachable { get; set; } = new();
        /// <summary>Volunteered, nothing assigned yet — surfaced as a strip above the grid.</summary>
        public List<GridVolunteer> Volunteers { get; set; } = new();
        /// <summary>Planera dagen: hur många som anmält sig som funktionärer alls — så att "3 av 12 har uppdrag" går att säga.</summary>
        public int VolunteersTotal { get; set; }
        /// <summary>…och hur många av dem som har ett uppdrag som inte är avböjt.</summary>
        public int VolunteersWithJob { get; set; }
        /// <summary>Planera dagen: planeringens förval för funktionärer som skjuter (null = inte valt — då ställs frågan).</summary>
        public string? ShooterMode { get; set; }
        /// <summary>Planera dagen steg 3b: planeringen hör till en fälttävling, så startlistan kan byggas runt funktionärerna.</summary>
        public bool PatrolPlanAvailable { get; set; }
        /// <summary>Planera dagen: senaste tillämpade förslag som går att ångra (orörda Planerade rader kvar). Null = inget.</summary>
        public Guid? UndoBatchId { get; set; }
        public int UndoRows { get; set; }
        /// <summary>Planera dagen: "Dagens plan" — stegen härledda ur raderna ovan (<see cref="PlanStepRules"/>). Tom i en familjs gemensamma vy; stegen hör till en del.</summary>
        public List<PlanStep> PlanSteps { get; set; } = new();
        /// <summary>Platser (behov minus bemannat, per roll och dag eller pass) — EN räkning på servern som både bandet och sidans rubrik läser.</summary>
        public int PlacesNeeded { get; set; }
        public int PlacesMissing { get; set; }
        /// <summary>Same person, two posts, overlapping time, same day.</summary>
        public List<GridClash> Clashes { get; set; } = new();
        /// <summary>Omgång 3 steg 9: krockar över hela veckan — med en annan planering, en egen start eller en avsikt att tävla.</summary>
        public List<GridClash> FamilyClashes { get; set; } = new();
        /// <summary>
        /// Omgång 3 steg 10: planeringens roller är inte valda än. Rutnätet visar då bara roller med folk, behov eller
        /// arrangörens egna, och ber om ett val ur <see cref="RoleChoices"/>.
        /// </summary>
        public bool RoleChoicePending { get; set; }
        /// <summary>Grenens inbyggda roller för "Välj roller" (bara för en planering).</summary>
        public List<GridRoleChoice> RoleChoices { get; set; } = new();
    }

    /// <summary>
    /// Steg 2: en del av familjens rutnät — en planerings egen <see cref="GridResponse"/>, oförändrad (varje dels grupp är
    /// exakt dagens rutnät för delen). Huvudplaneringen är det gemensamma.
    /// </summary>
    public class FamilyGridPart
    {
        public int PlanningId { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Huvudplaneringen: det som gäller hela veckan.</summary>
        public bool IsShared { get; set; }
        /// <summary>Vem som sköter delen ("Hallands krets"), eller null.</summary>
        public string? HostLabel { get; set; }
        /// <summary>Delens egna dagar (kolumnnycklar). Övriga dagar i familjens axel är gråa i delen.</summary>
        public List<string> DayKeys { get; set; } = new();
        public GridResponse Grid { get; set; } = new();
    }

    public class FamilyGridResponse
    {
        public bool Success { get; set; } = true;
        public string? Message { get; set; }
        /// <summary>Familjens dagaxel: unionen av delarnas dagar, i datumordning.</summary>
        public List<GridColumn> Columns { get; set; } = new();
        /// <summary>Gemensamt först, sedan delarna efter namn. Bara de delar läsaren får ändra bemanningen i.</summary>
        public List<FamilyGridPart> Parts { get; set; } = new();
        /// <summary>Delar läsaren inte får ändra — nämns, men visas inte.</summary>
        public int HiddenParts { get; set; }
        public List<GridClash> FamilyClashes { get; set; } = new();
    }

    public class GridRoleChoice
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        /// <summary>Förvalt i dialogen: framme i rutnätet (eller, före valet, med folk eller behov).</summary>
        public bool Chosen { get; set; }
        /// <summary>Har folk — kan inte döljas.</summary>
        public bool HasCrew { get; set; }
    }

    /// <summary>Omgång 3 steg 10: "Välj roller" — de inbyggda rollerna som ska vara framme.</summary>
    public class ChooseStaffRolesRequest
    {
        public int CompetitionId { get; set; }
        public List<string> Keys { get; set; } = new();
    }

    public class GridClash
    {
        public string PersonKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string? DateKey { get; set; }
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        /// <summary>Omgång 3 steg 9: null = inom planeringen; annars <see cref="FamilyClashRules"/> (uppdrag, start, avsikt).</summary>
        public string? Kind { get; set; }
    }

    /// <summary>An uncovered stretch between two shifts on the same role and day.</summary>
    public class GridGap
    {
        public string DateKey { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    public class SplitAssignmentRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        /// <summary>"HH:mm" — where to cut.</summary>
        public string? At { get; set; }
        public int MemberId { get; set; }
        public string? NewName { get; set; }
    }

}
