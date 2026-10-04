using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Training;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fas B4: klubbadminens träningslista, *Ny träning*, *Nytt schema* och skjutledarbytet.
    ///
    /// <para><b>Behörighet:</b> att skapa och ändra = klubbadmin eller styrelse (sajtadmin alltid).
    /// Att LÄSA listan får även klubbens skjutledare. Att lämna över ett pass får dessutom den som
    /// HAR passet — det är hen som vet att hen inte kan komma.</para>
    /// </summary>
    public class ClubTrainingController : SurfaceController
    {
        private readonly ClubTrainingService _trainings;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoles;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;

        public ClubTrainingController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            ClubTrainingService trainings,
            AdminAuthorizationService auth,
            BoardRoleService boardRoles,
            IMemberManager memberManager,
            IMemberService memberService,
            MemberClubService memberClubs)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _trainings = trainings;
            _auth = auth;
            _boardRoles = boardRoles;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
        }

        private const string Denied = "Du har inte behörighet att hantera klubbens träningar.";

        [HttpGet]
        public async Task<IActionResult> List(int clubId, string? from = null, string? to = null)
        {
            var me = await CurrentMemberIdAsync();
            if (!await CanAdminAsync(clubId, me) && !await _auth.IsSkjutledareForClub(clubId))
                return Json(new { success = false, message = Denied });

            var f = DateTime.TryParse(from, out var fd) ? fd : DateTime.Today.AddDays(-7);
            var t = DateTime.TryParse(to, out var td) ? td : DateTime.Today.AddMonths(6);
            return Json(new
            {
                success = true,
                canEdit = await CanAdminAsync(clubId, me),
                trainings = _trainings.List(clubId, f, t)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save([FromBody] ClubTrainingService.TrainingInput input)
        {
            var me = await CurrentMemberIdAsync();
            if (input == null || !await CanAdminAsync(input.ClubId, me)) return Json(new { success = false, message = Denied });
            var (id, error) = _trainings.Save(input, me);
            return Json(new { success = error == null, message = error ?? "Träningen är sparad.", id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PreviewSchedule([FromBody] ClubTrainingService.ScheduleInput input)
        {
            var me = await CurrentMemberIdAsync();
            if (input == null || !await CanAdminAsync(input.ClubId, me)) return Json(new { success = false, message = Denied });
            var plan = _trainings.Plan(input);
            return Json(new
            {
                success = plan.Error == null,
                message = plan.Error,
                count = plan.Count,
                summary = plan.Summary,
                firstDate = plan.FirstDate,
                lastDate = plan.LastDate,
                capped = plan.Capped
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSchedule([FromBody] ClubTrainingService.ScheduleInput input)
        {
            var me = await CurrentMemberIdAsync();
            if (input == null || !await CanAdminAsync(input.ClubId, me)) return Json(new { success = false, message = Denied });
            var (scheduleId, created, error) = _trainings.CreateSchedule(input, me);
            return Json(new
            {
                success = error == null,
                message = error ?? $"Schemat är skapat med {created} tillfällen.",
                scheduleId,
                created
            });
        }

        public class TrainingIdRequest
        {
            public int TrainingId { get; set; }
            public int? MemberId { get; set; }
            /// <summary>"1" = ställ in, "0" = ångra. Sträng — "1" binder inte till bool.</summary>
            public string? Cancelled { get; set; }
        }

        /// <summary>Lämna över passet. null/0 = ingen utsedd.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSkjutledare([FromBody] TrainingIdRequest req)
        {
            var t = _trainings.Get(req?.TrainingId ?? 0);
            if (t == null) return Json(new { success = false, message = "Träningen finns inte." });
            var me = await CurrentMemberIdAsync();
            var holdsShift = me > 0 && t.SkjutledareMemberId == me;
            if (!holdsShift && !await CanAdminAsync(t.ClubId, me)) return Json(new { success = false, message = Denied });

            // Den nya skjutledaren ska tillhöra klubben — annars kan ett pass lämnas till vem som helst.
            if (req!.MemberId is > 0)
            {
                var m = _memberService.GetById(req.MemberId.Value);
                if (m == null) return Json(new { success = false, message = "Medlemmen finns inte." });
                if (!_memberClubs.IsMemberOfClub(m, t.ClubId))
                    return Json(new { success = false, message = $"{m.Name} är inte medlem i klubben." });
            }
            var error = _trainings.SetSkjutledare(t.Id, req.MemberId);
            var name = req.MemberId is > 0 ? _memberService.GetById(req.MemberId.Value)?.Name : null;
            return Json(new
            {
                success = error == null,
                message = error ?? (name != null ? $"{name} är nu skjutledare." : "Ingen skjutledare är utsedd."),
                skjutledareName = name ?? ""
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetCancelled([FromBody] TrainingIdRequest req)
        {
            var t = _trainings.Get(req?.TrainingId ?? 0);
            if (t == null) return Json(new { success = false, message = "Träningen finns inte." });
            if (!await CanAdminAsync(t.ClubId, await CurrentMemberIdAsync())) return Json(new { success = false, message = Denied });
            var cancel = req!.Cancelled == "1";
            var error = _trainings.SetCancelled(t.Id, cancel);
            return Json(new { success = error == null, message = error ?? (cancel ? "Träningen är inställd." : "Träningen är inte längre inställd.") });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete([FromBody] TrainingIdRequest req)
        {
            var t = _trainings.Get(req?.TrainingId ?? 0);
            if (t == null) return Json(new { success = false, message = "Träningen finns inte." });
            if (!await CanAdminAsync(t.ClubId, await CurrentMemberIdAsync())) return Json(new { success = false, message = Denied });
            var error = _trainings.Delete(t.Id);
            return Json(new { success = error == null, message = error ?? "Träningen är borttagen." });
        }

        private async Task<bool> CanAdminAsync(int clubId, int memberId)
        {
            if (clubId <= 0) return false;
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (await _auth.IsClubAdminForClub(clubId)) return true;
            try { return memberId > 0 && _boardRoles.IsBoardMemberOf(DocumentOwnerType.Club, clubId, memberId); }
            catch { return false; }
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }
    }
}
