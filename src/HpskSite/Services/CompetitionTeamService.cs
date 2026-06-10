using HpskSite.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Springskytte.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace HpskSite.Services
{
    public class CompetitionTeamService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly ClubService _clubService;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ILogger<CompetitionTeamService> _logger;

        public CompetitionTeamService(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextAccessor umbracoContextAccessor,
            AdminAuthorizationService authorizationService,
            ClubService clubService,
            IMemberService memberService,
            IContentService contentService,
            ILogger<CompetitionTeamService> logger)
        {
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
            _authorizationService = authorizationService;
            _clubService = clubService;
            _memberService = memberService;
            _contentService = contentService;
            _logger = logger;
        }

        public async Task<(bool success, string message, int? teamId)> CreateTeamAsync(
            int competitionId, string teamName, string teamClass, int clubId,
            int[] memberIds, int? spareId, int createdByMemberId, bool isRelay = false)
        {
            var (coreMembers, maxSpares) = TeamClassHelper.GetTeamSize(teamClass);
            var nonSpareIds = spareId.HasValue
                ? memberIds.Where(id => id != spareId.Value).ToArray()
                : memberIds;
            var spareIds = spareId.HasValue ? new[] { spareId.Value } : Array.Empty<int>();

            if (nonSpareIds.Length != coreMembers)
                return (false, $"Laget behöver exakt {coreMembers} ordinarie medlemmar.", null);

            if (spareIds.Length > maxSpares)
                return (false, $"Max {maxSpares} reserv(er) tillåtna.", null);

            // Build member name lookup
            Dictionary<int, string> memberNameLookup;

            if (isRelay)
            {
                // Relay: members do NOT need to be individually registered — look up names directly
                memberNameLookup = new Dictionary<int, string>();
                foreach (var memberId in memberIds)
                {
                    var member = _memberService.GetById(memberId);
                    if (member != null)
                        memberNameLookup[memberId] = $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}";
                    else
                        memberNameLookup[memberId] = $"Medlem #{memberId}";
                }
            }
            else
            {
                // Standard: validate all members are registered in compatible classes
                var isSpringskytte = GetCompetitionType(competitionId) == "Springskytte";
                var compatibleClasses = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte);
                var registeredMembers = GetRegisteredMembersInClasses(competitionId, compatibleClasses, isSpringskytte);
                var registeredMemberIds = registeredMembers.Select(r => r.MemberId).ToHashSet();
                memberNameLookup = registeredMembers.ToDictionary(r => r.MemberId, r => r.Name);

                foreach (var memberId in memberIds)
                {
                    if (!registeredMemberIds.Contains(memberId))
                    {
                        string GetNameReg(int id) => memberNameLookup.GetValueOrDefault(id, $"Medlem #{id}");
                        return (false, $"{GetNameReg(memberId)} är inte anmäld i en kompatibel klass.", null);
                    }
                }
            }

            string GetName(int id) => memberNameLookup.GetValueOrDefault(id, $"Medlem #{id}");

            // Check none are already in a team for this team class
            using var db = _databaseFactory.CreateDatabase();
            var existingTeamMembers = await db.FetchAsync<CompetitionTeamMemberDto>(
                @"SELECT ctm.* FROM CompetitionTeamMember ctm
                  INNER JOIN CompetitionTeam ct ON ct.Id = ctm.TeamId
                  WHERE ct.CompetitionId = @0 AND ct.TeamClass = @1",
                competitionId, teamClass);

            var alreadyInTeam = existingTeamMembers.Select(m => m.MemberId).ToHashSet();
            foreach (var memberId in memberIds)
            {
                if (alreadyInTeam.Contains(memberId))
                    return (false, $"{GetName(memberId)} är redan med i ett lag i klassen {teamClass}.", null);
            }

            // Create team
            var teamId = await db.ExecuteScalarAsync<int>(
                @"INSERT INTO CompetitionTeam (CompetitionId, TeamName, TeamClass, ClubId, CreatedBy, CreatedAt, IsRelay)
                  VALUES (@0, @1, @2, @3, @4, @5, @6); SELECT SCOPE_IDENTITY();",
                competitionId, teamName.Trim(), teamClass, clubId, createdByMemberId, DateTime.UtcNow, isRelay);

            // Add members
            foreach (var memberId in nonSpareIds)
            {
                await db.InsertAsync(new CompetitionTeamMemberDto
                {
                    TeamId = teamId,
                    MemberId = memberId,
                    IsSpare = false,
                    JoinedAt = DateTime.UtcNow
                });
            }

            foreach (var memberId in spareIds)
            {
                await db.InsertAsync(new CompetitionTeamMemberDto
                {
                    TeamId = teamId,
                    MemberId = memberId,
                    IsSpare = true,
                    JoinedAt = DateTime.UtcNow
                });
            }

            // Create Umbraco registration doc for backoffice visibility and invoice linking
            try
            {
                var clubName = _clubService.GetClubNameById(clubId) ?? "Okänd förening";
                var memberNames = nonSpareIds.Select(id => GetName(id)).ToList();
                if (spareIds.Length > 0) memberNames.AddRange(spareIds.Select(id => $"{GetName(id)} (reserv)"));

                CreateTeamRegistrationDoc(competitionId, teamId, teamName, teamClass, clubId, clubName, memberNames, isRelay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create team registration doc for team {TeamId}, continuing anyway", teamId);
            }

            return (true, "Laget har skapats.", teamId);
        }

        public async Task<(bool success, string message)> JoinTeamAsync(int teamId, int memberId, bool isSpare)
        {
            using var db = _databaseFactory.CreateDatabase();
            var team = await db.FirstOrDefaultAsync<CompetitionTeamDto>($"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE Id = @0", teamId);
            if (team == null)
                return (false, "Laget finns inte.");

            var members = await db.FetchAsync<CompetitionTeamMemberDto>("WHERE TeamId = @0", teamId);
            var (coreMembers, maxSpares) = TeamClassHelper.GetTeamSize(team.TeamClass);
            var currentCore = members.Count(m => !m.IsSpare);
            var currentSpares = members.Count(m => m.IsSpare);

            if (!isSpare && currentCore >= coreMembers)
                return (false, "Laget har redan fullt antal ordinarie medlemmar.");

            if (isSpare && currentSpares >= maxSpares)
                return (false, "Laget har redan fullt antal reserver.");

            if (members.Any(m => m.MemberId == memberId))
                return (false, "Du är redan med i detta lag.");

            // Check member is not in another team for this class
            var existingTeamMembers = await db.FetchAsync<CompetitionTeamMemberDto>(
                @"SELECT ctm.* FROM CompetitionTeamMember ctm
                  INNER JOIN CompetitionTeam ct ON ct.Id = ctm.TeamId
                  WHERE ct.CompetitionId = @0 AND ct.TeamClass = @1 AND ctm.MemberId = @2",
                team.CompetitionId, team.TeamClass, memberId);

            if (existingTeamMembers.Any())
                return (false, "Du är redan med i ett annat lag i samma klass.");

            // Validate member is registered in a compatible class
            var isSpringskytte = GetCompetitionType(team.CompetitionId) == "Springskytte";
            var compatibleClasses = TeamClassHelper.GetCompatibleIndividualClasses(team.TeamClass, isSpringskytte);
            var registeredMembers = GetRegisteredMembersInClasses(team.CompetitionId, compatibleClasses, isSpringskytte);
            if (!registeredMembers.Any(r => r.MemberId == memberId))
                return (false, "Du är inte anmäld i en kompatibel klass.");

            await db.InsertAsync(new CompetitionTeamMemberDto
            {
                TeamId = teamId,
                MemberId = memberId,
                IsSpare = isSpare,
                JoinedAt = DateTime.UtcNow
            });

            return (true, "Du har gått med i laget.");
        }

        public async Task<(bool success, string message)> LeaveTeamAsync(int teamId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var deleted = await db.ExecuteAsync(
                "DELETE FROM CompetitionTeamMember WHERE TeamId = @0 AND MemberId = @1",
                teamId, memberId);

            return deleted > 0
                ? (true, "Du har lämnat laget.")
                : (false, "Du kunde inte hittas i laget.");
        }

        public async Task<(bool success, string message)> DeleteTeamAsync(int teamId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var team = await db.FirstOrDefaultAsync<CompetitionTeamDto>($"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE Id = @0", teamId);
            if (team == null)
                return (false, "Laget kunde inte hittas.");

            var deleted = await db.ExecuteAsync("DELETE FROM CompetitionTeam WHERE Id = @0", teamId);
            if (deleted <= 0)
                return (false, "Laget kunde inte tas bort.");

            // Remove the Umbraco registration doc
            try
            {
                DeleteTeamRegistrationDoc(team.CompetitionId, teamId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete team registration doc for team {TeamId}", teamId);
            }

            return (true, "Laget har tagits bort.");
        }

        public async Task<(bool success, string message)> UpdateTeamAsync(
            int teamId, string teamName, int[] memberIds, int? spareId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var team = await db.FirstOrDefaultAsync<CompetitionTeamDto>($"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE Id = @0", teamId);
            if (team == null)
                return (false, "Laget finns inte.");

            // Validate team size
            var (coreMembers, maxSpares) = TeamClassHelper.GetTeamSize(team.TeamClass);
            var nonSpareIds = spareId.HasValue
                ? memberIds.Where(id => id != spareId.Value).ToArray()
                : memberIds;
            var spareIds = spareId.HasValue ? new[] { spareId.Value } : Array.Empty<int>();

            if (nonSpareIds.Length != coreMembers)
                return (false, $"Laget behöver exakt {coreMembers} ordinarie medlemmar.");

            if (spareIds.Length > maxSpares)
                return (false, $"Max {maxSpares} reserv(er) tillåtna.");

            // Update team name
            if (!string.IsNullOrWhiteSpace(teamName))
            {
                await db.ExecuteAsync(
                    "UPDATE CompetitionTeam SET TeamName = @0 WHERE Id = @1",
                    teamName.Trim(), teamId);
            }

            // Replace all members: delete existing, insert new
            await db.ExecuteAsync("DELETE FROM CompetitionTeamMember WHERE TeamId = @0", teamId);

            foreach (var memberId in nonSpareIds)
            {
                await db.InsertAsync(new CompetitionTeamMemberDto
                {
                    TeamId = teamId,
                    MemberId = memberId,
                    IsSpare = false,
                    JoinedAt = DateTime.UtcNow
                });
            }

            foreach (var memberId in spareIds)
            {
                await db.InsertAsync(new CompetitionTeamMemberDto
                {
                    TeamId = teamId,
                    MemberId = memberId,
                    IsSpare = true,
                    JoinedAt = DateTime.UtcNow
                });
            }

            // Update the Umbraco registration doc
            try
            {
                var isSpringskytte2 = GetCompetitionType(team.CompetitionId) == "Springskytte";
                var compatClasses = TeamClassHelper.GetCompatibleIndividualClasses(team.TeamClass, isSpringskytte2);
                var regMembers = GetRegisteredMembersInClasses(team.CompetitionId, compatClasses, isSpringskytte2);
                var nameLookup = regMembers.ToDictionary(r => r.MemberId, r => r.Name);
                string MemberName(int id) => nameLookup.GetValueOrDefault(id, $"Medlem #{id}");

                var memberNames = nonSpareIds.Select(id => MemberName(id)).ToList();
                if (spareIds.Length > 0) memberNames.AddRange(spareIds.Select(id => $"{MemberName(id)} (reserv)"));

                UpdateTeamRegistrationDoc(team.CompetitionId, teamId, team.TeamName, memberNames);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update team registration doc for team {TeamId}", teamId);
            }

            return (true, "Laget har uppdaterats.");
        }

        public async Task<(bool success, string message)> SetSpareStatusAsync(int teamId, int memberId, bool isSpare)
        {
            using var db = _databaseFactory.CreateDatabase();

            var team = await db.FirstOrDefaultAsync<CompetitionTeamDto>($"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE Id = @0", teamId);
            if (team == null)
                return (false, "Laget finns inte.");

            var members = await db.FetchAsync<CompetitionTeamMemberDto>("WHERE TeamId = @0", teamId);
            var member = members.FirstOrDefault(m => m.MemberId == memberId);
            if (member == null)
                return (false, "Medlemmen finns inte i laget.");

            if (member.IsSpare == isSpare)
                return (true, "Ingen ändring behövdes.");

            var (coreMembers, maxSpares) = TeamClassHelper.GetTeamSize(team.TeamClass);

            if (!isSpare)
            {
                // Promoting spare to core - check core isn't full
                var currentCore = members.Count(m => !m.IsSpare && m.MemberId != memberId);
                if (currentCore >= coreMembers)
                    return (false, "Laget har redan fullt antal ordinarie medlemmar.");
            }
            else
            {
                // Demoting core to spare - check spares not full
                var currentSpares = members.Count(m => m.IsSpare && m.MemberId != memberId);
                if (currentSpares >= maxSpares)
                    return (false, "Laget har redan fullt antal reserver.");
            }

            await db.ExecuteAsync(
                "UPDATE CompetitionTeamMember SET IsSpare = @0 WHERE TeamId = @1 AND MemberId = @2",
                isSpare, teamId, memberId);

            return (true, isSpare ? "Medlem ändrad till reserv." : "Medlem ändrad till ordinarie.");
        }

        public async Task<List<TeamWithMembers>> GetTeamsForCompetitionAsync(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var cols = TeamSelectCols;
            var teams = await db.FetchAsync<CompetitionTeamDto>(
                $"SELECT {cols} FROM CompetitionTeam WHERE CompetitionId = @0 ORDER BY TeamClass, TeamName", competitionId);

            var result = new List<TeamWithMembers>();
            if (!teams.Any()) return result;

            var teamIds = teams.Select(t => t.Id).ToList();
            var allMembers = await db.FetchAsync<CompetitionTeamMemberDto>(
                $"WHERE TeamId IN ({string.Join(",", teamIds)}) ORDER BY IsSpare, JoinedAt");

            var membersByTeam = allMembers.GroupBy(m => m.TeamId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var team in teams)
            {
                var members = membersByTeam.GetValueOrDefault(team.Id, new List<CompetitionTeamMemberDto>());
                var memberInfos = new List<TeamMemberInfo>();

                foreach (var m in members)
                {
                    var member = _memberService.GetById(m.MemberId);
                    var memberName = member != null
                        ? $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}"
                        : $"Medlem #{m.MemberId}";

                    memberInfos.Add(new TeamMemberInfo
                    {
                        MemberId = m.MemberId,
                        Name = memberName,
                        IsSpare = m.IsSpare
                    });
                }

                result.Add(new TeamWithMembers
                {
                    Team = team,
                    Members = memberInfos,
                    ClubName = _clubService.GetClubNameById(team.ClubId) ?? "Okänd förening"
                });
            }

            return result;
        }

        public async Task<List<EligibleMember>> GetEligibleMembersAsync(
            int competitionId, string teamClass, int clubId)
        {
            var isSpringskytte = GetCompetitionType(competitionId) == "Springskytte";
            var compatibleClasses = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte);

            var registeredMembers = GetRegisteredMembersInClasses(competitionId, compatibleClasses, isSpringskytte);

            // Filter to members from the specified club
            var clubMembers = registeredMembers.Where(r => r.ClubId == clubId).ToList();

            // Exclude members already in a team for this class
            using var db = _databaseFactory.CreateDatabase();
            var existingTeamMemberIds = (await db.FetchAsync<CompetitionTeamMemberDto>(
                @"SELECT ctm.* FROM CompetitionTeamMember ctm
                  INNER JOIN CompetitionTeam ct ON ct.Id = ctm.TeamId
                  WHERE ct.CompetitionId = @0 AND ct.TeamClass = @1",
                competitionId, teamClass))
                .Select(m => m.MemberId)
                .ToHashSet();

            return clubMembers
                .Where(m => !existingTeamMemberIds.Contains(m.MemberId))
                .ToList();
        }

        public async Task<List<TeamResultGroup>> CalculateTeamResultsAsync(
            int competitionId, string competitionType, int numberOfSeries)
        {
            var teams = await GetTeamsForCompetitionAsync(competitionId);
            if (!teams.Any()) return new List<TeamResultGroup>();

            using var db = _databaseFactory.CreateDatabase();

            var isSpringskytte = competitionType == "Springskytte";
            var resultGroups = new List<TeamResultGroup>();

            // Group teams by class
            var teamsByClass = teams.GroupBy(t => t.Team.TeamClass);

            foreach (var classGroup in teamsByClass)
            {
                var teamResults = new List<TeamResult>();

                foreach (var teamWithMembers in classGroup)
                {
                    // Only count non-spare members
                    var coreMembers = teamWithMembers.Members.Where(m => !m.IsSpare).ToList();

                    if (isSpringskytte)
                    {
                        var memberResults = new List<TeamMemberResult>();
                        decimal totalTime = 0;
                        bool allComplete = true;

                        foreach (var member in coreMembers)
                        {
                            var entry = await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                                "WHERE CompetitionId = @0 AND MemberId = @1",
                                competitionId, member.MemberId);

                            if (entry?.TotalTimeSeconds != null && string.IsNullOrEmpty(entry.Status))
                            {
                                totalTime += entry.TotalTimeSeconds.Value;
                                memberResults.Add(new TeamMemberResult
                                {
                                    MemberId = member.MemberId,
                                    Name = member.Name,
                                    Score = 0,
                                    TimeSeconds = entry.TotalTimeSeconds.Value,
                                    HasResult = true
                                });
                            }
                            else
                            {
                                allComplete = false;
                                memberResults.Add(new TeamMemberResult
                                {
                                    MemberId = member.MemberId,
                                    Name = member.Name,
                                    HasResult = false,
                                    Status = entry?.Status
                                });
                            }
                        }

                        teamResults.Add(new TeamResult
                        {
                            TeamId = teamWithMembers.Team.Id,
                            TeamName = teamWithMembers.Team.TeamName,
                            ClubName = teamWithMembers.ClubName,
                            TotalScore = 0,
                            TotalTimeSeconds = allComplete ? totalTime : null,
                            MemberResults = memberResults,
                            IsComplete = allComplete
                        });
                    }
                    else
                    {
                        // Standard: sum of individual scores (first min(7, numberOfSeries) series)
                        var seriesToCount = Math.Min(7, numberOfSeries);
                        var memberResults = new List<TeamMemberResult>();
                        int totalScore = 0;
                        int totalXCount = 0;
                        bool allComplete = true;

                        foreach (var member in coreMembers)
                        {
                            // Query by competition type - use base PrecisionResultEntry for all standard types
                            var entries = await db.FetchAsync<PrecisionResultEntry>(
                                $"WHERE CompetitionId = @0 AND MemberId = @1 ORDER BY SeriesNumber",
                                competitionId, member.MemberId);

                            if (entries.Any())
                            {
                                var memberScore = 0;
                                var memberXCount = 0;
                                var seriesToUse = entries.Take(seriesToCount).ToList();

                                foreach (var entry in seriesToUse)
                                {
                                    try
                                    {
                                        var shots = JsonSerializer.Deserialize<string[]>(entry.Shots) ?? Array.Empty<string>();
                                        foreach (var shot in shots)
                                        {
                                            if (shot == "X")
                                            {
                                                memberScore += 10;
                                                memberXCount++;
                                            }
                                            else if (int.TryParse(shot, out int val))
                                            {
                                                memberScore += val;
                                            }
                                        }
                                    }
                                    catch { }
                                }

                                totalScore += memberScore;
                                totalXCount += memberXCount;
                                memberResults.Add(new TeamMemberResult
                                {
                                    MemberId = member.MemberId,
                                    Name = member.Name,
                                    Score = memberScore,
                                    XCount = memberXCount,
                                    HasResult = true
                                });
                            }
                            else
                            {
                                allComplete = false;
                                memberResults.Add(new TeamMemberResult
                                {
                                    MemberId = member.MemberId,
                                    Name = member.Name,
                                    HasResult = false
                                });
                            }
                        }

                        teamResults.Add(new TeamResult
                        {
                            TeamId = teamWithMembers.Team.Id,
                            TeamName = teamWithMembers.Team.TeamName,
                            ClubName = teamWithMembers.ClubName,
                            TotalScore = totalScore,
                            TotalXCount = totalXCount,
                            MemberResults = memberResults,
                            IsComplete = allComplete
                        });
                    }
                }

                // Sort: Springskytte by time (lowest first), standard by score (highest first)
                if (isSpringskytte)
                {
                    teamResults = teamResults
                        .OrderBy(t => !t.IsComplete) // Complete teams first
                        .ThenBy(t => t.TotalTimeSeconds ?? decimal.MaxValue)
                        .ToList();
                }
                else
                {
                    teamResults = teamResults
                        .OrderBy(t => !t.IsComplete)
                        .ThenByDescending(t => t.TotalScore)
                        .ThenByDescending(t => t.TotalXCount)
                        .ToList();
                }

                // Assign ranks
                for (int i = 0; i < teamResults.Count; i++)
                {
                    teamResults[i].Rank = teamResults[i].IsComplete ? i + 1 : 0;
                }

                resultGroups.Add(new TeamResultGroup
                {
                    TeamClass = classGroup.Key,
                    Teams = teamResults
                });
            }

            return resultGroups;
        }

        /// <summary>
        /// Gets all registered members for a competition filtered by club, with all their registered classes.
        /// </summary>
        public List<RegisteredMemberInfo> GetRegisteredMembersForClub(int competitionId, int clubId)
        {
            var result = new List<RegisteredMemberInfo>();

            try
            {
                var competitionContent = _contentService.GetById(competitionId);
                if (competitionContent == null) return result;

                long totalChildren;
                var children = _contentService.GetPagedChildren(competitionContent.Id, 0, 100, out totalChildren).ToList();
                var registrationsHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub");

                if (registrationsHub == null) return result;

                // Do NOT filter on r.Published. RegisterForCompetition Save()s the
                // registration synchronously (data is committed there) but defers Publish()
                // to a best-effort background task ~10s later that is unreliable on the
                // production host. Every other registration read works off the Saved node;
                // requiring Published here made freshly-registered shooters invisible in the
                // team builder ("Skapa lag") even though they show in the public list.
                var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out totalChildren)
                    .Where(r => r.ContentType.Alias == "competitionRegistration")
                    .ToList();

                foreach (var reg in registrations)
                {
                    var memberId = reg.GetValue<int>("memberId");
                    if (memberId <= 0) continue;

                    // Get club ID from member
                    var member = _memberService.GetById(memberId);
                    var memberClubId = 0;
                    if (member != null)
                    {
                        var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(primaryClubIdStr))
                            int.TryParse(primaryClubIdStr, out memberClubId);
                    }

                    if (memberClubId != clubId) continue;

                    var memberName = reg.GetValue<string>("memberName") ?? "";
                    var classesJson = reg.GetValue<string>("shootingClasses") ?? "[]";
                    var classes = new List<string>();

                    try
                    {
                        var parsed = JsonSerializer.Deserialize<JsonElement[]>(classesJson);
                        if (parsed != null)
                        {
                            foreach (var cls in parsed)
                            {
                                var classId = cls.ValueKind == JsonValueKind.Object
                                    ? cls.GetProperty("class").GetString() ?? ""
                                    : cls.GetString() ?? "";
                                if (!string.IsNullOrEmpty(classId))
                                    classes.Add(classId);
                            }
                        }
                    }
                    catch { }

                    result.Add(new RegisteredMemberInfo
                    {
                        MemberId = memberId,
                        Name = memberName,
                        ClubId = memberClubId,
                        ShootingClasses = classes
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting registered members for competition {CompetitionId}, club {ClubId}", competitionId, clubId);
            }

            return result;
        }

        /// <summary>
        /// Gets ALL approved members from a club (not just those registered in the competition).
        /// Used for relay (stafett) registration where members don't need individual registration.
        /// </summary>
        public List<ClubMemberInfo> GetClubMembers(int clubId)
        {
            var result = new List<ClubMemberInfo>();
            try
            {
                // Get all members and filter by club
                long totalRecords;
                var allMembers = _memberService.GetAll(0, 5000, out totalRecords);
                foreach (var member in allMembers)
                {
                    var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                    if (string.IsNullOrEmpty(primaryClubIdStr)) continue;
                    if (!int.TryParse(primaryClubIdStr, out int memberClubId) || memberClubId != clubId) continue;

                    // Check member is approved (has "Users" role, not just "PendingApproval")
                    var roles = _memberService.GetAllRoles(member.Id);
                    if (!roles.Contains("Users")) continue;

                    var firstName = member.GetValue<string>("firstName") ?? "";
                    var lastName = member.GetValue<string>("lastName") ?? "";
                    result.Add(new ClubMemberInfo
                    {
                        MemberId = member.Id,
                        Name = $"{firstName} {lastName}".Trim()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting club members for club {ClubId}", clubId);
            }

            return result.OrderBy(m => m.Name).ToList();
        }

        #region Helpers

        private const string TeamSelectCols = "Id, CompetitionId, TeamName, TeamClass, ClubId, CreatedBy, CreatedAt, IsRelay";

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

        private string GetResultTableName(string competitionType)
        {
            return competitionType switch
            {
                "Milsnabb" => "MilsnabbResultEntry",
                "Duell" => "DuellResultEntry",
                "NationellHelmatch" => "NationellHelmatchResultEntry",
                "MagnumPrecision" => "MagnumPrecisionResultEntry",
                _ => "PrecisionResultEntry"
            };
        }

        /// <summary>
        /// Gets members registered for a competition in the specified individual classes.
        /// </summary>
        private List<EligibleMember> GetRegisteredMembersInClasses(
            int competitionId, string[] compatibleClasses, bool isSpringskytte)
        {
            var result = new List<EligibleMember>();

            try
            {
                // Find registrations hub under competition
                var competitionContent = _contentService.GetById(competitionId);
                if (competitionContent == null) return result;

                long totalChildren;
                var children = _contentService.GetPagedChildren(competitionContent.Id, 0, 100, out totalChildren).ToList();
                var registrationsHub = children.FirstOrDefault(c =>
                    c.ContentType.Alias == "competitionRegistrationsHub");

                if (registrationsHub == null) return result;

                // Do NOT filter on r.Published. RegisterForCompetition Save()s the
                // registration synchronously (data is committed there) but defers Publish()
                // to a best-effort background task ~10s later that is unreliable on the
                // production host. Every other registration read works off the Saved node;
                // requiring Published here made freshly-registered shooters invisible in the
                // team builder ("Skapa lag") even though they show in the public list.
                var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out totalChildren)
                    .Where(r => r.ContentType.Alias == "competitionRegistration")
                    .ToList();

                foreach (var reg in registrations)
                {
                    var memberId = reg.GetValue<int>("memberId");
                    if (memberId <= 0) continue;

                    var classesJson = reg.GetValue<string>("shootingClasses") ?? "[]";
                    var memberName = reg.GetValue<string>("memberName") ?? "";
                    var memberClub = reg.GetValue<string>("memberClub") ?? "";

                    // Get club ID from member
                    var member = _memberService.GetById(memberId);
                    var clubId = 0;
                    if (member != null)
                    {
                        var primaryClubIdStr = member.GetValue<string>("primaryClubId");
                        if (!string.IsNullOrEmpty(primaryClubIdStr))
                            int.TryParse(primaryClubIdStr, out clubId);
                    }

                    if (isSpringskytte)
                    {
                        // Springskytte registration class format: "A-D 21", "C-H 35"
                        // The shooting classes may be stored differently
                        try
                        {
                            var classes = JsonSerializer.Deserialize<JsonElement[]>(classesJson);
                            if (classes != null)
                            {
                                foreach (var cls in classes)
                                {
                                    var classId = cls.ValueKind == JsonValueKind.Object
                                        ? cls.GetProperty("class").GetString() ?? ""
                                        : cls.GetString() ?? "";

                                    if (compatibleClasses.Contains(classId))
                                    {
                                        if (!result.Any(r => r.MemberId == memberId))
                                        {
                                            result.Add(new EligibleMember
                                            {
                                                MemberId = memberId,
                                                Name = memberName,
                                                ClubId = clubId,
                                                ClubName = memberClub,
                                                ShootingClass = classId
                                            });
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        // Standard: classes stored as JSON array of objects [{class:"A1",startPreference:"Early"}, ...]
                        try
                        {
                            var classes = JsonSerializer.Deserialize<JsonElement[]>(classesJson);
                            if (classes != null)
                            {
                                foreach (var cls in classes)
                                {
                                    var classId = cls.ValueKind == JsonValueKind.Object
                                        ? cls.GetProperty("class").GetString() ?? ""
                                        : cls.GetString() ?? "";

                                    if (compatibleClasses.Contains(classId))
                                    {
                                        if (!result.Any(r => r.MemberId == memberId))
                                        {
                                            result.Add(new EligibleMember
                                            {
                                                MemberId = memberId,
                                                Name = memberName,
                                                ClubId = clubId,
                                                ClubName = memberClub,
                                                ShootingClass = classId
                                            });
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting registered members for competition {CompetitionId}", competitionId);
            }

            return result;
        }

        /// <summary>
        /// Creates a competitionTeamRegistration Umbraco doc under the competition's registrations hub.
        /// </summary>
        private void CreateTeamRegistrationDoc(int competitionId, int teamId, string teamName, string teamClass,
            int clubId, string clubName, List<string> memberNames, bool isRelay = false)
        {
            _logger.LogInformation("CreateTeamRegistrationDoc called - CompetitionId: {CompetitionId}, TeamId: {TeamId}", competitionId, teamId);

            var competitionContent = _contentService.GetById(competitionId);
            if (competitionContent == null)
            {
                _logger.LogWarning("Competition content {CompetitionId} not found", competitionId);
                return;
            }

            long totalChildren;
            var children = _contentService.GetPagedChildren(competitionContent.Id, 0, 100, out totalChildren).ToList();
            _logger.LogInformation("Competition has {Count} children: {Children}",
                children.Count, string.Join(", ", children.Select(c => $"{c.Name} ({c.ContentType.Alias})")));

            var registrationsHub = children.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub");

            if (registrationsHub == null)
            {
                _logger.LogInformation("No registrations hub found, creating one");
                registrationsHub = _contentService.Create("Anmälningar", competitionContent.Id, "competitionRegistrationsHub");
                var hubSave = _contentService.Save(registrationsHub);
                if (!hubSave.Success)
                {
                    _logger.LogError("Failed to save registrations hub");
                    return;
                }
                _contentService.Publish(registrationsHub, new[] { "*" }, -1);
            }

            _logger.LogInformation("Using registrations hub {HubId} ({HubName})", registrationsHub.Id, registrationsHub.Name);

            var docName = $"Lag: {teamName} ({clubName})";
            var doc = _contentService.Create(docName, registrationsHub.Id, "competitionTeamRegistration");

            if (doc == null)
            {
                _logger.LogError("Failed to create competitionTeamRegistration doc. Check that the doc type exists and is an allowed child of competitionRegistrationsHub.");
                return;
            }

            doc.SetValue("teamId", teamId);
            doc.SetValue("teamName", teamName);
            doc.SetValue("teamClass", teamClass);
            doc.SetValue("clubId", clubId);
            doc.SetValue("clubName", clubName);
            doc.SetValue("members", string.Join(", ", memberNames));
            doc.SetValue("isRelay", isRelay);

            var saveResult = _contentService.Save(doc);
            _logger.LogInformation("Save result: {Success}", saveResult.Success);

            if (saveResult.Success)
            {
                var publishResult = _contentService.Publish(doc, new[] { "*" }, -1);
                _logger.LogInformation("Publish result: {Success}", publishResult.Success);
                if (!publishResult.Success)
                {
                    _logger.LogWarning("Publish failed for team registration doc {DocId}. Reasons: {Reasons}",
                        doc.Id, string.Join(", ", publishResult.EventMessages?.GetAll()?.Select(m => m.Message) ?? new[] { "*" }, -1));
                }
            }
            else
            {
                _logger.LogError("Save failed for team registration doc. Reasons: {Reasons}",
                    string.Join(", ", saveResult.EventMessages?.GetAll()?.Select(m => m.Message) ?? new[] { "*" }, -1));
            }
        }

        /// <summary>
        /// Updates the team registration doc when team name or members change.
        /// </summary>
        private void UpdateTeamRegistrationDoc(int competitionId, int teamId, string teamName, List<string> memberNames)
        {
            var doc = FindTeamRegistrationDoc(competitionId, teamId);
            if (doc == null) return;

            var clubName = doc.GetValue<string>("clubName") ?? "";
            doc.Name = $"Lag: {teamName} ({clubName})";
            doc.SetValue("teamName", teamName);
            doc.SetValue("members", string.Join(", ", memberNames));

            var saveResult = _contentService.Save(doc);
            if (saveResult.Success)
                _contentService.Publish(doc, new[] { "*" }, -1);
        }

        /// <summary>
        /// Deletes (unpublishes + deletes) the team registration doc.
        /// </summary>
        private void DeleteTeamRegistrationDoc(int competitionId, int teamId)
        {
            var doc = FindTeamRegistrationDoc(competitionId, teamId);
            if (doc == null) return;

            _contentService.Unpublish(doc);
            _contentService.Delete(doc);
            _logger.LogInformation("Deleted team registration doc for team {TeamId}", teamId);
        }

        /// <summary>
        /// Finds the competitionTeamRegistration doc for a given team.
        /// </summary>
        private Umbraco.Cms.Core.Models.IContent? FindTeamRegistrationDoc(int competitionId, int teamId)
        {
            var competitionContent = _contentService.GetById(competitionId);
            if (competitionContent == null) return null;

            long totalChildren;
            var children = _contentService.GetPagedChildren(competitionContent.Id, 0, 100, out totalChildren).ToList();
            var registrationsHub = children.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub");

            if (registrationsHub == null) return null;

            var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out totalChildren)
                .Where(r => r.ContentType.Alias == "competitionTeamRegistration")
                .ToList();

            return registrations.FirstOrDefault(r => r.GetValue<int>("teamId") == teamId);
        }

        #endregion
    }

    #region View Models

    public class TeamWithMembers
    {
        public CompetitionTeamDto Team { get; set; } = new();
        public List<TeamMemberInfo> Members { get; set; } = new();
        public string ClubName { get; set; } = "";
    }

    public class TeamMemberInfo
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public bool IsSpare { get; set; }
    }

    public class EligibleMember
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public int ClubId { get; set; }
        public string ClubName { get; set; } = "";
        public string ShootingClass { get; set; } = "";
    }

    public class RegisteredMemberInfo
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public int ClubId { get; set; }
        public List<string> ShootingClasses { get; set; } = new();
    }

    public class TeamResultGroup
    {
        public string TeamClass { get; set; } = "";
        public List<TeamResult> Teams { get; set; } = new();
    }

    public class TeamResult
    {
        public int TeamId { get; set; }
        public string TeamName { get; set; } = "";
        public string ClubName { get; set; } = "";
        public int Rank { get; set; }
        public int TotalScore { get; set; }
        public int TotalXCount { get; set; }
        public decimal? TotalTimeSeconds { get; set; }
        public List<TeamMemberResult> MemberResults { get; set; } = new();
        public bool IsComplete { get; set; }
    }

    public class TeamMemberResult
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public int Score { get; set; }
        public int XCount { get; set; }
        public decimal TimeSeconds { get; set; }
        public bool HasResult { get; set; }
        public string? Status { get; set; }
    }

    public class ClubMemberInfo
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
    }

    #endregion
}
