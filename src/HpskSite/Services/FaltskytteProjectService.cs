using HpskSite.CompetitionTypes.Faltskytte.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// CRUD + authorization for Fältskytte "Projekt" — lightweight containers that
    /// group standalone Fältskytte configurations.
    ///
    /// Authorization model (Phase 1):
    ///   - Owner can view + edit (metadata, members, archive) + delete.
    ///   - Site admins can do anything.
    ///   - Members can view the project and get view + edit on its configs
    ///     (the config-access rollup is enforced in FaltskytteConfigurationService).
    ///   - Status 'Archived' hides the project + its configs from the default listing.
    /// </summary>
    public class FaltskytteProjectService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<FaltskytteProjectService> _logger;
        private readonly AdminAuthorizationService _authService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;

        public FaltskytteProjectService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<FaltskytteProjectService> logger,
            AdminAuthorizationService authService,
            IMemberManager memberManager,
            IMemberService memberService,
            ClubService clubService)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
            _authService = authService;
            _memberManager = memberManager;
            _memberService = memberService;
            _clubService = clubService;
        }

        // ── Status constants ─────────────────────────────────────────────

        public const string StatusActive = "Active";
        public const string StatusArchived = "Archived";

        public static string NormalizeStatus(string? raw) =>
            string.IsNullOrEmpty(raw) ? StatusActive : raw;

        public static bool IsArchived(FaltskytteProject project) =>
            NormalizeStatus(project?.Status) == StatusArchived;

        // ── Current-member helper ────────────────────────────────────────

        public async Task<int?> GetCurrentMemberIdAsync()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return null;
            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            return member?.Id;
        }

        // ── Reads ────────────────────────────────────────────────────────

        public async Task<FaltskytteProject?> GetByIdAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<FaltskytteProject>("WHERE Id = @0", id);
        }

        /// <summary>True if the member is the owner of, or a member on, the given project.</summary>
        public async Task<bool> IsMemberOrOwnerAsync(int projectId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM FaltskytteProject WHERE Id = @0 AND OwnerMemberId = @1",
                projectId, memberId);
            if (count > 0) return true;
            var memberCount = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskytteProjectMember WHERE ProjectId = @0 AND MemberId = @1",
                projectId, memberId);
            return memberCount > 0;
        }

        public async Task<List<FaltskytteProjectMember>> GetMembersAsync(int projectId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<FaltskytteProjectMember>(
                "WHERE ProjectId = @0 ORDER BY AddedDate", projectId);
        }

        /// <summary>
        /// Returns every project the member can see (owned, member-on, or — for site
        /// admins — all), ordered by ModifiedDate desc.
        /// </summary>
        public async Task<List<FaltskytteProject>> ListAccessibleAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (await _authService.IsCurrentUserAdminAsync())
                return await db.FetchAsync<FaltskytteProject>("ORDER BY ModifiedDate DESC");

            return await db.FetchAsync<FaltskytteProject>(
                @"WHERE OwnerMemberId = @0
                     OR Id IN (SELECT ProjectId FROM FaltskytteProjectMember WHERE MemberId = @0)
                   ORDER BY ModifiedDate DESC", memberId);
        }

        // ── Authorization ────────────────────────────────────────────────

        /// <summary>Owner + site admin may edit project metadata / members / archive.</summary>
        public async Task<bool> CanEditAsync(FaltskytteProject project, int? memberId)
        {
            if (project == null || memberId == null) return false;
            if (project.OwnerMemberId == memberId.Value) return true;
            return await _authService.IsCurrentUserAdminAsync();
        }

        // ── Writes ───────────────────────────────────────────────────────

        public async Task<(bool Success, string? Message, FaltskytteProject? Created)> CreateAsync(
            CreateFaltskytteProjectRequest request, int ownerMemberId)
        {
            if (request == null) return (false, "Ogiltig förfrågan (saknar body).", null);
            if (string.IsNullOrWhiteSpace(request.Name)) return (false, "Namn krävs.", null);

            var now = DateTime.Now;
            var project = new FaltskytteProject
            {
                Name = request.Name.Trim(),
                Description = request.Description,
                OwnerMemberId = ownerMemberId,
                OwnerClubId = request.OwnerClubId,
                Status = StatusActive,
                CreatedDate = now,
                ModifiedDate = now
            };

            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(project);
            return (true, null, project);
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(
            UpdateFaltskytteProjectRequest request, int memberId)
        {
            if (request == null) return (false, "Ogiltig förfrågan (saknar body).");
            using var db = _databaseFactory.CreateDatabase();
            var project = await db.SingleOrDefaultAsync<FaltskytteProject>("WHERE Id = @0", request.Id);
            if (project == null) return (false, "Projektet hittades inte.");
            if (!await CanEditAsync(project, memberId))
                return (false, "Endast ägare eller administratör kan ändra projektet.");

            if (request.Name != null)
            {
                if (string.IsNullOrWhiteSpace(request.Name)) return (false, "Namn krävs.");
                project.Name = request.Name.Trim();
            }
            if (request.Description != null) project.Description = request.Description;
            if (request.OwnerClubId.HasValue) project.OwnerClubId = request.OwnerClubId.Value;

            project.ModifiedDate = DateTime.Now;
            await db.UpdateAsync(project);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> SetStatusAsync(
            int projectId, string status, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var project = await db.SingleOrDefaultAsync<FaltskytteProject>("WHERE Id = @0", projectId);
            if (project == null) return (false, "Projektet hittades inte.");
            if (!await CanEditAsync(project, memberId))
                return (false, "Endast ägare eller administratör kan arkivera projektet.");

            project.Status = status == StatusArchived ? StatusArchived : StatusActive;
            project.ModifiedDate = DateTime.Now;
            await db.UpdateAsync(project);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteAsync(int projectId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var project = await db.SingleOrDefaultAsync<FaltskytteProject>("WHERE Id = @0", projectId);
            if (project == null) return (false, "Projektet hittades inte.");
            if (!await CanEditAsync(project, memberId))
                return (false, "Endast ägare eller administratör kan ta bort projektet.");

            // FK on FaltskytteConfiguration.ProjectId is ON DELETE SET NULL, so configs
            // survive as standalone; the member-list cascades.
            await db.ExecuteAsync("DELETE FROM FaltskytteProject WHERE Id = @0", projectId);
            return (true, null);
        }

        // ── Members ──────────────────────────────────────────────────────

        public async Task<(bool Success, string? Message)> AddMemberAsync(int projectId, int memberId, int actorMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var project = await db.SingleOrDefaultAsync<FaltskytteProject>("WHERE Id = @0", projectId);
            if (project == null) return (false, "Projektet hittades inte.");
            if (!await CanEditAsync(project, actorMemberId))
                return (false, "Endast ägare eller administratör kan hantera medlemmar.");

            var existing = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskytteProjectMember WHERE ProjectId = @0 AND MemberId = @1",
                projectId, memberId);
            if (existing > 0) return (true, null); // idempotent

            await db.InsertAsync(new FaltskytteProjectMember
            {
                ProjectId = projectId,
                MemberId = memberId,
                AddedDate = DateTime.Now
            });
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> RemoveMemberAsync(int projectId, int memberId, int actorMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var project = await db.SingleOrDefaultAsync<FaltskytteProject>("WHERE Id = @0", projectId);
            if (project == null) return (false, "Projektet hittades inte.");
            if (!await CanEditAsync(project, actorMemberId))
                return (false, "Endast ägare eller administratör kan hantera medlemmar.");

            await db.ExecuteAsync(
                "DELETE FROM FaltskytteProjectMember WHERE ProjectId = @0 AND MemberId = @1",
                projectId, memberId);
            return (true, null);
        }

        // ── View-model builder ───────────────────────────────────────────

        public async Task<FaltskytteProjectView> BuildViewAsync(FaltskytteProject project, int? memberId)
        {
            var members = await GetMembersAsync(project.Id);

            // Rollup over the project's configs: total + per ApprovalStatus.
            int configCount = 0, approved = 0, pending = 0;
            using (var db = _databaseFactory.CreateDatabase())
            {
                configCount = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM FaltskytteConfiguration WHERE ProjectId = @0", project.Id);
                approved = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM FaltskytteConfiguration WHERE ProjectId = @0 AND ApprovalStatus = @1",
                    project.Id, FaltskytteConfigurationService.StatusApproved);
                pending = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM FaltskytteConfiguration WHERE ProjectId = @0 AND ApprovalStatus = @1",
                    project.Id, FaltskytteConfigurationService.StatusPendingApproval);
            }

            return new FaltskytteProjectView
            {
                Id = project.Id,
                Name = project.Name,
                Description = project.Description,
                OwnerMemberId = project.OwnerMemberId,
                OwnerMemberName = ResolveMemberName(project.OwnerMemberId) ?? $"Medlem {project.OwnerMemberId}",
                OwnerClubId = project.OwnerClubId,
                OwnerClubName = project.OwnerClubId.HasValue ? _clubService.GetClubNameById(project.OwnerClubId.Value) : null,
                Status = NormalizeStatus(project.Status),
                IsArchived = IsArchived(project),
                CreatedDate = project.CreatedDate,
                ModifiedDate = project.ModifiedDate,
                Members = members.Select(m => new ProjectMemberView
                {
                    MemberId = m.MemberId,
                    MemberName = ResolveMemberName(m.MemberId) ?? $"Medlem {m.MemberId}",
                    AddedDate = m.AddedDate
                }).ToList(),
                CanEdit = await CanEditAsync(project, memberId),
                CanDelete = await CanEditAsync(project, memberId),
                ConfigCount = configCount,
                ApprovedConfigCount = approved,
                PendingConfigCount = pending
            };
        }

        private string? ResolveMemberName(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m == null) return null;
            var first = m.GetValue<string>("firstName");
            var last = m.GetValue<string>("lastName");
            var full = $"{first} {last}".Trim();
            return string.IsNullOrEmpty(full) ? m.Name : full;
        }
    }
}
