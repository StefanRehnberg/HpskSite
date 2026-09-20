using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>Resultatet av ett bygge: listan, ett felmeddelande, eller ingetdera.</summary>
    public sealed class FaltskytteResultsBuildResult
    {
        public FaltskylteFinalResults? Results { get; init; }

        /// <summary>Meddelande att visa när listan inte kunde byggas. Null vid lyckat bygge.</summary>
        public string? Error { get; init; }

        /// <summary>
        /// Medaljställningarna per mästerskapskategori — ordnade som medaljerna ska delas ut.
        /// Tom för en tävling som inte är ett mästerskap.
        /// </summary>
        public List<FaltskytteCategoryStanding> CategoryStandings { get; init; } = new();

        public static FaltskytteResultsBuildResult Failed(string message) => new() { Error = message };
    }

    /// <summary>
    /// Bygger fältskyttets resultatlista — EN gång, för alla som behöver den.
    ///
    /// ⚠️ DEN HÄR KLASSEN ÄR SVARET PÅ EN KONKRET FARA. Logiken låg tidigare bara i
    /// <c>FaltskytteController.GetFaltskytteResults</c>, och när prisutdelningen behövde samma
    /// ställning var den frestande vägen att räkna om den på andra sidan. Det är exakt så
    /// mästerskapskategori och skicklighetsklass har förväxlats fyra gånger i den här kodbasen.
    /// Nu bygger både resultatsidan, särskjutningskortet och prisutdelningens artefakt ur den
    /// här metoden, och kan därför aldrig säga olika saker om samma medalj.
    ///
    /// ⚠️ Fältskytte äger sin egen resultattabell och sina egna ytor och routar ALDRIG genom
    /// precisionsfamiljens delade resultatendpoints — se
    /// <see cref="CompetitionResultTables.ForSharedResultEndpoint"/>, som kastar med flit för
    /// Faltskytte/MagnumFalt.
    /// </summary>
    public class FaltskytteResultsBuilder
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly ClubService _clubService;
        private readonly UmbracoStartListRepository _startListRepository;
        private readonly FaltskytteShootOffService _shootOffService;
        private readonly ILogger<FaltskytteResultsBuilder> _logger;

        public FaltskytteResultsBuilder(
            IContentService contentService,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ClubService clubService,
            UmbracoStartListRepository startListRepository,
            FaltskytteShootOffService shootOffService,
            ILogger<FaltskytteResultsBuilder> logger)
        {
            _contentService = contentService;
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _clubService = clubService;
            _startListRepository = startListRepository;
            _shootOffService = shootOffService;
            _logger = logger;
        }

        /// <summary>Sort order for class names in result lists: C→B→A→R→M, then by level and variant.</summary>
        public static int GetClassSortOrder(string className)
        {
            if (string.IsNullOrEmpty(className)) return 9999;
            // Weapon group order
            var weaponOrder = className[0] switch { 'C' => 100, 'L' => 200, 'B' => 300, 'A' => 400, 'R' => 500, 'M' => 600, _ => 800 };
            // Sub-order within weapon group: class number, then variant
            var sub = 0;
            if (className.Contains("1")) sub = 10;
            else if (className.Contains("2")) sub = 20;
            else if (className.Contains("3")) sub = 30;
            // Variant suffix
            if (className.Contains("Dam")) sub += 1;
            else if (className.Contains("Vet Y")) sub += 2;
            else if (className.Contains("Vet Ä")) sub += 3;
            else if (className.Contains("Vet")) sub += 2;
            else if (className.Contains("Jun")) sub += 4;
            // Merged classes (contain +) sort after their base
            if (className.Contains("+")) sub += 5;
            return weaponOrder + sub;
        }

        public static FaltskytteCompetitionConfig ParseCompetitionConfig(IContent competition)
        {
            var configJson = competition.GetValue<string>("stationConfig");
            return FaltskytteConfigParser.Parse(configJson);
        }

        public async Task<FaltskytteResultsBuildResult> BuildAsync(
            int competitionId, string? mergeConfig = null, bool subCompetitionOnly = false)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return FaltskytteResultsBuildResult.Failed("Tävlingen hittades inte.");

            var competitionConfig = ParseCompetitionConfig(competition);
            // Tävlingstyp from the config first (the property is a stale-able mirror) —
            // this decides Normalfält "32/22" vs Poängfält "54 p" scoring below.
            var scoringMode = FaltskytteScoringMode.Resolve(competitionConfig, competition.GetValue<string>("scoringMode"));
            // For result display, use the first available weapon class config to determine station count.
            // Stations marked IsShootOffOnly are NOT counted — they don't contribute to the qualification
            // ranking and they're filtered out everywhere else (admin links, public station card).
            var firstWcConfig = competitionConfig.WeaponConfigs.Values.FirstOrDefault();
            var stationCount = firstWcConfig?.Stations.Count(s => !s.IsShootOffOnly) ?? 0;
            var shootOffOnlyStationNumbers = (firstWcConfig?.Stations
                .Where(s => s.IsShootOffOnly)
                .Select(s => s.Station)
                .ToHashSet()) ?? new HashSet<int>();

            using var db = _umbracoDatabaseFactory.CreateDatabase();
            var allResults = await db.FetchAsync<FaltskytteResultEntry>(
                "WHERE CompetitionId = @0 ORDER BY MemberId, StationNumber", competitionId);

            // Belt-and-braces: even if any legacy FaltskytteResultEntry rows exist for a
            // station that's now marked IsShootOffOnly, exclude them from the qualification
            // totals. Shoot-off scores live in FaltskytteShootOffEntry.
            if (shootOffOnlyStationNumbers.Count > 0)
                allResults = allResults.Where(r => !shootOffOnlyStationNumbers.Contains(r.StationNumber)).ToList();

            if (!allResults.Any())
                return FaltskytteResultsBuildResult.Failed("Inga resultat finns.");

            // Get patrol members for name/club lookup
            var patrols = await db.FetchAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0", competitionId);
            var patrolIds = patrols.Select(p => p.Id).ToList();
            var allMembers = patrolIds.Any()
                ? await db.FetchAsync<FaltskyttePatrolMember>(
                    $"WHERE PatrolId IN ({string.Join(",", patrolIds)})")
                : new List<FaltskyttePatrolMember>();
            var memberLookup = allMembers
                .GroupBy(m => m.MemberId)
                .ToDictionary(g => g.Key, g => g.First());

            // Build shooter results
            var shooterResults = allResults
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g =>
                {
                    var memberId = g.Key.MemberId;
                    var member = memberLookup.GetValueOrDefault(memberId);
                    var stationResults = g.OrderBy(r => r.StationNumber)
                        .Select(r => new FaltskytteStationResult
                        {
                            StationNumber = r.StationNumber,
                            Hits = r.Hits,
                            Figures = r.Figures,
                            TiebreakerScore = r.TiebreakerScore
                        }).ToList();

                    var totalHits = stationResults.Sum(s => s.Hits);
                    var totalFigures = stationResults.Sum(s => s.Figures);
                    var totalPoints = stationResults.Sum(s => s.Points);
                    var totalTiebreaker = stationResults.Where(s => s.TiebreakerScore.HasValue)
                        .Sum(s => s.TiebreakerScore!.Value);

                    return new FaltskytteShooterResult
                    {
                        MemberId = memberId,
                        Name = member?.MemberName ?? "Okänd skytt",
                        Club = HpskSite.Helpers.ClubNameHelper.Shorten(member?.ClubName ?? ""),
                        ShootingClass = HpskSite.Models.ShootingClasses.ToCanonicalName(g.Key.ShootingClass),
                        Stations = stationResults,
                        TotalHits = totalHits,
                        TotalFigures = totalFigures,
                        TotalPoints = totalPoints,
                        TotalTiebreakerScore = totalTiebreaker
                    };
                }).ToList();

            // Filter for sub-competition if requested
            if (subCompetitionOnly)
            {
                var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                var subCompMemberIds = new HashSet<int>(
                    registrations.Where(r => r.IsSubCompetition).Select(r => r.MemberId));
                shooterResults = shooterResults.Where(s => subCompMemberIds.Contains(s.MemberId)).ToList();
            }

            // Locate the competitionResult child node — used both for the sub-comp's
            // own merge config / official flag and as a fallback when nothing was passed
            // in. The node may not exist yet if results have never been published.
            var resultPageNode = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

            // Build merge lookup from config (if provided)
            var mergeLookup = new Dictionary<string, string>(); // source class → combined group name
            if (string.IsNullOrEmpty(mergeConfig))
            {
                // Sub-comp reads from its own slot on the competitionResult node;
                // main reads from the competition's mergeConfig (existing pattern).
                if (subCompetitionOnly)
                {
                    mergeConfig = resultPageNode != null && resultPageNode.HasProperty("subCompetitionMergeConfig")
                        ? resultPageNode.GetValue<string>("subCompetitionMergeConfig") ?? ""
                        : "";
                }
                else
                {
                    mergeConfig = competition.HasProperty("mergeConfig") ? competition.GetValue<string>("mergeConfig") ?? "" : "";
                }
            }
            if (!string.IsNullOrEmpty(mergeConfig))
            {
                try
                {
                    var mergeActions = Newtonsoft.Json.JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeConfig);
                    if (mergeActions != null)
                    {
                        // Use union-find so multi-source merges (C2 Dam + C3 Dam + C Vet Y all → C2)
                        // collapse into ONE combined group, matching the Precision fix.
                        var unified = ClassMergingService.BuildMergeGroupLookup(mergeActions);
                        foreach (var kv in unified)
                            mergeLookup[kv.Key] = kv.Value;
                    }
                }
                catch { /* ignore invalid merge config */ }
            }

            // Group by class (applying merge lookup) and rank
            var isPoang = scoringMode.Equals("Poang", StringComparison.OrdinalIgnoreCase);
            var tieBreaker = new FaltskylteTieBreaker(isPoang);
            var classGroups = shooterResults
                .GroupBy(s => mergeLookup.GetValueOrDefault(s.ShootingClass, s.ShootingClass))
                .Select(g => new FaltskytteClassGroup
                {
                    ClassName = g.Key,
                    Shooters = g.OrderByDescending(s => s, tieBreaker).ToList()
                })
                .OrderBy(g => GetClassSortOrder(g.ClassName))
                .ToList();

            // Standard medals are calculated on whatever shooter set we have — for the
            // Deltävling that's the (smaller) filtered subset, so 1/9 silver and 1/3 bronze
            // quotas are computed over the Deltävling participants only. Gated on
            // isAwardingStandardMedals AND !isClubOnly per BR-PS.1.3 (club competitions
            // never award standard medals). When either gate fails the StandardMedal field
            // on each shooter stays empty and the views drop the Std column.
            var isAwardingStandardMedals = competition.GetValue<bool>("isAwardingStandardMedals");
            var isClubOnly = competition.GetValue<bool>("isClubOnly");
            var competitionScope = competition.GetValue<string>("competitionScope") ?? "";
            // SHB 2026: standard medals are split per C-category at SM AND Landsdelsmästerskap
            // (pre-existing hardcode; KrM/KM use the merged C grouping). Keep the SM-only split here
            // because the StandardMedalService's `isChampionship` flag specifically gates the C-split.
            var isSmOrLdm = competitionScope == CompetitionScopeHelper.SvensktMasterskap
                         || competitionScope == CompetitionScopeHelper.Landsdelsmasterskap;
            if (isAwardingStandardMedals && !isClubOnly)
            {
                var medalService = new FaltskytteStandardMedalService();
                medalService.CalculateStandardMedals(shooterResults, scoringMode, stationCount, isSmOrLdm);
            }
            // ── Mästerskapskategorier och särskjutning ───────────────────────────────
            //
            // ⚠️ MEDALJEN AVGÖRS PER MÄSTERSKAPSKATEGORI, INTE PER SKICKLIGHETSKLASS.
            // C1, C2 och C3 är samma mästerskap (vapengrupp C öppen) och delar EN uppsättning
            // medaljer; vid mästerskap delas C dessutom i Dam, Vet Y, Vet Ä och Jun. SHB
            // C.3.6.5.1 räknar upp grupperna: "vapengrupperna A, B, C, samt klasserna Damer C,
            // Juniorer C, Veteraner C" — och R i fält.
            //
            // Detekteringen gick tidigare på resultatlistans klassgrupper, alltså på
            // skicklighetsklasserna. Det är inte bara fel etikett utan FEL MEDALJ: står två
            // lika i C3 kan det se ut som en silverstrid, medan en C1-skytt ligger emellan i
            // kategorin C och striden i själva verket gäller bronset. Precisionsfamiljen bar
            // samma fel till 2026-09-07 (3e1df1e).
            //
            // Resultatlistan visar fortfarande C1/C2/C3 var för sig — det är bara medaljfrågan
            // som ställs per kategori.
            var competitionType = competition.GetValue<string>("competitionType") ?? "Faltskytte";
            var medalCategoryTies = new List<FaltskytteMedalCategoryTies>();
            var categoryStandings = new List<FaltskytteCategoryStanding>();

            if (CompetitionScopeHelper.IsChampionshipScope(competitionScope))
            {
                // Arrangörens val (klubb/krets): en uppsättning medaljer per vapengrupp, eller
                // delade mästerskapsklasser. Nivåspärren ligger i MedalGrouping.
                var splitC = ChampionshipCategory.SplitsGroupC(
                    competitionScope, MedalGrouping.PerWeaponGroup(competition));
                categoryStandings = shooterResults
                    .GroupBy(s => ChampionshipCategory.For(s.ShootingClass, splitC))
                    .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                    .Select(g => new FaltskytteCategoryStanding
                    {
                        CategoryName = g.Key,
                        WeaponGroup = ChampionshipCategory.WeaponGroupFor(g.First().ShootingClass),
                        Participants = g.Select(s => s.MemberId).Distinct().Count(),
                        // Samma objektreferenser som klassgrupperna håller, så särskjutningens
                        // annotering (den publika Sär-brickan) syns i resultatlistan också.
                        Ordered = g.OrderByDescending(s => s, tieBreaker).ToList()
                    })
                    .OrderBy(c => GetClassSortOrder(c.CategoryName))
                    .ToList();

                var comparer = FaltskytteShootOffService.ComparerFor(competitionType, scoringMode);
                var shootOffEntries = await _shootOffService.GetEntriesForCompetitionAsync(competitionId);
                var entriesByMember = shootOffEntries.ToLookup(e => e.MemberId);

                foreach (var standing in categoryStandings)
                {
                    var tied = FaltskytteShootOffService.DetectTiedMedalGroups(
                        standing.Ordered, scoringMode, competitionType);
                    if (tied.Count == 0) continue;

                    FaltskytteShootOffService.ApplyShootOffOverride(
                        standing.Ordered, tied, entriesByMember, comparer);

                    medalCategoryTies.Add(new FaltskytteMedalCategoryTies
                    {
                        CategoryName = standing.CategoryName,
                        Groups = tied
                    });

                    foreach (var g in tied)
                    {
                        if (!g.Resolved || g.Shooters.Count < 2) continue;
                        var ordered = g.Shooters
                            .Where(s => s.Rounds != null && s.Rounds.Count > 0)
                            .ToList();
                        if (ordered.Count < 2) continue;

                        var medalNouns = FaltskytteShootOffService.MedalNounsForRange(g.FirstRank, g.LastRank);
                        var parts = ordered.Select(s =>
                        {
                            var lastRound = s.Rounds.OrderByDescending(r => r.Round).First();
                            return $"{s.Name} {lastRound.Display}";
                        });
                        var note = $"Särskjutning avgjorde {medalNouns} i {standing.CategoryName}: "
                                 + string.Join(" vs ", parts);

                        // ⚠️ Noten hör till kategorin men RENDERAS per klasstabell. Låg den bara
                        // på kategorin blev en avgjord särskjutning osynlig för läsaren, så den
                        // upprepas under varje klass en tiad skytt står i — och namnger
                        // kategorin, så det framgår var medaljen avgjordes.
                        foreach (var cg in classGroups.Where(cg =>
                                     cg.Shooters.Any(sh => g.Shooters.Any(ts => ts.MemberId == sh.MemberId
                                         && string.Equals(ts.ShootingClass, sh.ShootingClass, StringComparison.OrdinalIgnoreCase)))))
                        {
                            if (!cg.ShootOffNotes.Contains(note)) cg.ShootOffNotes.Add(note);
                        }
                    }
                }
            }

            // Header metadata for the result-list printout / on-screen card —
            // matches what the Precision result page surfaces (competition
            // name, date, organiser, status).
            var competitionName = competition.Name ?? competition.GetValue<string>("competitionName") ?? "";
            var competitionDateValue = competition.GetValue<DateTime?>("competitionDate");
            var competitionDateStr = competitionDateValue.HasValue
                ? competitionDateValue.Value.ToString("yyyy-MM-dd")
                : "";
            var organizerClubId = competition.GetValue<int>("clubId");
            var organizerName = organizerClubId > 0
                ? (_clubService.GetClubNameById(organizerClubId) ?? "")
                : "";

            // IsOfficial reflects whichever flag is relevant for this payload:
            //   sub-comp → resultPageNode.subCompetitionIsOfficial
            //   main    → competition.faltskytteResultsOfficial
            bool isOfficialForPayload;
            if (subCompetitionOnly)
            {
                isOfficialForPayload = resultPageNode != null
                    && resultPageNode.HasProperty("subCompetitionIsOfficial")
                    && resultPageNode.GetValue<bool>("subCompetitionIsOfficial");
            }
            else
            {
                isOfficialForPayload = competition.HasProperty("faltskytteResultsOfficial")
                    && competition.GetValue<bool>("faltskytteResultsOfficial");
            }

            var subCompetitionName = competition.HasProperty("subCompetitionName")
                ? competition.GetValue<string>("subCompetitionName") ?? ""
                : "";

            return new FaltskytteResultsBuildResult
            {
                CategoryStandings = categoryStandings,
                Results = new FaltskylteFinalResults
                {
                    CompetitionId = competitionId,
                    UpdatedAt = DateTime.Now,
                    IsOfficial = isOfficialForPayload,
                    ScoringMode = scoringMode,
                    StationCount = stationCount,
                    Config = competitionConfig,
                    ClassGroups = classGroups,
                    MedalCategoryTies = medalCategoryTies,
                    CompetitionName = competitionName,
                    CompetitionDate = competitionDateStr,
                    OrganizerName = organizerName,
                    IsSubCompetition = subCompetitionOnly,
                    SubCompetitionName = subCompetitionName,
                    IsAwardingStandardMedals = isAwardingStandardMedals && !isClubOnly
                }
            };
        }
    }
}
