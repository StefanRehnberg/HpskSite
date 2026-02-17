using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.Shared.Models;

namespace HpskSite.Services
{
    public class TrainingGroupService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberManager _memberManager;

        public TrainingGroupService(
            IUmbracoDatabaseFactory databaseFactory,
            IMemberService memberService,
            ClubService clubService,
            AdminAuthorizationService authorizationService,
            IMemberManager memberManager)
        {
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _clubService = clubService;
            _authorizationService = authorizationService;
            _memberManager = memberManager;
        }

        public List<TrainingGroup> GetTrainingGroupsForClub(int clubId, bool includeInactive = false)
        {
            using var db = _databaseFactory.CreateDatabase();

            var sql = includeInactive
                ? @"SELECT g.*,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Member') AS MemberCount,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Trainer') AS TrainerCount
                  FROM TrainingGroups g
                  WHERE g.ClubId = @0
                  ORDER BY g.IsActive DESC, g.StartDate DESC"
                : @"SELECT g.*,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Member') AS MemberCount,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Trainer') AS TrainerCount
                  FROM TrainingGroups g
                  WHERE g.ClubId = @0 AND g.IsActive = 1
                  ORDER BY g.StartDate DESC";

            var records = db.Fetch<dynamic>(sql, clubId);

            return records.Select(r => (TrainingGroup)MapTrainingGroup(r)).ToList();
        }

        public List<TrainingGroup> GetTrainingGroupsForMember(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var records = db.Fetch<dynamic>(
                @"SELECT g.*,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Member') AS MemberCount,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Trainer') AS TrainerCount
                  FROM TrainingGroups g
                  INNER JOIN TrainingGroupMembers gm ON g.Id = gm.TrainingGroupId
                  WHERE gm.MemberId = @0 AND gm.IsActive = 1 AND g.IsActive = 1
                  ORDER BY g.StartDate DESC",
                memberId);

            var groups = records.Select(r => (TrainingGroup)MapTrainingGroup(r)).ToList();

            // For each group, fetch the member's role
            foreach (var group in groups)
            {
                var memberRecord = db.Fetch<dynamic>(
                    @"SELECT Role FROM TrainingGroupMembers
                      WHERE TrainingGroupId = @0 AND MemberId = @1 AND IsActive = 1",
                    group.Id, memberId);

                if (memberRecord.Any())
                {
                    // Store the user's role in a temporary way by adding it to Members
                    group.Members.Add(new TrainingGroupMember
                    {
                        MemberId = memberId,
                        Role = (string)memberRecord.First().Role
                    });
                }

                // Also load trainer names for display
                var trainers = db.Fetch<dynamic>(
                    @"SELECT MemberId FROM TrainingGroupMembers
                      WHERE TrainingGroupId = @0 AND Role = 'Trainer' AND IsActive = 1",
                    group.Id);

                foreach (var trainer in trainers)
                {
                    int trainerId = (int)trainer.MemberId;
                    if (trainerId == memberId) continue; // already added above
                    var trainerMember = _memberService.GetById(trainerId);
                    group.Members.Add(new TrainingGroupMember
                    {
                        MemberId = trainerId,
                        Role = "Trainer",
                        MemberName = trainerMember?.Name ?? "Okänd"
                    });
                }
            }

            return groups;
        }

        public TrainingGroup? GetTrainingGroup(int trainingGroupId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var records = db.Fetch<dynamic>(
                @"SELECT g.*,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Member') AS MemberCount,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Trainer') AS TrainerCount
                  FROM TrainingGroups g
                  WHERE g.Id = @0",
                trainingGroupId);

            if (!records.Any()) return null;

            var group = MapTrainingGroup(records.First());

            // Load all members
            var memberRecords = db.Fetch<dynamic>(
                @"SELECT * FROM TrainingGroupMembers
                  WHERE TrainingGroupId = @0 AND IsActive = 1
                  ORDER BY Role DESC, JoinedDate ASC",
                trainingGroupId);

            foreach (var mr in memberRecords)
            {
                var member = _memberService.GetById((int)mr.MemberId);
                var primaryClubIdStr = member?.GetValue("primaryClubId")?.ToString();
                string? clubName = null;
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int cid))
                {
                    clubName = _clubService.GetClubNameById(cid);
                }

