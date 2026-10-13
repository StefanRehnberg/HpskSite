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
using HpskSite.Services.Ops;

namespace HpskSite.Controllers
{
    /// <summary>
    /// C3-planen F6: driftskollen under Admin → Underhåll. Bara sajtadministratör. Läser
    /// <c>dbo.RequestErrorLog</c> och <c>dbo.OpsAlert</c>; hälsoläget hämtar ytan direkt från
    /// <c>/health/competitions</c>.
    /// </summary>
    public class OpsController : SurfaceController
    {
        private readonly AdminAuthorizationService _auth;
        private readonly IUmbracoDatabaseFactory _db;
        private readonly RequestErrorFlushService _flush;
        private readonly CriticalAlertService _alerts;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<OpsController> _logger;

        public OpsController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, RequestErrorFlushService flush, CriticalAlertService alerts,
            IMemberManager memberManager, ILogger<OpsController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _db = databaseFactory;
            _flush = flush;
            _alerts = alerts;
            _memberManager = memberManager;
            _logger = logger;
        }

        private const string NotAdmin = "Bara sajtadministratör.";

        public sealed class DayRow
        {
            public DateTime Day { get; set; }
            public int C4xx { get; set; }
            public int C5xx { get; set; }
            public int Bot4xx { get; set; }
            public int Bot5xx { get; set; }
        }

        public sealed class AreaRow
        {
            public int StatusCode { get; set; }
            public string Area { get; set; } = "";
            public bool IsBot { get; set; }
            public int Count { get; set; }
            public string LastPath { get; set; } = "";
            public DateTime LastAt { get; set; }
        }

        /// <summary>Felsvar per dag de senaste <paramref name="days"/> dagarna (högst 120), människor och botar var för sig.</summary>
        [HttpGet]
        public async Task<IActionResult> GetErrorDays(int days = 28)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            days = Math.Clamp(days, 1, RequestErrorFlushService.KeepDays);
            _flush.Flush();
            using var db = _db.CreateDatabase();
            if (db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.RequestErrorLog', 'U')") == null)
                return Json(new { success = false, message = "Tabellen dbo.RequestErrorLog saknas. Kör Migrations/create-ops-tables.sql." });
            var rows = db.Fetch<DayRow>(@"SELECT [Day],
  SUM(CASE WHEN StatusCode < 500 AND IsBot = 0 THEN [Count] ELSE 0 END) AS C4xx,
  SUM(CASE WHEN StatusCode >= 500 AND IsBot = 0 THEN [Count] ELSE 0 END) AS C5xx,
  SUM(CASE WHEN StatusCode < 500 AND IsBot = 1 THEN [Count] ELSE 0 END) AS Bot4xx,
  SUM(CASE WHEN StatusCode >= 500 AND IsBot = 1 THEN [Count] ELSE 0 END) AS Bot5xx
FROM dbo.RequestErrorLog WHERE [Day] >= @0 GROUP BY [Day] ORDER BY [Day] DESC", DateTime.Today.AddDays(-(days - 1)));
            return Json(new { success = true, days = rows.Select(r => new { day = r.Day.ToString("yyyy-MM-dd"), r.C4xx, r.C5xx, r.Bot4xx, r.Bot5xx }) });
        }

        /// <summary>En dags felsvar per område, med senaste adressen.</summary>
        [HttpGet]
        public async Task<IActionResult> GetErrorDay(string day)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            if (!DateTime.TryParse(day, out var d)) return Json(new { success = false, message = "Ogiltigt datum." });
            _flush.Flush();
            using var db = _db.CreateDatabase();
            var rows = db.Fetch<AreaRow>(@"SELECT StatusCode, Area, IsBot, [Count], LastPath, LastAt FROM dbo.RequestErrorLog
WHERE [Day] = @0 ORDER BY IsBot, [Count] DESC", d.Date);
            return Json(new { success = true, rows = rows.Select(r => new { r.StatusCode, r.Area, r.IsBot, r.Count, r.LastPath, lastAt = r.LastAt.ToString("HH:mm") }) });
        }

        /// <summary>De senaste larmen (Critical).</summary>
        [HttpGet]
        public async Task<IActionResult> GetAlerts(int take = 30)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            await _alerts.ProcessAsync(force: false);
            using var db = _db.CreateDatabase();
            if (db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.OpsAlert', 'U')") == null)
                return Json(new { success = false, message = "Tabellen dbo.OpsAlert saknas. Kör Migrations/create-ops-tables.sql." });
            var rows = db.Fetch<AlertView>(@"SELECT TOP (@0) Id, FirstAt, LastAt, Category, [Message], [Count], MailedAt
FROM dbo.OpsAlert ORDER BY LastAt DESC", Math.Clamp(take, 1, 200));
            return Json(new
            {
                success = true,
                alerts = rows.Select(a => new
                {
                    a.Id, firstAt = a.FirstAt.ToString("yyyy-MM-dd HH:mm"), lastAt = a.LastAt.ToString("yyyy-MM-dd HH:mm"),
                    a.Category, a.Message, a.Count, mailedAt = a.MailedAt?.ToString("yyyy-MM-dd HH:mm"),
                }),
            });
        }

        public sealed class AlertView
        {
            public int Id { get; set; }
            public DateTime FirstAt { get; set; }
            public DateTime LastAt { get; set; }
            public string Category { get; set; } = "";
            public string Message { get; set; } = "";
            public int Count { get; set; }
            public DateTime? MailedAt { get; set; }
        }

        /// <summary>
        /// Skickar ett provlarm: loggar en Critical-rad och kör larmet direkt. Visar att hela vägen fungerar —
        /// loggen, tabellen och mejlet — innan den behövs på riktigt.
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTestAlert()
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            var m = await _memberManager.GetCurrentMemberAsync();
            var start = DateTime.Now.AddSeconds(-1);
            // Klockslaget gör raden unik: ett upprepat provlarm ska mejlas igen, inte räknas upp på förra raden.
            _logger.LogCritical("Provlarm från driftskollen ({Name}, {At}). Inget är fel — det här visar att larmet når fram.",
                m?.Name ?? "okänd", DateTime.Now.ToString("HH:mm:ss"));
            await _alerts.ProcessAsync(force: true);

            // Läs utfallet ur tabellen: bakgrundstjänsten kan ha hunnit ta raden ur kön före anropet ovan.
            using var db = _db.CreateDatabase();
            if (db.ExecuteScalar<int?>("SELECT OBJECT_ID('dbo.OpsAlert', 'U')") == null)
                return Json(new { success = false, message = "Provlarmet kunde inte sparas: tabellen dbo.OpsAlert saknas. Kör Migrations/create-ops-tables.sql." });
            var row = db.FirstOrDefault<AlertView>(@"SELECT TOP 1 Id, FirstAt, LastAt, Category, [Message], [Count], MailedAt
FROM dbo.OpsAlert WHERE Category = @0 AND LastAt >= @1 ORDER BY LastAt DESC", typeof(OpsController).FullName, start);
            if (row == null)
                return Json(new { success = false, message = "Provlarmet nådde inte larmet. Loggraden skrevs, men larmet (CriticalAlertSink) fick den inte — se loggen." });
            return Json(new
            {
                success = true,
                mailSent = row.MailedAt != null,
                message = row.MailedAt != null
                    ? "Provlarmet är sparat och mejlat till sajtens administratörsadress."
                    : "Provlarmet är sparat, men mejlet kunde inte skickas. Kontrollera e-postinställningarna (Email:AdminEmail och SMTP).",
            });
        }
    }
}
