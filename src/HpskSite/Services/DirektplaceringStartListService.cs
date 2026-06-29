using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Builds and persists the auto-generated start list for direktplacering / Egenbokning
    /// competitions. Extracted from CompetitionController so other controllers (RegistrationAdmin
    /// late registrations / edits, future bulk imports) can keep the start list in sync without
    /// duplicating the rendering logic.
    /// </summary>
    public class DirektplaceringStartListService
    {
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly ILogger<DirektplaceringStartListService> _logger;

        public DirektplaceringStartListService(
            IContentService contentService,
            ClubService clubService,
            ILogger<DirektplaceringStartListService> logger)
        {
            _contentService = contentService;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>
        /// Recomputes the precisionStartList document under the competition based on the
        /// current set of registrations. Best-effort — failures are logged but do not throw,
        /// because the calling write (registration save) has already succeeded.
        /// </summary>
        public void Regenerate(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return;

                var dpConfig = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
                if (dpConfig == null) return;

                Regenerate(competitionId, competition, dpConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to regenerate direktplacering start list for {CompId}", competitionId);
            }
        }

        public void Regenerate(int competitionId, IContent competition, DirektplaceringConfig dpConfig)
        {
            var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub"
                || c.Name.Contains("Anmälningar")
                || c.Name.Contains("Registration"));

            var registrationDocs = registrationsHub != null
                ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration").ToList()
                : new List<IContent>();

            var shootersByTeam = new Dictionary<int, List<ShooterRow>>();
            foreach (var team in dpConfig.Teams)
                shootersByTeam[team.TeamNumber] = new List<ShooterRow>();

            // Process registrations in the order they were created so that the position a
            // shooter picked at registration is preserved (first-come, first-served within a
            // team). Sorting by name here would re-shuffle everyone whenever a new shooter
            // with an alphabetically-earlier name registers — the Egenbokning bug.
            registrationDocs = registrationDocs.OrderBy(c => c.CreateDate).ThenBy(c => c.Id).ToList();
            var sortSeq = 0;

            foreach (var reg in registrationDocs)
            {
                var classesJson = reg.GetValue<string>("shootingClasses");
                if (string.IsNullOrWhiteSpace(classesJson)) continue;
                var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(classesJson);
                var memberName = reg.GetValue<string>("memberName") ?? "Okänd";
                var memberId = reg.GetValue<int>("memberId");
                var clubName = "Okänd förening";
                var clubId = reg.GetValue<int>("clubId");
                if (clubId > 0) clubName = _clubService.GetClubNameById(clubId) ?? "Okänd förening";

                foreach (var entry in classes)
                {
                    if (!entry.TeamNumber.HasValue) continue;
                    if (!shootersByTeam.ContainsKey(entry.TeamNumber.Value))
                        shootersByTeam[entry.TeamNumber.Value] = new List<ShooterRow>();
                    shootersByTeam[entry.TeamNumber.Value].Add(new ShooterRow
                    {
                        Name = memberName,
                        Club = clubName,
                        WeaponClass = entry.Class,
                        MemberId = memberId,
                        SortOrder = sortSeq++
                    });
                }
            }

            var teams = dpConfig.Teams.Select(team =>
            {
                var shooters = shootersByTeam.TryGetValue(team.TeamNumber, out var s) ? s : new List<ShooterRow>();
                var pos = 0;
                return new
                {
                    TeamNumber = team.TeamNumber,
                    StartTime = team.StartTime,
                    EndTime = team.EndTime,
                    ShooterCount = shooters.Count,
                    WeaponClasses = shooters.Select(sh => sh.WeaponClass).Distinct().OrderBy(c => c).ToList(),
                    Shooters = shooters.OrderBy(sh => sh.SortOrder).Select(sh =>
                    {
                        pos++;
                        return new { Position = pos, sh.Name, sh.Club, sh.WeaponClass, sh.MemberId };
                    }).ToList()
                };
            }).ToList();

            var config = new
            {
                Settings = new
                {
                    Format = dpConfig.AllowMixedClasses ? "Mixade Skjutlag" : "En vapengrupp per Skjutlag",
                    MaxShootersPerTeam = dpConfig.Teams.Any() ? dpConfig.Teams.Max(t => t.Positions) : 30,
                    FirstStartTime = dpConfig.Teams.FirstOrDefault()?.StartTime ?? "09:00",
                    Generated = DateTime.Now
                },
                Teams = teams
            };

            var configJson = System.Text.Json.JsonSerializer.Serialize(config);

            var existingStartList = competitionChildren.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");
            IContent startList = existingStartList ?? _contentService.Create("Startlista", competition.Id, "precisionStartList");

            var html = new System.Text.StringBuilder();
            html.AppendLine("<div class='start-list-content'>");
            html.AppendLine($"<h3 class='competition-title'>{System.Net.WebUtility.HtmlEncode(competition.Name ?? "")}</h3>");
            foreach (var team in teams)
            {
                var timeStr = !string.IsNullOrEmpty(team.EndTime) ? $"{team.StartTime}-{team.EndTime}" : team.StartTime;
                html.AppendLine($"<h3>Skjutlag: {team.TeamNumber} Tid (ca): {timeStr} ({team.ShooterCount} st)</h3>");
                html.AppendLine("<table class='table table-striped'>");
                html.AppendLine("<thead><tr><th>Plats</th><th>Namn</th><th>Förening</th><th>Vapengrupp</th></tr></thead>");
                html.AppendLine("<tbody>");
                foreach (var shooter in team.Shooters)
                {
                    html.AppendLine($"<tr><td>{shooter.Position}</td><td>{System.Net.WebUtility.HtmlEncode(shooter.Name)}</td><td>{System.Net.WebUtility.HtmlEncode(shooter.Club)}</td><td>{shooter.WeaponClass}</td></tr>");
                }
                html.AppendLine("</tbody></table><br>");
            }
            html.AppendLine("</div>");

            startList.SetValue("competitionId", competitionId);
            startList.SetValue("teamFormat", dpConfig.AllowMixedClasses ? "Mixade Skjutlag" : "En vapengrupp per Skjutlag");
            startList.SetValue("generatedDate", DateTime.Now);
            startList.SetValue("generatedBy", "Egenbokning (auto)");
            startList.SetValue("notes", "Autogenererad vid anmälan");
            startList.SetValue("isOfficialStartList", true);
            startList.SetValue("configurationData", configJson);
            startList.SetValue("startListContent", html.ToString());

            try { _contentService.Save(startList); }
            catch (Exception ex) when (IsDocumentUrlTimeout(ex))
            {
                _logger.LogWarning("Start list saved but URL segment rebuild timed out (non-critical)");
            }

            try { _contentService.Publish(startList, new[] { "*" }, -1); }
            catch { /* publish is non-critical */ }

            _logger.LogInformation("Auto-updated Egenbokning start list for competition {CompId} with {Teams} teams",
                competitionId, teams.Count);
        }

        /// <summary>
        /// Compute remaining capacity per team across the competition's registrations. Used
        /// by walk-in / edit endpoints to refuse over-booking before the JSON is written.
        /// Optionally exclude one registration's contribution (when re-saving an existing
        /// registration, its old assignments shouldn't double-count against the new ones).
        /// </summary>
        public Dictionary<int, int> GetTeamUsage(int competitionId, int? excludeRegistrationId = null)
        {
            var usage = new Dictionary<int, int>();

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return usage;

            var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (registrationsHub == null) return usage;

            var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration");

            foreach (var reg in registrations)
            {
                if (excludeRegistrationId.HasValue && reg.Id == excludeRegistrationId.Value) continue;
                var json = reg.GetValue<string>("shootingClasses");
                if (string.IsNullOrWhiteSpace(json)) continue;
                var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(json);
                foreach (var entry in classes)
                {
                    if (!entry.TeamNumber.HasValue) continue;
                    var t = entry.TeamNumber.Value;
                    usage[t] = usage.GetValueOrDefault(t) + 1;
                }
            }

            return usage;
        }

        private sealed class ShooterRow
        {
            public string Name { get; set; } = "";
            public string Club { get; set; } = "";
            public string WeaponClass { get; set; } = "";
            public int MemberId { get; set; }
            public int SortOrder { get; set; }
        }

        private static bool IsDocumentUrlTimeout(Exception ex)
        {
            var inner = ex is AggregateException agg ? agg.InnerException : ex;
            if (inner is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Number == -2)
            {
                return ex.ToString().Contains("DocumentUrlRepository") || ex.ToString().Contains("DocumentUrlService");
            }
            return false;
        }
    }
}
