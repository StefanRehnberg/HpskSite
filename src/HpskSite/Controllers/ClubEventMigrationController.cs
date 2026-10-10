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
using HpskSite.Services.Events;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Underhållsfönstret för händelseflytten (sajtstrukturen fas 2d). <b>Bara sajtadministratör.</b>
    /// Ordningen och skälen står på <see cref="ClubEventMigrationService"/>.
    /// </summary>
    public class ClubEventMigrationController : SurfaceController
    {
        private readonly ClubEventMigrationService _migration;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public ClubEventMigrationController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            ClubEventMigrationService migration, AdminAuthorizationService auth, IMemberManager memberManager, IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _migration = migration;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        private async Task<IActionResult?> DenyAsync()
            => await _auth.IsCurrentUserAdminAsync() ? null : Json(new { success = false, message = "Bara sajtadministratör." });

        private async Task<int?> ActorAsync()
        {
            var m = await _memberManager.GetCurrentMemberAsync();
            return m == null ? null : _memberService.GetByKey(m.Key)?.Id;
        }

        [HttpGet]
        public async Task<IActionResult> Preview()
        {
            if (await DenyAsync() is { } deny) return deny;
            return Json(new { success = true, preview = _migration.Preview() });
        }

        [HttpGet]
        public async Task<IActionResult> Verify()
        {
            if (await DenyAsync() is { } deny) return deny;
            var (compared, problems) = _migration.Verify();
            return Json(new { success = true, compared, mismatches = problems.Count, problems = problems.Take(200) });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Freeze(string on)
        {
            if (await DenyAsync() is { } deny) return deny;
            var value = on == "1" || string.Equals(on, "true", StringComparison.OrdinalIgnoreCase);
            _migration.SetWriteFreeze(value, await ActorAsync());
            return Json(new { success = true, writeFrozen = value });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Run()
        {
            if (await DenyAsync() is { } deny) return deny;
            var r = _migration.Apply();
            return Json(new { success = r.Ok, message = r.Message, inserted = r.Inserted, skipped = r.Skipped, legacyUrls = r.LegacyUrls, reseed = r.Reseed, errors = r.Errors });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Switch(string store)
        {
            if (await DenyAsync() is { } deny) return deny;
            var (ok, message, problems) = _migration.Switch(store ?? "", await ActorAsync());
            return Json(new { success = ok, message, problems });
        }

        /// <summary>Tömmer tabellerna inför ett nytt försök. Bara när nodlagringen gäller.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Clear()
        {
            if (await DenyAsync() is { } deny) return deny;
            var (ok, message) = _migration.ClearTables(await ActorAsync());
            return Json(new { success = ok, message });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Unpublish(int batch = 50)
        {
            if (await DenyAsync() is { } deny) return deny;
            var (done, remaining, message) = _migration.UnpublishNodes(batch);
            return Json(new { success = true, unpublished = done, remaining, message });
        }
    }
}
