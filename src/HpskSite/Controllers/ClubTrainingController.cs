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
                // Lånevapen syns bara när klubben har minst ett aktivt lånebart vapen — dialogen säger det.
                loanableWeapons = _trainings.LoanableWeaponCount(clubId),
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
            if (error != null) return Json(new { success = false, message = error, id });
            // "Den här och alla kommande" — frågan ställs i dialogen när serien har senare tillfällen.
            var following = string.Equals(input.Scope, "following", StringComparison.OrdinalIgnoreCase) ? _trainings.ApplyToFollowing(id) : 0;
            return Json(new
            {
                success = true, id, following,
                message = following > 0 ? $"Träningen är sparad, och {following} kommande tillfällen ändrades likadant." : "Träningen är sparad."
            });
        }

        /// <summary>Träningar att kopiera från (kalenderns läge "Kopiera in träning").</summary>
        [HttpGet]
        public async Task<IActionResult> CopySources(int clubId)
        {
            if (!await CanAdminAsync(clubId, await CurrentMemberIdAsync())) return Json(new { success = false, message = Denied });
            return Json(new { success = true, trainings = _trainings.CopySources(clubId) });
        }

        public class CopyDatesRequest
        {
            public int SourceId { get; set; }
            public List<string>? Dates { get; set; }
        }

        /// <summary>Kopierar en träning till valda datum (en dag i taget från kalendern, eller flera).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyToDates([FromBody] CopyDatesRequest req)
        {
            var src = _trainings.Get(req?.SourceId ?? 0);
            if (src == null) return Json(new { success = false, message = "Träningen finns inte." });
            var me = await CurrentMemberIdAsync();
            if (!await CanAdminAsync(src.ClubId, me)) return Json(new { success = false, message = Denied });
            var dates = (req!.Dates ?? new()).Select(d => DateTime.TryParseExact(d, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var x) ? x : (DateTime?)null).Where(d => d != null)
                .Select(d => (d!.Value, (int?)null)).ToList();
            var r = _trainings.CopyTo(src.Id, dates, me);
            return Json(CopyJson(r));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PreviewCopyPeriod([FromBody] ClubTrainingService.CopyPeriodInput input)
        {
            var src = _trainings.Get(input?.SourceId ?? 0);
            if (src == null || !await CanAdminAsync(src.ClubId, await CurrentMemberIdAsync())) return Json(new { success = false, message = Denied });
            var (occ, already, error) = _trainings.PlanCopyPeriod(input!);
            if (error != null) return Json(new { success = false, message = error });
            var newOnes = occ.Count - already.Count;
            return Json(new
            {
                success = true,
                count = newOnes,
                already = already.Count,
                firstDate = occ.FirstOrDefault()?.Date.ToString("yyyy-MM-dd"),
                lastDate = occ.LastOrDefault()?.Date.ToString("yyyy-MM-dd"),
                capped = occ.Count >= HpskSite.Services.Training.TrainingSchedulePlanner.MaxOccasions
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyPeriod([FromBody] ClubTrainingService.CopyPeriodInput input)
        {
            var src = _trainings.Get(input?.SourceId ?? 0);
            var me = await CurrentMemberIdAsync();
            if (src == null || !await CanAdminAsync(src.ClubId, me)) return Json(new { success = false, message = Denied });
            var (occ, _, error) = _trainings.PlanCopyPeriod(input!);
            if (error != null) return Json(new { success = false, message = error });
            var r = _trainings.CopyTo(src.Id, occ.Select(o => (o.Date, o.SkjutledareMemberId)), me);
            return Json(CopyJson(r));
        }

        private static object CopyJson(ClubTrainingService.CopyResult r) => new
        {
            success = r.Error == null && r.CreatedIds.Count > 0,
            created = r.CreatedIds.Count,
            createdIds = r.CreatedIds,
            createdDates = r.CreatedDates,
            skippedDates = r.SkippedDates,
            message = r.Error
                ?? (r.CreatedIds.Count == 0 ? "Inget nytt tillfälle — träningen finns redan på de dagarna."
                    : (r.CreatedIds.Count == 1 ? "Ett tillfälle är inlagt." : $"{r.CreatedIds.Count} tillfällen är inlagda.")
                      + (r.SkippedDates.Count > 0 ? $" {r.SkippedDates.Count} dagar hade redan träningen och hoppades över." : ""))
        };

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

        public class DeleteManyRequest
        {
            public int ClubId { get; set; }
            public List<int>? Ids { get; set; }
            /// <summary>Alternativ till Ids: en träning vars serie tas bort.</summary>
            public int SeriesOf { get; set; }
            /// <summary>"following" = bara från och med träningen, annars hela serien.</summary>
            public string? Scope { get; set; }
        }

        /// <summary>Tar bort flera träningar (markerade, eller en hel serie). Tillfällen med anmälda
        /// eller lånevapen hoppas över och namnges i svaret.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMany([FromBody] DeleteManyRequest req)
        {
            if (req == null) return Json(new { success = false, message = "Inget att ta bort." });
            var clubId = req.ClubId;
            List<int> ids;
            if (req.SeriesOf > 0)
            {
                var t = _trainings.Get(req.SeriesOf);
                if (t == null) return Json(new { success = false, message = "Träningen finns inte." });
                clubId = t.ClubId;
                ids = _trainings.SeriesIds(t.Id, string.Equals(req.Scope, "following", StringComparison.OrdinalIgnoreCase));
            }
            else ids = req.Ids ?? new();
            if (!await CanAdminAsync(clubId, await CurrentMemberIdAsync())) return Json(new { success = false, message = Denied });
            if (ids.Count == 0) return Json(new { success = false, message = "Inga träningar valda." });

            var r = _trainings.DeleteMany(clubId, ids);
            var msg = r.DeletedIds.Count == 0 ? "Ingen träning togs bort."
                : r.DeletedIds.Count == 1 ? "1 träning är borttagen." : $"{r.DeletedIds.Count} träningar är borttagna.";
            if (r.Blocked.Count > 0)
                msg += $" {r.Blocked.Count} har anmälda eller bokade lånevapen och togs inte bort — ställ in dem i stället.";
            return Json(new { success = r.DeletedIds.Count > 0, deleted = r.DeletedIds.Count, blocked = r.Blocked, message = msg });
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
