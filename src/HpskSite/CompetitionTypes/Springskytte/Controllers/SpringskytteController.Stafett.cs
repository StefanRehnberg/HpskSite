using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.Helpers;
using HpskSite.Models;
using HpskSite.Services;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Springskytte.Controllers
{
    /// <summary>
    /// Springskytte STAFETT (relay) start lists + team results.
    ///
    /// Isolated from the individual flow because the rulebook makes stafett a third scoring model
    /// (SHB Del L): mass start ("gemensam start", L.6.1.3.2), elapsed clock (L.6.11.3 "tiden räknas
    /// från start till målgång ... det lag som kommer först i mål är segrare"), and misses are physical
    /// straffrundor already inside the elapsed time. So: no interval time-engine on the start list, and
    /// results are one elapsed-time row per team — NOT the individual per-shooter table, NOT the regular
    /// sum-of-members path (CompetitionTeamService.CalculateTeamResultsAsync, which ignores IsRelay).
    ///
    /// Storage reuses the precisionStartList child node, tagged teamFormat="SpringskytteStafett" and
    /// carrying a SpringskytteStafettStartListConfig (Teams, not Starters) in configurationData.
    /// </summary>
    public partial class SpringskytteController
    {
        public const string StafettTeamFormat = "SpringskytteStafett";

        // ===== STAFETT: relay teams (for the class picker + result-entry surface) =====

        [HttpGet]
        public async Task<IActionResult> GetSpringskytteStafettTeams(int competitionId)
        {
            try
            {
                if (competitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                var teams = await _teamService.GetTeamsForCompetitionAsync(competitionId);
                var relay = teams.Where(t => t.Team.IsRelay)
                    .OrderBy(t => t.Team.TeamClass)
                    .ThenBy(t => t.Team.TeamName, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new
                    {
                        teamId = t.Team.Id,
                        teamName = t.Team.TeamName,
                        stafettClass = t.Team.TeamClass,
                        club = t.ClubName,
                        members = BuildLegMembers(t)
                    })
                    .ToList();

                var classes = relay.Select(r => r.stafettClass).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();

                return Json(new { success = true, teams = relay, classes });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte stafett teams for {Comp}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== STAFETT: start list generation (mass start) =====

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateSpringskytteStafettStartList([FromBody] SpringskytteStafettStartListRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var allTeams = await _teamService.GetTeamsForCompetitionAsync(request.CompetitionId);
                var relayTeams = allTeams.Where(t => t.Team.IsRelay).ToList();
                if (!relayTeams.Any())
                    return Json(new { success = false, message = "Inga stafettlag hittades. Skapa stafettlag under Anmälningar först." });

                // Filter by chosen stafett class(es)
                var coveredClasses = request.CoveredClasses?.Where(c => !string.IsNullOrWhiteSpace(c)).ToList() ?? new List<string>();
                if (coveredClasses.Any())
                {
                    var coveredSet = new HashSet<string>(coveredClasses, StringComparer.OrdinalIgnoreCase);
                    relayTeams = relayTeams.Where(t => coveredSet.Contains(t.Team.TeamClass?.Trim() ?? "")).ToList();
                    if (!relayTeams.Any())
                        return Json(new { success = false, message = "Inga stafettlag matchar de valda klasserna." });
                }

                // Validate the common start time
                if (!TimeSpan.TryParse(request.CommonStartTime, out var commonStart))
                    return Json(new { success = false, message = "Ogiltig starttid. Använd HH:mm." });
                var commonStartStr = commonStart.ToString(@"hh\:mm\:ss");

                // Mass start: every team shares the common start time; order by class then team name.
                var ordered = relayTeams
                    .OrderBy(t => t.Team.TeamClass, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Team.TeamName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var teamEntries = new List<SpringskytteStafettStartListEntry>();
                int startOrder = 1;
                foreach (var t in ordered)
                {
                    teamEntries.Add(new SpringskytteStafettStartListEntry
                    {
                        StartOrder = startOrder++,
                        StartTime = commonStartStr,
                        TeamId = t.Team.Id,
                        TeamName = t.Team.TeamName,
                        Club = t.ClubName,
                        StafettClass = t.Team.TeamClass,
                        Members = BuildLegMembers(t)
                    });
                }

                var listName = !string.IsNullOrWhiteSpace(request.ListName) ? request.ListName.Trim() : "Stafett";
                var newSlug = SlugHelper.Slugify(listName);
                if (string.IsNullOrEmpty(newSlug))
                    return Json(new { success = false, message = "Ogiltigt listnamn. Använd bokstäver eller siffror." });

                // Unique slug across ALL start lists (individual + stafett) — both types set node.Name to
                // the list name, and they share the public /startlista/{comp}/{slug} URL space.
                var collision = _contentService.GetPagedChildren(competition.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "precisionStartList")
                    .Where(c => !(request.ExistingNodeId.HasValue && c.Id == request.ExistingNodeId.Value))
                    .Any(c => string.Equals(SlugHelper.Slugify(c.Name ?? ""), newSlug, StringComparison.OrdinalIgnoreCase));
                if (collision)
                    return Json(new { success = false, message = $"Det finns redan en startlista med namnet \"{listName}\" (eller ett namn som ger samma webbadress). Välj ett unikt namn." });

                var config = new SpringskytteStafettStartListConfig
                {
                    TeamFormat = StafettTeamFormat,
                    CommonStartTime = request.CommonStartTime,
                    ListName = listName,
                    ListDate = (request.ListDate ?? "").Trim(),
                    CoveredClasses = coveredClasses,
                    Teams = teamEntries
                };

                Umbraco.Cms.Core.Models.IContent? node = null;
                if (request.ExistingNodeId.HasValue && request.ExistingNodeId.Value > 0)
                {
                    node = _contentService.GetById(request.ExistingNodeId.Value);
                    if (node == null || node.ParentId != competition.Id)
                        return Json(new { success = false, message = "Startlistan hittades inte." });
                }
                if (node == null)
                {
                    var contentType = _contentTypeService.Get("precisionStartList");
                    if (contentType == null)
                        return Json(new { success = false, message = "Dokumenttypen precisionStartList saknas." });
                    node = _contentService.Create(listName, competition, contentType.Alias);
                }

                node.Name = listName;
                node.SetValue("configurationData", JsonConvert.SerializeObject(config));
                node.SetValue("teamFormat", StafettTeamFormat);
                node.SetValue("generatedDate", DateTime.Now);
                node.SetValue("startListContent", BuildStafettStartListHtml(config));
                _contentService.Save(node);
                _contentService.Publish(node, new[] { "*" });

                _logger.LogInformation("Generated Springskytte STAFETT start list '{ListName}' for CompetitionId={CompetitionId}, {Count} teams, NodeId={NodeId}",
                    listName, request.CompetitionId, teamEntries.Count, node.Id);

                return Json(new
                {
                    success = true,
                    message = $"Stafett-startlista \"{listName}\" genererad med {teamEntries.Count} lag.",
                    nodeId = node.Id,
                    teams = teamEntries
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating Springskytte stafett start list");
                return Json(new { success = false, message = "Ett fel uppstod vid generering av stafett-startlista." });
            }
        }

        [HttpGet]
        public IActionResult GetSpringskytteStafettStartLists(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lists = new List<object>();

                foreach (var node in _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                             .Where(c => c.ContentType.Alias == "precisionStartList"))
                {
                    var json = node.GetValue<string>("configurationData");
                    if (string.IsNullOrEmpty(json)) continue;
                    if (!IsStafettConfig(json)) continue;

                    SpringskytteStafettStartListConfig? cfg = null;
                    try { cfg = JsonConvert.DeserializeObject<SpringskytteStafettStartListConfig>(json); } catch { }
                    if (cfg == null) continue;

                    var listName = !string.IsNullOrWhiteSpace(cfg.ListName) ? cfg.ListName : (node.Name ?? "Stafett");
                    var baseSlug = SlugHelper.Slugify(listName);
                    if (string.IsNullOrEmpty(baseSlug)) baseSlug = "lista-" + node.Id;
                    var slug = baseSlug;
                    var n = 2;
                    while (!usedSlugs.Add(slug)) slug = $"{baseSlug}-{n++}";

                    lists.Add(new
                    {
                        nodeId = node.Id,
                        listName,
                        slug,
                        listDate = cfg.ListDate ?? "",
                        commonStartTime = cfg.CommonStartTime ?? "10:00",
                        coveredClasses = cfg.CoveredClasses ?? new List<string>(),
                        teams = cfg.Teams ?? new List<SpringskytteStafettStartListEntry>(),
                        teamCount = cfg.Teams?.Count ?? 0,
                        generatedDate = node.GetValue<DateTime?>("generatedDate")?.ToString("yyyy-MM-dd HH:mm") ?? "",
                        isOfficial = node.HasProperty("isOfficialStartList") && node.GetValue<bool>("isOfficialStartList")
                    });
                }

                return Json(new { success = true, lists });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte stafett start lists for {Comp}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== STAFETT: result entry (one elapsed-time row per team) =====

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSpringskytteStafettResult([FromBody] SpringskytteStafettResultRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.TeamId <= 0)
                    return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Ogiltig begäran." });

                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Åtkomst nekad." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition != null && competition.GetValue<bool>("isExternal"))
                    return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Extern tävling - resultat kan inte registreras." });

                // Resolve the team (must be a relay team in this competition) for its stafett class.
                CompetitionTeamDto? team;
                using (var lookupDb = _umbracoDatabaseFactory.CreateDatabase())
                {
                    team = await lookupDb.FirstOrDefaultAsync<CompetitionTeamDto>(
                        "WHERE Id = @0", request.TeamId);
                }
                if (team == null || team.CompetitionId != request.CompetitionId)
                    return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Laget hittades inte." });
                if (!team.IsRelay)
                    return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Laget är inte ett stafettlag." });

                var (startOrder, startTime) = FindStafettStartInfo(competition, request.TeamId);

                // Resolve elapsed time: explicit seconds → MM:SS input → finish-time − common start.
                decimal? elapsed = request.ElapsedSeconds;
                if (elapsed == null && !string.IsNullOrWhiteSpace(request.ElapsedInput))
                {
                    elapsed = _scoringService.ParseSprintTime(request.ElapsedInput);
                    if (elapsed == null)
                        return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Ogiltigt tidsformat. Använd MM:SS eller H:MM:SS." });
                }
                if (elapsed == null && !string.IsNullOrWhiteSpace(request.FinishTimeInput))
                {
                    var finish = _scoringService.ParseSprintTime(request.FinishTimeInput);
                    if (finish == null)
                        return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Ogiltigt måltidsformat. Använd HH:MM:SS." });
                    if (string.IsNullOrWhiteSpace(startTime))
                        return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Starttid saknas — generera stafett-startlista först." });
                    var start = _scoringService.ParseSprintTime(startTime);
                    if (start == null)
                        return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Kunde inte tolka lagets starttid." });
                    elapsed = finish.Value - start.Value;
                    if (elapsed < 0)
                        return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Måltid är före starttid — kontrollera tiderna." });
                }

                // A status (DNS/DNF) clears the elapsed time, mirroring the individual flow.
                var effectiveElapsed = !string.IsNullOrEmpty(request.Status) ? (decimal?)null : elapsed;

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                int enteredBy = currentMember != null ? int.Parse(currentMember.Id) : 0;
                var now = DateTime.Now;

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                using var transaction = db.GetTransaction();

                var mergeSql = @"
                    MERGE INTO [SpringskytteStafettResultEntry] AS target
                    USING (SELECT @0 AS CompetitionId, @1 AS TeamId) AS source
                    ON target.CompetitionId = source.CompetitionId AND target.TeamId = source.TeamId
                    WHEN MATCHED THEN
                        UPDATE SET StafettClass = @2, StartOrder = @3, StartTime = @4,
                                   ElapsedSeconds = @5, PenaltyLoops = @6, Status = @7,
                                   EnteredBy = @8, LastModified = @9
                    WHEN NOT MATCHED THEN
                        INSERT (CompetitionId, TeamId, StafettClass, StartOrder, StartTime,
                                ElapsedSeconds, PenaltyLoops, Status, EnteredBy, EnteredAt, LastModified)
                        VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @9)
                    OUTPUT INSERTED.Id;";

                var savedId = await db.ExecuteScalarAsync<int>(mergeSql,
                    request.CompetitionId,                              // @0
                    request.TeamId,                                     // @1
                    team.TeamClass ?? "",                               // @2
                    startOrder,                                         // @3
                    (object?)startTime ?? DBNull.Value,                 // @4
                    (object?)effectiveElapsed ?? DBNull.Value,          // @5
                    (object?)request.PenaltyLoops ?? DBNull.Value,      // @6
                    (object?)request.Status ?? DBNull.Value,            // @7
                    enteredBy,                                          // @8
                    now);                                               // @9

                transaction.Complete();

                return Json(new SpringskytteStafettResultResponse
                {
                    Success = true,
                    Message = "Resultat sparat.",
                    ResultId = savedId,
                    ElapsedSeconds = effectiveElapsed,
                    ElapsedTimeDisplay = SpringskytteStafettTeamResult.FormatTime(effectiveElapsed)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Springskytte stafett result for {Comp}/{Team}", request?.CompetitionId, request?.TeamId);
                return Json(new SpringskytteStafettResultResponse { Success = false, Message = "Ett fel uppstod vid sparning av resultat." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSpringskytteStafettResult([FromBody] SpringskytteStafettDeleteResultRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0 || request.TeamId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var rows = await db.ExecuteAsync(
                    "DELETE FROM SpringskytteStafettResultEntry WHERE CompetitionId = @0 AND TeamId = @1",
                    request.CompetitionId, request.TeamId);

                return Json(new { success = true, message = "Resultat borttaget.", rowsDeleted = rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Springskytte stafett result");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== STAFETT: ranked results =====

        [HttpGet]
        public async Task<IActionResult> GetSpringskytteStafettResults(int competitionId)
        {
            try
            {
                var allTeams = await _teamService.GetTeamsForCompetitionAsync(competitionId);
                var relayTeams = allTeams.Where(t => t.Team.IsRelay).ToList();
                if (!relayTeams.Any())
                    return Json(new { success = true, classGroups = new List<object>() });

                List<SpringskytteStafettResultEntry> entries;
                using (var db = _umbracoDatabaseFactory.CreateDatabase())
                {
                    entries = await db.FetchAsync<SpringskytteStafettResultEntry>(
                        "WHERE CompetitionId = @0", competitionId);
                }
                var resultByTeam = entries.ToDictionary(e => e.TeamId, e => e);

                // Build display teams (every relay team; those with a valid elapsed time get ranked).
                var displayTeams = relayTeams.Select(t =>
                {
                    resultByTeam.TryGetValue(t.Team.Id, out var r);
                    return new SpringskytteStafettTeamResult
                    {
                        TeamId = t.Team.Id,
                        TeamName = t.Team.TeamName,
                        Club = t.ClubName,
                        StafettClass = t.Team.TeamClass,
                        StartOrder = r?.StartOrder ?? 0,
                        StartTime = r?.StartTime,
                        ElapsedSeconds = r?.ElapsedSeconds,
                        PenaltyLoops = r?.PenaltyLoops,
                        Status = r?.Status,
                        Members = BuildLegMembers(t)
                    };
                }).ToList();

                var classGroups = displayTeams
                    .GroupBy(t => t.StafettClass ?? "")
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        // Ranked = finished teams (has elapsed, no DNS/DNF), lowest elapsed first.
                        var ranked = g.Where(t => t.ElapsedSeconds != null && string.IsNullOrEmpty(t.Status))
                            .OrderBy(t => t.ElapsedSeconds)
                            .ToList();
                        for (int i = 0; i < ranked.Count; i++) ranked[i].Rank = i + 1;

                        // Unfinished/status teams after, DNS/DNF last.
                        var rest = g.Where(t => t.ElapsedSeconds == null || !string.IsNullOrEmpty(t.Status))
                            .OrderBy(t => t.Status == "DNS" ? 2 : t.Status == "DNF" ? 1 : 0)
                            .ThenBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        var teams = ranked.Concat(rest).Select(t => new
                        {
                            teamId = t.TeamId,
                            teamName = t.TeamName,
                            club = t.Club,
                            stafettClass = t.StafettClass,
                            rank = t.Rank,
                            elapsedSeconds = t.ElapsedSeconds,
                            elapsedDisplay = t.ElapsedTimeDisplay,
                            penaltyLoops = t.PenaltyLoops,
                            startTime = t.StartTime,
                            status = t.Status,
                            members = t.Members.Select(m => new { m.MemberId, m.Name, m.LegNumber, m.IsSpare }).ToList()
                        }).ToList();

                        return new { className = g.Key, teams };
                    })
                    .ToList();

                return Json(new { success = true, classGroups, isOfficial = await GetStafettResultsOfficialAsync(competitionId) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Springskytte stafett results for {Comp}", competitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ===== STAFETT: publish results (official flag → public /stafettresultat/{comp}) =====

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PublishSpringskytteStafettResults([FromBody] SpringskytteStafettPublishRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begäran." });
                if (!await HasCompetitionAccess(request.CompetitionId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                int by = currentMember != null ? int.Parse(currentMember.Id) : 0;

                using var db = _umbracoDatabaseFactory.CreateDatabase();
                await db.ExecuteAsync(@"
                    MERGE INTO [SpringskytteStafettResultPublish] AS target
                    USING (SELECT @0 AS CompetitionId) AS source
                    ON target.CompetitionId = source.CompetitionId
                    WHEN MATCHED THEN UPDATE SET IsOfficial = @1, PublishedDate = @2, PublishedBy = @3
                    WHEN NOT MATCHED THEN INSERT (CompetitionId, IsOfficial, PublishedDate, PublishedBy)
                        VALUES (@0, @1, @2, @3);",
                    request.CompetitionId, request.IsOfficial, request.IsOfficial ? (object)DateTime.Now : DBNull.Value, by);

                return Json(new
                {
                    success = true,
                    isOfficial = request.IsOfficial,
                    message = request.IsOfficial ? "Stafettresultat publicerade som officiella." : "Stafettresultat satta till preliminära."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing Springskytte stafett results for {Comp}", request?.CompetitionId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>Read the stafett-results published flag (default false when no row / table empty).</summary>
        internal async Task<bool> GetStafettResultsOfficialAsync(int competitionId)
        {
            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var val = await db.ExecuteScalarAsync<int?>(
                    "SELECT CAST(IsOfficial AS INT) FROM SpringskytteStafettResultPublish WHERE CompetitionId = @0", competitionId);
                return val == 1;
            }
            catch { return false; }
        }

        // ===== STAFETT: helpers =====

        /// <summary>True when the configurationData JSON is a stafett config (teamFormat discriminator).</summary>
        internal static bool IsStafettConfig(string configJson)
        {
            if (string.IsNullOrEmpty(configJson)) return false;
            try
            {
                var probe = JsonConvert.DeserializeObject<SpringskytteStartListFormatProbe>(configJson);
                return string.Equals(probe?.TeamFormat, StafettTeamFormat, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static List<SpringskytteStafettMember> BuildLegMembers(TeamWithMembers t)
        {
            var members = new List<SpringskytteStafettMember>();
            int leg = 1;
            foreach (var m in t.Members)
            {
                members.Add(new SpringskytteStafettMember
                {
                    MemberId = m.MemberId,
                    Name = m.Name,
                    IsSpare = m.IsSpare,
                    LegNumber = m.IsSpare ? 0 : leg++
                });
            }
            return members;
        }

        /// <summary>Finds a relay team's start order + common start time from its stafett start list, if generated.</summary>
        private (int startOrder, string? startTime) FindStafettStartInfo(Umbraco.Cms.Core.Models.IContent? competition, int teamId)
        {
            if (competition == null) return (0, null);
            foreach (var node in _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                         .Where(c => c.ContentType.Alias == "precisionStartList"))
            {
                var json = node.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(json) || !IsStafettConfig(json)) continue;
                SpringskytteStafettStartListConfig? cfg = null;
                try { cfg = JsonConvert.DeserializeObject<SpringskytteStafettStartListConfig>(json); } catch { }
                var entry = cfg?.Teams?.FirstOrDefault(e => e.TeamId == teamId);
                if (entry != null)
                    return (entry.StartOrder, !string.IsNullOrWhiteSpace(entry.StartTime) ? entry.StartTime : cfg!.CommonStartTime);
            }
            return (0, null);
        }

        /// <summary>Cached HTML for a stafett start list (admin preview / print). Public page re-renders from config.</summary>
        private static string BuildStafettStartListHtml(SpringskytteStafettStartListConfig config)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<table class='table table-striped'><thead><tr>")
              .Append("<th>Nr</th><th>Tid</th><th>Lag</th><th>Klubb</th><th>Klass</th><th>Deltagare</th>")
              .Append("</tr></thead><tbody>");
            foreach (var team in config.Teams.OrderBy(t => t.StartOrder))
            {
                var legs = string.Join(", ", team.Members
                    .Where(m => !m.IsSpare)
                    .Select(m => System.Net.WebUtility.HtmlEncode(m.Name)));
                var spares = team.Members.Where(m => m.IsSpare).Select(m => System.Net.WebUtility.HtmlEncode(m.Name)).ToList();
                if (spares.Any()) legs += $" <span class='text-muted'>(reserv: {string.Join(", ", spares)})</span>";
                sb.Append("<tr>")
                  .Append($"<td>{team.StartOrder}</td>")
                  .Append($"<td>{System.Net.WebUtility.HtmlEncode(team.StartTime)}</td>")
                  .Append($"<td>{System.Net.WebUtility.HtmlEncode(team.TeamName)}</td>")
                  .Append($"<td>{System.Net.WebUtility.HtmlEncode(team.Club)}</td>")
                  .Append($"<td>{System.Net.WebUtility.HtmlEncode(team.StafettClass)}</td>")
                  .Append($"<td>{legs}</td>")
                  .Append("</tr>");
            }
            sb.Append("</tbody></table>");
            return sb.ToString();
        }
    }
}
