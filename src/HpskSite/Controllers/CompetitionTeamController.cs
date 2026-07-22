using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    public class CompetitionTeamController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ILogger<CompetitionTeamController> _logger;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly CompetitionTeamService _teamService;
        private readonly ClubService _clubService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public CompetitionTeamController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            ILogger<CompetitionTeamController> logger,
            AdminAuthorizationService authorizationService,
            CompetitionTeamService teamService,
            ClubService clubService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _logger = logger;
            _authorizationService = authorizationService;
            _teamService = teamService;
            _clubService = clubService;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> CreateTeam([FromBody] CompetitionCreateTeamRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                // Auth: member can create for own club, admin/regional admin for other clubs
                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var isClubAdmin = await _authorizationService.IsClubAdminForClub(request.ClubId);

                if (!isSiteAdmin && !isClubAdmin)
                {
                    // Regular member - verify it's their own club
                    var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                    if (!int.TryParse(primaryClubIdStr, out int memberClubId) || memberClubId != request.ClubId)
                        return Json(new { success = false, message = "Du kan bara skapa lag för din egen förening." });
                }

                var (success, message, teamId) = await _teamService.CreateTeamAsync(
                    request.CompetitionId, request.TeamName, request.TeamClass,
                    request.ClubId, request.MemberIds, request.SpareId, memberData.Id);

                return Json(new { success, message, teamId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating team");
                return Json(new { success = false, message = "Ett fel uppstod vid skapande av lag." });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> JoinTeam([FromBody] JoinTeamRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                var (success, message) = await _teamService.JoinTeamAsync(
                    request.TeamId, memberData.Id, request.IsSpare);

                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error joining team");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> LeaveTeam([FromBody] LeaveTeamRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                // Members can leave themselves; admins can remove anyone
                var memberId = request.MemberId > 0 ? request.MemberId : memberData.Id;
                if (memberId != memberData.Id)
                {
                    var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                    if (!isSiteAdmin)
                        return Json(new { success = false, message = "Bara administratörer kan ta bort andra medlemmar." });
                }

                var (success, message) = await _teamService.LeaveTeamAsync(request.TeamId, memberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error leaving team");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> DeleteTeam([FromBody] DeleteTeamRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                // Auth: a team is owned by one club — only a primary member of that club, a
                // club admin / regional admin for it, or a site admin may delete it.
                var teamClubId = await _teamService.GetTeamClubIdAsync(request.TeamId);
                if (teamClubId == 0)
                    return Json(new { success = false, message = "Laget hittades inte." });
                if (!await CanManageTeamAsync(teamClubId, memberData.GetValue<string>("primaryClubId")))
                    return Json(new { success = false, message = "Du har inte behörighet att ta bort det här laget." });

                var (success, message) = await _teamService.DeleteTeamAsync(request.TeamId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting team");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UpdateTeam([FromBody] UpdateTeamRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                // Auth: only a primary member of the team's club, a club/regional admin for it,
                // or a site admin may edit the roster.
                var teamClubId = await _teamService.GetTeamClubIdAsync(request.TeamId);
                if (teamClubId == 0)
                    return Json(new { success = false, message = "Laget hittades inte." });
                if (!await CanManageTeamAsync(teamClubId, memberData.GetValue<string>("primaryClubId")))
                    return Json(new { success = false, message = "Du har inte behörighet att ändra det här laget." });

                var (success, message) = await _teamService.UpdateTeamAsync(
                    request.TeamId, request.TeamName, request.MemberIds, request.SpareId);

                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating team");
                return Json(new { success = false, message = "Ett fel uppstod vid uppdatering av lag." });
            }
        }

        /// <summary>
        /// Can the current user manage (edit/delete) a team owned by <paramref name="teamClubId"/>?
        /// Mirrors the CreateTeam rule: site admin, club admin / regional admin for the team's
        /// club (IsClubAdminForClub covers regional admins of the club's region), or a member
        /// whose PRIMARY club is that club. Teams are always made up of a club's own members.
        /// </summary>
        private async Task<bool> CanManageTeamAsync(int teamClubId, string? memberPrimaryClubId)
        {
            if (teamClubId <= 0) return false;
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;
            if (await _authorizationService.IsClubAdminForClub(teamClubId)) return true;
            return int.TryParse(memberPrimaryClubId, out var primaryClubId) && primaryClubId == teamClubId;
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SetSpareStatus([FromBody] SetSpareStatusRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                if (!isSiteAdmin)
                    return Json(new { success = false, message = "Bara administratörer kan ändra reservstatus." });

                var (success, message) = await _teamService.SetSpareStatusAsync(
                    request.TeamId, request.MemberId, request.IsSpare);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting spare status");
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetTeamsForCompetition(int competitionId)
        {
            try
            {
                var teams = await _teamService.GetTeamsForCompetitionAsync(competitionId);

                // Get available team classes for context
                var isSpringskytte = GetCompetitionType(competitionId) == "Springskytte";
                var competitionClassIds = GetCompetitionClassIds(competitionId);
                var teamClasses = TeamClassHelper.GetTeamClasses(competitionClassIds, isSpringskytte);
                var stafettTeamClasses = TeamClassHelper.GetStafettTeamClasses();

                return Json(new
                {
                    success = true,
                    isSpringskytte,
                    teams = teams.Select(t => new
                    {
                        id = t.Team.Id,
                        teamName = t.Team.TeamName,
                        teamClass = t.Team.TeamClass,
                        clubId = t.Team.ClubId,
                        clubName = HpskSite.Helpers.ClubNameHelper.Shorten(t.ClubName ?? ""),
                        createdBy = t.Team.CreatedBy,
                        isRelay = t.Team.IsRelay,
                        members = t.Members.Select(m => new
                        {
                            memberId = m.MemberId,
                            name = m.Name,
                            isSpare = m.IsSpare
                        })
                    }),
                    teamClasses = teamClasses.Select(tc => new
                    {
                        teamClass = tc.TeamClass,
                        coreMembers = tc.CoreMembers,
                        maxSpares = tc.MaxSpares,
                        compatibleClasses = tc.CompatibleClasses
                    }),
                    stafettTeamClasses = stafettTeamClasses.Select(sc => new
                    {
                        teamClass = sc.TeamClass,
                        coreMembers = sc.CoreMembers,
                        maxSpares = sc.MaxSpares,
                        genderRestriction = sc.GenderRestriction,
                        description = sc.Description
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting teams for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Kunde inte hämta lag." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetEligibleMembers(int competitionId, string teamClass, int clubId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var members = await _teamService.GetEligibleMembersAsync(competitionId, teamClass, clubId);
                return Json(new
                {
                    success = true,
                    members = members.Select(m => new
                    {
                        memberId = m.MemberId,
                        name = m.Name,
                        shootingClass = m.ShootingClass
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting eligible members");
                return Json(new { success = false, message = "Kunde inte hämta medlemmar." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetRegisteredMembersForClub(int competitionId, int clubId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var members = _teamService.GetRegisteredMembersForClub(competitionId, clubId);
                return Json(new
                {
                    success = true,
                    members = members.Select(m => new
                    {
                        memberId = m.MemberId,
                        name = m.Name,
                        shootingClasses = m.ShootingClasses
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting registered members for club");
                return Json(new { success = false, message = "Kunde inte hämta medlemmar." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetTeamResults(int competitionId)
        {
            try
            {
                var competitionType = GetCompetitionType(competitionId);
                var numberOfSeries = GetTeamResultSeriesCount(competitionId);

                var results = await _teamService.CalculateTeamResultsAsync(
                    competitionId, competitionType, numberOfSeries);

                var isSpringskytte = competitionType == "Springskytte";

                return Json(new
                {
                    success = true,
                    isSpringskytte,
                    classGroups = results.Select(g => new
                    {
                        teamClass = g.TeamClass,
                        teams = g.Teams.Select(t => new
                        {
                            rank = t.Rank,
                            teamName = t.TeamName,
                            clubName = HpskSite.Helpers.ClubNameHelper.Shorten(t.ClubName ?? ""),
                            totalScore = t.TotalScore,
                            totalXCount = t.TotalXCount,
                            totalTimeSeconds = t.TotalTimeSeconds,
                            totalTimeDisplay = t.TotalTimeSeconds.HasValue
                                ? FormatTime(t.TotalTimeSeconds.Value)
                                : "-",
                            isComplete = t.IsComplete,
                            members = t.MemberResults.Select(m => new
                            {
                                name = m.Name,
                                score = m.Score,
                                xCount = m.XCount,
                                timeSeconds = m.TimeSeconds,
                                timeDisplay = m.TimeSeconds > 0 ? FormatTime(m.TimeSeconds) : "-",
                                hasResult = m.HasResult,
                                status = m.Status
                            })
                        })
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting team results");
                return Json(new { success = false, message = "Kunde inte beräkna lagresultat." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetClubMembersForRelay(int competitionId, int clubId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                // Auth: own club, club admin, or site admin
                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var isClubAdmin = await _authorizationService.IsClubAdminForClub(clubId);

                if (!isSiteAdmin && !isClubAdmin)
                {
                    var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                    if (!int.TryParse(primaryClubIdStr, out int memberClubId) || memberClubId != clubId)
                        return Json(new { success = false, message = "Ingen behörighet." });
                }

                var members = _teamService.GetClubMembers(clubId);
                return Json(new
                {
                    success = true,
                    members = members.Select(m => new
                    {
                        memberId = m.MemberId,
                        name = m.Name
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting club members for relay");
                return Json(new { success = false, message = "Kunde inte hämta medlemmar." });
            }
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> CreateStafettTeam([FromBody] CompetitionCreateTeamRequest request)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                // Auth: member can create for own club, admin/regional admin for other clubs
                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var isClubAdmin = await _authorizationService.IsClubAdminForClub(request.ClubId);

                if (!isSiteAdmin && !isClubAdmin)
                {
                    var primaryClubIdStr = memberData.GetValue<string>("primaryClubId");
                    if (!int.TryParse(primaryClubIdStr, out int memberClubId) || memberClubId != request.ClubId)
                        return Json(new { success = false, message = "Du kan bara skapa lag för din egen förening." });
                }

                var (success, message, teamId) = await _teamService.CreateTeamAsync(
                    request.CompetitionId, request.TeamName, request.TeamClass,
                    request.ClubId, request.MemberIds, request.SpareId, memberData.Id, isRelay: true);

                return Json(new { success, message, teamId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating stafett team");
                return Json(new { success = false, message = "Ett fel uppstod vid skapande av stafettlag." });
            }
        }

        #region Helpers

        private string GetCompetitionType(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    return comp?.Value<string>("competitionType") ?? "Precision";
                }
            }
            catch { }
            return "Precision";
        }

        private int GetNumberOfSeries(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    // Value<int> returns 0 (not null) when the property is unset/empty,
                    // so `?? 7` never fires. A 0 here caps the team sum at
                    // Math.Min(7, 0) = 0 → team totals show as 0. Treat 0 as unset.
                    var n = comp?.Value<int>("numberOfSeriesOrStations") ?? 0;
                    return n > 0 ? n : 7;
                }
            }
            catch { }
            return 7;
        }

        /// <summary>
        /// How many series count toward a team's total. Organiser can set this explicitly
        /// via the "Antal serier i lagresultat" field (e.g. a 7+3 finals comp counts only the
        /// 7 qualifying series). When unset (0) we default to the qualification series count
        /// (numberOfSeriesOrStations is the TOTAL incl. finals — see Competition.cs
        /// QualificationSeriesCount), so finals series never inflate team totals by default.
        /// </summary>
        private int GetTeamResultSeriesCount(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    var explicitCount = comp?.Value<int>("teamResultSeriesCount") ?? 0;
                    if (explicitCount > 0) return explicitCount;

                    var total = comp?.Value<int>("numberOfSeriesOrStations") ?? 0;
                    var finals = comp?.Value<int>("numberOfFinalSeries") ?? 0;
                    var qualifying = total - finals;
                    if (qualifying > 0) return qualifying;
                    if (total > 0) return total;
                }
            }
            catch { }
            return 7;
        }

        private string[] GetCompetitionClassIds(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    var raw = comp?.Value<string>("shootingClassIds") ?? "";
                    if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();

                    if (raw.TrimStart().StartsWith("["))
                    {
                        return System.Text.Json.JsonSerializer.Deserialize<string[]>(raw) ?? Array.Empty<string>();
                    }
                    return raw.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                }
            }
            catch { }
            return Array.Empty<string>();
        }

        private static string FormatTime(decimal totalSeconds)
        {
            var ts = TimeSpan.FromSeconds((double)totalSeconds);
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
        }

        #endregion
    }

    #region Request Models

    public class CompetitionCreateTeamRequest
    {
        public int CompetitionId { get; set; }
        public string TeamName { get; set; } = "";
        public string TeamClass { get; set; } = "";
        public int ClubId { get; set; }
        public int[] MemberIds { get; set; } = Array.Empty<int>();
        public int? SpareId { get; set; }
    }

    public class JoinTeamRequest
    {
        public int TeamId { get; set; }
        public bool IsSpare { get; set; }
    }

    public class LeaveTeamRequest
    {
        public int TeamId { get; set; }
        public int MemberId { get; set; }
    }

    public class DeleteTeamRequest
    {
        public int TeamId { get; set; }
    }

    public class SetSpareStatusRequest
    {
        public int TeamId { get; set; }
        public int MemberId { get; set; }
        public bool IsSpare { get; set; }
    }

    public class UpdateTeamRequest
    {
        public int TeamId { get; set; }
        public string TeamName { get; set; } = "";
        public int[] MemberIds { get; set; } = Array.Empty<int>();
        public int? SpareId { get; set; }
    }

    #endregion
}
