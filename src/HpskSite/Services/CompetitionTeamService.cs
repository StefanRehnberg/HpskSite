using HpskSite.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Springskytte.Models;
using Umbraco.Cms.Core.Models;
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
        private readonly PaymentService _paymentService;
        private readonly ILogger<CompetitionTeamService> _logger;

        public CompetitionTeamService(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextAccessor umbracoContextAccessor,
            AdminAuthorizationService authorizationService,
            ClubService clubService,
            IMemberService memberService,
            IContentService contentService,
            PaymentService paymentService,
            ILogger<CompetitionTeamService> logger)
        {
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
            _authorizationService = authorizationService;
            _clubService = clubService;
            _memberService = memberService;
            _contentService = contentService;
            _paymentService = paymentService;
            _logger = logger;
        }

        public async Task<(bool success, string message, int? teamId)> CreateTeamAsync(
            int competitionId, string teamName, string teamClass, int clubId,
            int[] memberIds, int? spareId, int createdByMemberId, bool isRelay = false)
        {
            var (coreMembers, maxSpares) = TeamClassHelper.GetTeamSize(teamClass);
            // Springskytte: shooters may be named later (any time before the event), so a team
            // or relay can be created with no/partial members and the fee paid up front. All
            // other disciplines still require the exact core count at creation.
            var isSpringskytteComp = GetCompetitionType(competitionId) == "Springskytte";
            var nonSpareIds = spareId.HasValue
                ? memberIds.Where(id => id != spareId.Value).ToArray()
                : memberIds;
            var spareIds = spareId.HasValue ? new[] { spareId.Value } : Array.Empty<int>();

            if (isSpringskytteComp)
            {
                if (nonSpareIds.Length > coreMembers)
                    return (false, $"Max {coreMembers} ordinarie medlemmar.", null);
            }
            else if (nonSpareIds.Length != coreMembers)
            {
                return (false, $"Laget behöver exakt {coreMembers} ordinarie medlemmar.", null);
            }

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

                // With no registration class to read a gender from, the stafett class restriction is
                // the only gate — a Dam relay must stay Dam-only.
                var genderError = ValidateStafettGender(teamClass, memberIds, memberNameLookup);
                if (genderError != null)
                    return (false, genderError, null);
            }
            else
            {
                // Standard: validate all members are registered in compatible classes
                var isSpringskytte = isSpringskytteComp;
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

            using var db = _databaseFactory.CreateDatabase();

            // Already in another team? For Springskytte this is a WARNING, not a refusal — a shooter
            // may legitimately hold one lag per weapon class plus one stafett, and blocking mid
            // registration is the worse failure. Other disciplines keep the original hard block.
            var conflicts = await FetchMembershipConflictsAsync(
                db, competitionId, teamClass, memberIds, isSpringskytteComp, excludeTeamId: null);
            string? conflictWarning = null;
            if (conflicts.Count > 0)
            {
                if (!isSpringskytteComp)
                    return (false, $"{GetName(conflicts[0].MemberId)} är redan med i ett lag i klassen {conflicts[0].TeamClass}.", null);
                conflictWarning = BuildConflictWarning(conflicts);
            }

            // Lagnamn are unique per competition in the database (UX_CompetitionTeam_Name), and a
            // club naming every one of its teams after itself is exactly what happens in practice —
            // "Västerås Pistolskyttar" as both A-lag and stafett raised a raw SqlException 2627 that
            // the modal could only report as "Ett fel uppstod vid skapande av stafettlag".
            //
            // Only a same-CLASS collision is rejected up front: that one is wrong under any index
            // shape. A different-class collision is left to the INSERT, so this code is correct both
            // before and after the index is relaxed to (CompetitionId, TeamClass, TeamName) — the DB
            // decides, and either way the user gets the same readable message instead of a stack
            // trace's worth of nothing.
            var trimmedName = (teamName ?? "").Trim();
            var sameClassClash = await db.FirstOrDefaultAsync<CompetitionTeamDto>(
                $"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE CompetitionId = @0 AND TeamName = @1 AND TeamClass = @2",
                competitionId, trimmedName, teamClass);
            if (sameClassClash != null)
                return (false, DuplicateTeamNameMessage(trimmedName, sameClassClash), null);

            // Create team
            int teamId;
            try
            {
                teamId = await db.ExecuteScalarAsync<int>(
                    @"INSERT INTO CompetitionTeam (CompetitionId, TeamName, TeamClass, ClubId, CreatedBy, CreatedAt, IsRelay)
                      VALUES (@0, @1, @2, @3, @4, @5, @6); SELECT SCOPE_IDENTITY();",
                    competitionId, trimmedName, teamClass, clubId, createdByMemberId, DateTime.UtcNow, isRelay);
            }
            catch (Exception ex) when (IsUniqueKeyViolation(ex))
            {
                _logger.LogWarning(ex, "Duplicate team name '{TeamName}' in competition {CompetitionId}", trimmedName, competitionId);
                return (false, DuplicateTeamNameMessage(trimmedName, await FindTeamByNameAsync(db, competitionId, trimmedName, null)), null);
            }

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

                CreateTeamRegistrationDoc(competitionId, teamId, trimmedName, teamClass, clubId, clubName, memberNames, isRelay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create team registration doc for team {TeamId}, continuing anyway", teamId);
            }

            // Eager team invoice: create the Pending team/relay fee invoice now (best-effort) so
            // the team carries it from creation and shows a payment status on the Anmälningar desk
            // — instead of one being lazily minted only when "Betala med Swish" is clicked.
            try
            {
                await EnsureTeamInvoiceCoreAsync(competitionId, teamId, trimmedName, clubId, isRelay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create eager team invoice for team {TeamId}, continuing anyway", teamId);
            }

            // The warning rides along on the success message so it surfaces even for callers that
            // skipped the CheckTeamMembership pre-flight the modal uses.
            return (true, conflictWarning == null ? "Laget har skapats." : $"Laget har skapats. {conflictWarning}", teamId);
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

            // Validate team size. Springskytte allows naming shooters later, so a partial/
            // empty roster is accepted on edit too (other disciplines need the exact core).
            var (coreMembers, maxSpares) = TeamClassHelper.GetTeamSize(team.TeamClass);
            var isSpringskytteComp = GetCompetitionType(team.CompetitionId) == "Springskytte";
            var nonSpareIds = spareId.HasValue
                ? memberIds.Where(id => id != spareId.Value).ToArray()
                : memberIds;
            var spareIds = spareId.HasValue ? new[] { spareId.Value } : Array.Empty<int>();

            if (isSpringskytteComp)
            {
                if (nonSpareIds.Length > coreMembers)
                    return (false, $"Max {coreMembers} ordinarie medlemmar.");
            }
            else if (nonSpareIds.Length != coreMembers)
            {
                return (false, $"Laget behöver exakt {coreMembers} ordinarie medlemmar.");
            }

            if (spareIds.Length > maxSpares)
                return (false, $"Max {maxSpares} reserv(er) tillåtna.");

            // Re-validate eligibility on roster edit — creation checks this, editing used not to, so
            // a Herr could be swapped into a Damlag (or a man into a Dam-stafett) after the fact.
            if (team.IsRelay)
            {
                var relayNames = memberIds.ToDictionary(id => id, id => GetMemberDisplayName(id));
                var genderError = ValidateStafettGender(team.TeamClass, memberIds, relayNames);
                if (genderError != null)
                    return (false, genderError);
            }
            else if (memberIds.Length > 0)
            {
                var compatible = TeamClassHelper.GetCompatibleIndividualClasses(team.TeamClass, isSpringskytteComp);
                var eligible = GetRegisteredMembersInClasses(team.CompetitionId, compatible, isSpringskytteComp);
                var eligibleIds = eligible.Select(r => r.MemberId).ToHashSet();
                var eligibleNames = eligible.ToDictionary(r => r.MemberId, r => r.Name);
                foreach (var memberId in memberIds)
                {
                    if (!eligibleIds.Contains(memberId))
                    {
                        var who = eligibleNames.GetValueOrDefault(memberId, GetMemberDisplayName(memberId));
                        return (false, $"{who} är inte anmäld i en kompatibel klass för {team.TeamClass}.");
                    }
                }
            }

            // Same advisory as creation — a shooter added to this roster who already holds a
            // same-bucket team elsewhere is reported, never refused.
            var editConflicts = await FetchMembershipConflictsAsync(
                db, team.CompetitionId, team.TeamClass, memberIds, isSpringskytteComp, excludeTeamId: teamId);
            var editWarning = editConflicts.Count > 0 ? BuildConflictWarning(editConflicts) : null;

            // Update team name — same uniqueness story as creation (see CreateTeamAsync): reject a
            // same-class clash up front, let the DB rule on the rest.
            if (!string.IsNullOrWhiteSpace(teamName))
            {
                var newName = teamName.Trim();
                var renameClash = await db.FirstOrDefaultAsync<CompetitionTeamDto>(
                    $"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE CompetitionId = @0 AND TeamName = @1 AND TeamClass = @2 AND Id <> @3",
                    team.CompetitionId, newName, team.TeamClass, teamId);
                if (renameClash != null)
                    return (false, DuplicateTeamNameMessage(newName, renameClash));

                try
                {
                    await db.ExecuteAsync(
                        "UPDATE CompetitionTeam SET TeamName = @0 WHERE Id = @1",
                        newName, teamId);
                }
                catch (Exception ex) when (IsUniqueKeyViolation(ex))
                {
                    _logger.LogWarning(ex, "Duplicate team name '{TeamName}' on rename of team {TeamId}", newName, teamId);
                    return (false, DuplicateTeamNameMessage(newName, await FindTeamByNameAsync(db, team.CompetitionId, newName, teamId)));
                }
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
                Dictionary<int, string> nameLookup;
                if (team.IsRelay)
                {
                    // Relay members aren't individually registered — resolve names directly.
                    nameLookup = new Dictionary<int, string>();
                    foreach (var id in memberIds)
                    {
                        var member = _memberService.GetById(id);
                        nameLookup[id] = member != null
                            ? $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}"
                            : $"Medlem #{id}";
                    }
                }
                else
                {
                    var compatClasses = TeamClassHelper.GetCompatibleIndividualClasses(team.TeamClass, isSpringskytteComp);
                    var regMembers = GetRegisteredMembersInClasses(team.CompetitionId, compatClasses, isSpringskytteComp);
                    nameLookup = regMembers.ToDictionary(r => r.MemberId, r => r.Name);
                }
                string MemberName(int id) => nameLookup.GetValueOrDefault(id, $"Medlem #{id}");

                var memberNames = nonSpareIds.Select(id => MemberName(id)).ToList();
                if (spareIds.Length > 0) memberNames.AddRange(spareIds.Select(id => $"{MemberName(id)} (reserv)"));

                UpdateTeamRegistrationDoc(team.CompetitionId, teamId, team.TeamName, memberNames);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update team registration doc for team {TeamId}", teamId);
            }

            return (true, editWarning == null ? "Laget har uppdaterats." : $"Laget har uppdaterats. {editWarning}");
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

        /// <summary>
        /// Returns the owning club id of a team (0 if not found). Used for edit/delete
        /// authorization — a team is always owned by a single club.
        /// </summary>
        public async Task<int> GetTeamClubIdAsync(int teamId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var team = await db.FirstOrDefaultAsync<CompetitionTeamDto>(
                $"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE Id = @0", teamId);
            return team?.ClubId ?? 0;
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

            // Springskytte: being in another team is a WARNING at save time, not a bar to entry, so
            // those members must stay SELECTABLE here — hiding them would make the rule a silent
            // block again. Other disciplines still hard-block on create, so keep hiding them there.
            if (isSpringskytte)
                return clubMembers;

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

            // Springskytte range-master penalties/reductions live in their own ledger and are folded
            // into a shooter's total by SpringskytteController.ApplyTimeAdjustmentsAsync — the raw
            // SpringskytteResultEntry.TotalTimeSeconds does NOT include them. Reading the entry alone
            // therefore understated a team whose member picked up a straff (the individual result list
            // showed 21:15 while the Lag total counted 19:15). Load the same ledger and apply the same
            // net delta, keyed (MemberId|WeaponClass) exactly as the individual path keys it.
            var netAdjustmentByKey = new Dictionary<string, int>();
            if (isSpringskytte)
            {
                try
                {
                    var adjustments = await db.FetchAsync<SpringskytteTimeAdjustment>(
                        "SELECT * FROM SpringskytteTimeAdjustment WHERE CompetitionId = @0", competitionId);
                    netAdjustmentByKey = adjustments
                        .GroupBy(a => $"{a.MemberId}|{a.WeaponClass}")
                        .ToDictionary(g => g.Key, g => g.Sum(a => a.Seconds));
                }
                catch (Exception ex)
                {
                    // Same posture as the individual path: a missing/unmigrated table must not take
                    // the whole team result list down.
                    _logger.LogWarning(ex, "Could not load Springskytte time adjustments for comp {Comp}", competitionId);
                }
            }

            // Group teams by class
            var teamsByClass = teams.GroupBy(t => t.Team.TeamClass);

            foreach (var classGroup in teamsByClass)
            {
                var teamResults = new List<TeamResult>();

                // Springskytte team classes are weapon-group scoped ("C-Herrar", "A-Damer", …) and at a
                // two-day competition most shooters register in BOTH A and C, so a member can hold two
                // result entries. Pin the lookup to this team's weapon group — an unscoped
                // FirstOrDefault could score a C team off the member's A run.
                var teamWeaponClass =
                    classGroup.Key.StartsWith("A-", StringComparison.OrdinalIgnoreCase) ? "A" :
                    classGroup.Key.StartsWith("C-", StringComparison.OrdinalIgnoreCase) ? "C" : null;

                foreach (var teamWithMembers in classGroup)
                {
                    // Only count non-spare members
                    var coreMembers = teamWithMembers.Members.Where(m => !m.IsSpare).ToList();

                    // An UNDER-STRENGTH team is NOT complete. Springskytte lets a team be created
                    // name-only with the roster deferred until just before the start, so a short
                    // roster is a normal intermediate state — but the loops below only iterate the
                    // members that ARE named, so a 0-of-3 team kept allComplete=true with a total of
                    // 0 and sorted FIRST (rank 1, "0:00.00") ahead of every team that actually shot,
                    // and a 2-of-3 team was ranked on two runners' time against rivals who ran three.
                    // A team only places once its full core roster is named and has results.
                    var (requiredCore, _) = TeamClassHelper.GetTeamSize(classGroup.Key);
                    var rosterComplete = coreMembers.Count >= requiredCore;

                    if (isSpringskytte)
                    {
                        var memberResults = new List<TeamMemberResult>();
                        decimal totalTime = 0;
                        bool allComplete = true;

                        foreach (var member in coreMembers)
                        {
                            var entry = teamWeaponClass != null
                                ? await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                                    "WHERE CompetitionId = @0 AND MemberId = @1 AND WeaponClass = @2",
                                    competitionId, member.MemberId, teamWeaponClass)
                                : await db.FirstOrDefaultAsync<SpringskytteResultEntry>(
                                    "WHERE CompetitionId = @0 AND MemberId = @1",
                                    competitionId, member.MemberId);

                            if (entry?.TotalTimeSeconds != null && string.IsNullOrEmpty(entry.Status))
                            {
                                var memberTime = entry.TotalTimeSeconds.Value
                                    + (netAdjustmentByKey.TryGetValue($"{member.MemberId}|{entry.WeaponClass}", out var net) ? net : 0);

                                totalTime += memberTime;
                                memberResults.Add(new TeamMemberResult
                                {
                                    MemberId = member.MemberId,
                                    Name = member.Name,
                                    Score = 0,
                                    TimeSeconds = memberTime,
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
                            TotalTimeSeconds = (allComplete && rosterComplete) ? totalTime : null,
                            MemberResults = memberResults,
                            IsComplete = allComplete && rosterComplete,
                            IsRelay = teamWithMembers.Team.IsRelay
                        });
                    }
                    else
                    {
                        // Standard: sum of individual scores over the first `numberOfSeries`
                        // series (resolved by the controller from the organiser's
                        // "Antal serier i lagresultat" setting, or the qualification series
                        // count by default). Entries are ordered by SeriesNumber, so taking
                        // the first N naturally excludes any finals series.
                        var seriesToCount = numberOfSeries > 0 ? numberOfSeries : 7;
                        var memberResults = new List<TeamMemberResult>();
                        int totalScore = 0;
                        int totalXCount = 0;
                        bool allComplete = true;

                        // Duell / Milsnabb / MagnumPrecision / NationellHelmatch store results
                        // in their own tables (all inherit PrecisionResultEntry's schema), so a
                        // hard-coded PrecisionResultEntry query zeroed their team totals.
                        var resultTable = GetResultTableName(competitionType);

                        foreach (var member in coreMembers)
                        {
                            var entries = await db.FetchAsync<PrecisionResultEntry>(
                                $"SELECT * FROM {resultTable} WHERE CompetitionId = @0 AND MemberId = @1 ORDER BY SeriesNumber",
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
                            IsComplete = allComplete && rosterComplete,
                            IsRelay = teamWithMembers.Team.IsRelay
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
                        Name = $"{firstName} {lastName}".Trim(),
                        // Lets the stafett picker grey out members the class can't take. Empty =
                        // unknown, which the server treats as allowed (see ValidateStafettGender).
                        Gender = GenderFromValues(
                            member.GetValue<string>("gender"),
                            member.GetValue<string>("personNumber")) ?? ""
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

        /// <summary>
        /// The one message for a team-name collision. Says WHO holds the name (class + club) when
        /// we know it: the constraint spans the whole competition, so the clashing team is usually
        /// invisible from the modal the user is standing in — another club that took a generic
        /// "Lag 1", or the same club's lagtävling team rather than their stafett.
        /// </summary>
        private string DuplicateTeamNameMessage(string teamName, CompetitionTeamDto? existing)
        {
            var who = "";
            if (existing != null)
            {
                var clubName = existing.ClubId > 0 ? _clubService.GetClubNameById(existing.ClubId) : null;
                who = string.IsNullOrWhiteSpace(clubName)
                    ? $" ({existing.TeamClass})"
                    : $" ({existing.TeamClass}, {clubName})";
            }
            // The advice is deliberately "add the class or a number", not "use the club name" —
            // the name that collides is usually the club's own name already.
            return $"Det finns redan ett lag som heter \"{teamName}\" i tävlingen{who}. " +
                   $"Välj ett annat lagnamn, t.ex. \"{teamName} 2\" eller med lagklassen i namnet.";
        }

        /// <summary>The team holding <paramref name="teamName"/> in the competition, for the message above.</summary>
        private static async Task<CompetitionTeamDto?> FindTeamByNameAsync(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId, string teamName, int? excludeTeamId)
        {
            try
            {
                return await db.FirstOrDefaultAsync<CompetitionTeamDto>(
                    $"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE CompetitionId = @0 AND TeamName = @1 AND Id <> @2",
                    competitionId, teamName, excludeTeamId ?? 0);
            }
            catch { return null; }
        }

        /// <summary>
        /// True for SQL Server unique index/constraint violations (2601 / 2627). NPoco wraps driver
        /// exceptions, so walk the inner chain rather than matching only the outermost type.
        /// </summary>
        private static bool IsUniqueKeyViolation(Exception? ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is Microsoft.Data.SqlClient.SqlException sqlEx &&
                    (sqlEx.Number == 2601 || sqlEx.Number == 2627))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Other teams in the competition that already hold one of <paramref name="memberIds"/> in the
        /// SAME bucket as <paramref name="teamClass"/>. Public entry point for the pre-save check the
        /// registration modals do so they can warn before writing anything.
        /// </summary>
        public async Task<List<TeamMembershipConflict>> GetMembershipConflictsAsync(
            int competitionId, string teamClass, int[] memberIds, int? excludeTeamId = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            var isSpringskytte = GetCompetitionType(competitionId) == "Springskytte";
            return await FetchMembershipConflictsAsync(db, competitionId, teamClass, memberIds, isSpringskytte, excludeTeamId);
        }

        private async Task<List<TeamMembershipConflict>> FetchMembershipConflictsAsync(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
            int competitionId, string teamClass, int[] memberIds, bool isSpringskytte, int? excludeTeamId)
        {
            var result = new List<TeamMembershipConflict>();
            if (memberIds == null || memberIds.Length == 0) return result;

            var rows = await db.FetchAsync<MembershipRow>(
                @"SELECT ctm.MemberId, ctm.IsSpare, ct.Id AS TeamId, ct.TeamName, ct.TeamClass
                  FROM CompetitionTeamMember ctm
                  INNER JOIN CompetitionTeam ct ON ct.Id = ctm.TeamId
                  WHERE ct.CompetitionId = @0",
                competitionId);

            var wanted = memberIds.ToHashSet();
            foreach (var row in rows)
            {
                if (!wanted.Contains(row.MemberId)) continue;
                if (excludeTeamId.HasValue && row.TeamId == excludeTeamId.Value) continue;
                if (!SharesTeamBucket(teamClass, row.TeamClass, isSpringskytte)) continue;

                result.Add(new TeamMembershipConflict
                {
                    MemberId = row.MemberId,
                    MemberName = GetMemberDisplayName(row.MemberId),
                    TeamId = row.TeamId,
                    TeamName = row.TeamName,
                    TeamClass = row.TeamClass,
                    IsSpare = row.IsSpare
                });
            }
            return result;
        }

        /// <summary>
        /// Do two team classes compete for the same slot on a shooter?
        /// Springskytte (Stefan, 2026-08-03): one lag PER WEAPON CLASS plus one stafett is fine, so
        /// the bucket is (kind, weapon class) — A-Herrar collides with A-Damer but not with C-Herrar
        /// and never with a stafett. Every stafett collides with every other stafett (always class C).
        /// Other disciplines keep their original, narrower rule: the exact same team class.
        /// </summary>
        private static bool SharesTeamBucket(string a, string b, bool isSpringskytte)
        {
            if (!isSpringskytte)
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            var aRelay = TeamClassHelper.IsStafettClass(a);
            var bRelay = TeamClassHelper.IsStafettClass(b);
            if (aRelay != bRelay) return false;   // a lag and a stafett never collide
            if (aRelay) return true;              // stafett is always weapon class C

            var wa = TeamClassHelper.GetSpringskytteWeaponGroup(a);
            var wb = TeamClassHelper.GetSpringskytteWeaponGroup(b);
            return wa.Length > 0 && string.Equals(wa, wb, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildConflictWarning(List<TeamMembershipConflict> conflicts)
        {
            var parts = conflicts.Select(c =>
                $"{c.MemberName} är även med i {c.TeamName} ({c.TeamClass})");
            return "OBS: " + string.Join("; ", parts) + ".";
        }

        private class MembershipRow
        {
            public int MemberId { get; set; }
            public bool IsSpare { get; set; }
            public int TeamId { get; set; }
            public string TeamName { get; set; } = "";
            public string TeamClass { get; set; } = "";
        }

        private string GetMemberDisplayName(int memberId)
        {
            try
            {
                var member = _memberService.GetById(memberId);
                if (member != null)
                    return $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}".Trim();
            }
            catch { }
            return $"Medlem #{memberId}";
        }

        /// <summary>
        /// Resolves a member's gender as "M" / "F", or null when it cannot be determined.
        /// The `gender` member property is authoritative ("Man"/"Kvinna" from the profile dropdown,
        /// or whatever a member CSV import wrote); personnummer is the fallback, since the
        /// second-to-last digit is odd for men and even for women.
        /// </summary>
        public string? ResolveMemberGender(int memberId)
        {
            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null) return null;

                return GenderFromValues(
                    member.GetValue<string>("gender"),
                    member.GetValue<string>("personNumber"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve gender for member {MemberId}", memberId);
            }
            return null;
        }

        /// <summary>Gender ("M"/"F"/null) from the raw property values. See ResolveMemberGender.</summary>
        internal static string? GenderFromValues(string? gender, string? personNumber)
        {
            var raw = (gender ?? "").Trim();
            if (raw.Length > 0)
            {
                // "Kvinna" / "Kvinnligt" / "F(emale)" / "Dam"
                if (raw.StartsWith("K", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("F", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("D", StringComparison.OrdinalIgnoreCase))
                    return "F";
                // "Man" / "Male" / "Herr"
                if (raw.StartsWith("M", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("H", StringComparison.OrdinalIgnoreCase))
                    return "M";
            }

            var digits = new string((personNumber ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length >= 10)
                return (digits[digits.Length - 2] - '0') % 2 == 1 ? "M" : "F";

            return null;
        }

        /// <summary>
        /// Enforces a stafett class's gender restriction. Returns an error message, or null when OK.
        /// A member whose gender cannot be determined is ALLOWED through — refusing on missing data
        /// would block legitimate registrations for every member without a gender or personnummer on
        /// file, which is a worse failure than a wrongly-entered relay the organiser can correct.
        /// </summary>
        private string? ValidateStafettGender(string teamClass, int[] memberIds, Dictionary<int, string> nameLookup)
        {
            var restriction = TeamClassHelper.GetStafettGenderRestriction(teamClass);
            if (restriction == null || memberIds.Length == 0) return null;

            foreach (var memberId in memberIds)
            {
                var gender = ResolveMemberGender(memberId);
                if (gender == null || gender == restriction) continue;

                var who = nameLookup.GetValueOrDefault(memberId, GetMemberDisplayName(memberId));
                return restriction == "F"
                    ? $"{who} kan inte ingå i {teamClass} – klassen är endast för damer."
                    : $"{who} kan inte ingå i {teamClass}.";
            }
            return null;
        }

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
        /// Public entry point: ensure a team/relay has its Pending fee invoice and return its
        /// id (0 when none applies). Resolves the team from SQL, then delegates to the core.
        /// Used by the Anmälningar desk to mark a team's fee paid even if it was created before
        /// eager invoicing (or with "Betala senare").
        /// </summary>
        public async Task<int> EnsureTeamInvoiceAsync(int competitionId, int teamId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var team = await db.FirstOrDefaultAsync<CompetitionTeamDto>(
                $"SELECT {TeamSelectCols} FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
            if (team == null) return 0;
            var invoice = await EnsureTeamInvoiceCoreAsync(competitionId, team.Id, team.TeamName, team.ClubId, team.IsRelay);
            return invoice?.Id ?? 0;
        }

        /// <summary>
        /// Create (or return the existing) Pending team/relay fee invoice for a team.
        /// Idempotent — returns the existing non-cancelled team invoice when present. Returns
        /// null for external competitions or a 0 fee. Team invoices use memberId "team-{teamId}".
        /// </summary>
        private async Task<IContent?> EnsureTeamInvoiceCoreAsync(int competitionId, int teamId, string teamName, int clubId, bool isRelay)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.GetValue<bool>("isExternal")) return null;

            var feeProp = isRelay ? "stafettRegistrationFee" : "teamRegistrationFee";
            var feeStr = competition.GetValue<string>(feeProp) ?? "0";
            if (!decimal.TryParse(feeStr, out var fee) || fee <= 0) return null;

            var teamMemberId = $"team-{teamId}";
            var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (hub != null)
            {
                var existing = _contentService.GetPagedChildren(hub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "registrationInvoice"
                        && c.GetValue<string>("memberId") == teamMemberId
                        && (c.GetValue<string>("paymentStatus") ?? "") != "Cancelled")
                    .OrderByDescending(c => c.Id)
                    .FirstOrDefault();
                if (existing != null) return existing;
            }

            var clubName = _clubService.GetClubNameById(clubId) ?? "Okänd förening";
            var teamRegDoc = FindTeamRegistrationDoc(competitionId, teamId);
            return await _paymentService.CreateTeamInvoiceAsync(
                competitionId, teamId, teamName, clubName, fee, teamRegDoc?.Id ?? 0);
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

        /// <summary>
        /// True for stafett (relay) teams. Relay is scored on ONE elapsed clock per team
        /// (see SpringskytteStafettResultEntry), NOT by summing member rows the way this
        /// calculation does — so consumers must not present a relay row as a Lag result.
        /// </summary>
        public bool IsRelay { get; set; }
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

    /// <summary>
    /// A member already sitting in another team that competes for the same slot. Advisory only for
    /// Springskytte — see SharesTeamBucket.
    /// </summary>
    public class TeamMembershipConflict
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public int TeamId { get; set; }
        public string TeamName { get; set; } = "";
        public string TeamClass { get; set; } = "";
        public bool IsSpare { get; set; }
    }

    public class ClubMemberInfo
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";

        /// <summary>"M", "F", or "" when unknown.</summary>
        public string Gender { get; set; } = "";
    }

    #endregion
}
