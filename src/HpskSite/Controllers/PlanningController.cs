using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models;
using HpskSite.Models.Planning;
using HpskSite.Services;
using HpskSite.Services.Planning;
using HpskSite.Services.Ops;
using HpskSite.Services.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Tävlingsplaneringens API (P1, 2026-10-21): planeringen, deltagarna, meddelandena och mötena.
    ///
    /// <para><b>Två sätt att vara inloggad.</b> Antingen sessionen (medlemmen), eller parametern
    /// <c>t</c> — den signerade länken ur ett mejl (<see cref="PlanningLinkService"/>). Länken gör det
    /// möjligt att svara och rösta utan att logga in, och är gästens ENDA väg in.</para>
    ///
    /// <para><b>⚠️ Den som inte får läsa planeringen får svaret "finns inte"</b> — aldrig "nekad".
    /// Ett nej hade avslöjat att planeringen finns.</para>
    ///
    /// <para><b>⚠️ Notiserna skickas EFTER skrivningen</b>, när dess scope är stängt.</para>
    /// </summary>
    public partial class PlanningController : SurfaceController
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        private readonly PlanningService _planning;
        private readonly PlanningNotifier _notify;
        private readonly PlanningLinkService _links;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;
        private readonly BoardWorkAccess _board;
        private readonly MemberDirectoryService _directory;
        private readonly ClubService _clubs;
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Notifications.WebPushService _pushInfo;
        private readonly ILogger<PlanningController> _logger;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.CompetitionLinks _competitionLinks;
        private readonly HpskSite.Services.Kretsgranskning.KretsPlanningService _kretsPlanning;
        private readonly HpskSite.Services.Staffing.PrepDocumentStorage _docs;
        private readonly Microsoft.Extensions.Options.IOptions<HpskSite.Models.Configuration.DocumentArchiveOptions> _archiveOptions;
        private readonly HpskSite.Services.Staffing.StaffingService _staffing;
        private readonly HpskSite.Services.Staffing.StaffDayService _staffDays;
        private readonly HpskSite.Services.SiteSettings.SiteSettingService _siteSettings;
        private readonly BoardWorkService _boardWork;
        private readonly BoardWorkNotifier _boardNotify;

        public PlanningController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            PlanningService planning, PlanningNotifier notify, PlanningLinkService links,
            IMemberManager memberManager, IMemberService memberService,
            MemberClubService memberClubs, BoardWorkAccess board, MemberDirectoryService directory, ClubService clubs,
            AdminAuthorizationService auth, HpskSite.Services.Notifications.WebPushService pushInfo, ILogger<PlanningController> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions, HpskSite.Services.Competitions.CompetitionLinks competitionLinks,
            HpskSite.Services.Kretsgranskning.KretsPlanningService kretsPlanning, HpskSite.Services.Staffing.PrepDocumentStorage docs,
            Microsoft.Extensions.Options.IOptions<HpskSite.Models.Configuration.DocumentArchiveOptions> archiveOptions,
            HpskSite.Services.Staffing.StaffingService staffing, HpskSite.Services.Staffing.StaffDayService staffDays,
            HpskSite.Services.SiteSettings.SiteSettingService siteSettings,
            BoardWorkService boardWork, BoardWorkNotifier boardNotify)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _boardWork = boardWork;
            _boardNotify = boardNotify;
            _staffing = staffing;
            _staffDays = staffDays;
            _siteSettings = siteSettings;
            _competitions = competitions;
            _competitionLinks = competitionLinks;
            _kretsPlanning = kretsPlanning;
            _docs = docs;
            _archiveOptions = archiveOptions;
            _planning = planning;
            _notify = notify;
            _links = links;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
            _board = board;
            _directory = directory;
            _clubs = clubs;
            _auth = auth;
            _pushInfo = pushInfo;
            _logger = logger;
        }

        // =====================================================================================
        //  Vem frågar?
        // =====================================================================================

        private int? _memberId;
        private async Task<int> MemberIdAsync()
        {
            if (_memberId.HasValue) return _memberId.Value;
            var cur = await _memberManager.GetCurrentMemberAsync();
            _memberId = cur?.Email == null ? 0 : (_memberService.GetByEmail(cur.Email)?.Id ?? 0);
            return _memberId.Value;
        }

        private sealed record Ctx(PlanningRow Planning, PlanningCaller Caller, int MemberId);

        /// <summary>
        /// Planeringen och den som frågar. Null = planeringen finns inte ELLER får inte läsas — de två
        /// går medvetet inte att skilja åt utåt.
        /// </summary>
        /// <param name="allowParentReader">
        /// Omgång 2 steg 5: släpp in huvudplaneringens sammankallande som LÄSARE av en delplanering. ⚠️ Bara de läsande
        /// anropen sätter den — ett skrivande anrop som fick en läsare skulle annars bara ha sina Can-regler kvar som skydd.
        /// </param>
        private async Task<Ctx?> ResolveAsync(int planningId, string? t, bool allowParentReader = false)
        {
            var p = _planning.Get(planningId);
            if (p == null) return null;

            if (!string.IsNullOrWhiteSpace(t))
            {
                var target = _links.ParseMail(t);
                if (target == null || target.PlanningId != planningId) return null;
                var row = _planning.Participant(planningId, target.ParticipantId);
                if (row == null || !row.IsActive) return null;
                // ⚠️ En rad som kom via tävlingen prövas om vid nästa inloggade besök (då tas den bort om
                // behörigheten är borta). Mejllänken kan inte pröva tävlingens behörighet åt någon annan än
                // den inloggade, och den gäller högst 60 dagar.
                return new Ctx(p, new PlanningCaller(row, true), row.MemberId ?? 0);
            }

            var me = await MemberIdAsync();
            if (me <= 0) return null;
            var participant = _planning.ParticipantForMember(planningId, me);
            participant = await ApplyCompetitionAccessAsync(planningId, me, participant);
            var c = new PlanningCaller(participant is { IsActive: true } ? participant : null, false);
            if (!c.IsParticipant && allowParentReader && _planning.IsParentConvener(p, me))
                c = new PlanningCaller(null, false, ParentReader: true);
            if (!PlanningAccess.CanReadContent(c)) return null;
            return new Ctx(p, c, me);
        }

        /// <summary>
        /// Tillgång via kopplingen (Stefan 2026-10-23): den som har tillgång till den kopplade tävlingen
        /// (tävlingsledning, klubbadmin, skjutledare, kretsadmin, sajtadmin) är Kan ändra utan inbjudan. En rad
        /// som bara fanns genom tävlingen tas bort när behörigheten är borta.
        /// </summary>
        private async Task<PlanningMemberRow?> ApplyCompetitionAccessAsync(int planningId, int memberId, PlanningMemberRow? participant)
        {
            if (participant is { IsActive: true } && !participant.ViaCompetition) return participant;
            var link = _planning.LinkFor(planningId);
            var hasAccess = link != null && await _auth.HasCompetitionStaffAccessAsync(link.TargetId);
            if (participant is { IsActive: true })
            {
                if (hasAccess) return participant;
                _planning.DropCompetitionParticipant(planningId, participant.Id);
                return null;
            }
            if (!hasAccess) return participant;
            var m = _memberService.GetById(memberId);
            return _planning.EnsureCompetitionParticipant(planningId, memberId, MemberDisplayName(m)) ?? participant;
        }

        private IActionResult Missing() => Json(new { success = false, missing = true, message = "Planeringen finns inte, eller så är du inte med i den." });
        private IActionResult No(string message) => Json(new { success = false, message });
        private IActionResult Ok(object? extra = null) => Json(extra ?? new { success = true });

        private static string DT(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm", Inv) : "";
        private static string D(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd", Inv) : "";
        private static DateTime? ParseDate(string? s) =>
            DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null;
        /// <summary>Omgång 3 (K8): ett ifyllt datum som inte går att läsa vägras — förut blev det tyst inget datum.</summary>
        private static bool UnreadableDate(string? s) => !string.IsNullOrWhiteSpace(s) && ParseDate(s) == null;
        private const string UnreadableDateMessage = "Ett datum går inte att läsa. Skriv till exempel 2027-05-16.";
        private static DateTime? ParseDateTime(string? s)
        {
            var v = (s ?? "").Trim().Replace('T', ' ');
            return DateTime.TryParseExact(v, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }, Inv, DateTimeStyles.None, out var d) ? d : null;
        }
        private static List<int> Ids(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x.Trim(), out var i) ? i : 0).Where(i => i > 0).Distinct().ToList();

        private string OrgName(int? type, int? id)
        {
            if (!(id > 0)) return "";
            try
            {
                if (type == DocumentOwnerType.Club) return _clubs.GetClubNameById(id!.Value) ?? "";
                if (UmbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content?.GetById(id!.Value) is { } n)
                    return RegionName(n);
            }
            catch { /* namnet är bara text på skärmen */ }
            return "";
        }

        /// <summary>Kretsens visningsnamn: nodnamnen saknar å/ä/ö ("Jonkoping"), egenskapen regionName har dem.</summary>
        private static string RegionName(IPublishedContent r) =>
            r.Value<string>("regionName") is { Length: > 0 } n ? n : r.Name ?? "";

        /// <summary>Medlemmens klubbar, för valet av vad hen företräder.</summary>
        private List<object> ClubChoices(int memberId)
        {
            try
            {
                var m = _memberService.GetById(memberId);
                return m == null ? new List<object>() : _memberClubs.GetAllClubIds(m)
                    .Select(id => (object)new { id, name = OrgName(DocumentOwnerType.Club, id) }).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering: medlemmens klubbar kunde inte läsas."); return new List<object>(); }
        }

        private string Represents(PlanningMemberRow r) =>
            r.RepresentsClubId > 0 ? OrgName(DocumentOwnerType.Club, r.RepresentsClubId)
            : r.RepresentsRegionId > 0 ? OrgName(DocumentOwnerType.Region, r.RepresentsRegionId)
            : r.RepresentsText ?? "";

        // =====================================================================================
        //  Mina planeringar och en ny planering
        // =====================================================================================

        /// <summary>
        /// Listsidan (Stefan 2026-10-23): mina planeringar, de som den sammankallande valt att visa för alla
        /// medlemmar (så att man kan be om att få vara med), och — för sajtadmin, som SUPPORT — de som har
        /// förfrågningar som väntar på godkännande. Andras planeringar visar bara namn, tid och sammankallande.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> MyPlannings()
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            var mine = _planning.ForMember(me);
            var mineIds = mine.Select(m => m.PlanningId).ToHashSet();
            var listed = _planning.Listed().Where(p => !mineIds.Contains(p.Id)).ToList();
            var isAdmin = await _auth.IsCurrentUserAdminAsync();
            var support = new List<PlanningRow>();
            Dictionary<int, int> pendingAll = new();
            if (isAdmin)
            {
                pendingAll = _planning.AllPendingCounts();
                support = pendingAll.Keys.Where(k => !mineIds.Contains(k)).Select(k => _planning.Get(k)).Where(p => p != null).Select(p => p!).ToList();
            }
            var ownerIds = mine.Where(m => m.Access == PlanningAccessLevel.Owner).Select(m => m.PlanningId).ToList();
            var pending = _planning.PendingCounts(ownerIds);
            var conveners = _planning.ConvenerNames(mineIds.Concat(listed.Select(p => p.Id)).Concat(support.Select(p => p.Id)));
            var myRequests = _planning.OpenRequestsByMember(me);
            // Stefan 2026-10-25: delplaneringarna under sin huvudplanering — varje rad säger vilken huvudplanering den hör
            // till, huvudplaneringar man inte själv är med i följer med som rubriker, och den som leder huvudplaneringen ser
            // delplaneringarna (läsa) även när hen inte är med i dem.
            var (parentOf, parents, readableSubs) = _planning.HubFamilies(me, mineIds);
            var subConveners = _planning.ConvenerNames(readableSubs.Select(p => p.Id));
            string Conv(int pid) => conveners.TryGetValue(pid, out var l) ? string.Join(", ", l) : subConveners.TryGetValue(pid, out var l2) ? string.Join(", ", l2) : "";
            return Json(new
            {
                success = true,
                isSiteAdmin = isAdmin,
                inviteCap = PlanningInviteRules.MaxPerDay,
                plannings = mine.Select(m => (object)new
                {
                    id = m.PlanningId, participantId = m.ParticipantId, name = m.Name, status = m.Status,
                    targetText = m.TargetText, targetStart = D(m.TargetStart), access = m.Access,
                    accessLabel = PlanningAccessLevel.Label(m.Access), unread = m.Unread, votesOwed = m.VotesOwed,
                    nextMeetingAt = DT(m.NextMeetingAt), nextMeetingTitle = m.NextMeetingTitle,
                    conveners = Conv(m.PlanningId), pendingRequests = pending.GetValueOrDefault(m.PlanningId),
                    parentId = parentOf.TryGetValue(m.PlanningId, out var par) ? par : (int?)null, readOnly = false,
                }).Concat(readableSubs.Select(p => (object)new
                {
                    id = p.Id, participantId = 0, name = p.Name, status = p.Status, targetText = p.TargetText, targetStart = (string?)null,
                    access = "", accessLabel = ParentReaderLabel, unread = 0, votesOwed = 0, nextMeetingAt = (string?)null,
                    nextMeetingTitle = (string?)null, conveners = Conv(p.Id), pendingRequests = 0,
                    parentId = p.ParentPlanningId, readOnly = true,
                })),
                // Huvudplaneringar man bara är med i via en delplanering: en rubrik, inte en länk (man får inte läsa den).
                parents = parents.Select(p => new { id = p.Id, name = p.Name, status = p.Status, targetText = p.TargetText }),
                listed = listed.Select(p => new
                {
                    id = p.Id, name = p.Name, targetText = p.TargetText, targetStart = D(p.TargetStart),
                    conveners = Conv(p.Id), requested = myRequests.ContainsKey(p.Id),
                }),
                support = support.Select(p => new
                {
                    id = p.Id, name = p.Name, targetText = p.TargetText, conveners = Conv(p.Id), pendingRequests = pendingAll.GetValueOrDefault(p.Id),
                }),
            });
        }

        /// <summary>Alla kretsar (för <i>Företräder</i>), sorterade på svenska.</summary>
        private List<(int Id, string Name, string? Area)>? _regions;
        private List<(int Id, string Name, string? Area)> AllRegions()
        {
            if (_regions != null) return _regions;
            var list = new List<(int Id, string Name, string? Area)>();
            if (!UmbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return list;
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return list;
            foreach (var r in root.Children.Where(c => c.ContentType.Alias == "regionalPage"))
                list.Add((r.Id, RegionName(r), r.Value<string>("area")));
            return _regions = list.OrderBy(r => r.Name, StringComparer.Create(Sv, false)).ToList();
        }

        /// <summary>Det deltagaren företräder: en klubb, en krets eller egen text ("Förbundet", "Markägaren").</summary>
        private string? RepresentsError(int? clubId, int? regionId)
        {
            if (regionId > 0 && AllRegions().All(r => r.Id != regionId)) return "Okänd krets.";
            if (clubId > 0 && string.IsNullOrEmpty(OrgName(DocumentOwnerType.Club, clubId))) return "Okänd klubb.";
            return null;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string name, string? targetText, string? targetStart, string? targetEnd, string? discipline = null)
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            if (UnreadableDate(targetStart) || UnreadableDate(targetEnd)) return No(UnreadableDateMessage);
            var start = ParseDate(targetStart);
            var end = ParseDate(targetEnd);
            var err = PlanningService.Validate(name, start, end);
            if (err != null) return No(err);
            // Omgång 2 steg 4: grenen väljs när planeringen skapas (valfri). Den styr vilka roller bemanningen föreslår.
            var disciplineKey = string.IsNullOrWhiteSpace(discipline) ? null : HpskSite.Models.CompetitionTypes.GetById(discipline.Trim())?.Id;
            if (!string.IsNullOrWhiteSpace(discipline) && disciplineKey == null) return No("Grenen finns inte.");
            // Omgång 3 (Expertgruppen K1): inbjudningstaket gäller per planering — utan ett tak på planeringarna räckte det
            // att skapa en till.
            if (_planning.CreatedLastDay(me) >= PlanningContactRules.MaxNewPlanningsPerDay && !await _auth.IsCurrentUserAdminAsync())
                return No($"Du har skapat {PlanningContactRules.MaxNewPlanningsPerDay} planeringar det senaste dygnet. Vänta till i morgon, eller fortsätt i en av dem.");

            var member = _memberService.GetById(me);
            var display = MemberDisplayName(member);
            var primary = _memberClubs.GetPrimaryClubId(member);
            var (pid, _) = _planning.Create(new PlanningService.CreateInput(name, PlanningKind.Group, targetText,
                start, end, me, display, primary > 0 ? primary : null, null));
            if (disciplineKey != null) _planning.SetDisciplineKey(pid, disciplineKey, me);
            return Ok(new { success = true, id = pid, url = $"/tavlingsplanering/{pid}" });
        }

        private static string MemberDisplayName(Umbraco.Cms.Core.Models.IMember? m)
        {
            if (m == null) return "Medlem";
            var f = m.GetValue<string>("firstName") ?? "";
            var l = m.GetValue<string>("lastName") ?? "";
            var n = $"{f} {l}".Trim();
            return n.Length > 0 ? n : (m.Name ?? "Medlem");
        }

        // =====================================================================================
        //  Planeringen
        // =====================================================================================

        [HttpGet]
        public async Task<IActionResult> Get(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            var c = x.Caller;
            if (c.IsParticipant) _planning.TouchSeen(c.ParticipantId);
            var people = _planning.Participants(id);
            var meCanManage = PlanningAccess.CanManageParticipants(c);
            var roles = _planning.Roles(id).ToLookup(r => r.ParticipantId);
            // "Under tävlingen": uppdragen i planeringens bemanning (P2b). Ett fel får inte fälla sidan.
            Dictionary<int, List<string>> duties;
            try { duties = _staffing.DutiesByMember(new HpskSite.Models.Staffing.StaffOwner(id, _planning.LinkFor(id)?.TargetId)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Bemanningens uppdrag kunde inte läsas för planering {Id}", id); duties = new(); }
            var tasks = _planning.Tasks(id);
            return Json(new
            {
                success = true,
                planning = new
                {
                    id = x.Planning.Id, name = x.Planning.Name, kind = x.Planning.Kind, kindLabel = PlanningKind.Label(x.Planning.Kind),
                    targetText = x.Planning.TargetText, targetStart = D(x.Planning.TargetStart), targetEnd = D(x.Planning.TargetEnd),
                    status = x.Planning.Status, listed = x.Planning.ListedForMembers, groupName = x.Planning.GroupName ?? "",
                    createdAt = D(x.Planning.CreatedAt),
                    doneAtMeetingsOnly = x.Planning.DoneAtMeetingsOnly,
                    competition = CompetitionJson(x.Planning.Id),
                    // P2b: grenen styr bemanningens roller tills planeringen kopplas (då gäller tävlingens).
                    disciplineKey = x.Planning.DisciplineKey ?? "",
                    disciplines = HpskSite.Models.CompetitionTypes.All.Select(d => new { id = d.Id, name = d.Name }),
                    staffingUrl = $"/tavlingsplanering/{x.Planning.Id}#vy=bemanning",
                    // Dagsprogrammet hör till tävlingen — en del i planeringen när den är kopplad.
                    agendaUrl = _planning.LinkFor(x.Planning.Id) != null ? $"/tavlingsplanering/{x.Planning.Id}/dagsprogram" : "",
                    // P3 steg F: ansökan till kretsen ur planeringen (ett utkast i klubbens ansökningar).
                    application = await ApplicationJsonAsync(x.Planning.Id, c),
                    // Omgång 2 steg 5: huvudplaneringen (för en delplanering) och delplaneringarnas läge (för huvudplaneringen).
                    parent = ParentJson(x.Planning, x.MemberId),
                    subPlannings = SubPlanningsJson(x.Planning, c, x.MemberId),
                    // Omgång 3: familjens tävlingar och "Bemannar för".
                    family = FamilyJson(x.Planning),
                    // Stefan 2026-10-25: stationernas ENDA plats i familjen (rälsen visar Stationerna bara där).
                    stationsOwner = StationsOwnerJson(x.Planning),
                    // Omgång 3 steg 10 (progressivt avslöjande): delar som visas först när de har innehåll eller slagits på.
                    parts = PartsJson(x.Planning),
                },
                me = new
                {
                    participantId = c.ParticipantId, isParticipant = c.IsParticipant,
                    viaLink = c.ViaLink, isGuest = c.Participant?.IsGuest ?? false, name = c.Participant?.DisplayName,
                    access = c.Access, accessLabel = c.ParentReader && !c.IsParticipant ? ParentReaderLabel : PlanningAccessLevel.Label(c.Access),
                    notifyMode = c.Participant?.NotifyMode, notifyLabel = PlanningNotifyMode.Label(c.Participant?.NotifyMode),
                    canEdit = PlanningAccess.CanEditPlanning(c), canManage = PlanningAccess.CanManagePlanning(c),
                    canInvite = PlanningAccess.CanInvite(c), canReadMessages = PlanningAccess.CanReadMessages(c),
                    canUseMeetings = PlanningAccess.CanUseMeetings(c), canWrite = PlanningAccess.CanWriteMessages(c),
                    // Förbundets adress (en sajtinställning) fylls i av sajtadmin under Inställningar.
                    isSiteAdmin = !c.ViaLink && await _auth.IsCurrentUserAdminAsync(),
                    pendingRequests = PlanningAccess.CanManagePlanning(c) ? _planning.PendingCounts(new[] { id }).GetValueOrDefault(id) : 0,
                    icsUrl = PlanningAccess.CanUseMeetings(c) ? _links.IcsUrl(c.ParticipantId, id, Request.PublicBase()) : null,
                    pushConfigured = _pushInfo.IsConfigured,
                    canManageRoles = PlanningAccess.CanManageRoles(c),
                    canCreateTasks = PlanningAccess.CanCreateTasks(c),
                    canManageAreas = PlanningAccess.CanManageAreas(c),
                    canAddDocument = PlanningAccess.CanAddDocument(c),
                    canLinkCompetition = PlanningAccess.CanLinkCompetition(c),
                    viaCompetition = c.Participant?.ViaCompetition ?? false,
                    // Omgång 2 steg 5: läser delplaneringen som sammankallande i huvudplaneringen (inte med, inga notiser).
                    isParentReader = c.ParentReader && !c.IsParticipant,
                    canSplit = PlanningAccess.CanSplitArea(c, x.Planning),
                    openTasks = c.IsParticipant ? tasks.Count(tk => tk.AssigneeParticipantId == c.ParticipantId && PlanningTaskStatus.IsOpen(tk.Status)) : 0,
                },
                participants = people.Select(p => new
                {
                    id = p.Id, name = p.DisplayName, access = p.Access, accessLabel = PlanningAccessLevel.Label(p.Access),
                    isGuest = p.IsGuest, represents = Represents(p), representsClubId = p.RepresentsClubId,
                    representsRegionId = p.RepresentsRegionId, representsText = p.RepresentsText,
                    lastSeen = DT(p.LastSeenAt), inviteSent = DT(p.InviteSentAt),
                    // E-post visas bara för den som hanterar deltagarna, och bara gästens (medlemmens finns i registret).
                    guestEmail = meCanManage && p.IsGuest ? p.GuestEmail : null,
                    isMe = p.Id == c.ParticipantId,
                    // Klubbarna personen kan företräda: bara för den som får ändra raden.
                    clubChoices = (meCanManage || p.Id == c.ParticipantId) && p.MemberId > 0 ? ClubChoices(p.MemberId.Value) : null,
                    roles = roles[p.Id].Select(r => r.Name),
                    duties = p.MemberId > 0 && duties.TryGetValue(p.MemberId.Value, out var du) ? du : new List<string>(),
                    viaCompetition = p.ViaCompetition,
                    openTasks = tasks.Count(tk => tk.AssigneeParticipantId == p.Id && PlanningTaskStatus.IsOpen(tk.Status)),
                }),
                // Omgång 2 steg 4: områdena (ersätter delarna) — för meddelandena, Inställningar och dokumentkategorierna.
                areas = AreasJson(id, _planning.Participants(id, includeRemoved: true).ToDictionary(pp => pp.Id, pp => pp.DisplayName), tasks),
                // Förslagen när en roll ges: bemanningens katalog + planeringens vanliga ansvar. Egen text går alltid.
                roleSuggestions = PlanningTaskRules.RoleSuggestions().Select(r => r.Name),
                // Nivåerna med förklaringar — texten finns bara i PlanningAccessLevel.Help.
                accessLevels = PlanningAccessLevel.All.Select(a => new
                {
                    value = a, label = PlanningAccessLevel.Label(a), help = PlanningAccessLevel.Help(a), canGrant = PlanningAccess.CanGrant(c, a),
                }),
                regions = PlanningAccess.CanReadContent(c) && !c.ViaLink ? AllRegions().Select(r => new { id = r.Id, name = r.Name }) : null,
                inviteCap = PlanningInviteRules.MaxPerDay,
                notifyModes = PlanningNotifyMode.Values.Select(v => new { value = v, label = PlanningNotifyMode.Label(v), help = PlanningNotifyMode.Help(v) }),
                version = _planning.Version(id),
            });
        }

        /// <summary>Pollningen: en PK-läsning. Svarar 200 även när svaret är nej, så att felräknaren inte fylls.</summary>
        [HttpGet]
        public async Task<IActionResult> Version(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Json(new { ok = false });
            return Json(new { ok = true, version = _planning.Version(id) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update(int id, string name, string? targetText, string? targetStart, string? targetEnd, string? listed, string? groupName = null, string? groupNameSet = null)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Du kan inte ändra planeringen.");
            if (UnreadableDate(targetStart) || UnreadableDate(targetEnd)) return No(UnreadableDateMessage);
            bool? list = listed == null ? null : listed == "1";
            if (list.HasValue && list.Value != x.Planning.ListedForMembers && !PlanningAccess.CanManagePlanning(x.Caller))
                return No("Bara den sammankallande väljer om planeringen visas för alla medlemmar.");
            var err = _planning.Update(id, name, targetText, ParseDate(targetStart), ParseDate(targetEnd), x.MemberId, list,
                // P4: ett tomt textfält binds som null — flaggan säger att fältet skickades (tomt = ta bort namnet).
                groupNameSet == "1" ? (groupName ?? "") : null);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStatus(int id, string status)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Bara den sammankallande arkiverar planeringen.");
            _planning.SetStatus(id, status, x.MemberId);
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Bara den sammankallande tar bort planeringen.");
            var r = _planning.Delete(id, x.MemberId);
            if (r.Error != null) return No(r.Error);
            // Tävlingarnas tävlingsansvariga och stationschefer speglas om — de som hade behörighet genom planeringen tappar den.
            foreach (var c in r.ReleasedCompetitionIds)
            {
                try { _staffing.SyncCompetitionMirrors(c); }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: speglingen till tävling {Comp} efter borttagningen misslyckades.", id, c); }
            }
            return Ok();
        }

        // =====================================================================================
        //  Deltagarna
        // =====================================================================================

        [HttpGet]
        public async Task<IActionResult> SearchMembers(int id, string? q)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            // Sökningen används också när en uppgift ges till någon som inte är med (hen läggs då till som Deltar).
            if (!PlanningAccess.CanInvite(x.Caller) && !(PlanningAccess.CanCreateTasks(x.Caller) && !x.Caller.ViaLink))
                return No("Du kan inte bjuda in.");
            var query = (q ?? "").Trim();
            if (query.Length < 2) return Json(new { success = true, members = Array.Empty<object>() });
            var inPlanning = _planning.Participants(id).Where(p => p.MemberId > 0).Select(p => p.MemberId!.Value).ToHashSet();
            var hits = _directory.Search(query, 15, null, approvedOnly: true, searchNames: true);
            // Bara namn och klubb — aldrig e-post eller telefon.
            return Json(new
            {
                success = true,
                members = hits.Select(h => new
                {
                    memberId = h.MemberId, name = h.DisplayName, club = h.PrimaryClubId > 0 ? (_clubs.GetClubNameById(h.PrimaryClubId.Value) ?? "") : "",
                    clubId = h.PrimaryClubId, alreadyIn = inPlanning.Contains(h.MemberId),
                }),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddMember(int id, int memberId, string? access, int? representsClubId, int? representsRegionId,
            string? representsText, string? representsNothing)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var acc = PlanningAccessLevel.Normalize(access) ?? PlanningAccessLevel.Participant;
            if (!PlanningAccess.CanGrant(x.Caller, acc)) return No("Du kan inte bjuda in med den nivån.");
            var m = memberId > 0 ? _memberService.GetById(memberId) : null;
            if (m == null || !m.IsApproved) return No("Medlemmen finns inte.");
            if (RepresentsError(representsClubId, representsRegionId) is { } repErr) return No(repErr);
            if (_planning.ParticipantForMember(id, memberId) is not { IsActive: true } && _planning.InvitesLastDay(id) >= PlanningInviteRules.MaxPerDay)
                return No(PlanningInviteRules.LimitMessage);
            // Inget angivet = hens egen klubb. "Inget särskilt" skickas uttryckligen (representsNothing = "1").
            var given = representsClubId > 0 || representsRegionId > 0 || !string.IsNullOrWhiteSpace(representsText) || representsNothing == "1";
            var rc = representsClubId > 0 ? representsClubId : (!given && _memberClubs.GetPrimaryClubId(m) is var pc && pc > 0 ? pc : null);
            var r = _planning.AddParticipant(id, memberId, MemberDisplayName(m), null, acc, rc, representsRegionId > 0 ? representsRegionId : null,
                representsText, x.MemberId);
            if (r.Error != null) return No(r.Error);
            var mailed = false;
            if (r.Created || r.Reactivated) mailed = await _notify.InviteAsync(id, r.ParticipantId, x.Caller.Participant, false);
            return Ok(new { success = true, participantId = r.ParticipantId, alreadyIn = !r.Created && !r.Reactivated, mailed });
        }

        /// <summary>
        /// Bjud in någon som saknar konto (Stefan 2026-10-21: i undantagsfall — någon i planeringen är
        /// inte skytt).
        /// <para><b>⚠️ Hör adressen till en medlem VÄGRAS inbjudan</b> (Stefan 2026-10-22) och svaret säger att
        /// hen ska bjudas in på vanligt sätt — via medlemssökningen. En gäst är för den som inte har något
        /// konto; samma person ska aldrig bli både gäst och medlem, och en medlem ska delta med sitt konto.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> InviteByEmail(int id, string name, string email, string? message)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanInvite(x.Caller)) return No("Du kan inte bjuda in.");
            var addr = (email ?? "").Trim();
            if (addr.Length == 0 || !addr.Contains('@')) return No("Ange en giltig e-postadress.");
            if ((name ?? "").Trim().Length == 0) return No("Skriv personens namn.");

            var existing = _memberService.GetByEmail(addr);
            if (existing != null)
                return Json(new
                {
                    success = false, isMember = true,
                    message = existing.IsApproved
                        ? "Adressen hör till en medlem på pistol.nu. Bjud in hen som medlem i stället: välj En medlem på pistol.nu och sök på namnet. Då deltar hen med sitt eget konto."
                        : "Adressen hör till ett konto på pistol.nu som ännu inte är godkänt av klubben. När kontot är godkänt bjuder du in hen som medlem.",
                });
            var r = _planning.CreateInvite(id, name!, addr, message, x.MemberId);
            if (r.Error != null) return No(r.Error);
            var mailed = await _notify.InviteGuestAsync(r.RequestId, x.Caller.Participant);
            // Supportvägen: sajtadministratören får länken i svaret, så att den går att lämna vidare när
            // mejlet inte når fram — och så att flödet går att pröva utan SMTP. Länken ger ingen tillgång.
            var supportLink = await _auth.IsCurrentUserAdminAsync() ? _links.InviteUrl(r.RequestId, id, Request.PublicBase()) : null;
            return Ok(new { success = true, requestId = r.RequestId, mailed, supportLink });
        }

        /// <summary>Vad en medlem kan företräda: hens klubbar och kretsarna de ligger i (guidens steg 2).</summary>
        [HttpGet]
        public async Task<IActionResult> RepresentOptions(int id, int memberId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanInvite(x.Caller)) return No("Du kan inte bjuda in.");
            return Ok(new { success = true, primaryClubId = PrimaryClub(memberId), clubs = ClubChoices(memberId), regions = RegionChoices(memberId) });
        }

        /// <summary>
        /// En deltagares kontaktuppgifter (radens Åtgärder → Kontaktuppgifter, Stefan 2026-10-23). Bara för den som
        /// själv är med i planeringen, och bara för en deltagare som är med — hämtas när man frågar, ligger aldrig i
        /// <c>Get</c>. Medlem: e-post och telefon ur kontot; gäst: adressen hen bjöds in med.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ContactInfo(int id, int participantId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null || !x.Caller.IsParticipant) return Missing();
            var row = _planning.Participant(id, participantId);
            if (row == null || !row.IsActive) return No("Personen är inte med i planeringen.");
            if (row.MemberId is not int mid)
                return Ok(new { success = true, name = row.DisplayName, email = row.GuestEmail ?? "", phone = "", club = "", isGuest = true });
            var m = _memberService.GetById(mid);
            if (m == null) return No("Personen finns inte längre.");
            var clubId = PrimaryClub(mid);
            // ⚠️ Omgång 3 (Expertgruppen K1, Stefan 2026-10-24): en medlem som någon annan lagt till är INBJUDEN tills hen själv
            // öppnat planeringen. Förut kunde vem som helst skapa en planering, lägga till medlemmar och läsa deras e-post och
            // telefon. "Tackat ja" = har varit inne (LastSeenAt; sätts av Get och mejllänken, nollas när någon lägger till hen igen).
            if (PlanningContactRules.IsPendingInvite(row) && row.Id != x.Caller.ParticipantId)
                return Ok(new { success = true, name = row.DisplayName, email = "", phone = "", isGuest = false, pending = true,
                    club = clubId > 0 ? (_clubs.GetClubNameById(clubId) ?? "") : "" });
            var phone = m.HasProperty("phoneNumber") ? (m.GetValue<string>("phoneNumber") ?? "") : "";
            return Ok(new
            {
                success = true, name = row.DisplayName, email = m.Email ?? "", phone = phone.Trim(),
                club = clubId > 0 ? (_clubs.GetClubNameById(clubId) ?? "") : "", isGuest = false,
            });
        }

        private int PrimaryClub(int memberId)
        {
            try { var m = _memberService.GetById(memberId); return m == null ? 0 : _memberClubs.GetPrimaryClubId(m); }
            catch { return 0; }
        }

        /// <summary>Kretsarna medlemmens klubbar ligger i.</summary>
        private List<object> RegionChoices(int memberId)
        {
            var list = new List<object>();
            try
            {
                var m = _memberService.GetById(memberId);
                if (m == null) return list;
                var seen = new HashSet<int>();
                foreach (var c in _memberClubs.GetAllClubIds(m))
                {
                    var r = _board.RegionIdForClub(c);
                    if (r > 0 && seen.Add(r)) list.Add(new { id = r, name = OrgName(DocumentOwnerType.Region, r) });
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering: medlemmens kretsar kunde inte läsas."); }
            return list;
        }

        // =====================================================================================
        //  Förfrågningar — inbjudan med e-post och "be om att få vara med" (Stefan 2026-10-23)
        //  Godkänns av den sammankallande, eller av sajtadmin som SUPPORT (loggat, ingen läsrätt).
        // =====================================================================================

        private sealed record Approver(PlanningRow Planning, PlanningCaller Caller, int MemberId, bool Support);

        private async Task<Approver?> ResolveApproverAsync(int planningId)
        {
            var p = _planning.Get(planningId);
            if (p == null) return null;
            var me = await MemberIdAsync();
            if (me <= 0) return null;
            var row = _planning.ParticipantForMember(planningId, me);
            var c = new PlanningCaller(row is { IsActive: true } ? row : null, false);
            if (PlanningAccess.CanManagePlanning(c)) return new Approver(p, c, me, false);
            if (await _auth.IsCurrentUserAdminAsync()) return new Approver(p, c, me, true);
            return null;
        }

        [HttpGet]
        public async Task<IActionResult> Requests(int id)
        {
            var a = await ResolveApproverAsync(id);
            if (a == null)
            {
                // En som kan bjuda in ser sina skickade inbjudningar (för att kunna dra tillbaka dem).
                var x = await ResolveAsync(id, null);
                if (x == null || !PlanningAccess.CanInvite(x.Caller)) return Missing();
            }
            var people = _planning.Participants(id, includeRemoved: true);
            string ByName(int memberId) => people.FirstOrDefault(p => p.MemberId == memberId)?.DisplayName ?? "";
            var canDecide = a != null;
            return Ok(new
            {
                success = true, canDecide, support = a?.Support ?? false,
                requests = _planning.OpenRequests(id).Where(r => canDecide || r.Status == PlanningRequestStatus.Sent).Select(r => new
                {
                    id = r.Id, kind = r.Kind, status = r.Status, name = r.Name, email = r.Email, memberId = r.MemberId,
                    club = r.MemberId > 0 ? OrgName(DocumentOwnerType.Club, PrimaryClub(r.MemberId.Value)) : "",
                    message = r.Message, proposedRepresents = r.ProposedRepresents,
                    createdAt = DT(r.CreatedAt), respondedAt = DT(r.RespondedAt), invitedBy = r.Kind == PlanningRequestKind.Invite ? ByName(r.CreatedBy) : "",
                    clubChoices = canDecide && r.MemberId > 0 ? ClubChoices(r.MemberId.Value) : null,
                    regionChoices = canDecide && r.MemberId > 0 ? RegionChoices(r.MemberId.Value) : null,
                }),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveRequest(int id, int requestId, string? access, int? representsClubId, int? representsRegionId, string? representsText)
        {
            var a = await ResolveApproverAsync(id);
            if (a == null) return No("Bara den sammankallande godkänner.");
            var acc = PlanningAccessLevel.Normalize(access) ?? PlanningAccessLevel.Participant;
            if (!a.Support && !PlanningAccess.CanGrant(a.Caller, acc)) return No("Du kan inte ge den nivån.");
            if (RepresentsError(representsClubId, representsRegionId) is { } repErr) return No(repErr);
            var req = _planning.Request(id, requestId);
            if (req == null) return No("Förfrågan finns inte.");
            var display = req.Name;
            if (req.Kind == PlanningRequestKind.Request && req.MemberId > 0)
            {
                var m = _memberService.GetById(req.MemberId.Value);
                if (m == null || !m.IsApproved) return No("Medlemmen finns inte längre.");
                display = MemberDisplayName(m);
            }
            var r = _planning.ApproveRequest(id, requestId, acc, representsClubId > 0 ? representsClubId : null,
                representsRegionId > 0 ? representsRegionId : null, representsText, display, a.MemberId);
            if (r.Error != null) return No(r.Error);
            if (a.Support) _logger.LogWarning("Planering {PlanningId}: sajtadmin {MemberId} godkände förfrågan {RequestId} som support ({Access}).", id, a.MemberId, requestId, acc);
            var mailed = await _notify.InviteAsync(id, r.ParticipantId, a.Caller.Participant, false, approved: true);
            // Supportvägen: sajtadmin får gästens länk in (som förut vid inbjudan med e-post).
            var supportLink = req.Kind == PlanningRequestKind.Invite && await _auth.IsCurrentUserAdminAsync() ? _links.MailUrl(r.ParticipantId, id, "oppna", baseUrl: Request.PublicBase()) : null;
            return Ok(new { success = true, participantId = r.ParticipantId, mailed, supportLink });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectRequest(int id, int requestId, string? note)
        {
            var a = await ResolveApproverAsync(id);
            if (a == null) return No("Bara den sammankallande avböjer.");
            var err = _planning.RejectRequest(id, requestId, note, a.MemberId);
            if (err != null) return No(err);
            if (a.Support) _logger.LogWarning("Planering {PlanningId}: sajtadmin {MemberId} avböjde förfrågan {RequestId} som support.", id, a.MemberId, requestId);
            var req = _planning.Request(id, requestId);
            var mailed = req != null && await _notify.RequestRejectedAsync(req, a.Caller.Participant);
            return Ok(new { success = true, mailed });
        }

        /// <summary>Den som kan bjuda in drar tillbaka en inbjudan som inte besvarats.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawInvite(int id, int requestId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanInvite(x.Caller)) return No("Du kan inte bjuda in.");
            var req = _planning.Request(id, requestId);
            if (req == null || req.Kind != PlanningRequestKind.Invite) return No("Inbjudan finns inte.");
            var err = _planning.WithdrawRequest(id, requestId);
            return err == null ? Ok() : No(err);
        }

        /// <summary>En medlem ber om att få vara med i en planering som visas för alla medlemmar.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestAccess(int id, string? message)
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            var m = _memberService.GetById(me);
            var r = _planning.CreateMemberRequest(id, me, MemberDisplayName(m), message);
            if (r.Error != null) return No(r.Error);
            var reached = await _notify.RequestToConvenersAsync(id, r.RequestId);
            return Ok(new { success = true, requestId = r.RequestId, reached });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawMyRequest(int id)
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            if (!_planning.OpenRequestsByMember(me).TryGetValue(id, out var req)) return No("Du har ingen förfrågan som väntar.");
            var err = _planning.WithdrawRequest(id, req.Id);
            return err == null ? Ok() : No(err);
        }

        /// <summary>
        /// Den inbjudna svarar — UTAN inloggning, med länken i inbjudan. Ett ja ger ingen tillgång: förfrågan
        /// väntar på den sammankallande, som får ett mejl.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AnswerInvite(string? t, string? accept, string? represents)
        {
            var target = _links.ParseInvite(t);
            var req = target == null ? null : _planning.Request(target.PlanningId, target.ParticipantId);
            if (req == null) return No("Länken gäller inte längre. Be den som bjöd in dig att skicka en ny inbjudan.");
            var yes = accept == "1";
            var err = _planning.AnswerInvite(req.Id, yes, represents);
            if (err != null) return No(err);
            var reached = yes ? await _notify.RequestToConvenersAsync(req.PlanningId, req.Id) : 0;
            return Ok(new { success = true, accepted = yes, reached });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResendInvite(int id, int participantId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanInvite(x.Caller)) return No("Du kan inte bjuda in.");
            var mailed = await _notify.InviteAsync(id, participantId, x.Caller.Participant, true);
            return mailed ? Ok(new { success = true, mailed }) : No("Inbjudan kunde inte skickas. Kontrollera adressen, eller försök igen om en stund.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateParticipant(int id, int participantId, string? access, int? representsClubId,
            int? representsRegionId, string? representsText)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var row = _planning.Participant(id, participantId);
            if (row == null || !row.IsActive) return No("Personen är inte med i planeringen.");
            var acc = PlanningAccessLevel.Normalize(access) ?? row.Access;
            var self = participantId == x.Caller.ParticipantId;
            if (acc != row.Access && !PlanningAccess.CanManageParticipants(x.Caller)) return No("Bara den sammankallande ändrar nivåerna.");
            if (!self && !PlanningAccess.CanManageParticipants(x.Caller)) return No("Du kan bara ändra dina egna uppgifter.");
            if (acc != row.Access && !PlanningAccess.CanGrant(x.Caller, acc)) return No("Du kan inte ge den nivån.");
            if (RepresentsError(representsClubId, representsRegionId) is { } repErr) return No(repErr);
            var err = _planning.UpdateParticipant(id, participantId, acc, representsClubId > 0 ? representsClubId : null,
                representsRegionId > 0 ? representsRegionId : null, representsText);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveParticipant(int id, int participantId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var self = participantId == x.Caller.ParticipantId && x.Caller.IsParticipant;
            if (!self && !PlanningAccess.CanManageParticipants(x.Caller)) return No("Bara den sammankallande tar bort någon.");
            var err = _planning.RemoveParticipant(id, participantId, x.MemberId);
            return err == null ? Ok(new { success = true, left = self }) : No(err);
        }

        /// <summary>
        /// Byt ut en deltagare mot en medlem (<paramref name="memberId"/>) eller en gäst med e-post. Den nya får
        /// samma nivå och samma Företräder och ett inbjudningsmejl. Bara den sammankallande, och aldrig sig själv
        /// (den som själv slutar lämnar planeringen när någon annan är sammankallande).
        /// </summary>
        /// <summary>
        /// Omgång 3: var i familjen (huvud- och delplaneringar) personen som byts ut också är med, och om den som frågar får
        /// byta ut hen där (sammankallande i just den planeringen). Skriver ingenting.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ReplaceFamily(int id, int participantId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageParticipants(x.Caller)) return No("Bara den sammankallande byter ut någon.");
            var old = _planning.Participant(id, participantId);
            if (old == null || !old.IsActive) return No("Personen är inte med i planeringen.");
            var list = new List<object>();
            foreach (var (f, row) in _planning.FamilyMemberships(id, old))
            {
                var fx = await ResolveAsync(f.Id, null);
                list.Add(new { planningId = f.Id, name = f.Name, access = PlanningAccessLevel.Label(row.Access),
                    canReplace = fx != null && PlanningAccess.CanManageParticipants(fx.Caller) && row.Id != fx.Caller.ParticipantId });
            }
            return Ok(new { success = true, plannings = list });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReplaceParticipant(int id, int participantId, int? memberId, string? name, string? email, string? alsoIn = null)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageParticipants(x.Caller)) return No("Bara den sammankallande byter ut någon.");
            if (participantId == x.Caller.ParticipantId) return No("Du kan inte byta ut dig själv. Gör någon annan till sammankallande och lämna sedan planeringen.");
            var oldRow = _planning.Participant(id, participantId);
            string display;
            string? guestEmail = null;
            if (memberId > 0)
            {
                var m = _memberService.GetById(memberId.Value);
                if (m == null || !m.IsApproved) return No("Medlemmen finns inte.");
                display = MemberDisplayName(m);
            }
            else
            {
                guestEmail = (email ?? "").Trim();
                if (guestEmail.Length == 0 || !guestEmail.Contains('@')) return No("Ange en giltig e-postadress.");
                if ((name ?? "").Trim().Length == 0) return No("Skriv personens namn.");
                // Samma regel som Bjud in med e-post: en medlem deltar med sitt konto, aldrig som gäst.
                if (_memberService.GetByEmail(guestEmail) != null)
                    return Json(new { success = false, isMember = true, message = "Adressen hör till en medlem på pistol.nu. Välj hen bland medlemmarna i stället — då deltar hen med sitt eget konto." });
                display = name!.Trim();
            }
            // Familjen räknas FÖRE bytet — efteråt är den gamla raden borttagen och går inte att matcha.
            var family = oldRow == null ? new List<(PlanningRow Planning, PlanningMemberRow Participant)>() : _planning.FamilyMemberships(id, oldRow);
            var r = _planning.ReplaceParticipant(id, participantId, memberId ?? 0, display, guestEmail, x.MemberId);
            if (r.Error != null) return No(r.Error);
            var mailed = await _notify.InviteAsync(id, r.ParticipantId, x.Caller.Participant, false);

            // Omgång 3 (Expertgruppen): Byt ut följer med ner i familjen — bara där den sammankallande valt det OCH själv är
            // sammankallande. Personen får den nivå hen hade i varje planering, inte huvudplaneringens.
            var wanted = (alsoIn ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToHashSet();
            var replacedIn = new List<string>(); var notIn = new List<string>();
            foreach (var (f, row) in family.Where(f => wanted.Contains(f.Planning.Id)))
            {
                var fx = await ResolveAsync(f.Id, null);
                if (fx == null || !PlanningAccess.CanManageParticipants(fx.Caller) || row.Id == fx.Caller.ParticipantId)
                { notIn.Add(f.Name); continue; }
                var fr = _planning.ReplaceParticipant(f.Id, row.Id, memberId ?? 0, display, guestEmail, x.MemberId);
                if (fr.Error != null) { notIn.Add($"{f.Name} ({fr.Error})"); continue; }
                await _notify.InviteAsync(f.Id, fr.ParticipantId, fx.Caller.Participant, false);
                replacedIn.Add(f.Name);
            }
            return Ok(new { success = true, participantId = r.ParticipantId, mailed, replacedIn, notReplacedIn = notIn });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMyNotify(int id, string mode, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null || !x.Caller.IsParticipant) return Missing();
            _planning.SetNotifyMode(id, x.Caller.ParticipantId, mode);
            return Ok(new { success = true, label = PlanningNotifyMode.Label(mode) });
        }

        // =====================================================================================
        //  Meddelandena
        // =====================================================================================

        private object MessageDto(PlanningMessageRow m, Dictionary<int, PlanningMemberRow> people, ILookup<int, int> mentions,
            Dictionary<int, PlanningMeetingRow> meetings, int me)
        {
            string Name(int? pid) => pid.HasValue && people.TryGetValue(pid.Value, out var p) ? p.DisplayName : "Någon";
            string? systemText = null;
            if (m.Kind == "System")
            {
                var who = Name(m.SubjectParticipantId);
                var when = m.RefKind == "meeting" && m.RefId.HasValue && meetings.TryGetValue(m.RefId.Value, out var mt) && mt.StartsAt.HasValue
                    ? " till " + mt.StartsAt.Value.ToString("dddd d MMMM 'kl.' HH:mm", Sv) : "";
                systemText = m.SystemKey switch
                {
                    "planning-created" => $"{who} skapade planeringen.",
                    "member-joined" => $"{who} är med i planeringen.",
                    "member-left" => $"{who} är inte längre med.",
                    "member-replaced" => $"{who} ersatte {Name(m.RefId)}" + (m.RefId.HasValue && people.TryGetValue(m.RefId.Value, out var gone)
                        && Represents(gone) is { Length: > 0 } rp ? $" ({rp})." : "."),
                    "meeting-proposed" => $"{who} föreslog ett möte: {m.Body}",
                    "meeting-booked" => $"{who} bokade mötet {m.Body}{when}.",
                    "meeting-rebooked" => $"{who} flyttade mötet {m.Body}{when}.",
                    "meeting-cancelled" => $"{who} ställde in mötet {m.Body}.",
                    _ => m.Body,
                };
            }
            return new
            {
                id = m.Id, kind = m.Kind, authorId = m.ParticipantId, author = m.Kind == "Message" ? Name(m.ParticipantId) : null,
                body = m.DeletedAt == null ? m.Body : "", deleted = m.DeletedAt != null, edited = m.EditedAt != null,
                systemText, replyTo = m.ReplyToMessageId, createdAt = DT(m.CreatedAt), viaEmail = m.ViaEmail,
                mentions = mentions[m.Id].ToArray(), mine = m.ParticipantId == me,
                refKind = m.RefKind, refId = m.RefId, areaId = m.AreaId,
            };
        }

        [HttpGet]
        public async Task<IActionResult> Messages(int id, string? t, int take = 300)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            if (!PlanningAccess.CanReadMessages(x.Caller)) return No("Meddelandena är deltagarnas.");
            var rows = _planning.Messages(id, 0, Math.Clamp(take, 20, 1000));
            var people = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id);
            var mentions = _planning.MentionsFor(id, rows.Select(r => r.Id)).ToLookup(m => m.MessageId, m => m.ParticipantId);
            var meetings = _planning.Meetings(id).ToDictionary(m => m.Id);
            var reads = _planning.ReadRows(id).Where(r => people.TryGetValue(r.ParticipantId, out var p) && p.IsActive)
                .Select(r => new { participantId = r.ParticipantId, lastRead = r.LastReadMessageId });
            var myRead = _planning.ReadRows(id).FirstOrDefault(r => r.ParticipantId == x.Caller.ParticipantId)?.LastReadMessageId ?? 0;
            return Json(new
            {
                success = true,
                messages = rows.Select(r => MessageDto(r, people, mentions, meetings, x.Caller.ParticipantId)),
                reads,
                myLastRead = myRead,
                version = _planning.Version(id),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Post(int id, string body, int? replyTo, string? mentions, string? documents, string? t, int? areaId = null)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanWriteMessages(x.Caller)) return No("Bara den som är med skriver i planeringen.");
            var r = _planning.Post(id, x.Caller.ParticipantId, body, replyTo, Ids(mentions), x.Caller.ViaLink, areaId);
            if (r.Error != null) return No(r.Error);
            // Bifogade dokument ur arkivet. Meddelandet är redan skickat; går bifogningen inte igenom sägs det.
            string? docWarning = null;
            var docIds = ParseIds(documents);
            if (docIds.Count > 0)
            {
                var attachError = PlanningAccess.CanAttachDocument(x.Caller)
                    ? _planning.AttachDocuments(id, docIds, PlanningDocTarget.Message, r.MessageId, x.Caller.ParticipantId).Error
                    : "Bara den som deltar bifogar dokument.";
                if (attachError != null) docWarning = "Meddelandet är skickat, men dokumenten kunde inte bifogas: " + attachError;
            }
            if (r.MentionedParticipantIds.Count > 0)
            {
                try { await _notify.MentionsAsync(id, r.MessageId, r.MentionedParticipantIds); }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: omnämnandena kunde inte aviseras.", id); }
            }
            return Ok(new { success = true, messageId = r.MessageId, docWarning });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditMessage(int id, int messageId, string body, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var m = _planning.Message(id, messageId);
            if (m == null || !PlanningAccess.CanEditMessage(x.Caller, m)) return No("Du kan bara ändra dina egna meddelanden.");
            var err = _planning.Edit(id, messageId, body);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMessage(int id, int messageId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var m = _planning.Message(id, messageId);
            if (m == null || !PlanningAccess.CanEditMessage(x.Caller, m)) return No("Du kan bara ta bort dina egna meddelanden.");
            _planning.Remove(id, messageId);
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkRead(int id, int upTo, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null || !x.Caller.IsParticipant) return Json(new { ok = false });
            _planning.MarkRead(id, x.Caller.ParticipantId, upTo);
            return Json(new { ok = true });
        }

        // =====================================================================================
        //  Mötena
        // =====================================================================================

        [HttpGet]
        public async Task<IActionResult> Meetings(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            var meetings = _planning.Meetings(id);
            var ids = meetings.Select(m => m.Id).ToList();
            var options = _planning.Options(ids).ToLookup(o => o.PlanningMeetingId);
            var votes = _planning.Votes(ids).ToLookup(v => v.PlanningMeetingId);
            var people = _planning.Participants(id);
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var me = x.Caller.ParticipantId;
            var showNotes = PlanningAccess.CanReadContent(x.Caller);
            var topics = _planning.Topics(id, ids).ToLookup(tp => tp.PlanningMeetingId);
            var now = DateTime.Now;
            return Json(new
            {
                success = true,
                canUse = PlanningAccess.CanUseMeetings(x.Caller),
                meetings = meetings.Select(m =>
                {
                    var current = PlanningTaskRules.MeetingIsCurrent(m, now);
                    var opts = options[m.Id].OrderBy(o => o.StartsAt).ToList();
                    var vs = votes[m.Id].ToList();
                    var voters = vs.Select(v => v.ParticipantId).ToHashSet();
                    var best = m.Status == PlanningMeetingStatus.Proposed ? PlanningMeetingRules.BestOption(opts, vs) : null;
                    return new
                    {
                        id = m.Id, title = m.Title, status = m.Status, startsAt = DT(m.StartsAt), endsAt = DT(m.EndsAt),
                        place = m.Place, onlineUrl = showNotes ? m.OnlineUrl : null, agenda = m.Agenda, notes = showNotes ? m.Notes : null,
                        voteDeadline = D(m.VoteDeadline), createdBy = names.GetValueOrDefault(m.CreatedBy, "Någon"),
                        bookedBy = m.BookedBy.HasValue ? names.GetValueOrDefault(m.BookedBy.Value, "Någon") : null,
                        canEdit = PlanningAccess.CanEditMeeting(x.Caller, m),
                        bestOptionId = best,
                        options = opts.Select(o => new
                        {
                            id = o.Id, startsAt = DT(o.StartsAt), endsAt = DT(o.EndsAt),
                            votes = vs.Where(v => v.PlanningMeetingOptionId == o.Id).Select(v => new { participantId = v.ParticipantId, answer = v.Answer }),
                            mine = vs.FirstOrDefault(v => v.PlanningMeetingOptionId == o.Id && v.ParticipantId == me)?.Answer,
                        }),
                        notAnswered = m.Status == PlanningMeetingStatus.Proposed
                            ? people.Where(p => PlanningAccess.IsCalledToMeetings(p) && !voters.Contains(p.Id)).Select(p => new { id = p.Id, name = p.DisplayName }) : null,
                        answeredCount = voters.Count,
                        // Avstämningen (Stefan 2026-10-23): "Att prata om" och om mötet beslutar om uppgifterna nu.
                        isCurrent = current,
                        canDecideTasks = PlanningAccess.CanDecideTask(x.Caller, current),
                        topics = topics[m.Id].Select(tp => new
                        {
                            id = tp.Id, text = tp.Text, by = names.GetValueOrDefault(tp.AddedBy, "Någon"), at = DT(tp.AddedAt),
                            canRemove = PlanningAccess.CanRemoveTopic(x.Caller, tp),
                        }),
                    };
                }),
            });
        }

        private static List<(DateTime, DateTime)>? ParseOptions(string? csv, out string? error)
        {
            error = null;
            var list = new List<(DateTime, DateTime)>();
            foreach (var part in (csv ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var se = part.Split('|');
                var a = se.Length == 2 ? ParseDateTime(se[0]) : null;
                var b = se.Length == 2 ? ParseDateTime(se[1]) : null;
                if (a == null || b == null) { error = "En tid gick inte att läsa."; return null; }
                list.Add((a.Value, b.Value));
            }
            return list;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateMeeting(int id, string title, string? options, string? place, string? onlineUrl,
            string? agenda, string? voteDeadline, string? bookNow, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanUseMeetings(x.Caller)) return No("Bara den som är med skapar möten.");
            var opts = ParseOptions(options, out var perr);
            if (opts == null) return No(perr!);
            var book = bookNow is "1" or "true" && opts.Count == 1;
            var input = new PlanningService.MeetingInput(title, opts, place, onlineUrl, agenda, ParseDate(voteDeadline)?.AddHours(23).AddMinutes(59));
            var err = PlanningService.ValidateMeeting(input, requireOptions: true);
            if (err != null) return No(err);
            var mid = _planning.CreateMeeting(id, x.Caller.ParticipantId, input);
            try
            {
                if (book)
                {
                    var first = _planning.Options(new[] { mid }).First();
                    var br = _planning.Book(id, mid, first.Id, null, null, x.Caller.ParticipantId);
                    if (br.Error == null) await _notify.MeetingBookedAsync(id, mid, false);
                }
                else await _notify.MeetingProposedAsync(id, mid);
            }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: mötet {Mid} kunde inte aviseras.", id, mid); }
            return Ok(new { success = true, meetingId = mid, booked = book });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddOption(int id, int meetingId, string start, string end, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanUseMeetings(x.Caller)) return No("Bara den som är med föreslår tider.");
            var a = ParseDateTime(start); var b = ParseDateTime(end);
            if (a == null || b == null) return No("Tiden gick inte att läsa.");
            var err = _planning.AddOption(id, meetingId, a.Value, b.Value);
            return err == null ? Ok() : No(err);
        }

        /// <summary>Svaren på ett mötes tider, alla på en gång: "optionId:Yes,optionId:No".</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Vote(int id, int meetingId, string answers, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanUseMeetings(x.Caller)) return No("Bara den som är med svarar på tiderna.");
            var n = 0;
            foreach (var pair in (answers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[0], out var oid)) continue;
                var err = _planning.Vote(id, meetingId, oid, x.Caller.ParticipantId, kv[1], x.Caller.ViaLink);
                if (err != null) return No(err);
                n++;
            }
            return n > 0 ? Ok(new { success = true, saved = n }) : No("Välj ett svar för minst en tid.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Book(int id, int meetingId, int? optionId, string? start, string? end, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanUseMeetings(x.Caller)) return No("Bara den som är med bokar möten.");
            var r = _planning.Book(id, meetingId, optionId, ParseDateTime(start), ParseDateTime(end), x.Caller.ParticipantId);
            if (r.Error != null) return No(r.Error);
            try { await _notify.MeetingBookedAsync(id, meetingId, r.WasRebooked); }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: bokningen av möte {Mid} kunde inte aviseras.", id, meetingId); }
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMeeting(int id, int meetingId, string title, string? place, string? onlineUrl,
            string? agenda, string? notes, string? voteDeadline, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var m = _planning.Meeting(id, meetingId);
            if (m == null) return No("Mötet finns inte.");
            // Anteckningarna får alla som är med skriva — det är mötets gemensamma minnesanteckningar.
            if (!PlanningAccess.CanEditMeeting(x.Caller, m))
            {
                if (!PlanningAccess.CanUseMeetings(x.Caller)) return No("Du kan inte ändra mötet.");
                var e2 = _planning.UpdateMeeting(id, meetingId, m.Title, m.Place, m.OnlineUrl, m.Agenda, notes, m.VoteDeadline);
                return e2 == null ? Ok() : No(e2);
            }
            var err = _planning.UpdateMeeting(id, meetingId, title, place, onlineUrl, agenda, notes,
                ParseDate(voteDeadline)?.AddHours(23).AddMinutes(59));
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelMeeting(int id, int meetingId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var m = _planning.Meeting(id, meetingId);
            if (m == null || !PlanningAccess.CanEditMeeting(x.Caller, m)) return No("Bara den som skapade mötet, eller kan ändra planeringen, ställer in det.");
            var wasBooked = m.Status == PlanningMeetingStatus.Scheduled;
            var err = _planning.Cancel(id, meetingId, x.Caller.ParticipantId);
            if (err != null) return No(err);
            if (wasBooked)
            {
                try { await _notify.MeetingCancelledAsync(id, meetingId); }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: inställt möte {Mid} kunde inte aviseras.", id, meetingId); }
            }
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMeeting(int id, int meetingId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var m = _planning.Meeting(id, meetingId);
            if (m == null || !PlanningAccess.CanEditMeeting(x.Caller, m)) return No("Du kan inte ta bort mötet.");
            var err = _planning.DeleteMeeting(id, meetingId);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemindNonVoters(int id, int meetingId, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanUseMeetings(x.Caller)) return No("Du kan inte påminna.");
            var n = await _notify.RemindNonVotersAsync(id, meetingId, manual: true);
            return Ok(new { success = true, reached = n });
        }

        /// <summary>Sajtadmin: kör planeringens svep nu, med en vald tid (prov utan att vänta till kvällen).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunSweepNow(string? asOf)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return No("Bara sajtadministratören.");
            var now = ParseDateTime(asOf) ?? DateTime.Now;
            var scopes = HttpContext.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var r = await PlanningNotificationHostedService.RunOnceAsync(scopes, now, _logger);
            return Json(new { success = true, ran = r != null, digests = r?.Digests, pushes = r?.Pushes, meetingReminders = r?.MeetingReminders, voteReminders = r?.VoteReminders, taskBatches = r?.TaskBatches });
        }

        /// <summary>Sajtadmin: kör rensningen av borttagna planeringar nu, med en vald tid (samma som dygnssvepet).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RunPurgeNow(string? asOf)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return No("Bara sajtadministratören.");
            var r = await HttpContext.RequestServices.GetRequiredService<PlanningPurgeService>().PurgeAsync(ParseDateTime(asOf) ?? DateTime.Now);
            return Json(new { success = true, plannings = r.Plannings, files = r.Files });
        }
    }
}
