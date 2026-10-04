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
    /// Fas B3: sajtadminens väg för att flytta klubbarnas gamla träningshändelser till
    /// <c>ClubTraining</c>. Se <see cref="TrainingMigrationService"/> för vad som flyttas.
    ///
    /// <para><b>Två endpoints, och skillnaden är hela poängen.</b> <c>Preview</c> är en GET och
    /// skriver ingenting. <c>Run</c> är en POST och skriver bara när <c>apply</c> uttryckligen är
    /// "1" — ett utelämnat värde är en torrkörning, aldrig en skarp körning.</para>
    /// </summary>
    public class TrainingMigrationController : SurfaceController
    {
        private readonly TrainingMigrationService _migration;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public TrainingMigrationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            TrainingMigrationService migration,
            AdminAuthorizationService auth,
            IMemberManager memberManager,
            IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _migration = migration;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        public class RunRequest
        {
            public int ClubId { get; set; }
            /// <summary>"1" = skriv. Sträng, eftersom "1" inte binder till bool i ASP.NET Core.</summary>
            public string? Apply { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> Preview(int clubId = 0)
        {
            var me = await SiteAdminIdAsync();
            if (me == null) return Json(new { success = false, message = "Bara sajtadministratören kan flytta träningar." });
            return Json(Shape(_migration.Run(apply: false, me.Value, clubId)));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run([FromBody] RunRequest request)
        {
            var me = await SiteAdminIdAsync();
            if (me == null) return Json(new { success = false, message = "Bara sajtadministratören kan flytta träningar." });
            var apply = request?.Apply == "1";
            return Json(Shape(_migration.Run(apply, me.Value, request?.ClubId ?? 0)));
        }

        private static object Shape(TrainingMigrationService.Result r) => new
        {
            success = true,
            applied = r.Applied,
            toMigrate = r.ToMigrate,
            skipped = r.Skipped,
            failed = r.Failed,
            participantsMoved = r.ParticipantsMoved,
            bookingsMoved = r.BookingsMoved,
            rows = r.Rows
        };

        private async Task<int?> SiteAdminIdAsync()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return null;
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return null;
            return _memberService.GetByEmail(current.Email)?.Id;
        }
    }
}
