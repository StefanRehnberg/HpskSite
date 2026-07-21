using HpskSite.Models.Staffing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Sourcing scope (where a comp draws crew from) + member self-sign-up (Phase 3, minus the Project/
    /// EventRef abstraction). Adding a source scope opens a competition for self-sign-up by members of that
    /// club/region; an eligible member can then volunteer for a role, creating their own Accepted assignment.
    /// </summary>
    public class StaffingSignupService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;

        public StaffingSignupService(IScopeProvider scopeProvider, IContentService contentService, IMemberService memberService, ClubService clubService)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _memberService = memberService;
            _clubService = clubService;
        }

        // ---- source scopes (organiser side) ----

        public List<SourceScopeView> GetScopes(int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<StaffingSourceScope>(
                "SELECT * FROM StaffingSourceScope WHERE CompetitionId = @0 ORDER BY ScopeType, ScopeKey", competitionId);
            return rows.Select(r => new SourceScopeView
            {
                Id = r.Id,
                ScopeType = r.ScopeType,
                ScopeKey = r.ScopeKey,
                Label = ResolveScopeLabel(r.ScopeType, r.ScopeKey),
            }).ToList();
        }

        public (bool Ok, string? Message, int Id) AddScope(int competitionId, string scopeType, string scopeKey, int byMemberId)
        {
            var type = string.Equals(scopeType, SourceScopeType.Region, StringComparison.OrdinalIgnoreCase) ? SourceScopeType.Region : SourceScopeType.Club;
            // "self"/blank = the hosting club or region, resolved from the competition.
            if (string.IsNullOrWhiteSpace(scopeKey) || string.Equals(scopeKey, "self", StringComparison.OrdinalIgnoreCase))
            {
                var comp = _contentService.GetById(competitionId);
                if (type == SourceScopeType.Club)
                {
                    var clubId = comp?.GetValue<int>("clubId") ?? 0;
                    if (clubId <= 0) return (false, "Tävlingen saknar arrangörsklubb.", 0);
                    scopeKey = clubId.ToString();
                }
                else
                {
                    var region = comp?.GetValue<string>("regionalFederation");
                    if (string.IsNullOrWhiteSpace(region) && (comp?.GetValue<int>("clubId") ?? 0) > 0)
                        try { region = _contentService.GetById(comp!.GetValue<int>("clubId"))?.GetValue<string>("regionalFederation"); } catch { }
                    if (string.IsNullOrWhiteSpace(region)) return (false, "Kunde inte hitta tävlingens krets.", 0);
                    scopeKey = region;
                }
            }
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var dup = db.ExecuteScalar<int>("SELECT COUNT(*) FROM StaffingSourceScope WHERE CompetitionId=@0 AND ScopeType=@1 AND ScopeKey=@2", competitionId, type, scopeKey.Trim());
            if (dup > 0) return (true, null, 0);   // already present — idempotent
            var id = Convert.ToInt32(db.Insert(new StaffingSourceScope
            {
                CompetitionId = competitionId,
                ScopeType = type,
                ScopeKey = scopeKey.Trim(),
                CreatedByMemberId = byMemberId,
                CreatedDate = DateTime.UtcNow,
            }));
            return (true, null, id);
        }

        public void RemoveScope(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("DELETE FROM StaffingSourceScope WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
        }

        // ---- member self-sign-up ----

        /// <summary>Open competitions the member may volunteer for: those whose source scope includes one of
        /// the member's clubs or their region, and where they hold no assignment yet.</summary>
        public List<OpenSignupView> GetOpenSignups(int memberId)
        {
            var (clubIds, regionCode) = MemberScope(memberId);
            if (clubIds.Count == 0 && string.IsNullOrEmpty(regionCode)) return new();

            List<StaffingSourceScope> scopes;
            HashSet<int> alreadyAssigned;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                scopes = db.Fetch<StaffingSourceScope>("SELECT * FROM StaffingSourceScope");
                alreadyAssigned = db.Fetch<int>("SELECT DISTINCT CompetitionId FROM StaffAssignment WHERE MemberId = @0", memberId).ToHashSet();
            }

            var clubKeys = clubIds.Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var compIds = scopes
                .Where(s => (string.Equals(s.ScopeType, SourceScopeType.Club, StringComparison.OrdinalIgnoreCase) && clubKeys.Contains(s.ScopeKey))
                         || (string.Equals(s.ScopeType, SourceScopeType.Region, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(regionCode) && string.Equals(s.ScopeKey, regionCode, StringComparison.OrdinalIgnoreCase)))
                .Select(s => s.CompetitionId).Distinct()
                .Where(id => !alreadyAssigned.Contains(id));

            var result = new List<OpenSignupView>();
            foreach (var compId in compIds)
            {
                var comp = _contentService.GetById(compId);
                if (comp == null || comp.ContentType.Alias != "competition") continue;
                var discipline = comp.GetValue<string>("competitionType") ?? "";
                var date = comp.GetValue<DateTime?>("competitionDate");
                result.Add(new OpenSignupView
                {
                    CompetitionId = compId,
                    CompName = comp.GetValue<string>("competitionName") ?? comp.Name ?? "Tävling",
                    CompDate = date is { } d && d != default ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                    Discipline = discipline,
                    Roles = FunctionaryRoles.ForDiscipline(discipline)
                        .Where(r => r.Key != "tavlingsledning")   // self-sign-up never grants leadership/app admin
                        .Select(r => new RoleOption { Key = r.Key, Name = r.DisplayName }).ToList(),
                });
            }
            return result.OrderBy(r => r.CompDate == null).ThenBy(r => r.CompDate, StringComparer.Ordinal).ToList();
        }

        public (bool Ok, string? Message) SelfSignUp(int memberId, int competitionId, string roleKey)
        {
            // Re-check eligibility server-side.
            if (!GetOpenSignups(memberId).Any(o => o.CompetitionId == competitionId))
                return (false, "Tävlingen är inte öppen för anmälan för dig.");
            var comp = _contentService.GetById(competitionId);
            if (comp == null) return (false, "Tävlingen hittades inte.");
            var discipline = comp.GetValue<string>("competitionType") ?? "";
            var role = FunctionaryRoles.Resolve(discipline, roleKey);
            if (role == null || role.Key == "tavlingsledning") return (false, "Ogiltig roll.");

            var m = _memberService.GetById(memberId);
            var name = m == null ? $"Medlem {memberId}" : ($"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim() is { Length: > 0 } n ? n : (m.Name ?? $"Medlem {memberId}"));
            var phone = m != null && m.HasProperty("phoneNumber") ? m.GetValue<string>("phoneNumber") : null;

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var now = DateTime.UtcNow;
            scope.Database.Insert(new StaffAssignment
            {
                CompetitionId = competitionId,
                MemberId = memberId,
                DisplayName = name,
                Phone = phone,
                RoleKey = role.Key,
                ScopeType = string.IsNullOrEmpty(role.DefaultScopeType) ? StaffScopeType.All : role.DefaultScopeType,
                IsResponsible = false,
                HasAdminAccess = false,
                Status = StaffAssignmentStatus.Accepted,   // volunteering = already said yes
                Note = "Anmäld via självanmälan",
                AssignedByMemberId = memberId,
                CreatedDate = now,
                ModifiedDate = now,
            });
            return (true, null);
        }

        // ---- helpers ----

        private (List<int> ClubIds, string? RegionCode) MemberScope(int memberId)
        {
            var clubIds = new List<int>();
            string? regionCode = null;
            try
            {
                var m = _memberService.GetById(memberId);
                if (m == null) return (clubIds, null);
                var primary = 0;
                int.TryParse(m.GetValue<string>("primaryClubId"), out primary);
                if (primary > 0) clubIds.Add(primary);
                var extra = m.GetValue<string>("memberClubIds");
                if (!string.IsNullOrWhiteSpace(extra))
                    foreach (var part in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (int.TryParse(part, out var cid) && cid > 0 && !clubIds.Contains(cid)) clubIds.Add(cid);
                if (primary > 0)
                    try { regionCode = _contentService.GetById(primary)?.GetValue<string>("regionalFederation"); } catch { }
            }
            catch { }
            return (clubIds, string.IsNullOrWhiteSpace(regionCode) ? null : regionCode);
        }

        private string ResolveScopeLabel(string scopeType, string scopeKey)
        {
            if (string.Equals(scopeType, SourceScopeType.Club, StringComparison.OrdinalIgnoreCase) && int.TryParse(scopeKey, out var clubId))
                return _clubService.GetClubNameById(clubId) ?? $"Klubb {scopeKey}";
            return $"Krets {scopeKey}";
        }
    }
}
