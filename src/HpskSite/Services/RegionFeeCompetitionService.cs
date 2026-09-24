using HpskSite.CompetitionTypes.Common;
using Microsoft.Extensions.Logging;
using HpskSite.Models;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Kretsavgiftens tävlingar: vilka som ingår för ett avgiftsår, och hur många starter varje klubb
    /// i kretsen hade i dem.
    ///
    /// <para><b>Varför det finns:</b> Hallands kretsfaktura debiterar "Startavgifter, 140 starter à 20 kr"
    /// och "Lagavgift, 11 tävlingar à 20 kr" över Hallandsserien i precision och fält. Kretskassören
    /// räknade det för hand, klubb för klubb (Stefan 2026-09-24).</para>
    ///
    /// <para><b>⚠️⚠️ EN START ÄR (tävling, skytt, KLASS) MED RESULTAT.</b> A och C i samma omgång är två
    /// starter (Stefans besked). Den som anmält sig men inte skjutit räknas inte — det finns inga
    /// resultatrader, och en DNS i Springskytte har uttryckligen status DNS. En DNF räknas: skytten startade.</para>
    ///
    /// <para><b>⚠️ Klubben är ANMÄLANS klubb</b>, med medlemmens huvudklubb som reserv. En skytt som
    /// tävlar för sin andra klubb ska räknas där — samma regel som resultatlistan.</para>
    ///
    /// <para><b>⚠️ Talet är ett FÖRSLAG.</b> Delar av serierna finns inte på pistol.nu, så kretsen rättar
    /// antalet per klubb; det rättade talet sparas på avgiften (<see cref="MembershipFeeCharge.StartCount"/>).</para>
    /// </summary>
    public class RegionFeeCompetitionService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly InvoiceAdminService _invoiceAdmin;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;
        private readonly ClubService _clubService;
        private readonly AppCaches _appCaches;
        private readonly ILogger<RegionFeeCompetitionService> _logger;

        public RegionFeeCompetitionService(IScopeProvider scopeProvider, InvoiceAdminService invoiceAdmin,
            IContentService contentService, IMemberService memberService, MemberClubService memberClubs,
            ClubService clubService, AppCaches appCaches, ILogger<RegionFeeCompetitionService> logger)
        {
            _scopeProvider = scopeProvider;
            _invoiceAdmin = invoiceAdmin;
            _contentService = contentService;
            _memberService = memberService;
            _memberClubs = memberClubs;
            _clubService = clubService;
            _appCaches = appCaches;
            _logger = logger;
        }

        /// <summary>
        /// Kretsens tävlingar under <paramref name="year"/> att välja bland: kretsens egna och de klubbarna
        /// i kretsen arrangerar. Klubbinterna tävlingar (<c>isClubOnly</c>) är inte kretsens och tas inte med.
        /// </summary>
        public List<RegionFeeCandidate> GetCandidates(string regionCode, ISet<int> regionClubIds, int year)
        {
            var seriesNames = new Dictionary<int, string?>();
            string? SeriesOf(IContent c)
            {
                if (c.ParentId <= 0) return null;
                if (seriesNames.TryGetValue(c.ParentId, out var cached)) return cached;
                var parent = _contentService.GetById(c.ParentId);
                var name = parent?.ContentType.Alias == "competitionSeries" ? parent.Name : null;
                seriesNames[c.ParentId] = name;
                return name;
            }

            return _invoiceAdmin.GetCompetitionsInRegion(regionCode, regionClubIds)
                .Where(c => !c.Trashed && !c.GetValue<bool>("isClubOnly"))
                .Select(c => new { c, date = DateOf(c) })
                .Where(x => x.date?.Year == year)
                .Select(x =>
                {
                    var clubId = x.c.GetValue<int>("clubId");
                    var type = x.c.GetValue<string>("competitionType") ?? "";
                    return new RegionFeeCandidate
                    {
                        Id = x.c.Id,
                        Name = x.c.Name ?? "",
                        Date = x.date!.Value,
                        SeriesName = SeriesOf(x.c),
                        TypeLabel = HpskSite.Models.CompetitionTypes.GetFuzzy(type)?.Name ?? type,
                        HostName = clubId > 0 ? (_clubService.GetClubNameById(clubId) ?? "") : "Kretsen",
                        TakesFee = decimal.TryParse((x.c.GetValue<string>("registrationFee") ?? "").Replace(',', '.'),
                                       System.Globalization.NumberStyles.Number,
                                       System.Globalization.CultureInfo.InvariantCulture, out var fee) && fee > 0
                    };
                })
                .OrderBy(c => c.SeriesName ?? "￿").ThenBy(c => c.Date).ThenBy(c => c.Name)
                .ToList();
        }

        // ⚠️ Value<DateTime?> ger DateTime.MinValue för en osatt egenskap, inte null.
        private static DateTime? DateOf(IContent c)
        {
            var d = c.GetValue<DateTime?>("competitionDate");
            return d is null || d.Value == DateTime.MinValue ? null : d;
        }

        public List<int> GetSelection(int regionId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT CompetitionId FROM RegionFeeCompetition WHERE RegionId = @0 AND Year = @1 ORDER BY CompetitionId",
                regionId, year);
        }

        /// <summary>Ersätter årets val. Tävlingarna ska redan vara prövade mot kretsens kandidater.</summary>
        public void SaveSelection(int regionId, int year, IEnumerable<int> competitionIds, int byMemberId)
        {
            var ids = competitionIds.Where(i => i > 0).Distinct().ToList();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("DELETE FROM RegionFeeCompetition WHERE RegionId = @0 AND Year = @1", regionId, year);
            foreach (var id in ids)
                db.Insert(new RegionFeeCompetition
                {
                    RegionId = regionId, Year = year, CompetitionId = id,
                    CreatedUtc = DateTime.UtcNow, CreatedByMemberId = byMemberId
                });
        }

        /// <summary>
        /// Starter per klubb i de valda tävlingarna. Bara klubbar i <paramref name="regionClubIds"/> tas med —
        /// en gästande klubb från en annan krets betalar inte den här kretsens avgift.
        /// </summary>
        /// <param name="useCache">false precis före ett utskick, så att räkningen bygger på dagens resultat.</param>
        public Dictionary<int, RegionClubStarts> Count(IReadOnlyCollection<int> competitionIds, ISet<int> regionClubIds,
            bool useCache = true)
        {
            var result = new Dictionary<int, RegionClubStarts>();
            if (competitionIds.Count == 0) return result;

            var key = "regionfee_counts_" + string.Join(",", competitionIds.OrderBy(i => i))
                    + "|" + string.Join(",", regionClubIds.OrderBy(i => i));
            if (useCache && _appCaches.RuntimeCache.Get(key) is Dictionary<int, RegionClubStarts> hit) return hit;

            var primaryCache = new Dictionary<int, int>();
            foreach (var competitionId in competitionIds)
            {
                try
                {
                    var starts = StartsIn(competitionId);
                    if (starts.Count == 0) continue;

                    var regClubs = _memberClubs.GetRegistrationClubIds(competitionId);
                    var needPrimary = starts.Select(s => s.MemberId).Distinct()
                        .Where(m => !regClubs.ContainsKey(m) && !primaryCache.ContainsKey(m)).ToArray();
                    if (needPrimary.Length > 0)
                        foreach (var m in _memberService.GetAllMembers(needPrimary))
                            primaryCache[m.Id] = _memberClubs.GetPrimaryClubId(m);

                    foreach (var s in starts)
                    {
                        var clubId = regClubs.TryGetValue(s.MemberId, out var rc) ? rc : primaryCache.GetValueOrDefault(s.MemberId);
                        if (clubId <= 0 || !regionClubIds.Contains(clubId)) continue;
                        if (!result.TryGetValue(clubId, out var club)) result[clubId] = club = new RegionClubStarts();
                        club.Starts++;
                        club.ByCompetition[competitionId] = club.ByCompetition.GetValueOrDefault(competitionId) + 1;
                    }
                }
                catch (Exception ex)
                {
                    // En tävling som inte går att läsa får inte ta ner kretsens hela lista — men den SÄGS
                    // i loggen, eftersom en tyst lucka blir en för låg räkning.
                    _logger.LogError(ex, "Kretsavgift: starterna i tävling {CompetitionId} gick inte att räkna", competitionId);
                }
            }

            foreach (var club in result.Values) club.Competitions = club.ByCompetition.Count;
            _appCaches.RuntimeCache.Insert(key, () => result, TimeSpan.FromMinutes(2));
            return result;
        }

        private sealed record Start(int MemberId, string ClassKey);

        /// <summary>Tävlingens starter: en per (skytt, klass) med resultat.</summary>
        private List<Start> StartsIn(int competitionId)
        {
            var comp = _contentService.GetById(competitionId);
            if (comp is null) return new List<Start>();
            var type = (comp.GetValue<string>("competitionType") ?? "").Trim();
            var canonical = HpskSite.Models.CompetitionTypes.GetFuzzy(type)?.Id ?? type;
            var table = CompetitionResultTables.For(canonical);

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            List<(int MemberId, string Cls)> rows;
            if (table == "SpringskytteResultEntry")
            {
                // ⚠️ En DNS har en rad med status DNS — skytten startade aldrig.
                rows = db.Fetch<ClassRow>(
                        "SELECT DISTINCT MemberId, WeaponClass AS Cls FROM SpringskytteResultEntry "
                      + "WHERE CompetitionId = @0 AND (Status IS NULL OR Status <> 'DNS')", competitionId)
                    .Select(r => (r.MemberId, r.Cls ?? "")).ToList();
            }
            else
            {
                rows = db.Fetch<ClassRow>(
                        $"SELECT DISTINCT MemberId, ShootingClass AS Cls FROM [{table}] WHERE CompetitionId = @0", competitionId)
                    .Select(r => (r.MemberId, r.Cls ?? "")).ToList();
            }

            // ⚠️ Klassen finns i två former (C_Vet_Y / "C Vet Y"). Viks ihop, annars blir en serie
            //    inmatad i båda formerna två starter.
            return rows.Where(r => r.MemberId > 0)
                .Select(r => new Start(r.MemberId, ShootingClasses.NormalizeKey(r.Cls)))
                .Distinct()
                .ToList();
        }

        private sealed class ClassRow
        {
            public int MemberId { get; set; }
            public string? Cls { get; set; }
        }
    }

    public class RegionFeeCandidate
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime Date { get; set; }
        public string? SeriesName { get; set; }
        public string TypeLabel { get; set; } = "";
        public string HostName { get; set; } = "";

        /// <summary>Tävlingen tar anmälningsavgift på pistol.nu — ytan varnar för att ta betalt två gånger.</summary>
        public bool TakesFee { get; set; }
    }
}
