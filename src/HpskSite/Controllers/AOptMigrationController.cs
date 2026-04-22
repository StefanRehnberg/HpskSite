using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// One-shot migration that rewrites legacy "A_opt" references in Umbraco content
    /// after promoting A_opt to its own weapon class with three levels.
    /// Site-admin gated; idempotent (a second run reports zero changes).
    /// Trigger: POST /umbraco/surface/AOptMigration/Run
    /// </summary>
    public class AOptMigrationController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _authService;

        private const string LegacyId = "A_opt";
        private const string LegacyName = "A Opt";
        private const string ReplacementId = "A_opt_2";
        private const string ReplacementName = "A Opt 2";

        public AOptMigrationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            AdminAuthorizationService authService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _authService = authService;
        }

        // One-shot maintenance endpoint. The action body re-checks site-admin authorization,
        // so antiforgery isn't necessary and would only complicate calling it from DevTools.
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Run()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Access denied — site admin required." });

            var report = new MigrationReport();
            try
            {
                MigrateCompetitionShootingClassIds(report);
                MigrateCompetitionResultMergeConfigs(report);
            }
            catch (Exception ex)
            {
                report.Error = ex.Message;
                return Json(new { success = false, report });
            }

            return Json(new { success = true, report });
        }

        // ── Competitions: shootingClassIds JSON / CSV ───────────────────────

        private void MigrateCompetitionShootingClassIds(MigrationReport report)
        {
            var rootContent = _contentService.GetRootContent().FirstOrDefault();
            if (rootContent == null) return;

            var descendants = _contentService.GetPagedDescendants(rootContent.Id, 0, int.MaxValue, out _);
            foreach (var node in descendants)
            {
                if (node.ContentType.Alias != "competition") continue;
                report.CompetitionsScanned++;

                var raw = node.GetValue<string>("shootingClassIds") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!raw.Contains(LegacyId, StringComparison.OrdinalIgnoreCase)) continue;

                var ids = ParseIds(raw);
                if (!ids.Any(s => string.Equals(s, LegacyId, StringComparison.OrdinalIgnoreCase))) continue;

                var rewritten = ids
                    .Select(s => string.Equals(s, LegacyId, StringComparison.OrdinalIgnoreCase) ? ReplacementId : s)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var newJson = System.Text.Json.JsonSerializer.Serialize(rewritten);
                node.SetValue("shootingClassIds", newJson);

                // Save + Publish per CLAUDE.md memory: Publish() alone does not reliably persist property changes.
                _contentService.Save(node);
                _contentService.Publish(node, Array.Empty<string>());

                report.CompetitionsUpdated++;
            }
        }

        private static List<string> ParseIds(string raw)
        {
            raw = raw.Trim();
            if (raw.StartsWith("["))
            {
                try
                {
                    var arr = JArray.Parse(raw);
                    return arr.Select(t => (t.ToString() ?? string.Empty).Trim())
                              .Where(s => !string.IsNullOrEmpty(s))
                              .ToList();
                }
                catch
                {
                    // fall through to CSV parse
                }
            }
            return raw.Split(',')
                      .Select(s => s.Trim().Trim('"'))
                      .Where(s => !string.IsNullOrEmpty(s))
                      .ToList();
        }

        // ── competitionResult: mergeConfig JSON ─────────────────────────────

        private void MigrateCompetitionResultMergeConfigs(MigrationReport report)
        {
            var rootContent = _contentService.GetRootContent().FirstOrDefault();
            if (rootContent == null) return;

            var descendants = _contentService.GetPagedDescendants(rootContent.Id, 0, int.MaxValue, out _);
            foreach (var node in descendants)
            {
                if (node.ContentType.Alias != "competitionResult") continue;
                if (!node.HasProperty("mergeConfig")) continue;
                report.MergeConfigsScanned++;

                var raw = node.GetValue<string>("mergeConfig") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (!raw.Contains(LegacyId) && !raw.Contains(LegacyName)) continue;

                var rewritten = raw
                    .Replace(LegacyId, ReplacementId)
                    .Replace(LegacyName, ReplacementName);

                if (rewritten == raw) continue;

                node.SetValue("mergeConfig", rewritten);
                _contentService.Save(node);
                _contentService.Publish(node, Array.Empty<string>());
                report.MergeConfigsUpdated++;
            }
        }
    }

    public class MigrationReport
    {
        public int CompetitionsScanned { get; set; }
        public int CompetitionsUpdated { get; set; }
        public int MergeConfigsScanned { get; set; }
        public int MergeConfigsUpdated { get; set; }
        public string? Error { get; set; }
    }
}