                group.Members.Add(new TrainingGroupMember
                {
                    Id = (int)mr.Id,
                    TrainingGroupId = (int)mr.TrainingGroupId,
                    MemberId = (int)mr.MemberId,
                    Role = (string)mr.Role,
                    JoinedDate = (DateTime)mr.JoinedDate,
                    AddedByMemberId = mr.AddedByMemberId as int?,
                    IsActive = (bool)mr.IsActive,
                    MemberName = member?.Name ?? "Okänd",
                    ClubName = clubName
                });
            }

            return group;
        }

        public TrainingGroup CreateTrainingGroup(string name, int clubId, string? description, DateTime startDate, int createdByMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            db.Insert("TrainingGroups", "Id", true, new
            {
                Name = name,
                ClubId = clubId,
                Description = description,
                StartDate = startDate,
                IsActive = true,
                CreatedDate = DateTime.Now,
                CreatedByMemberId = createdByMemberId
            });

            // Get the inserted ID
            var inserted = db.Fetch<dynamic>(
                @"SELECT TOP 1 * FROM TrainingGroups
                  WHERE Name = @0 AND ClubId = @1 AND CreatedByMemberId = @2
                  ORDER BY Id DESC",
                name, clubId, createdByMemberId);

            if (!inserted.Any())
                throw new Exception("Failed to retrieve created training group");

            return MapTrainingGroup(inserted.First());
        }

        public bool UpdateTrainingGroup(int trainingGroupId, string name, string? description, DateTime startDate, bool? isActive = null)
        {
            using var db = _databaseFactory.CreateDatabase();

            if (isActive.HasValue)
            {
                db.Execute(
                    @"UPDATE TrainingGroups
                      SET Name = @0, Description = @1, StartDate = @2, IsActive = @3
                      WHERE Id = @4",
                    name, description, startDate, isActive.Value ? 1 : 0, trainingGroupId);
            }
            else
            {
                db.Execute(
                    @"UPDATE TrainingGroups
                      SET Name = @0, Description = @1, StartDate = @2
                      WHERE Id = @3",
                    name, description, startDate, trainingGroupId);
            }

            return true;
        }

        public bool DeactivateTrainingGroup(int trainingGroupId)
        {
            using var db = _databaseFactory.CreateDatabase();

            db.Execute(
                @"UPDATE TrainingGroups SET IsActive = 0 WHERE Id = @0",
                trainingGroupId);

            return true;
        }

        public bool AddTrainingGroupMember(int trainingGroupId, int memberId, string role, int? addedByMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            // Check if member already exists (may be inactive)
            var existing = db.Fetch<dynamic>(
                @"SELECT * FROM TrainingGroupMembers
                  WHERE TrainingGroupId = @0 AND MemberId = @1",
                trainingGroupId, memberId);

            if (existing.Any())
            {
                // Reactivate if inactive
                db.Execute(
                    @"UPDATE TrainingGroupMembers
                      SET IsActive = 1, Role = @0, AddedByMemberId = @1, JoinedDate = GETDATE()
                      WHERE TrainingGroupId = @2 AND MemberId = @3",
                    role, addedByMemberId, trainingGroupId, memberId);
            }
            else
            {
                db.Insert("TrainingGroupMembers", "Id", true, new
                {
                    TrainingGroupId = trainingGroupId,
                    MemberId = memberId,
                    Role = role,
                    JoinedDate = DateTime.Now,
                    AddedByMemberId = addedByMemberId,
                    IsActive = true
                });
            }

            return true;
        }

        public bool RemoveTrainingGroupMember(int trainingGroupId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            db.Execute(
                @"UPDATE TrainingGroupMembers
                  SET IsActive = 0
                  WHERE TrainingGroupId = @0 AND MemberId = @1",
                trainingGroupId, memberId);

            return true;
        }

        public bool SetTrainingGroupMemberRole(int trainingGroupId, int memberId, string role)
        {
            using var db = _databaseFactory.CreateDatabase();

            db.Execute(
                @"UPDATE TrainingGroupMembers
                  SET Role = @0
                  WHERE TrainingGroupId = @1 AND MemberId = @2 AND IsActive = 1",
                role, trainingGroupId, memberId);

            return true;
        }

        public async Task<bool> IsTrainerForMember(int trainerMemberId, int targetMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var result = db.Fetch<dynamic>(
                @"SELECT 1
                  FROM TrainingGroupMembers trainer
                  INNER JOIN TrainingGroupMembers target ON trainer.TrainingGroupId = target.TrainingGroupId
                  INNER JOIN TrainingGroups g ON trainer.TrainingGroupId = g.Id
                  WHERE trainer.MemberId = @0 AND trainer.Role = 'Trainer' AND trainer.IsActive = 1
                    AND target.MemberId = @1 AND target.IsActive = 1
                    AND g.IsActive = 1",
                trainerMemberId, targetMemberId);

            return result.Any();
        }

        public List<TrainingGroup> GetAllTrainingGroups(string? regionFilter, bool includeInactive = false)
        {
            using var db = _databaseFactory.CreateDatabase();

            var sql = includeInactive
                ? @"SELECT g.*,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Member') AS MemberCount,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Trainer') AS TrainerCount
                  FROM TrainingGroups g
                  ORDER BY g.IsActive DESC, g.StartDate DESC"
                : @"SELECT g.*,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Member') AS MemberCount,
                    (SELECT COUNT(*) FROM TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role = 'Trainer') AS TrainerCount
                  FROM TrainingGroups g
                  WHERE g.IsActive = 1
                  ORDER BY g.StartDate DESC";

            var records = db.Fetch<dynamic>(sql);

            var groups = records.Select(r => (TrainingGroup)MapTrainingGroup(r)).ToList();

            // If region filter, filter by club region
            if (!string.IsNullOrEmpty(regionFilter))
            {
                var clubsInRegion = new HashSet<int>();
                var allClubs = _clubService.GetAllClubs();
                // We'd need to check each club's region, but ClubInfo doesn't have region.
                // For now, return all and let the controller filter.
            }

            return groups;
        }

        public async Task<bool> CanManageTrainingGroup(int trainingGroupId)
        {
            // Site admin can manage any training group
            if (await _authorizationService.IsCurrentUserAdminAsync())
                return true;

            // Get the training group to check club
            using var db = _databaseFactory.CreateDatabase();
            var records = db.Fetch<dynamic>(
                "SELECT ClubId FROM TrainingGroups WHERE Id = @0", trainingGroupId);

            if (!records.Any()) return false;
            int clubId = (int)records.First().ClubId;

            // Club admin for this club's region or club
            if (await _authorizationService.IsClubAdminForClub(clubId))
                return true;

            // Skjutledare for this club
            if (await _authorizationService.IsSkjutledareForClub(clubId))
                return true;

            // Trainer in this training group
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return false;

            var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
            if (memberData == null) return false;

            var trainerCheck = db.Fetch<dynamic>(
                @"SELECT 1 FROM TrainingGroupMembers
                  WHERE TrainingGroupId = @0 AND MemberId = @1 AND Role = 'Trainer' AND IsActive = 1",
                trainingGroupId, memberData.Id);

            return trainerCheck.Any();
        }

        public int GetTrainingGroupClubId(int trainingGroupId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var records = db.Fetch<dynamic>(
                "SELECT ClubId FROM TrainingGroups WHERE Id = @0", trainingGroupId);
            return records.Any() ? (int)records.First().ClubId : 0;
        }

        private TrainingGroup MapTrainingGroup(dynamic r)
        {
            var group = new TrainingGroup
            {
                Id = (int)r.Id,
                Name = (string)r.Name,
                ClubId = (int)r.ClubId,
                Description = r.Description as string,
                StartDate = (DateTime)r.StartDate,
                IsActive = (bool)r.IsActive,
                CreatedDate = (DateTime)r.CreatedDate,
                CreatedByMemberId = (int)r.CreatedByMemberId
            };

            // Try to get computed counts if present
            try
            {
                group.MemberCount = (int)r.MemberCount;
                group.TrainerCount = (int)r.TrainerCount;
            }
            catch
            {
                // Counts not available in this query
            }

            // Populate club name
            group.ClubName = _clubService.GetClubNameById(group.ClubId);

            return group;
        }
    }
}
