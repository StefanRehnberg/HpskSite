using HpskSite.Models;
using HpskSite.Models.Firearms;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Vem som får läsa en medlems vapeninnehav, och vem som får utse den personen.
    ///
    /// <para><b>Behörigheten är HÄRLEDD, inte tilldelad</b>, och det är hela konstruktionen:</para>
    /// <code>
    /// CanReadMemberFirearms(medlem) =
    ///       medlemmen är den inloggade
    ///   OR  (gruppen Foreningsintygsansvarig_{klubb}  AND  aktivt styrelseuppdrag i SAMMA klubb)
    /// </code>
    ///
    /// <para><b>Varför konjunktionen.</b> Ett rent gruppmedlemskap lever vidare efter att
    /// styrelseuppdraget upphört, och ingen märker det. <c>BoardRoles</c> mjukraderar
    /// (<c>IsActive=0</c>) när någon tas bort ur styrelsen, så behörigheten upphör i samma sekund —
    /// utan städjobb, utan påminnelse, utan att någon behöver komma ihåg något.</para>
    ///
    /// <para><b>⚠️ SAJTADMIN HAR INGEN IMPLICIT LÄSRÄTT HÄR.</b> Varje annan klubbkontroll i
    /// <c>AdminAuthorizationService</c> börjar med <c>if (IsCurrentUserAdminAsync()) return true;</c>.
    /// Den här gör medvetet inte det. Driften kan tekniskt komma åt datat via databas och nyckel —
    /// men kodvägen ska inte rendera det, och den skillnaden är hela löftet. En sajtadmin som ÄR
    /// styrelsemedlem i sin egen klubb och har rollen läser förstås som alla andra: åtkomsten kommer
    /// då från uppdraget, inte från adminskapet.</para>
    ///
    /// <para><b>⚠️ Att LÄSA och att ADMINISTRERA rollen är två olika frågor.</b> Sajtadmin får utse
    /// (det är den kvarvarande vägen när en klubb kört sig i hörnet) men aldrig läsa. Just den
    /// delningen är det som gör löftet till kod i stället för en policy.</para>
    ///
    /// <para><b>Klubbskopat, aldrig kretsskopat</b> (Stefans beslut 2026-09-02): ett föreningsintyg
    /// är en klubbangelägenhet. <c>BoardRoles</c> rymmer kretsstyrelser, men ingen kodväg här frågar
    /// om dem.</para>
    /// </summary>
    public class FirearmAuthorizationService
    {
        /// <summary>Umbraco-gruppens namnform. Samma mönster som <c>Skjutledare_{clubId}</c>.</summary>
        public const string GroupPrefix = "Foreningsintygsansvarig_";

        public static string GroupName(int clubId) => $"{GroupPrefix}{clubId}";

        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IMemberGroupService _memberGroupService;
        private readonly BoardRoleService _boardRoleService;
        private readonly AdminAuthorizationService _adminAuth;
        private readonly MemberClubService _memberClubService;
        private readonly ILogger<FirearmAuthorizationService> _logger;

        public FirearmAuthorizationService(
            IMemberManager memberManager,
            IMemberService memberService,
            IMemberGroupService memberGroupService,
            BoardRoleService boardRoleService,
            AdminAuthorizationService adminAuth,
            MemberClubService memberClubService,
            ILogger<FirearmAuthorizationService> logger)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _memberGroupService = memberGroupService;
            _boardRoleService = boardRoleService;
            _adminAuth = adminAuth;
            _memberClubService = memberClubService;
            _logger = logger;
        }

        // ── Läsning ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Håller den angivna personen behörigheten för klubben? Det HÄRLEDDA svaret: gruppen OCH ett
        /// aktivt styrelseuppdrag i samma klubb.
        ///
        /// <para><b>⚠️ Mandatets slutdatum revoquerar INTE.</b> <c>IsBoardMemberOf</c> frågar bara
        /// efter <c>IsActive=1</c>, och det är avsiktligt här: en styrelse sitter regelmässigt kvar
        /// från mandatets utgång till nästa årsmöte. Skulle ett utgånget mandat stänga behörigheten
        /// hade klubben stått utan läsare i just den luckan — alltså precis det fel den här designen
        /// finns för att undvika. Ett utgånget mandat SYNS i stället som en varning på adminytan.</para>
        /// </summary>
        public bool IsFirearmViewerForClub(int memberId, int clubId)
        {
            if (memberId <= 0 || clubId <= 0) return false;

            // Beslutet ligger i FirearmAccessRules, uttömmande enhetstestat. Här samlas bara de två
            // booleska svaren in.
            return FirearmAccessRules.ViewerHasAccess(
                holdsGroupForClub: SafeRoles(memberId).Contains(GroupName(clubId)),
                hasActiveBoardSeatInSameClub: _boardRoleService.IsBoardMemberOf(
                    DocumentOwnerType.Club, clubId, memberId));
        }

        /// <summary>Får den inloggade läsa den här medlemmens vapenuppgifter, och på vilken grund?</summary>
        public async Task<FirearmReadAccess> ResolveReadAccessAsync(int subjectMemberId)
        {
            var reader = await CurrentMemberIdAsync();
            if (reader <= 0 || subjectMemberId <= 0) return FirearmReadAccess.Denied;

            if (reader == subjectMemberId)
                return new FirearmReadAccess(true, FirearmAccessReason.Owner, reader, null);

            // Medlemmen kan tillhöra flera klubbar. Behörigheten prövas mot var och en av dem — en
            // läsare som är föreningsintygsansvarig i medlemmens ANDRA klubb har lika giltig grund.
            // ⚠️ GetAllClubIds går via IMember, inte via id: primaryClubId är en STRÄNG-egenskap och
            // ett eget uppslag här skulle riskera samma tysta 0 som redan gett walk-in-anmälningar
            // clubId=0.
            var subject = _memberService.GetById(subjectMemberId);
            foreach (var clubId in _memberClubService.GetAllClubIds(subject))
            {
                if (IsFirearmViewerForClub(reader, clubId))
                    return new FirearmReadAccess(true, FirearmAccessReason.Foreningsintyg, reader, clubId);
            }

            return new FirearmReadAccess(false, null, reader, null);
        }

        /// <summary>
        /// Får den inloggade läsa KLUBBENS egna vapen? Det är en annan fråga än medlemmarnas
        /// innehav: klubbvapen tillhör en juridisk person, inte en fysisk, så här gäller den vanliga
        /// klubbadmingrinden — inklusive sajtadmin.
        /// </summary>
        public async Task<FirearmReadAccess> ResolveClubWeaponAccessAsync(int clubId)
        {
            var reader = await CurrentMemberIdAsync();
            if (reader <= 0 || clubId <= 0) return FirearmReadAccess.Denied;

            if (await _adminAuth.IsClubAdminForClub(clubId))
                return new FirearmReadAccess(true, FirearmAccessReason.ClubWeapon, reader, clubId);

            return new FirearmReadAccess(false, null, reader, clubId);
        }

        // ── Administration av rollen ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Får den inloggade utse och ta bort klubbens föreningsintygsansvariga?
        ///
        /// <para><b>⚠️ `IsClubAdminForClub` viker in klubbens KRETSADMINISTRATÖRER</b> — det står i
        /// metodens egen dokumentation. Utan den andra halvan av konjunktionen kunde alltså en
        /// kretsadmin utan styrelseuppdrag i klubben utse den som får läsa klubbens medlemmars
        /// vapeninnehav. Kravet på ett aktivt styrelseuppdrag i SAMMA klubb stänger det.</para>
        ///
        /// <para>Sajtadmin får utse utan styrelseuppdrag: det är den kvarvarande vägen när en klubb
        /// har blivit utan läsare och ingen kvarvarande styrelsemedlem också är klubbadmin. Att utse
        /// är inte att läsa.</para>
        /// </summary>
        public async Task<bool> CanAssignViewersAsync(int clubId)
        {
            if (clubId <= 0) return false;

            var isSiteAdmin = await _adminAuth.IsCurrentUserAdminAsync();

            var actor = await CurrentMemberIdAsync();
            if (actor <= 0) return isSiteAdmin;

            return FirearmAccessRules.CanAssign(
                isSiteAdmin: isSiteAdmin,
                // ⚠️ Viker in klubbens kretsadministratörer — därför konjunktionen nedan.
                isClubAdmin: await _adminAuth.IsClubAdminForClub(clubId),
                hasActiveBoardSeatInSameClub: _boardRoleService.IsBoardMemberOf(
                    DocumentOwnerType.Club, clubId, actor));
        }

        /// <summary>
        /// Klubbens aktiva styrelsemedlemmar, med markering för vilka som håller behörigheten.
        /// Underlaget till adminytans lista OCH till dess "utse"-väljare — en enda källa, så listan
        /// och väljaren inte kan vara oense om vem som är valbar.
        /// </summary>
        public List<FirearmViewerCandidate> GetBoardCandidates(int clubId)
        {
            if (clubId <= 0) return new List<FirearmViewerCandidate>();

            var board = _boardRoleService.GetBoardMembers(DocumentOwnerType.Club, clubId, boardOnly: true);
            var group = GroupName(clubId);

            // En person kan bära flera styrelseroller. Personen, inte raden, är enheten här.
            return board
                .GroupBy(r => r.MemberId)
                .Select(g =>
                {
                    var first = g.OrderBy(r => r.SortOrder).First();
                    return new FirearmViewerCandidate
                    {
                        MemberId = g.Key,
                        Name = first.MemberName ?? $"Medlem {g.Key}",
                        RoleTitles = g.OrderBy(r => r.SortOrder).Select(r => r.DisplayTitle).Distinct().ToList(),
                        IsViewer = SafeRoles(g.Key).Contains(group),
                        TermEndsDate = g.Select(r => r.TermEndsDate).Where(d => d.HasValue)
                                        .OrderBy(d => d).FirstOrDefault(),
                    };
                })
                .OrderBy(c => c.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true))
                .ToList();
        }

        /// <summary>
        /// Alla som bär gruppen för klubben, inklusive de som <b>inte längre sitter i styrelsen</b>.
        ///
        /// <para><b>⚠️ De vilande måste med i listan.</b> Gruppmedlemskapet tas inte bort när någon
        /// lämnar styrelsen — behörigheten är härledd och därmed redan verkningslös. Men en person
        /// som återväljs får då tillbaka läsrätten automatiskt, och det får inte hända osett. Därför
        /// listas de med <c>IsDormant</c>, och adminytan kan erbjuda att ta bort gruppen på riktigt.</para>
        /// </summary>
        public List<FirearmViewerCandidate> GetViewers(int clubId)
        {
            if (clubId <= 0) return new List<FirearmViewerCandidate>();

            var group = GroupName(clubId);
            var board = GetBoardCandidates(clubId).ToDictionary(c => c.MemberId);
            var holders = MembersInGroup(group);
            var result = new List<FirearmViewerCandidate>();

            foreach (var memberId in holders)
            {
                if (board.TryGetValue(memberId, out var onBoard))
                {
                    result.Add(onBoard);
                    continue;
                }

                var member = _memberService.GetById(memberId);
                var name = member is null
                    ? $"Medlem {memberId}"
                    : $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}".Trim();

                result.Add(new FirearmViewerCandidate
                {
                    MemberId = memberId,
                    Name = string.IsNullOrWhiteSpace(name) ? $"Medlem {memberId}" : name,
                    IsViewer = true,
                    IsDormant = true,
                });
            }

            return result;
        }

        /// <summary>
        /// Utser en styrelsemedlem. Skapar Umbraco-gruppen vid behov.
        ///
        /// <para><b>⚠️ Vägrar en person som inte sitter i klubbens styrelse.</b> Grinden härleder
        /// ändå bort en sådan tilldelning, så den skulle vara verkningslös — men en knapp som
        /// rapporterar "tilldelad" om något som inte fungerar är värre än ett nej. Meddelandet
        /// namnger kravet.</para>
        /// </summary>
        public async Task<(bool Ok, string Message)> AssignViewerAsync(int clubId, int memberId)
        {
            if (clubId <= 0 || memberId <= 0) return (false, "Ogiltig klubb eller medlem.");

            if (!_boardRoleService.IsBoardMemberOf(DocumentOwnerType.Club, clubId, memberId))
                return (false, "Behörigheten kan bara ges till någon som sitter i klubbens styrelse. " +
                               "Lägg till personen i styrelsen först.");

            var member = _memberService.GetById(memberId);
            if (member is null) return (false, "Medlemmen hittades inte.");

            var group = GroupName(clubId);
            if (!await EnsureGroupAsync(group))
                return (false, $"Kunde inte skapa medlemsgruppen {group}.");

            if (SafeRoles(memberId).Contains(group))
                return (true, "Personen har redan behörigheten.");

            _memberService.AssignRole(memberId, group);
            _logger.LogInformation(
                "Firearm viewer granted: member {MemberId} -> {Group}", memberId, group);

            return (true, "Behörigheten är tilldelad.");
        }

        /// <summary>
        /// Tar bort behörigheten — gruppmedlemskapet, på riktigt. Används både för att återkalla och
        /// för att städa bort en vilande behörighet efter en avgång.
        /// </summary>
        public (bool Ok, string Message) RemoveViewer(int clubId, int memberId)
        {
            if (clubId <= 0 || memberId <= 0) return (false, "Ogiltig klubb eller medlem.");

            var group = GroupName(clubId);
            if (!SafeRoles(memberId).Contains(group))
                return (true, "Personen hade inte behörigheten.");

            _memberService.DissociateRole(memberId, group);
            _logger.LogInformation(
                "Firearm viewer revoked: member {MemberId} -> {Group}", memberId, group);

            return (true, "Behörigheten är borttagen.");
        }

        /// <summary>
        /// Håller den här personen behörigheten i någon klubb där hen fortfarande sitter i styrelsen?
        /// Läses av Styrelsen-fliken innan någon tas bort, så avgången kan varna på plats.
        /// </summary>
        public bool IsActiveViewerAnywhere(int memberId, out List<int> clubIds)
        {
            clubIds = new List<int>();
            if (memberId <= 0) return false;

            var group = GroupPrefix;
            foreach (var role in SafeRoles(memberId).Where(r => r.StartsWith(group, StringComparison.Ordinal)))
            {
                if (!int.TryParse(role[group.Length..], out var clubId) || clubId <= 0) continue;
                if (_boardRoleService.IsBoardMemberOf(DocumentOwnerType.Club, clubId, memberId))
                    clubIds.Add(clubId);
            }

            return clubIds.Count > 0;
        }

        /// <summary>
        /// Antalet personer som FAKTISKT kan läsa klubbens medlemmars vapeninnehav — alltså gruppen
        /// och ett aktivt styrelseuppdrag. Noll betyder att klubben inte kan utfärda ett
        /// föreningsintyg med vapenuppgifter, och det ska synas som en varning.
        /// </summary>
        public int CountActiveViewers(int clubId)
            => GetViewers(clubId).Count(v => !v.IsDormant);

        // ── Internt ──────────────────────────────────────────────────────────────────────────────

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }

        /// <summary>
        /// <c>GetAllRoles</c> kastar för ett id som inte är en medlem. En behörighetskontroll som
        /// kastar blir ett 500 i stället för ett nej — och ett 500 läses som ett produktfel.
        /// </summary>
        private HashSet<string> SafeRoles(int memberId)
        {
            try
            {
                return (_memberService.GetAllRoles(memberId) ?? Enumerable.Empty<string>())
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa roller för medlem {MemberId}.", memberId);
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private List<int> MembersInGroup(string groupName)
        {
            try
            {
                return (_memberService.GetMembersByGroup(groupName) ?? Enumerable.Empty<IMember>())
                    .Select(m => m.Id).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa medlemmar i gruppen {Group}.", groupName);
                return new List<int>();
            }
        }

        private async Task<bool> EnsureGroupAsync(string groupName)
        {
            try
            {
                var existing = await _memberGroupService.GetByNameAsync(groupName);
                if (existing is not null) return true;

                await _memberGroupService.CreateAsync(new MemberGroup { Name = groupName });
                _logger.LogInformation("Created member group {Group}", groupName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte säkerställa medlemsgruppen {Group}.", groupName);
                return false;
            }
        }
    }

    /// <summary>
    /// Utfallet av en behörighetsprövning: fick läsaren läsa, och på vilken GRUND. Grunden går vidare
    /// rakt in i <c>FirearmAccessLog</c>, så behörighetsbeslutet och loggraden inte kan säga emot
    /// varandra.
    /// </summary>
    public readonly record struct FirearmReadAccess(bool Allowed, string? Reason, int ReaderMemberId, int? ClubId)
    {
        public static readonly FirearmReadAccess Denied = new(false, null, 0, null);

        /// <summary>True när läsaren är någon annan än ägaren — den läsning medlemmen ska se i loggen.</summary>
        public bool IsForeignRead => Allowed && Reason != FirearmAccessReason.Owner;
    }

    public class FirearmViewerCandidate
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> RoleTitles { get; set; } = new();

        /// <summary>Håller gruppen för klubben.</summary>
        public bool IsViewer { get; set; }

        /// <summary>Håller gruppen men sitter inte längre i styrelsen — behörigheten är verkningslös.</summary>
        public bool IsDormant { get; set; }

        public DateTime? TermEndsDate { get; set; }

        /// <summary>
        /// Mandatet har gått ut. Revoquerar INGENTING (en styrelse sitter kvar till nästa årsmöte) —
        /// men det ska synas, så klubben kan se att uppdraget behöver förnyas.
        /// </summary>
        public bool TermExpired => TermEndsDate.HasValue && TermEndsDate.Value.Date < DateTime.Today;
    }
}
