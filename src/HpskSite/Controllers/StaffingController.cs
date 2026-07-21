using HpskSite.Models.Staffing;
using HpskSite.Services;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Backend for the Tävlingsplanering (competition planning) workspace on /competitionmanagement's
    /// Planering tab: the day-of functionary roster (Bemanning) + the preparation work-breakdown
    /// (Förberedelser). Auth is the same four-tier competition-staff gate used by the results/messaging
    /// controllers (site admin / competition manager / club admin / skjutledare, + regional admin for
    /// region-hosted comps). See Documentation/COMPETITION_STAFFING_SYSTEM.md.
    /// </summary>
    public class StaffingController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AdminAuthorizationService _auth;
        private readonly ClubService _clubService;
        private readonly StaffingService _staffing;
        private readonly WorkBreakdownService _work;
        private readonly PrepDocumentStorage _docs;
        private readonly ILogger<StaffingController> _logger;

        public StaffingController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            ClubService clubService,
            StaffingService staffing,
            WorkBreakdownService work,
            PrepDocumentStorage docs,
            ILogger<StaffingController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _auth = auth;
            _clubService = clubService;
            _staffing = staffing;
            _work = work;
            _docs = docs;
            _logger = logger;
        }

        // ======================= Bemanning (roster) =======================

        [HttpGet]
        public async Task<IActionResult> GetRoles(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var discipline = GetDiscipline(competitionId);
            var roles = FunctionaryRoles.ForDiscipline(discipline).Select(r => new
            {
                key = r.Key,
                name = r.DisplayName,
                plural = r.Plural,
                defaultScopeType = r.DefaultScopeType,
                supportsTargetRange = r.SupportsTargetRange,
                supportsFunctionTitle = r.SupportsFunctionTitle,
                description = r.Description,
                needs = r.Needs,
            });
            return Json(new { success = true, discipline, roles });
        }

        [HttpGet]
        public async Task<IActionResult> GetRoster(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var discipline = GetDiscipline(competitionId);
            var roster = _staffing.BuildRoster(competitionId, discipline, canEdit: true);
            return Json(new
            {
                success = true,
                discipline = roster.Discipline,
                canEdit = roster.CanEdit,
                totalAssigned = roster.TotalAssigned,
                groups = roster.Groups,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAssignment([FromBody] SaveStaffAssignmentRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.RoleKey))
                return Json(new { success = false, message = "Roll saknas" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            // Validate the role belongs to this competition's discipline.
            var discipline = GetDiscipline(request.CompetitionId);
            var role = FunctionaryRoles.Resolve(discipline, request.RoleKey);
            if (role == null)
                return Json(new { success = false, message = "Rollen är inte giltig för denna gren." });

            // Resolve/normalise the person: a member id autofills name (+ phone if not supplied).
            if (request.MemberId is > 0)
            {
                var m = _memberService.GetById(request.MemberId.Value);
                if (m != null)
                {
                    if (string.IsNullOrWhiteSpace(request.DisplayName))
                        request.DisplayName = MemberDisplayName(request.MemberId.Value) ?? m.Name;
                    if (string.IsNullOrWhiteSpace(request.Phone) && m.HasProperty("phoneNumber"))
                        request.Phone = m.GetValue<string>("phoneNumber");
                }
            }
            if (string.IsNullOrWhiteSpace(request.DisplayName))
                return Json(new { success = false, message = "Ange en person (medlem eller namn)." });

            try
            {
                var id = _staffing.Save(request, viewer.Id);
                return Json(new { success = true, id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: SaveAssignment failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte spara." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAssignment([FromBody] DeleteStaffAssignmentRequest request)
        {
            if (request == null || request.Id <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });

            var compId = _staffing.GetCompetitionIdFor(request.Id) ?? request.CompetitionId;
            if (!await HasCompetitionAccessAsync(compId))
                return Json(new { success = false, message = "Ingen behörighet" });

            _staffing.Delete(request.Id, compId);
            return Json(new { success = true });
        }

        // ======================= Förberedelser (work-breakdown) =======================

        [HttpGet]
        public async Task<IActionResult> GetWorkBreakdown(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var wb = _work.Build(competitionId, canEdit: true);

            // Enrich with competition context (date anchoring + Fält station-seed availability).
            var discipline = GetDiscipline(competitionId);
            var (compDate, daysUntil) = GetCompDate(competitionId);
            wb.Discipline = discipline;
            wb.CompDate = compDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            wb.DaysUntilComp = daysUntil;
            wb.StationSeed = GetStationSeed(competitionId, discipline);

            return Json(new
            {
                success = true,
                canEdit = wb.CanEdit,
                discipline = wb.Discipline,
                compDate = wb.CompDate,
                daysUntilComp = wb.DaysUntilComp,
                stationSeed = wb.StationSeed,
                compLinks = wb.CompLinks,
                areas = wb.Areas,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkArea([FromBody] SaveWorkAreaRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || string.IsNullOrWhiteSpace(request.Name))
                return Json(new { success = false, message = "Ange ett namn på området." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            if (request.ResponsibleMemberId is > 0 && string.IsNullOrWhiteSpace(request.ResponsibleName))
                request.ResponsibleName = MemberDisplayName(request.ResponsibleMemberId.Value);

            var id = _work.SaveArea(request, viewer.Id);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkArea([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.DeleteArea(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkItem([FromBody] SaveWorkItemRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.WorkAreaId <= 0 || string.IsNullOrWhiteSpace(request.Title))
                return Json(new { success = false, message = "Ange en uppgift." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            if (request.AssignedMemberId is > 0 && string.IsNullOrWhiteSpace(request.AssignedName))
                request.AssignedName = MemberDisplayName(request.AssignedMemberId.Value);

            var id = _work.SaveItem(request, viewer.Id, viewer.Name);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkItem([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.DeleteItem(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetWorkItemStatus([FromBody] SaveWorkItemRequest request)
        {
            if (request == null || request.Id <= 0 || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.SetItemStatus(request.Id, request.CompetitionId, request.Status ?? WorkItemStatus.Planerad, viewer.Id, viewer.Name);
            return Json(new { success = true });
        }

        // ======================= Kommentarer / logg + beroenden (uppgift-tråd) =======================

        [HttpGet]
        public async Task<IActionResult> GetWorkItemThread(int competitionId, int workItemId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var t = _work.GetThread(competitionId, workItemId, canEdit: true);
            return Json(new
            {
                success = t.Success,
                message = t.Message,
                canEdit = t.CanEdit,
                title = t.Title,
                comments = t.Comments,
                blockedBy = t.BlockedBy,
                candidates = t.Candidates,
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWorkItemComment([FromBody] AddWorkItemCommentRequest request)
        {
            if (request == null || request.CompetitionId <= 0 || request.WorkItemId <= 0 || string.IsNullOrWhiteSpace(request.Body))
                return Json(new { success = false, message = "Skriv en kommentar." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var body = request.Body.Trim();
            if (body.Length > 4000) body = body.Substring(0, 4000);
            var id = _work.AddComment(request.CompetitionId, request.WorkItemId, body, viewer.Id, viewer.Name);
            return Json(new { success = true, id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkItemComment([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.DeleteComment(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWorkItemDependency([FromBody] WorkItemDependencyRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var (ok, msg) = _work.AddDependency(request.CompetitionId, request.WorkItemId, request.BlockedByItemId, viewer.Id);
            return Json(new { success = ok, message = msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveWorkItemDependency([FromBody] WorkItemDependencyRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            _work.RemoveDependency(request.Id, request.CompetitionId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedPrepTemplate([FromBody] SeedPrepTemplateRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var discipline = GetDiscipline(request.CompetitionId);
            var (compDate, _) = GetCompDate(request.CompetitionId);
            var added = _work.SeedTemplate(request.CompetitionId, request.Size, discipline, compDate, viewer.Id);
            return Json(new { success = true, added });
        }

        /// <summary>Auto-seed one "Bygg station N" uppgift per configured station (Fältskytte).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SeedStationTasks([FromBody] SeedStationTasksRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var seed = GetStationSeed(request.CompetitionId, GetDiscipline(request.CompetitionId));
            if (seed == null || !seed.Available)
                return Json(new { success = false, message = "Ingen stationskonfiguration hittades." });
            var added = _work.SeedStationTasks(request.CompetitionId, seed.StationCount, viewer.Id);
            return Json(new { success = true, added });
        }

        // ======================= Dokument & länkar (WorkLink) =======================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWorkLink([FromBody] SaveWorkLinkRequest request)
        {
            if (request == null || request.CompetitionId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.Url)) return Json(new { success = false, message = "Ange en länk (URL)." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var url = request.Url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            var title = string.IsNullOrWhiteSpace(request.Title) ? url : request.Title.Trim();

            var id = _work.SaveLink(new WorkLink
            {
                CompetitionId = request.CompetitionId,
                WorkAreaId = request.WorkAreaId is > 0 ? request.WorkAreaId : null,
                WorkItemId = request.WorkItemId is > 0 ? request.WorkItemId : null,
                Title = title.Length > 200 ? title.Substring(0, 200) : title,
                Url = url,
                CreatedByMemberId = viewer.Id,
            });
            return Json(new { success = true, id });
        }

        [HttpPost]
        public async Task<IActionResult> UploadWorkDocument(int competitionId, int? workAreaId, int? workItemId, string? title, IFormFile? file)
        {
            if (competitionId <= 0 || file == null) return Json(new { success = false, message = "Ingen fil vald." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var (ok, err) = _docs.Validate(file.FileName, file.Length);
            if (!ok) return Json(new { success = false, message = err });

            string stored;
            using (var s = file.OpenReadStream()) stored = await _docs.SaveAsync(s, file.FileName);
            var display = string.IsNullOrWhiteSpace(title) ? file.FileName : title.Trim();

            var id = _work.SaveLink(new WorkLink
            {
                CompetitionId = competitionId,
                WorkAreaId = workAreaId is > 0 ? workAreaId : null,
                WorkItemId = workItemId is > 0 ? workItemId : null,
                Title = display.Length > 200 ? display.Substring(0, 200) : display,
                StoredFileName = stored,
                OriginalFileName = file.FileName,
                CreatedByMemberId = viewer.Id,
            });
            return Json(new { success = true, id });
        }

        [HttpGet]
        public async Task<IActionResult> DownloadWorkDocument(int id)
        {
            var link = _work.GetLink(id);
            if (link == null || string.IsNullOrEmpty(link.StoredFileName)) return NotFound();
            if (!await HasCompetitionAccessAsync(link.CompetitionId)) return Forbid();
            var path = _docs.GetFilePath(link.StoredFileName);
            if (path == null) return NotFound();
            var download = string.IsNullOrEmpty(link.OriginalFileName) ? link.StoredFileName : link.OriginalFileName;
            return PhysicalFile(path, PrepDocumentStorage.ContentTypeFor(link.StoredFileName), download);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWorkLink([FromBody] DeleteWorkRequest request)
        {
            if (request == null || request.Id <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            var storedFile = _work.DeleteLink(request.Id, request.CompetitionId);
            if (!string.IsNullOrEmpty(storedFile)) _docs.Delete(storedFile);
            return Json(new { success = true });
        }

        // ======================= Shared: member picker =======================

        [HttpGet]
        public async Task<IActionResult> SearchMembers(int competitionId, string query)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });
            if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                return Json(new { success = true, members = new List<object>() });

            var all = _memberService.GetAll(0, int.MaxValue, out _);
            var matches = all
                .Where(m => m.IsApproved
                    && ((m.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (m.Email ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
                .Take(20)
                .Select(m =>
                {
                    string? clubName = null;
                    var pcid = m.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(pcid) && int.TryParse(pcid, out int clubId))
                        clubName = _clubService.GetClubNameById(clubId);
                    var first = m.GetValue<string>("firstName");
                    var last = m.GetValue<string>("lastName");
                    var displayName = string.IsNullOrWhiteSpace($"{first} {last}".Trim()) ? m.Name : $"{first} {last}".Trim();
                    var phone = m.HasProperty("phoneNumber") ? (m.GetValue<string>("phoneNumber") ?? "") : "";
                    return new { memberId = m.Id, memberName = displayName, clubName, phone };
                })
                .ToList();
            return Json(new { success = true, members = matches });
        }

        // ======================= helpers =======================

        private record Viewer(int Id, string Name);

        private async Task<Viewer?> ResolveViewerAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            var md = _memberService.GetByEmail(current.Email ?? string.Empty);
            if (md == null) return null;
            var name = MemberDisplayName(md.Id) ?? md.Name ?? "";
            return new Viewer(md.Id, name);
        }

        private string? MemberDisplayName(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m == null) return null;
            var first = m.GetValue<string>("firstName") ?? "";
            var last = m.GetValue<string>("lastName") ?? "";
            var name = $"{first} {last}".Trim();
            return string.IsNullOrEmpty(name) ? m.Name : name;
        }

        private string GetDiscipline(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    return comp?.Value("competitionType")?.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        /// <summary>Competition date + whole days until it (negative once passed), for deadline anchoring.</summary>
        private (DateTime? Date, int? DaysUntil) GetCompDate(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    var d = comp?.Value<DateTime?>("competitionDate");
                    if (d.HasValue && d.Value != default)
                        return (d.Value, (int)(d.Value.Date - DateTime.Now.Date).TotalDays);
                }
            }
            catch { }
            return (null, null);
        }

        /// <summary>
        /// For a Fältskytte/MagnumFält comp, whether we can auto-seed station-build tasks: station count
        /// (numberOfSeriesOrStations) + the attached Fältkonfigurator id (from the stationConfig blob meta).
        /// </summary>
        private StationSeedInfo? GetStationSeed(int competitionId, string discipline)
        {
            if (discipline is not ("Faltskytte" or "MagnumFalt")) return null;
            try
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
                var comp = ctx.Content.GetById(competitionId);
                if (comp == null) return null;

                var stationCount = comp.Value<int>("numberOfSeriesOrStations");
                int attachedConfigId = 0;
                var cfgJson = comp.HasProperty("stationConfig") ? comp.Value<string>("stationConfig") : null;
                if (!string.IsNullOrWhiteSpace(cfgJson))
                {
                    try
                    {
                        var jo = Newtonsoft.Json.Linq.JObject.Parse(cfgJson);
                        attachedConfigId = jo.Value<int?>("_attachedConfigId") ?? 0;
                    }
                    catch { /* legacy/non-object blob — leave 0 */ }
                }
                return new StationSeedInfo
                {
                    Available = stationCount > 0,
                    StationCount = stationCount,
                    AttachedConfigId = attachedConfigId,
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// Four-tier competition-staff gate: site admin OR competition manager OR club admin for the
        /// competition's club OR skjutledare for that club; plus regional admin for region-hosted
        /// competitions. Mirrors EventMessageController.HasCompetitionAccessAsync.
        /// </summary>
        private async Task<bool> HasCompetitionAccessAsync(int competitionId)
        {
            if (competitionId <= 0) return false;
            try
            {
                if (await _auth.IsCurrentUserAdminAsync()) return true;
                if (await _auth.IsCompetitionManager(competitionId)) return true;

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return false;
                var comp = ctx.Content.GetById(competitionId);
                if (comp == null) return false;

                var clubId = comp.Value<int>("clubId");
                if (clubId > 0)
                {
                    if (await _auth.IsClubAdminForClub(clubId)) return true;   // includes regional admin for club's region
                    if (await _auth.IsSkjutledareForClub(clubId)) return true;
                }
                else
                {
                    var regionCode = comp.Value<string>("regionalFederation") ?? "";
                    if (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }
    }
}
