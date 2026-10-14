using HpskSite.Services.Staffing;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.C3
{
    /// <summary>
    /// C3-planen S0 punkt 5: vilka medlemmar passerar varje grind på en tävling — mätt med de RIKTIGA
    /// grindarna i <see cref="AdminAuthorizationService"/>, körda som varje kandidat (<c>EvaluateAs</c>),
    /// inte med en avskrift av reglerna. S2 (behörigheten via <c>CompetitionInfo</c>) ska lämna rapporten
    /// oförändrad.
    ///
    /// <para><b>Kandidater:</b> alla medlemmar i en grupp <c>ClubAdmin_*</c>, <c>RegionalAdmin_*</c> eller
    /// <c>Skjutledare_*</c>, tävlingens tävlingsansvariga (<c>competitionManagers</c>) och alla med
    /// appåtkomst i tävlingens bemanning. <b>Sajtadministratörer räknas inte</b> — de passerar allt och
    /// säger ingenting om regeln (planen: "inte som sajtadmin").</para>
    ///
    /// <para><b>Grundgrindarna</b> är metoderna i <see cref="AdminAuthorizationService"/>. <b>De sammansatta</b>
    /// är kontrollrarnas privata grindar, avskrivna med fil och metod angivna, byggda av grundgrindarnas
    /// svar. Avskriften prövas av referensfångsten, som anropar kontrollrarnas endpoints som personorna
    /// och jämför med rapporten.</para>
    /// </summary>
    public sealed class CompetitionAuthorizationReport
    {
        private readonly IServiceProvider _services;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberService _memberService;

        public CompetitionAuthorizationReport(IServiceProvider services, IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextAccessor umbracoContextAccessor, IMemberService memberService)
        {
            _services = services;
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberService = memberService;
        }

        /// <summary>De sammansatta grindarna och var de bor.</summary>
        public static readonly Dictionary<string, string> CompositeSources = new()
        {
            ["edit"] = "Controllers/CompetitionEditController.cs CanEditCompetitionAsync: management || clubAdminOfHost || regionHostAdmin",
            ["results"] = "Controllers/CompetitionResultsController.cs CanManageCompetitionResults: management || clubAdminOfHost || regionalAdminOfRegionProperty",
            ["desk"] = "Controllers/RegistrationAdminController.cs CanManageCompetitionDeskAsync/CanManageRegistrationsAsync: management || (clubId>0 ? clubAdminOfHost || skjutledareOfHost : regionalAdminOfRegionProperty)",
            ["startList"] = "CompetitionTypes/Precision/Controllers/StartListRequestValidator.cs CanManageCompetition: management || clubAdminOfHost || regionalAdminOfRegionProperty",
            ["springskytte"] = "CompetitionTypes/Springskytte/Controllers/SpringskytteController.cs HasCompetitionAccess: management || (clubId>0 && (clubAdminOfHost || skjutledareOfHost)) || regionalAdminOfRegionProperty",
            ["staffing"] = "Controllers/StaffingController.cs HasCompetitionAccessAsync: management || (clubId>0 ? clubAdminOfHost || skjutledareOfHost : regionalAdminOfRegionProperty)",
            ["adminList"] = "Controllers/CompetitionAdminController.cs IsRegionalAdminForCompetition (kopiera/ta bort): regionalAdminOfRegionProperty || regionalAdminOfClubRegion",
        };

        public sealed class Candidate
        {
            public int MemberId { get; set; }
            public string Email { get; set; } = "";
            public string Name { get; set; } = "";
            public List<string> Groups { get; set; } = new();
            public List<string> Basis { get; set; } = new();
        }

        private sealed class GroupRow { public int MemberId { get; set; } public string GroupName { get; set; } = ""; }

        public async Task<Dictionary<string, object?>> BuildAsync(int competitionId)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                throw new InvalidOperationException("Ingen Umbraco-kontext.");
            var comp = ctx.Content.GetById(competitionId);
            if (comp == null || comp.ContentType.Alias != "competition")
                return new Dictionary<string, object?> { ["competitionId"] = competitionId, ["error"] = "Ingen publicerad tävling med det id:t." };

            var clubId = comp.Value<int>("clubId");
            var regionProperty = (comp.Value<string>("regionalFederation") ?? "").Trim();
            string clubRegion = "";
            if (clubId > 0) clubRegion = (ctx.Content.GetById(clubId)?.Value<string>("regionalFederation") ?? "").Trim();

            var candidates = Candidates(competitionId, comp.Value<string>("competitionManagers"));

            var gates = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
            void Pass(string gate, int memberId)
            {
                if (!gates.TryGetValue(gate, out var list)) gates[gate] = list = new List<int>();
                list.Add(memberId);
            }

            foreach (var c in candidates)
            {
                var auth = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<AdminAuthorizationService>(_services);
                auth.EvaluateAs(c.Email);

                var staff = await auth.HasCompetitionStaffAccessAsync(competitionId);
                var management = await auth.HasCompetitionManagementAccess(competitionId);
                var finance = await auth.CanManageCompetitionFinanceAsync(competitionId);
                var regionHost = await auth.IsRegionHostAdminAsync(competitionId);
                var clubAdminOfHost = clubId > 0 && await auth.IsClubAdminForClub(clubId);
                var skjutledareOfHost = clubId > 0 && await auth.IsSkjutledareForClub(clubId);
                var regionalOfProperty = regionProperty.Length > 0 && await auth.IsRegionalAdminForRegion(regionProperty);
                var regionalOfClubRegion = clubRegion.Length > 0 && await auth.IsRegionalAdminForRegion(clubRegion);

                if (staff) Pass("staff", c.MemberId);
                if (management) Pass("management", c.MemberId);
                if (finance) Pass("finance", c.MemberId);
                if (regionHost) Pass("regionHostAdmin", c.MemberId);
                if (clubAdminOfHost) Pass("clubAdminOfHost", c.MemberId);
                if (skjutledareOfHost) Pass("skjutledareOfHost", c.MemberId);
                if (regionalOfProperty) Pass("regionalAdminOfRegionProperty", c.MemberId);
                if (regionalOfClubRegion) Pass("regionalAdminOfClubRegion", c.MemberId);

                if (management || clubAdminOfHost || regionHost) Pass("edit", c.MemberId);
                if (management || clubAdminOfHost || regionalOfProperty) Pass("results", c.MemberId);
                if (management || (clubId > 0 ? clubAdminOfHost || skjutledareOfHost : regionalOfProperty)) Pass("desk", c.MemberId);
                if (management || clubAdminOfHost || regionalOfProperty) Pass("startList", c.MemberId);
                if (management || (clubId > 0 && (clubAdminOfHost || skjutledareOfHost)) || regionalOfProperty) Pass("springskytte", c.MemberId);
                if (management || (clubId > 0 ? clubAdminOfHost || skjutledareOfHost : regionalOfProperty)) Pass("staffing", c.MemberId);
                if (regionalOfProperty || regionalOfClubRegion) Pass("adminList", c.MemberId);
            }

            return new Dictionary<string, object?>
            {
                ["competitionId"] = competitionId,
                ["name"] = comp.Name,
                ["clubId"] = clubId,
                ["regionalFederation"] = regionProperty,
                ["clubRegion"] = clubRegion,
                ["candidateCount"] = candidates.Count,
                ["candidates"] = candidates.Select(c => new { c.MemberId, c.Name, c.Groups, c.Basis }).ToList(),
                ["gates"] = gates.ToDictionary(g => g.Key, g => (object)g.Value.OrderBy(x => x).ToList()),
            };
        }

        private List<Candidate> Candidates(int competitionId, string? managersJson)
        {
            var byId = new Dictionary<int, Candidate>();
            Candidate Get(int id)
            {
                if (!byId.TryGetValue(id, out var c)) byId[id] = c = new Candidate { MemberId = id };
                return c;
            }

            using var db = _databaseFactory.CreateDatabase();
            var rows = db.Fetch<GroupRow>(
                @"SELECT m.Member AS MemberId, n.text AS GroupName FROM cmsMember2MemberGroup m
                  JOIN umbracoNode n ON n.id = m.MemberGroup");
            var admins = rows.Where(r => r.GroupName == "Administrators").Select(r => r.MemberId).ToHashSet();
            foreach (var r in rows)
            {
                if (r.GroupName.StartsWith("ClubAdmin_", StringComparison.Ordinal)
                    || r.GroupName.StartsWith("RegionalAdmin_", StringComparison.Ordinal)
                    || r.GroupName.StartsWith("Skjutledare_", StringComparison.Ordinal))
                {
                    var c = Get(r.MemberId);
                    if (!c.Basis.Contains("grupp")) c.Basis.Add("grupp");
                }
            }

            int[] managers = Array.Empty<int>();
            try { managers = JsonConvert.DeserializeObject<int[]>(managersJson ?? "[]") ?? Array.Empty<int>(); } catch { }
            foreach (var m in managers.Where(m => m > 0)) Get(m).Basis.Add("tävlingsansvarig");

            foreach (var m in db.Fetch<int>("SELECT DISTINCT MemberId FROM StaffAssignment WHERE CompetitionId = @0 AND HasAdminAccess = 1 AND MemberId > 0", competitionId))
                Get(m).Basis.Add("bemanning");

            var groupsByMember = rows.GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.Select(x => x.GroupName).OrderBy(x => x).ToList());
            var result = new List<Candidate>();
            foreach (var c in byId.Values.OrderBy(c => c.MemberId))
            {
                if (admins.Contains(c.MemberId)) continue;
                var member = _memberService.GetById(c.MemberId);
                if (member == null || string.IsNullOrEmpty(member.Email)) continue;
                c.Email = member.Email;
                c.Name = member.Name ?? "";
                c.Groups = groupsByMember.TryGetValue(c.MemberId, out var g) ? g : new List<string>();
                result.Add(c);
            }
            return result;
        }
    }
}
