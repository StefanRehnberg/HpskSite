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
    /// Fas B3: klubbens väg för att flytta sina gamla träningshändelser till <c>ClubTraining</c> —
    /// ytan är klubbens Admin → Träningar (<c>_TrainingMigrationClub.cshtml</c>), Stefans beslut
    /// 2026-10-07 (klubben vet vilka "Träning"-händelser som egentligen är något annat).
    /// Se <see cref="TrainingMigrationService"/>.
    ///
    /// <para><b>Behörigheten är densamma som för klubbens träningar</b> (<c>ClubTrainingController.
    /// CanAdminAsync</c>): sajtadmin, klubbadmin eller styrelseledamot i klubben. Varje anrop gäller
    /// EN klubb — <c>clubId</c> krävs.</para>
    ///
    /// <para><b>Läsning och skrivning är skilda endpoints.</b> <c>Preview</c>/<c>UndoPreview</c> är GET
    /// och skriver ingenting. <c>Run</c>/<c>Undo</c> skriver bara när <c>apply</c> uttryckligen är "1"
    /// — och bara för de id:n som skickas med.</para>
    /// </summary>
    public class TrainingMigrationController : SurfaceController
    {
        private readonly TrainingMigrationService _migration;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoles;
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
            BoardRoleService boardRoles,
            IMemberManager memberManager,
            IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _migration = migration;
            _auth = auth;
            _boardRoles = boardRoles;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        public class RunRequest
        {
            public int ClubId { get; set; }
            /// <summary>"1" = skriv. Sträng, eftersom "1" inte binder till bool i ASP.NET Core.</summary>
            public string? Apply { get; set; }
            /// <summary>Händelserna som ska flyttas. Krävs för en skarp körning.</summary>
            public List<int>? EventIds { get; set; }
        }

        public class UndoRequest
        {
            public int ClubId { get; set; }
            public string? Apply { get; set; }
            public List<int>? TrainingIds { get; set; }
        }

        private const string Denied = "Du har inte behörighet att flytta klubbens träningar.";

        [HttpGet]
        public async Task<IActionResult> Preview(int clubId)
        {
            var me = await AuthorizedMemberAsync(clubId);
            if (me == null) return Json(new { success = false, message = Denied });
            return Json(Shape(_migration.Run(apply: false, me.Value, clubId)));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Run([FromBody] RunRequest request)
        {
            var clubId = request?.ClubId ?? 0;
            var me = await AuthorizedMemberAsync(clubId);
            if (me == null) return Json(new { success = false, message = Denied });
            if (request?.Apply != "1")
                return Json(Shape(_migration.Run(apply: false, me.Value, clubId)));

            var result = _migration.RunSelected(me.Value, clubId, request.EventIds ?? new List<int>());
            if (result.Refused != null) return Json(new { success = false, applied = false, message = result.Refused });
            return Json(Shape(result));
        }

        [HttpGet]
        public async Task<IActionResult> UndoPreview(int clubId)
        {
            var me = await AuthorizedMemberAsync(clubId);
            if (me == null) return Json(new { success = false, message = Denied });
            return Json(ShapeUndo(_migration.PreviewUndo(clubId)));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Undo([FromBody] UndoRequest request)
        {
            var clubId = request?.ClubId ?? 0;
            var me = await AuthorizedMemberAsync(clubId);
            if (me == null) return Json(new { success = false, message = Denied });
            if (request?.Apply != "1")
                return Json(ShapeUndo(_migration.PreviewUndo(clubId)));

            var result = _migration.UndoSelected(me.Value, clubId, request.TrainingIds ?? new List<int>());
            if (result.Refused != null) return Json(new { success = false, applied = false, message = result.Refused });
            return Json(ShapeUndo(result));
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
            rows = r.Rows,
            kept = r.Kept
        };

        private static object ShapeUndo(TrainingMigrationService.UndoResult r)
        {
            if (r.Refused != null) return new { success = false, applied = false, message = r.Refused };
            return new
            {
                success = true,
                applied = r.Applied,
                restorable = r.Rows.Count(x => x.Action == TrainingMigrationService.ActionRestore),
                restored = r.Rows.Count(x => x.Action == TrainingMigrationService.ActionRestored),
                failed = r.Rows.Count(x => x.Action == TrainingMigrationService.ActionFailed),
                rows = r.Rows
            };
        }

        /// <summary>Medlemmens id om hen får hantera klubbens träningar, annars null. Samma regel som
        /// <c>ClubTrainingController.CanAdminAsync</c> — håll dem lika.</summary>
        private async Task<int?> AuthorizedMemberAsync(int clubId)
        {
            if (clubId <= 0) return null;
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return null;
            var memberId = _memberService.GetByEmail(current.Email)?.Id ?? 0;
            if (memberId <= 0) return null;
            if (await _auth.IsCurrentUserAdminAsync()) return memberId;
            if (await _auth.IsClubAdminForClub(clubId)) return memberId;
            try { return _boardRoles.IsBoardMemberOf(DocumentOwnerType.Club, clubId, memberId) ? memberId : null; }
            catch { return null; }
        }
    }
}
