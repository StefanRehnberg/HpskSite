using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for klubb- och kretsrekord. Reads are gated to logged-in
    /// members; writes require club admin (for Club records) or regional admin (for
    /// Region records). Site admins bypass both via existing AdminAuthorizationService.
    /// </summary>
    public class CompetitionRecordsController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _authService;
        private readonly CompetitionRecordsService _recordsService;
        private readonly CompetitionChampionsService _championsService;
        private readonly ClubService _clubService;
        private readonly ILogger<CompetitionRecordsController> _logger;

        public CompetitionRecordsController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberManager memberManager,
            AdminAuthorizationService authService,
            CompetitionRecordsService recordsService,
            CompetitionChampionsService championsService,
            ClubService clubService,
            ILogger<CompetitionRecordsController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _authService = authService;
            _recordsService = recordsService;
            _championsService = championsService;
            _clubService = clubService;
            _logger = logger;
        }

        // ── Champions + member autocomplete (logged-in only) ──────────

        [HttpGet]
        public async Task<IActionResult> ChampionsForClub(int clubId, bool includeHistory = false)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });
            try
            {
                var rows = includeHistory
                    ? await _championsService.GetAllForScopeAsync(RecordLevels.Club, clubId.ToString())
                    : await _championsService.GetReigningForScopeAsync(RecordLevels.Club, clubId.ToString());
                var clubMap = BuildHolderClubMap(rows);
                return Json(new { success = true, data = rows.Select(r => ProjectChampion(r, clubMap)) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading club champions for club {ClubId}", clubId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ChampionsForRegion(string regionCode, bool includeHistory = false)
        {
            if (string.IsNullOrWhiteSpace(regionCode))
                return Json(new { success = false, message = "Ogiltig kretskod." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });
            try
            {
                var rows = includeHistory
                    ? await _championsService.GetAllForScopeAsync(RecordLevels.Region, regionCode)
                    : await _championsService.GetReigningForScopeAsync(RecordLevels.Region, regionCode);
                var clubMap = BuildHolderClubMap(rows);
                return Json(new { success = true, data = rows.Select(r => ProjectChampion(r, clubMap)) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading region champions for region {RegionCode}", regionCode);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ChampionsForMember(int memberId)
        {
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });
            try
            {
                var rows = await _championsService.GetForMemberAsync(memberId);
                var clubMap = BuildHolderClubMap(rows);
                return Json(new { success = true, data = rows.Select(r => ProjectChampion(r, clubMap)) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading champions for member {MemberId}", memberId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ChampionHistory(string level, string scopeId, string discipline, string championType, string classCode)
        {
            if (string.IsNullOrWhiteSpace(level) || string.IsNullOrWhiteSpace(scopeId)
                || string.IsNullOrWhiteSpace(discipline) || string.IsNullOrWhiteSpace(championType)
                || string.IsNullOrWhiteSpace(classCode))
                return Json(new { success = false, message = "Ogiltig nyckel." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });
            var rows = await _championsService.GetHistoryAsync(level, scopeId, discipline, championType, classCode);
            var clubMap = BuildHolderClubMap(rows);
            return Json(new { success = true, data = rows.Select(r => ProjectChampion(r, clubMap)) });
        }

        /// <summary>
        /// Resolves each holder's primary club name once per response. Cheap (Umbraco
        /// caches member lookups) and a single lookup per distinct memberId.
        /// </summary>
        private Dictionary<int, string> BuildHolderClubMap(IEnumerable<CompetitionChampion> rows)
        {
            var map = new Dictionary<int, string>();
            var ids = rows.Where(r => r.HolderMemberId.HasValue && r.HolderMemberId.Value > 0)
                          .Select(r => r.HolderMemberId!.Value).Distinct();
            foreach (var id in ids)
            {
                try
                {
                    var m = _memberService.GetById(id);
                    if (m == null) continue;
                    var primaryClubIdStr = m.GetValue<string>("primaryClubId");
                    if (string.IsNullOrEmpty(primaryClubIdStr) || !int.TryParse(primaryClubIdStr, out int cid)) continue;
                    var name = _clubService.GetClubNameById(cid);
                    if (!string.IsNullOrEmpty(name)) map[id] = name!;
                }
                catch { /* swallow — holder club is informational only */ }
            }
            return map;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChampionCreate([FromBody] CreateChampionRequest req)
        {
            if (req == null) return Json(new { success = false, message = "Ogiltig begäran." });
            if (!await IsAuthorizedForScope(req.Level, req.ScopeId))
                return Json(new { success = false, message = "Access denied" });

            var actingId = await GetActingMemberIdAsync();
            if (actingId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (ok, id, msg) = await _championsService.CreateAsync(req, actingId);
            if (!ok) return Json(new { success = false, message = msg });
            return Json(new { success = true, championId = id, message = "Mästaren är registrerad." });
        }

        public class DeleteChampionBody { public int ChampionId { get; set; } }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChampionDelete([FromBody] DeleteChampionBody body)
        {
            if (body == null || body.ChampionId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var existing = await _championsService.GetByIdAsync(body.ChampionId);
            if (existing == null) return Json(new { success = false, message = "Mästaren hittades inte." });
            if (!await IsAuthorizedForScope(existing.Level, existing.ScopeId))
                return Json(new { success = false, message = "Access denied" });

            var (ok, msg) = await _championsService.DeleteAsync(body.ChampionId);
            return Json(new { success = ok, message = ok ? "Mästaren borttagen." : msg });
        }

        private static object ProjectChampion(CompetitionChampion c, Dictionary<int, string> holderClubMap)
        {
            string holderClubName = "";
            if (c.HolderMemberId.HasValue && holderClubMap.TryGetValue(c.HolderMemberId.Value, out var n))
                holderClubName = n;
            return new
            {
                id = c.Id,
                level = c.Level,
                scopeId = c.ScopeId,
                year = c.Year,
                discipline = c.Discipline,
                disciplineLabel = RecordDisciplines.DisplayName(c.Discipline),
                championType = c.ChampionType,
                championTypeLabel = RecordTypes.DisplayName(c.ChampionType),
                classCode = c.ClassCode,
                classLabel = RecordClassRegistry.GetClassDisplayName(c.ClassCode),
                totalScore = c.TotalScore,
                maxScore = RecordClassRegistry.GetMaxScore(c.Discipline, c.ChampionType),
                competitionName = c.CompetitionName ?? "",
                competitionDate = c.CompetitionDate?.ToString("yyyy-MM-dd"),
                holderMemberId = c.HolderMemberId,
                holderName = c.HolderName,
                holderClubName,
                teamName = c.TeamName,
                notes = c.Notes
            };
        }

        [HttpGet]
        public async Task<IActionResult> GetRegionMembers(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode))
                return Json(new { success = false, message = "Ogiltig kretskod." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });

            try
            {
                var clubIds = _authService.GetClubsInRegions(new List<string> { regionCode });
                var clubIdSet = new HashSet<int>(clubIds);
                var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
                var rows = allMembers
                    .Select(m =>
                    {
                        var s = m.GetValue<string>("primaryClubId");
                        if (string.IsNullOrEmpty(s) || !int.TryParse(s, out int cid) || !clubIdSet.Contains(cid))
                            return null;
                        var first = m.GetValue<string>("firstName") ?? "";
                        var last = m.GetValue<string>("lastName") ?? "";
                        var name = $"{first} {last}".Trim();
                        if (string.IsNullOrEmpty(name)) name = m.Name ?? $"Medlem {m.Id}";
                        var clubName = _clubService.GetClubNameById(cid) ?? "";
                        var shooterIdNumber = m.GetValue<string>("shooterIdNumber") ?? "";
                        return new
                        {
                            id = m.Id,
                            name,
                            clubName,
                            shooterIdNumber
                        };
                    })
                    .Where(r => r != null)
                    .OrderBy(r => r!.name)
                    .ToList();
                return Json(new { success = true, data = rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetRegionMembers for region {RegionCode}", regionCode);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Reads (logged-in only) ────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> ListForClub(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });

            var rows = await _recordsService.GetCurrentForScopeAsync(RecordLevels.Club, clubId.ToString());
            return Json(new { success = true, data = rows.Select(ProjectRecord) });
        }

        [HttpGet]
        public async Task<IActionResult> ListForRegion(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode))
                return Json(new { success = false, message = "Ogiltig kretskod." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });

            var rows = await _recordsService.GetCurrentForScopeAsync(RecordLevels.Region, regionCode);
            return Json(new { success = true, data = rows.Select(ProjectRecord) });
        }

        [HttpGet]
        public async Task<IActionResult> ListForMember(int memberId)
        {
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });

            var rows = await _recordsService.GetCurrentForMemberAsync(memberId);
            return Json(new { success = true, data = rows.Select(ProjectRecord) });
        }

        [HttpGet]
        public async Task<IActionResult> History(string level, string scopeId, string discipline, string recordType, string classCode)
        {
            if (string.IsNullOrWhiteSpace(level) || string.IsNullOrWhiteSpace(scopeId)
                || string.IsNullOrWhiteSpace(discipline) || string.IsNullOrWhiteSpace(recordType)
                || string.IsNullOrWhiteSpace(classCode))
            {
                return Json(new { success = false, message = "Ogiltig nyckel." });
            }
            if (!await IsLoggedIn()) return Json(new { success = false, message = "Login required." });

            var rows = await _recordsService.GetHistoryAsync(level, scopeId, discipline, recordType, classCode);
            return Json(new { success = true, data = rows.Select(ProjectRecord) });
        }

        // ── Writes (scope admin) ──────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromBody] CreateRecordRequest req)
        {
            if (req == null) return Json(new { success = false, message = "Ogiltig begäran." });
            if (!await IsAuthorizedForScope(req.Level, req.ScopeId))
                return Json(new { success = false, message = "Access denied" });

            var actingId = await GetActingMemberIdAsync();
            if (actingId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (ok, recordId, msg) = await _recordsService.CreateAsync(req, actingId);
            if (!ok) return Json(new { success = false, message = msg });
            return Json(new { success = true, recordId, message = "Rekordet är sparat." });
        }

        public class UpdateMetaBody
        {
            public int RecordId { get; set; }
            public int? TotalScore { get; set; }
            public DateTime? RecordDate { get; set; }
            public string? CompetitionName { get; set; }
            public int? HolderMemberId { get; set; }
            public bool HolderMemberIdSet { get; set; }
            public string? HolderName { get; set; }
            public string? TeamName { get; set; }
            public string? TeamMembersJson { get; set; }
            public string? Notes { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMeta([FromBody] UpdateMetaBody body)
        {
            if (body == null || body.RecordId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var existing = await _recordsService.GetByIdAsync(body.RecordId);
            if (existing == null) return Json(new { success = false, message = "Rekordet hittades inte." });
            if (!await IsAuthorizedForScope(existing.Level, existing.ScopeId))
                return Json(new { success = false, message = "Access denied" });

            var req = new UpdateRecordMetaRequest
            {
                TotalScore = body.TotalScore,
                RecordDate = body.RecordDate,
                CompetitionName = body.CompetitionName,
                HolderMemberId = body.HolderMemberId,
                HolderMemberIdSet = body.HolderMemberIdSet,
                HolderName = body.HolderName,
                TeamName = body.TeamName,
                TeamMembersJson = body.TeamMembersJson,
                Notes = body.Notes
            };

            var (ok, msg) = await _recordsService.UpdateMetaAsync(body.RecordId, req);
            return Json(new { success = ok, message = ok ? "Sparat." : msg });
        }

        public class DeleteRecordBody
        {
            public int RecordId { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete([FromBody] DeleteRecordBody body)
        {
            if (body == null || body.RecordId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var existing = await _recordsService.GetByIdAsync(body.RecordId);
            if (existing == null) return Json(new { success = false, message = "Rekordet hittades inte." });
            if (!await IsAuthorizedForScope(existing.Level, existing.ScopeId))
                return Json(new { success = false, message = "Access denied" });

            var (ok, msg) = await _recordsService.DeleteAsync(body.RecordId);
            return Json(new { success = ok, message = ok ? "Rekordet borttaget." : msg });
        }

        // ── Helpers ───────────────────────────────────────────────────

        private async Task<bool> IsLoggedIn()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            return current != null;
        }

        private async Task<bool> IsAuthorizedForScope(string level, string scopeId)
        {
            if (string.IsNullOrEmpty(level) || string.IsNullOrEmpty(scopeId)) return false;
            return level switch
            {
                RecordLevels.Club => int.TryParse(scopeId, out int cid) && await _authService.IsClubAdminForClub(cid),
                RecordLevels.Region => await _authService.IsRegionalAdminForRegion(scopeId),
                _ => false
            };
        }

        private async Task<int> GetActingMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            var data = _memberService.GetByEmail(current.Email ?? "");
            return data?.Id ?? 0;
        }

        private static object ProjectRecord(CompetitionRecord r)
        {
            return new
            {
                id = r.Id,
                level = r.Level,
                scopeId = r.ScopeId,
                discipline = r.Discipline,
                disciplineLabel = RecordDisciplines.DisplayName(r.Discipline),
                recordType = r.RecordType,
                recordTypeLabel = RecordTypes.DisplayName(r.RecordType),
                classCode = r.ClassCode,
                classLabel = RecordClassRegistry.GetClassDisplayName(r.ClassCode),
                totalScore = r.TotalScore,
                seriesCount = r.SeriesCount,
                maxScore = RecordClassRegistry.GetMaxScore(r.Discipline, r.RecordType),
                recordDate = r.RecordDate.ToString("yyyy-MM-dd"),
                competitionName = r.CompetitionName ?? "",
                holderMemberId = r.HolderMemberId,
                holderName = r.HolderName,
                teamName = r.TeamName,
                teamMembers = ParseTeamMembers(r.TeamMembersJson),
                notes = r.Notes,
                isCurrent = r.IsCurrent,
                isNew = r.RecordDate >= DateTime.Today.AddDays(-30)
            };
        }

        private static object[] ParseTeamMembers(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<object>();
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<TeamMember[]>(json);
                if (parsed == null) return Array.Empty<object>();
                return parsed.Select(p => (object)new { memberId = p.MemberId, name = p.Name }).ToArray();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        private class TeamMember
        {
            public int? MemberId { get; set; }
            public string Name { get; set; } = "";
        }
    }
}
