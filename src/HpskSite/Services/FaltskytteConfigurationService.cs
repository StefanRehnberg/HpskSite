using HpskSite.CompetitionTypes.Faltskytte.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// CRUD + authorization for standalone Fältskytte station configurations.
    /// Authorization model:
    ///   - Owner can always view + edit + delete.
    ///   - Collaborators can view + edit (not delete).
    ///   - Site admins can view + edit + delete anything.
    ///   - SecretUntil overrides visibility: while active, only owner + collaborators see it.
    ///   - Otherwise Visibility tier decides:
    ///       Private — owner + collaborators only.
    ///       Club    — anyone with admin tier in OwnerClubId.
    ///       Region  — anyone with regional admin in OwnerClubId's region.
    ///       Public  — any authenticated user.
    /// </summary>
    public class FaltskytteConfigurationService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<FaltskytteConfigurationService> _logger;
        private readonly AdminAuthorizationService _authService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ClubService _clubService;

        public FaltskytteConfigurationService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<FaltskytteConfigurationService> logger,
            AdminAuthorizationService authService,
            IMemberManager memberManager,
            IMemberService memberService,
            IUmbracoContextAccessor umbracoContextAccessor,
            ClubService clubService)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
            _authService = authService;
            _memberManager = memberManager;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _clubService = clubService;
        }

        // ── Visibility constants ─────────────────────────────────────────

        public const string VisibilityPrivate = "Private";
        public const string VisibilityClub = "Club";
        public const string VisibilityRegion = "Region";
        public const string VisibilityPublic = "Public";

        private static readonly HashSet<string> ValidVisibilities = new(StringComparer.OrdinalIgnoreCase)
        { VisibilityPrivate, VisibilityClub, VisibilityRegion, VisibilityPublic };

        // ── Current-member helper ────────────────────────────────────────

        /// <summary>Resolves the current member's id, or null when not logged in.</summary>
        public async Task<int?> GetCurrentMemberIdAsync()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return null;
            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            return member?.Id;
        }

        // ── Authorization ────────────────────────────────────────────────

        /// <summary>True if the given member is permitted to view this configuration.</summary>
        public async Task<bool> CanViewAsync(FaltskytteConfiguration config, int? viewerMemberId)
        {
            if (config == null || viewerMemberId == null) return false;

            // Owner — always yes.
            if (config.OwnerMemberId == viewerMemberId.Value) return true;

            // Site admin — always yes.
            if (await _authService.IsCurrentUserAdminAsync()) return true;

            // Collaborator — always yes (overrides secrecy + visibility tier).
            if (await IsCollaboratorAsync(config.Id, viewerMemberId.Value)) return true;

            // Secrecy gate — while active, only owner + collaborators (handled above).
            if (config.SecretUntil.HasValue && config.SecretUntil.Value > DateTime.Now) return false;

            // Visibility tier.
            return config.Visibility switch
            {
                VisibilityPublic => true,
                VisibilityRegion => await IsRegionalAdminForOwnerClubAsync(config.OwnerClubId),
                VisibilityClub   => config.OwnerClubId.HasValue && await _authService.IsClubAdminForClub(config.OwnerClubId.Value),
                _                => false // Private + unknown → deny.
            };
        }

        /// <summary>True if the given member can edit this configuration (owner, collaborator, or site admin).</summary>
        public async Task<bool> CanEditAsync(FaltskytteConfiguration config, int? viewerMemberId)
        {
            if (config == null || viewerMemberId == null) return false;
            if (config.OwnerMemberId == viewerMemberId.Value) return true;
            if (await _authService.IsCurrentUserAdminAsync()) return true;
            return await IsCollaboratorAsync(config.Id, viewerMemberId.Value);
        }

        /// <summary>True if the given member can delete (only owner + site admin, not collaborators).</summary>
        public async Task<bool> CanDeleteAsync(FaltskytteConfiguration config, int? viewerMemberId)
        {
            if (config == null || viewerMemberId == null) return false;
            if (config.OwnerMemberId == viewerMemberId.Value) return true;
            return await _authService.IsCurrentUserAdminAsync();
        }

        private async Task<bool> IsRegionalAdminForOwnerClubAsync(int? ownerClubId)
        {
            if (!ownerClubId.HasValue) return false;
            var regionCode = GetClubRegionCode(ownerClubId.Value);
            if (string.IsNullOrEmpty(regionCode)) return false;
            return await _authService.IsRegionalAdminForRegion(regionCode);
        }

        private string? GetClubRegionCode(int clubId)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
            var clubNode = ctx.Content.GetById(clubId);
            return clubNode?.Value<string>("regionalFederation");
        }

        // ── Reads ────────────────────────────────────────────────────────

        public async Task<FaltskytteConfiguration?> GetByIdAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<FaltskytteConfiguration>("WHERE Id = @0", id);
        }

        /// <summary>
        /// Returns all configurations the given member can view, ordered by ModifiedDate desc.
        /// Site admins get everything. Other callers get: owned + collaborator-on + accessible-via-visibility.
        /// </summary>
        public async Task<List<FaltskytteConfiguration>> ListAccessibleAsync(int? viewerMemberId)
        {
            if (viewerMemberId == null) return new();
            using var db = _databaseFactory.CreateDatabase();
            var all = await db.FetchAsync<FaltskytteConfiguration>("ORDER BY ModifiedDate DESC");

            var visible = new List<FaltskytteConfiguration>();
            foreach (var cfg in all)
            {
                if (await CanViewAsync(cfg, viewerMemberId)) visible.Add(cfg);
            }
            return visible;
        }

        public async Task<List<FaltskytteConfigurationCollaborator>> GetCollaboratorsAsync(int configId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<FaltskytteConfigurationCollaborator>(
                "WHERE ConfigId = @0 ORDER BY AddedDate", configId);
        }

        public async Task<bool> IsCollaboratorAsync(int configId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskytteConfigurationCollaborator WHERE ConfigId = @0 AND MemberId = @1",
                configId, memberId);
            return count > 0;
        }

        // ── Writes ───────────────────────────────────────────────────────

        public async Task<(bool Success, string? Message, FaltskytteConfiguration? Created)> CreateAsync(
            CreateFaltskytteConfigurationRequest request, int ownerMemberId)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return (false, "Namn krävs.", null);
            if (!ValidVisibilities.Contains(request.Visibility ?? ""))
                return (false, $"Ogiltig synlighet: {request.Visibility}", null);

            var now = DateTime.Now;
            var config = new FaltskytteConfiguration
            {
                Name = request.Name.Trim(),
                Description = request.Description,
                OwnerMemberId = ownerMemberId,
                OwnerClubId = request.OwnerClubId,
                Visibility = request.Visibility ?? VisibilityPrivate,
                SecretUntil = request.SecretUntil,
                JsonBlob = string.IsNullOrWhiteSpace(request.JsonBlob) ? "{}" : request.JsonBlob,
                CreatedDate = now,
                ModifiedDate = now
            };

            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(config);
            return (true, null, config);
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(UpdateFaltskytteConfigurationRequest request)
        {
            using var db = _databaseFactory.CreateDatabase();
            var config = await db.SingleOrDefaultAsync<FaltskytteConfiguration>("WHERE Id = @0", request.Id);
            if (config == null) return (false, "Konfigurationen hittades inte.");

            if (request.Name != null) config.Name = request.Name.Trim();
            if (request.Description != null) config.Description = request.Description;
            if (request.OwnerClubId.HasValue) config.OwnerClubId = request.OwnerClubId.Value;
            if (request.Visibility != null)
            {
                if (!ValidVisibilities.Contains(request.Visibility))
                    return (false, $"Ogiltig synlighet: {request.Visibility}");
                config.Visibility = request.Visibility;
            }
            if (request.ClearSecretUntil) config.SecretUntil = null;
            else if (request.SecretUntil.HasValue) config.SecretUntil = request.SecretUntil.Value;
            if (request.JsonBlob != null) config.JsonBlob = request.JsonBlob;

            config.ModifiedDate = DateTime.Now;
            await db.UpdateAsync(config);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            // CASCADE on FK handles collaborator rows automatically.
            var rows = await db.ExecuteAsync("DELETE FROM FaltskytteConfiguration WHERE Id = @0", id);
            return rows > 0 ? (true, null) : (false, "Konfigurationen hittades inte.");
        }

        public async Task<(bool Success, string? Message, FaltskytteConfiguration? Created)> DuplicateAsync(
            int sourceId, int newOwnerMemberId, string? newName = null)
        {
            var source = await GetByIdAsync(sourceId);
            if (source == null) return (false, "Källkonfigurationen hittades inte.", null);

            var copy = new FaltskytteConfiguration
            {
                Name = newName ?? (source.Name + " (kopia)"),
                Description = source.Description,
                OwnerMemberId = newOwnerMemberId,
                OwnerClubId = source.OwnerClubId,
                Visibility = VisibilityPrivate, // Always start private on duplicate.
                SecretUntil = null,
                JsonBlob = source.JsonBlob,
                CreatedDate = DateTime.Now,
                ModifiedDate = DateTime.Now
            };

            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(copy);
            return (true, null, copy);
        }

        // ── Collaborators ────────────────────────────────────────────────

        public async Task<(bool Success, string? Message)> AddCollaboratorAsync(int configId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM FaltskytteConfigurationCollaborator WHERE ConfigId = @0 AND MemberId = @1",
                configId, memberId);
            if (existing > 0) return (true, null); // Already there — idempotent.

            await db.InsertAsync(new FaltskytteConfigurationCollaborator
            {
                ConfigId = configId,
                MemberId = memberId,
                AddedDate = DateTime.Now
            });
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> RemoveCollaboratorAsync(int configId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync(
                "DELETE FROM FaltskytteConfigurationCollaborator WHERE ConfigId = @0 AND MemberId = @1",
                configId, memberId);
            return (true, null);
        }

        // ── View-model builder ───────────────────────────────────────────

        /// <summary>
        /// Builds an API view model with derived authorization flags and resolved
        /// member/club display names. Set includeJson=true on the editor page;
        /// keep it false on listings to avoid shipping big blobs over the wire.
        /// </summary>
        public async Task<FaltskytteConfigurationView> BuildViewAsync(
            FaltskytteConfiguration config, int? viewerMemberId, bool includeJson)
        {
            var collaborators = await GetCollaboratorsAsync(config.Id);

            int stationCount = 0;
            try
            {
                // Best-effort: count stations in the canonical class. The blob can be
                // either { stations: [...] } or { weaponConfigs: { wc: { stations: [...] } } }.
                var blob = Newtonsoft.Json.Linq.JObject.Parse(config.JsonBlob);
                var weaponConfigs = blob["weaponConfigs"] ?? blob["WeaponConfigs"];
                if (weaponConfigs != null)
                {
                    var firstClass = weaponConfigs.Children<Newtonsoft.Json.Linq.JProperty>().FirstOrDefault();
                    var stations = firstClass?.Value?["stations"] ?? firstClass?.Value?["Stations"];
                    if (stations is Newtonsoft.Json.Linq.JArray arr) stationCount = arr.Count;
                }
                else
                {
                    // Flat: top-level keys are weapon classes (e.g. "C"), each with stations.
                    foreach (var prop in blob.Properties())
                    {
                        if (prop.Name.StartsWith("_")) continue;
                        var stations = prop.Value?["stations"] ?? prop.Value?["Stations"];
                        if (stations is Newtonsoft.Json.Linq.JArray arr) { stationCount = arr.Count; break; }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not count stations on config {Id}", config.Id);
            }

            return new FaltskytteConfigurationView
            {
                Id = config.Id,
                Name = config.Name,
                Description = config.Description,
                OwnerMemberId = config.OwnerMemberId,
                OwnerMemberName = ResolveMemberName(config.OwnerMemberId) ?? $"Medlem {config.OwnerMemberId}",
                OwnerClubId = config.OwnerClubId,
                OwnerClubName = config.OwnerClubId.HasValue ? _clubService.GetClubNameById(config.OwnerClubId.Value) : null,
                Visibility = config.Visibility,
                SecretUntil = config.SecretUntil,
                IsSecret = config.SecretUntil.HasValue && config.SecretUntil.Value > DateTime.Now,
                CreatedDate = config.CreatedDate,
                ModifiedDate = config.ModifiedDate,
                StationCount = stationCount,
                Collaborators = collaborators.Select(c => new CollaboratorView
                {
                    MemberId = c.MemberId,
                    MemberName = ResolveMemberName(c.MemberId) ?? $"Medlem {c.MemberId}",
                    AddedDate = c.AddedDate
                }).ToList(),
                CanEdit = await CanEditAsync(config, viewerMemberId),
                CanDelete = await CanDeleteAsync(config, viewerMemberId),
                JsonBlob = includeJson ? config.JsonBlob : null
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
