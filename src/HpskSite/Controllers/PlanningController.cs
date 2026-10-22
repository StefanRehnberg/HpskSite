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
    public class PlanningController : SurfaceController
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        private readonly PlanningService _planning;
        private readonly PlanningNotifier _notify;
        private readonly PlanningLinkService _links;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly BoardWorkAccess _board;
        private readonly MemberClubService _memberClubs;
        private readonly MemberDirectoryService _directory;
        private readonly ClubService _clubs;
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Notifications.WebPushService _pushInfo;
        private readonly BoardRoleService _roles;
        private readonly ILogger<PlanningController> _logger;

        public PlanningController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            PlanningService planning, PlanningNotifier notify, PlanningLinkService links,
            IMemberManager memberManager, IMemberService memberService, BoardWorkAccess board,
            MemberClubService memberClubs, MemberDirectoryService directory, ClubService clubs,
            AdminAuthorizationService auth, HpskSite.Services.Notifications.WebPushService pushInfo, BoardRoleService roles, ILogger<PlanningController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _planning = planning;
            _notify = notify;
            _links = links;
            _memberManager = memberManager;
            _memberService = memberService;
            _board = board;
            _memberClubs = memberClubs;
            _directory = directory;
            _clubs = clubs;
            _auth = auth;
            _pushInfo = pushInfo;
            _roles = roles;
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
        private async Task<Ctx?> ResolveAsync(int planningId, string? t)
        {
            var p = _planning.Get(planningId);
            if (p == null) return null;

            if (!string.IsNullOrWhiteSpace(t))
            {
                var target = _links.ParseMail(t);
                if (target == null || target.PlanningId != planningId) return null;
                var row = _planning.Participant(planningId, target.ParticipantId);
                if (row == null || !row.IsActive) return null;
                return new Ctx(p, new PlanningCaller(row, false, true), row.MemberId ?? 0);
            }

            var me = await MemberIdAsync();
            if (me <= 0) return null;
            var participant = _planning.ParticipantForMember(planningId, me);
            var ownerOrg = false;
            if ((participant == null || !participant.IsActive) && p.Visibility == PlanningVisibility.OwnerOrg
                && p.OwnerType.HasValue && p.OwnerId > 0)
            {
                try { ownerOrg = await _board.CanAccessBoardWorkAsync(p.OwnerType.Value, p.OwnerId!.Value); }
                catch (Exception ex) { OpsSignals.AuthGateFailed(_logger, ex, "PlanningOwnerOrg", $"{planningId}/{me}"); }
            }
            var c = new PlanningCaller(participant is { IsActive: true } ? participant : null, ownerOrg, false);
            if (!PlanningAccess.CanReadContent(c)) return null;
            return new Ctx(p, c, me);
        }

        private IActionResult Missing() => Json(new { success = false, missing = true, message = "Planeringen finns inte, eller så är du inte med i den." });
        private IActionResult No(string message) => Json(new { success = false, message });
        private IActionResult Ok(object? extra = null) => Json(extra ?? new { success = true });

        private static string DT(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm", Inv) : "";
        private static string D(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd", Inv) : "";
        private static DateTime? ParseDate(string? s) =>
            DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", Inv, DateTimeStyles.None, out var d) ? d : null;
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

        private string Represents(PlanningMemberRow r) =>
            r.RepresentsClubId > 0 ? OrgName(DocumentOwnerType.Club, r.RepresentsClubId)
            : r.RepresentsRegionId > 0 ? OrgName(DocumentOwnerType.Region, r.RepresentsRegionId)
            : r.RepresentsText ?? "";

        // =====================================================================================
        //  Mina planeringar och en ny planering
        // =====================================================================================

        [HttpGet]
        public async Task<IActionResult> MyPlannings()
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            var mine = _planning.ForMember(me);
            // Föreningens planeringar som styrelsen får läsa, utom de man redan är med i.
            var orgs = new List<object>();
            try
            {
                var seen = mine.Select(m => m.PlanningId).ToHashSet();
                foreach (var (ot, oi) in _roles.GetBoardMembershipsForMember(me))
                    foreach (var p in _planning.VisibleToOwner(ot, oi).Where(p => !seen.Contains(p.Id)))
                    {
                        seen.Add(p.Id);
                        orgs.Add(new { id = p.Id, name = p.Name, org = OrgName(p.OwnerType, p.OwnerId), targetText = p.TargetText, status = p.Status });
                    }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering: föreningens planeringar kunde inte läsas."); }

            return Json(new
            {
                success = true,
                plannings = mine.Select(m => new
                {
                    id = m.PlanningId, name = m.Name, status = m.Status, kindLabel = PlanningKind.Label(m.Kind),
                    targetText = m.TargetText, targetStart = D(m.TargetStart), access = m.Access,
                    accessLabel = PlanningAccessLevel.Label(m.Access), unread = m.Unread, votesOwed = m.VotesOwed,
                    nextMeetingAt = DT(m.NextMeetingAt), nextMeetingTitle = m.NextMeetingTitle,
                }),
                orgPlannings = orgs,
                orgOptions = OrgOptions(me),
            });
        }

        /// <summary>
        /// Föreningarna vars styrelse kan få läsa planeringen: medlemmens klubbar, medlemmens kretsar och
        /// ALLA kretsar i samma landsdel (Stefan 2026-10-22: ett SM har en hel landsdel inblandad, och
        /// huvudarrangören är ofta en annan krets än ens egen). Landsdelen själv har ingen styrelse i
        /// pistol.nu och kan därför inte väljas.
        /// </summary>
        private sealed record Org(int Type, int Id, string Name, string Kind, bool Own);

        private List<Org>? _orgCache;
        private List<Org> OrgChoices(int memberId)
        {
            if (_orgCache != null) return _orgCache;
            var list = new List<Org>();
            try
            {
                var m = _memberService.GetById(memberId);
                var clubs = _memberClubs.GetAllClubIds(m);
                var myRegions = new HashSet<int>();
                foreach (var c in clubs)
                {
                    list.Add(new Org(DocumentOwnerType.Club, c, OrgName(DocumentOwnerType.Club, c), "Klubb", true));
                    var r = _board.RegionIdForClub(c);
                    if (r > 0) myRegions.Add(r);
                }
                var all = AllRegions();
                var areas = all.Where(r => myRegions.Contains(r.Id) && !string.IsNullOrWhiteSpace(r.Area))
                    .Select(r => r.Area!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var r in all.Where(r => myRegions.Contains(r.Id) || (r.Area != null && areas.Contains(r.Area.Trim())))
                             .OrderByDescending(r => myRegions.Contains(r.Id)).ThenBy(r => r.Name, StringComparer.Create(Sv, false)))
                    list.Add(new Org(DocumentOwnerType.Region, r.Id, r.Name, "Krets", myRegions.Contains(r.Id)));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering: medlemmens föreningar kunde inte läsas."); }
            return _orgCache = list;
        }

        private List<(int Id, string Name, string? Area)> AllRegions()
        {
            var list = new List<(int, string, string?)>();
            if (!UmbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return list;
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return list;
            foreach (var r in root.Children.Where(c => c.ContentType.Alias == "regionalPage"))
                list.Add((r.Id, RegionName(r), r.Value<string>("area")));
            return list;
        }

        private List<object> OrgOptions(int memberId, int? currentType = null, int? currentId = null)
        {
            var list = OrgChoices(memberId).Select(o => (object)new { type = o.Type, id = o.Id, name = o.Name, kind = o.Kind, own = o.Own }).ToList();
            // Den nuvarande föreningen ska alltid gå att se och behålla, även om den valts av någon annan.
            if (currentId > 0 && !OrgChoices(memberId).Any(o => o.Type == currentType && o.Id == currentId))
                list.Insert(0, new { type = currentType, id = currentId, name = OrgName(currentType, currentId), kind = currentType == DocumentOwnerType.Club ? "Klubb" : "Krets", own = false });
            return list;
        }

        private bool OrgAllowed(int memberId, int ownerType, int ownerId) =>
            OrgChoices(memberId).Any(o => o.Type == ownerType && o.Id == ownerId);

        private static (bool Ok, int? Type, int? Id) ParseOwner(string? owner)
        {
            var v = (owner ?? "").Trim();
            if (v.Length == 0) return (true, null, null);
            var parts = v.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && b > 0 && a is 0 or 1)
                return (true, a, b);
            return (false, null, null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(string name, string? kind, string? owner, string? targetText,
            string? targetStart, string? targetEnd, string? visibility)
        {
            var me = await MemberIdAsync();
            if (me <= 0) return No("Logga in först.");
            var (okOwner, ot, oi) = ParseOwner(owner);
            if (!okOwner || (oi > 0 && !OrgAllowed(me, ot!.Value, oi!.Value)))
                return No("Välj en av dina klubbar, din krets eller en krets i samma landsdel.");
            // Föreningen finns för EN sak: att dess styrelse får läsa planeringen. Vald förening = läsrätt.
            var vis = ot.HasValue ? PlanningVisibility.OwnerOrg : PlanningVisibility.Invited;
            var start = ParseDate(targetStart);
            var end = ParseDate(targetEnd);
            var err = PlanningService.Validate(name, start, end, ot, oi, vis);
            if (err != null) return No(err);

            var member = _memberService.GetById(me);
            var display = MemberDisplayName(member);
            var primary = _memberClubs.GetPrimaryClubId(member);
            var (pid, _) = _planning.Create(new PlanningService.CreateInput(name, PlanningKind.Normalize(kind), ot, oi, targetText,
                start, end, vis, me, display, primary > 0 ? primary : null, null));
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
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var c = x.Caller;
            if (c.IsParticipant) _planning.TouchSeen(c.ParticipantId);
            var people = _planning.Participants(id);
            var meCanManage = PlanningAccess.CanManageParticipants(c);
            return Json(new
            {
                success = true,
                planning = new
                {
                    id = x.Planning.Id, name = x.Planning.Name, kind = x.Planning.Kind, kindLabel = PlanningKind.Label(x.Planning.Kind),
                    targetText = x.Planning.TargetText, targetStart = D(x.Planning.TargetStart), targetEnd = D(x.Planning.TargetEnd),
                    visibility = x.Planning.Visibility, status = x.Planning.Status,
                    ownerType = x.Planning.OwnerType, ownerId = x.Planning.OwnerId, ownerName = OrgName(x.Planning.OwnerType, x.Planning.OwnerId),
                    createdAt = D(x.Planning.CreatedAt),
                },
                me = new
                {
                    participantId = c.ParticipantId, isParticipant = c.IsParticipant, ownerOrgReader = c.OwnerOrgReader && !c.IsParticipant,
                    viaLink = c.ViaLink, isGuest = c.Participant?.IsGuest ?? false, name = c.Participant?.DisplayName,
                    access = c.Access, accessLabel = PlanningAccessLevel.Label(c.Access),
                    notifyMode = c.Participant?.NotifyMode, notifyLabel = PlanningNotifyMode.Label(c.Participant?.NotifyMode),
                    canEdit = PlanningAccess.CanEditPlanning(c), canManage = PlanningAccess.CanManagePlanning(c),
                    canInvite = PlanningAccess.CanInvite(c), canReadMessages = PlanningAccess.CanReadMessages(c),
                    canUseMeetings = PlanningAccess.CanUseMeetings(c),
                    icsUrl = c.IsParticipant ? _links.IcsUrl(c.ParticipantId, id, Request.PublicBase()) : null,
                    pushConfigured = _pushInfo.IsConfigured,
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
                }),
                orgOptions = PlanningAccess.CanManagePlanning(c) ? OrgOptions(x.MemberId, x.Planning.OwnerType, x.Planning.OwnerId) : null,
                notifyModes = PlanningNotifyMode.Values.Select(v => new { value = v, label = PlanningNotifyMode.Label(v), help = PlanningNotifyMode.Help(v) }),
                version = _planning.Version(id),
            });
        }

        /// <summary>Pollningen: en PK-läsning. Svarar 200 även när svaret är nej, så att felräknaren inte fylls.</summary>
        [HttpGet]
        public async Task<IActionResult> Version(int id, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Json(new { ok = false });
            return Json(new { ok = true, version = _planning.Version(id) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update(int id, string name, string? targetText, string? targetStart, string? targetEnd, string? owner)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Du kan inte ändra planeringen.");
            int? ot = x.Planning.OwnerType, oi = x.Planning.OwnerId;
            // ⚠️ Modellbindningen gör "" till null — "ingen förening" känns igen på att fältet skickades.
            if (owner != null || (Request.HasFormContentType && Request.Form.ContainsKey("owner")))
            {
                var (okOwner, nt, ni) = ParseOwner(owner);
                if (!okOwner) return No("Okänd förening.");
                var changed = nt != ot || ni != oi;
                if (changed)
                {
                    if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Bara den som kan ändra allt ändrar vem som får läsa planeringen.");
                    if (ni > 0 && !OrgAllowed(x.MemberId, nt!.Value, ni!.Value))
                        return No("Välj en av dina klubbar, din krets eller en krets i samma landsdel.");
                    ot = nt; oi = ni;
                }
            }
            var vis = ot.HasValue ? PlanningVisibility.OwnerOrg : PlanningVisibility.Invited;
            var err = _planning.Update(id, name, targetText, ParseDate(targetStart), ParseDate(targetEnd), ot, oi, vis, x.MemberId);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStatus(int id, string status)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Bara den som kan ändra allt arkiverar planeringen.");
            _planning.SetStatus(id, status, x.MemberId);
            return Ok();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Bara den som kan ändra allt tar bort planeringen.");
            _planning.Delete(id, x.MemberId);
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
            if (!PlanningAccess.CanInvite(x.Caller)) return No("Du kan inte bjuda in.");
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
        public async Task<IActionResult> AddMember(int id, int memberId, string? access, int? representsClubId, int? representsRegionId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var acc = PlanningAccessLevel.Normalize(access) ?? PlanningAccessLevel.Participant;
            if (!PlanningAccess.CanGrant(x.Caller, acc)) return No("Du kan inte bjuda in med den rätten.");
            var m = memberId > 0 ? _memberService.GetById(memberId) : null;
            if (m == null || !m.IsApproved) return No("Medlemmen finns inte.");
            var rc = representsClubId > 0 ? representsClubId : (_memberClubs.GetPrimaryClubId(m) is var pc && pc > 0 ? pc : null);
            var r = _planning.AddParticipant(id, memberId, MemberDisplayName(m), null, acc, rc, representsRegionId > 0 ? representsRegionId : null, null, x.MemberId);
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
        public async Task<IActionResult> InviteByEmail(int id, string name, string email, string? access, string? representsText)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var acc = PlanningAccessLevel.Normalize(access) ?? PlanningAccessLevel.Participant;
            if (!PlanningAccess.CanGrant(x.Caller, acc)) return No("Du kan inte bjuda in med den rätten.");
            var addr = (email ?? "").Trim();
            if (addr.Length == 0 || !addr.Contains('@')) return No("Ange en giltig e-postadress.");
            if ((name ?? "").Trim().Length == 0) return No("Skriv personens namn.");

            var existing = _memberService.GetByEmail(addr);
            if (existing != null)
                return Json(new
                {
                    success = false, isMember = true,
                    message = existing.IsApproved
                        ? "Adressen hör till en medlem på pistol.nu. Bjud in hen på vanligt sätt: välj Bjud in en medlem och sök på namnet. Då deltar hen med sitt eget konto."
                        : "Adressen hör till ett konto på pistol.nu som ännu inte är godkänt av klubben. När kontot är godkänt bjuder du in hen på vanligt sätt: Bjud in en medlem och sök på namnet.",
                });
            var r = _planning.AddParticipant(id, 0, name!, addr, acc, null, null, representsText, x.MemberId);
            if (r.Error != null) return No(r.Error);
            var mailed = false;
            if (r.Created || r.Reactivated) mailed = await _notify.InviteAsync(id, r.ParticipantId, x.Caller.Participant, false);
            // Supportvägen (som kretsgranskningens caseLinks): sajtadministratören får gästens länk i svaret,
            // så att den går att lämna vidare när mejlet inte når fram — och så att flödet går att pröva utan SMTP.
            var supportLink = await _auth.IsCurrentUserAdminAsync() ? _links.MailUrl(r.ParticipantId, id, "oppna") : null;
            return Ok(new { success = true, participantId = r.ParticipantId, alreadyIn = !r.Created && !r.Reactivated, mailed, supportLink });
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
            if (acc != row.Access && !PlanningAccess.CanManageParticipants(x.Caller)) return No("Bara den som kan ändra allt ändrar rättigheterna.");
            if (!self && !PlanningAccess.CanManageParticipants(x.Caller)) return No("Du kan bara ändra dina egna uppgifter.");
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
            if (!self && !PlanningAccess.CanManageParticipants(x.Caller)) return No("Bara den som kan ändra allt tar bort någon.");
            var err = _planning.RemoveParticipant(id, participantId, x.MemberId);
            return err == null ? Ok(new { success = true, left = self }) : No(err);
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
                refKind = m.RefKind, refId = m.RefId,
            };
        }

        [HttpGet]
        public async Task<IActionResult> Messages(int id, string? t, int take = 300)
        {
            var x = await ResolveAsync(id, t);
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
        public async Task<IActionResult> Post(int id, string body, int? replyTo, string? mentions, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanWriteMessages(x.Caller)) return No("Bara den som är med skriver i planeringen.");
            var r = _planning.Post(id, x.Caller.ParticipantId, body, replyTo, Ids(mentions), x.Caller.ViaLink);
            if (r.Error != null) return No(r.Error);
            if (r.MentionedParticipantIds.Count > 0)
            {
                try { await _notify.MentionsAsync(id, r.MessageId, r.MentionedParticipantIds); }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: omnämnandena kunde inte aviseras.", id); }
            }
            return Ok(new { success = true, messageId = r.MessageId });
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
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var meetings = _planning.Meetings(id);
            var ids = meetings.Select(m => m.Id).ToList();
            var options = _planning.Options(ids).ToLookup(o => o.PlanningMeetingId);
            var votes = _planning.Votes(ids).ToLookup(v => v.PlanningMeetingId);
            var people = _planning.Participants(id);
            var names = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id, p => p.DisplayName);
            var me = x.Caller.ParticipantId;
            var showNotes = x.Caller.IsParticipant;
            return Json(new
            {
                success = true,
                canUse = PlanningAccess.CanUseMeetings(x.Caller),
                meetings = meetings.Select(m =>
                {
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
                            ? people.Where(p => !voters.Contains(p.Id)).Select(p => new { id = p.Id, name = p.DisplayName }) : null,
                        answeredCount = voters.Count,
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
            return Json(new { success = true, ran = r != null, digests = r?.Digests, pushes = r?.Pushes, meetingReminders = r?.MeetingReminders, voteReminders = r?.VoteReminders });
        }
    }
}
