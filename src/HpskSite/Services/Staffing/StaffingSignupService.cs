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
        private readonly RoleCatalogService _roles;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly StaffOwnerResolver _owners;

        public StaffingSignupService(IScopeProvider scopeProvider, IContentService contentService, IMemberService memberService, ClubService clubService, RoleCatalogService roles,
            HpskSite.Services.Competitions.ICompetitionStore competitions, StaffOwnerResolver owners)
        {
            _competitions = competitions;
            _owners = owners;
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _memberService = memberService;
            _clubService = clubService;
            _roles = roles;
        }

        // ---- source scopes (organiser side) ----

        public List<SourceScopeView> GetScopes(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<StaffingSourceScope>(
                "SELECT * FROM StaffingSourceScope WHERE " + StaffOwner.Where(0) + " ORDER BY ScopeType, ScopeKey", owner.P);
            return rows.Select(r => new SourceScopeView
            {
                Id = r.Id,
                ScopeType = r.ScopeType,
                ScopeKey = r.ScopeKey,
                Label = ResolveScopeLabel(r.ScopeType, r.ScopeKey),
            }).ToList();
        }

        public (bool Ok, string? Message, int Id) AddScope(StaffOwner owner, string scopeType, string scopeKey, int byMemberId)
        {
            var type = string.Equals(scopeType, SourceScopeType.Region, StringComparison.OrdinalIgnoreCase) ? SourceScopeType.Region : SourceScopeType.Club;
            // En klubb måste finnas; en krets måste vara en känd krets.
            if (!string.IsNullOrWhiteSpace(scopeKey) && !string.Equals(scopeKey, "self", StringComparison.OrdinalIgnoreCase))
            {
                if (type == SourceScopeType.Club && (!int.TryParse(scopeKey, out var cid) || _clubService.GetClubNameById(cid) == null))
                    return (false, "Klubben finns inte.", 0);
                if (type == SourceScopeType.Region && !Enum.TryParse<HpskSite.Models.Federations.RegionalFederations>(scopeKey, true, out _))
                    return (false, "Kretsen finns inte.", 0);
            }
            // "self"/blank = the hosting club or region, resolved from the competition.
            if (string.IsNullOrWhiteSpace(scopeKey) || string.Equals(scopeKey, "self", StringComparison.OrdinalIgnoreCase))
            {
                // P2b: en planering utan tävling har ingen arrangör att utgå från.
                if (!owner.HasCompetition)
                    return (false, "Planeringen är inte kopplad till någon tävling, så arrangören är inte känd. Sök fram klubben eller kretsen.", 0);
                var comp = _competitions.GetSaved(owner.Comp); // C3-planen §5d
                if (type == SourceScopeType.Club)
                {
                    var clubId = comp?.ClubId ?? 0;
                    if (clubId <= 0) return (false, "Tävlingen saknar arrangörsklubb.", 0);
                    scopeKey = clubId.ToString();
                }
                else
                {
                    var region = comp?.RegionalFederation;
                    if (string.IsNullOrWhiteSpace(region) && (comp?.ClubId ?? 0) > 0)
                        try { region = _contentService.GetById(comp!.ClubId)?.GetValue<string>("regionalFederation"); } catch { }
                    if (string.IsNullOrWhiteSpace(region)) return (false, "Kunde inte hitta tävlingens krets.", 0);
                    scopeKey = region;
                }
            }
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var dup = db.ExecuteScalar<int>("SELECT COUNT(*) FROM StaffingSourceScope WHERE " + StaffOwner.Where(0) + " AND ScopeType=@1 AND ScopeKey=@2", owner.P, type, scopeKey.Trim());
            if (dup > 0) return (true, null, 0);   // already present — idempotent
            var id = Convert.ToInt32(db.Insert(new StaffingSourceScope
            {
                PlanningId = owner.PlanningIdOrNull,
                CompetitionId = owner.CompetitionIdOrNull,
                ScopeType = type,
                ScopeKey = scopeKey.Trim(),
                CreatedByMemberId = byMemberId,
                CreatedDate = DateTime.UtcNow,
            }));
            return (true, null, id);
        }

        public void RemoveScope(int id, StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("DELETE FROM StaffingSourceScope WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P);
        }

        // ---- member self-sign-up ----

        /// <summary>Open competitions the member may volunteer for: those whose source scope includes one of
        /// the member's clubs or their region, and where they hold no assignment yet.</summary>
        public List<OpenSignupView> GetOpenSignups(int memberId)
        {
            var (clubIds, regionCode) = MemberScope(memberId);
            if (clubIds.Count == 0 && string.IsNullOrEmpty(regionCode)) return new();

            List<StaffingSourceScope> scopes;
            List<(int? PlanningId, int? CompetitionId)> assigned;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                scopes = db.Fetch<StaffingSourceScope>("SELECT * FROM StaffingSourceScope");
                assigned = db.Fetch<StaffAssignment>("SELECT DISTINCT PlanningId, CompetitionId FROM StaffAssignment WHERE MemberId = @0", memberId)
                    .Select(a => (a.PlanningId, a.CompetitionId)).ToList();
            }

            // P2b: en öppning hör till en ägare — planeringen, eller tävlingen som ännu inte har någon.
            var clubKeys = clubIds.Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var owners = scopes
                .Where(s => (string.Equals(s.ScopeType, SourceScopeType.Club, StringComparison.OrdinalIgnoreCase) && clubKeys.Contains(s.ScopeKey))
                         || (string.Equals(s.ScopeType, SourceScopeType.Region, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(regionCode) && string.Equals(s.ScopeKey, regionCode, StringComparison.OrdinalIgnoreCase)))
                .Select(s => _owners.OfRow(s.PlanningId, s.CompetitionId))
                .Where(o => o != null).Select(o => o!)
                .DistinctBy(o => o.MemoKey)
                .Where(o => !assigned.Any(a => o.Owns(a.PlanningId, a.CompetitionId)));

            var result = new List<OpenSignupView>();
            foreach (var owner in owners)
            {
                if (owner.HasCompetition && _competitions.GetSaved(owner.Comp) == null) continue;
                var discipline = _owners.Discipline(owner);
                var (date, _) = _owners.Span(owner);
                result.Add(new OpenSignupView
                {
                    CompetitionId = owner.Comp,
                    PlanningId = owner.PlanningId,
                    CompName = _owners.Name(owner),
                    CompDate = date is { } d && d != default ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                    Discipline = discipline,
                    Roles = _roles.For(owner, discipline)
                        .Where(r => r.Key != "tavlingsledning")   // self-sign-up never grants leadership/app admin
                        .Select(r => new RoleOption { Key = r.Key, Name = r.DisplayName }).ToList(),
                });
            }
            return result.OrderBy(r => r.CompDate == null).ThenBy(r => r.CompDate, StringComparer.Ordinal).ToList();
        }

        public (bool Ok, string? Message) SelfSignUp(int memberId, StaffOwner owner, string roleKey)
        {
            // Re-check eligibility server-side.
            if (!GetOpenSignups(memberId).Any(o => o.PlanningId == owner.PlanningId && o.CompetitionId == owner.Comp))
                return (false, "Tävlingen är inte öppen för anmälan för dig.");
            var discipline = _owners.Discipline(owner);
            var role = _roles.Resolve(owner, discipline, roleKey);
            if (role == null || role.Key == "tavlingsledning") return (false, "Ogiltig roll.");

            var m = _memberService.GetById(memberId);
            var name = m == null ? $"Medlem {memberId}" : ($"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim() is { Length: > 0 } n ? n : (m.Name ?? $"Medlem {memberId}"));
            var phone = m != null && m.HasProperty("phoneNumber") ? m.GetValue<string>("phoneNumber") : null;

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var now = DateTime.UtcNow;
            scope.Database.Insert(new StaffAssignment
            {
                PlanningId = owner.PlanningIdOrNull,
                CompetitionId = owner.CompetitionIdOrNull,
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
            return RegionName(scopeKey);
        }

        private static string RegionName(string code) =>
            Enum.TryParse<HpskSite.Models.Federations.RegionalFederations>(code, true, out var r)
                ? HpskSite.Models.Federations.GetDescription(r) : $"Krets {code}";

        /// <summary>
        /// Klubbarna och kretsarna man kan öppna anmälan för (väljaren i "Hitta funktionärer"). Demokretsen
        /// Ankeland är inte med. Är bemanningen kopplad till en tävling följer arrangörens klubb och krets
        /// med som förslag.
        /// </summary>
        public (List<SourceScopeOption> Clubs, List<SourceScopeOption> Regions, List<SourceScopeOption> Suggested) ScopeOptions(StaffOwner owner)
        {
            var sv = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("sv-SE"), true);
            var clubs = _clubService.GetAllClubs()
                .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new SourceScopeOption { ScopeType = SourceScopeType.Club, ScopeKey = c.Id.ToString(), Label = c.Name })
                .OrderBy(c => c.Label, sv).ToList();
            var regions = Enum.GetValues<HpskSite.Models.Federations.RegionalFederations>()
                .Where(r => r != HpskSite.Models.Federations.RegionalFederations.Ankeland)
                .Select(r => new SourceScopeOption { ScopeType = SourceScopeType.Region, ScopeKey = r.ToString(), Label = HpskSite.Models.Federations.GetDescription(r) })
                .OrderBy(r => r.Label, sv).ToList();
            var suggested = new List<SourceScopeOption>();
            if (owner.HasCompetition)
            {
                var comp = _competitions.GetSaved(owner.Comp);
                if (comp?.ClubId > 0)
                    suggested.Add(new SourceScopeOption { ScopeType = SourceScopeType.Club, ScopeKey = comp.ClubId.ToString(),
                        Label = (_clubService.GetClubNameById(comp.ClubId) ?? "Arrangörsklubben") + " (arrangör)" });
                var region = comp?.RegionalFederation;
                if (string.IsNullOrWhiteSpace(region) && (comp?.ClubId ?? 0) > 0)
                    try { region = _contentService.GetById(comp!.ClubId)?.GetValue<string>("regionalFederation"); } catch { }
                if (!string.IsNullOrWhiteSpace(region))
                    suggested.Add(new SourceScopeOption { ScopeType = SourceScopeType.Region, ScopeKey = region!, Label = RegionName(region!) });
            }
            return (clubs, regions, suggested);
        }
    }
}
