using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>C3-planen F5: seriens värd in i omgångarna. Bara sajtadministratör. Se tjänsten.</summary>
    public class CompetitionHostBackfillController : SurfaceController
    {
        private readonly CompetitionHostBackfillService _backfill;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public CompetitionHostBackfillController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CompetitionHostBackfillService backfill, AdminAuthorizationService auth, IMemberManager memberManager, IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _backfill = backfill;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        [HttpGet]
        public async Task<IActionResult> Preview()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = "Bara sajtadministratör." });
            return Json(new { success = true, rows = _backfill.Preview() });
        }

        /// <summary><paramref name="ids"/> = kommaseparerade tävlings-id:n ur förhandsgranskningen.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Apply(string ids)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = "Bara sajtadministratör." });
            var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var i) ? i : 0).Where(i => i > 0).ToList();
            if (list.Count == 0) return Json(new { success = false, message = "Inga tävlingar valda." });
            var m = await _memberManager.GetCurrentMemberAsync();
            var actor = m == null ? (int?)null : _memberService.GetByKey(m.Key)?.Id;
            var result = _backfill.Apply(list, actor);
            return Json(new
            {
                success = result.All(r => r.Ok),
                results = result.Select(r => new { id = r.Id, ok = r.Ok, message = r.Message, newUrl = r.NewUrl }),
            });
        }
    }
}
