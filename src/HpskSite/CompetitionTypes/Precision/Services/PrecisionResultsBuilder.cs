// Flyttad ur CompetitionResultsController 2026-11-06 (förarbete för "Utanför mästerskapet" i precisionsfamiljen, K9).
// RENT FLYTTSTEG: metodkropparna är ordagrant desamma, bara tjänsterna kommer från konstruktorn i stället för controllerns fält.
// Skälet: byggaren var en privat metod i en controller och kunde inte köras som "hur ser listan ut om…" (Rätta), från en
// bakgrundstjänst eller utan controllern. Bevis: golden-master identisk mot ref-P0 (tools/golden).
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services.Mail;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Milsnabb.Services;
using HpskSite.CompetitionTypes.Milsnabb.Models;
using HpskSite.CompetitionTypes.Duell.Services;
using HpskSite.CompetitionTypes.Duell.Models;
using HpskSite.CompetitionTypes.NationellHelmatch.Services;
using HpskSite.CompetitionTypes.NationellHelmatch.Models;
using HpskSite.CompetitionTypes.MagnumPrecision.Services;
using HpskSite.CompetitionTypes.MagnumPrecision.Models;
using Newtonsoft.Json;
using PrecisionResultEntry = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntry;
using ResultEntryRequest = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntryRequest;
using ResultEntryResponse = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntryResponse;
using DeleteResultRequest = HpskSite.CompetitionTypes.Precision.Models.PrecisionDeleteResultRequest;
using ShooterResult = HpskSite.CompetitionTypes.Precision.Models.PrecisionShooterResult;
using ClassGroup = HpskSite.CompetitionTypes.Precision.Models.PrecisionClassGroup;
using FinalResults = HpskSite.CompetitionTypes.Precision.Models.PrecisionFinalResults;
using System.Data.SqlClient;
using Microsoft.Data.SqlClient;
using System.Data;
using HpskSite.Controllers;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Räknar fram en precisionsfamiljs resultatlista (klassgrupper, placeringar, standardmedaljer, mästerskapskategorier,
    /// särskjutning) ur resultatraderna. Skriver ingenting.
    /// </summary>
    public class PrecisionResultsBuilder
    {
        private readonly ILogger<PrecisionResultsBuilder> _logger;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.StartLists.IStartListStore _startLists;
        private readonly ShootOffService _shootOffService;
        private readonly ParticipantStatusService _participantStatusService;
        private readonly UmbracoStartListRepository _startListRepository;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;

        public PrecisionResultsBuilder(
            ILogger<PrecisionResultsBuilder> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.StartLists.IStartListStore startLists,
            ShootOffService shootOffService,
            ParticipantStatusService participantStatusService,
            UmbracoStartListRepository startListRepository,
            IMemberService memberService,
            ClubService clubService)
        {
            _logger = logger;
            _competitions = competitions;
            _startLists = startLists;
            _shootOffService = shootOffService;
            _participantStatusService = participantStatusService;
            _startListRepository = startListRepository;
            _memberService = memberService;
            _clubService = clubService;
        }

        /// <summary>Valören för en placering. Regeln bor i <see cref="ChampionshipMedalCount"/>.</summary>
        private static string MedalNameForPlace(int place)
            => ChampionshipMedalCount.MedalNameForPlace(place);

        /// <summary>Antal mästerskapsmedaljer i en mästerskapsklass. Regeln bor i <see cref="ChampionshipMedalCount"/>.</summary>
        private static (int Medals, string Text) ChampionshipMedals(int participants, bool isJuniorCategory)
            => ChampionshipMedalCount.For(participants, isJuniorCategory);

        /// <summary>
        /// Namn och klubb för ALLA skyttar i en resultatlista — i ETT svep.
        ///
        /// ⚠️⚠️ Ersätter en loop som anropade <see cref="GetShooterNameAndClub"/> en gång per
        /// skytt. Kommentaren där sa "PERFORMANCE FIX: build the lookup ONCE", men den
        /// avdubblade bara per RESULTATRAD (1174 → 145 anrop på SSM 2026) — och varje anrop
        /// gjorde TVÅ fulla innehållsläsningar: alla tävlingens barn, och sedan hela
        /// anmälningsnavet med sina 94 anmälningsnoder. Alltså ~145 × (barn + 94 noder) per
        /// uppdatering av resultatlistan, vilket mättes till 8–16 sekunder. Rapporterat
        /// 2026-09-08. Samma mönster som gjorde Fakturor-sidan 12 sekunder lång.
        ///
        /// ⚠️ PRECEDENSEN ÄR OFÖRÄNDRAD och avsiktligt bevarad rad för rad: startlistan
        /// under det ÄLDRE navet först, sedan anmälningarna, sist medlemsregistret. Att här
        /// börja läsa den MODERNA startlistan (direkt barn) hade varit en tysk
        /// beteendeändring i en prestandafix — se anteckningen i CLAUDE.md om att
        /// resultatlistan inte läser startlistan först på moderna tävlingar, tvärtemot vad
        /// dokumentationen påstår.
        /// </summary>
        private async Task<Dictionary<int, (string Name, string Club)>> BuildShooterLookupAsync(
            int competitionId, List<int> memberIds)
        {
            var map = new Dictionary<int, (string Name, string Club)>();
            if (memberIds == null || memberIds.Count == 0) return map;

            // Steg 1 — startlistan under det äldre navet, läst EN gång.
            var fromStartList = LoadLegacyHubStartListShooters(competitionId);
            foreach (var id in memberIds)
            {
                if (fromStartList.TryGetValue(id, out var sl) && sl.Name != "Unknown")
                    map[id] = sl;
            }

            // Steg 2 — anmälningarna, lästa EN gång.
            var missing = memberIds.Where(id => !map.ContainsKey(id)).ToList();
            if (missing.Count > 0)
            {
                try
                {
                    var registrations = await _startListRepository.GetCompetitionRegistrations(competitionId);
                    var byMember = new Dictionary<int, (string Name, string Club)>();
                    foreach (var r in registrations ?? new())
                    {
                        if (r.MemberId <= 0 || string.IsNullOrEmpty(r.MemberName)) continue;
                        if (!byMember.ContainsKey(r.MemberId))
                            byMember[r.MemberId] = (r.MemberName, r.MemberClub ?? "Okänd klubb");
                    }
                    foreach (var id in missing)
                    {
                        if (byMember.TryGetValue(id, out var reg)) map[id] = reg;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read registrations for competition {CompetitionId}", competitionId);
                }
            }

            // Steg 3 — medlemsregistret, bara för dem som fortfarande saknas. En skytt som
            // direktplacerats vid disken kan sakna anmälningsnod, så grenen behövs — men den
            // gäller ett fåtal, inte alla.
            foreach (var id in memberIds.Where(id => !map.ContainsKey(id)))
            {
                try
                {
                    var member = _memberService.GetById(id);
                    if (member != null)
                    {
                        var clubId = member.GetValue<int>("primaryClubId");
                        map[id] = (member.Name ?? "Unknown",
                                   clubId > 0 ? (_clubService?.GetClubNameById(clubId) ?? "Okänd klubb") : "Okänd klubb");
                    }
                    else
                    {
                        _logger.LogWarning("Could not find name/club for MemberId {MemberId}", id);
                        map[id] = ("Unknown", "Unknown");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting shooter name and club for MemberId {MemberId}", id);
                    map[id] = ("Unknown", "Unknown");
                }
            }

            return map;
        }

        /// <summary>
        /// Skyttarna på den officiella startlistan UNDER DET ÄLDRE NAVET, memberId → (namn, klubb).
        /// Tom karta när navet inte finns, vilket är fallet för varje tävling skapad efter att
        /// startlistorna blev direkta barn.
        /// </summary>
        private Dictionary<int, (string Name, string Club)> LoadLegacyHubStartListShooters(int competitionId)
        {
            var map = new Dictionary<int, (string Name, string Club)>();
            try
            {
                var officialStartList = _startLists.List(competitionId, HpskSite.Models.StartLists.StartListKind.Qualifying)
                    .FirstOrDefault(r => r.LegacyHub && r.IsOfficial);
                if (officialStartList == null) return map;

                var json = officialStartList.ConfigurationData;
                if (string.IsNullOrEmpty(json)) return map;

                var config = JsonConvert.DeserializeObject<StartListConfiguration>(json);
                foreach (var team in config?.Teams ?? new List<StartListTeam>())
                {
                    foreach (var sh in team.Shooters ?? new List<StartListShooter>())
                    {
                        if (sh.MemberId > 0 && !map.ContainsKey(sh.MemberId))
                            map[sh.MemberId] = (sh.Name ?? "Unknown", sh.Club ?? "Unknown");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read legacy hub start list for competition {CompetitionId}", competitionId);
            }
            return map;
        }

        /// <summary>
        /// De STARTER som står på tävlingens finalstartlistor — nycklade
        /// <c>(MemberId, klass)</c> och inte bara på medlem. Tom mängd betyder att ingen
        /// finalstartlista är genererad än, och anroparen måste läsa det som "vet inte",
        /// aldrig som "ingen".
        ///
        /// ⚠️ NYCKELN MÅSTE BÄRA KLASSEN. En start är per (skytt, klass) i hela den här
        /// kodbasen, och en skytt kan gå till final i en vapengrupp men gallras bort i en
        /// annan — Ivan Slabiak är finalist i A, B och C på SSM 2026, medan andra är
        /// finalister i A men utslagna i C. Med en medlemsbaserad mängd räknades ALLA en
        /// finalists starter som medaljkandidater: mätt på SSM 2026 gav det 12 kandidater i
        /// vapengrupp C och 16 i B där det bara finns 10 finalister i var grupp. Det gav
        /// inget fel utfall där (en gallrad skytts sju serier når inte topp tre när
        /// finalisterna har tio, och grinden nedan filtrerar dem innan finalresultaten är
        /// inne), men det är fel fråga ställd, och den blev bredare när mängden 2026-09-08
        /// blev en union över en finalstartlista per vapengrupp.
        ///
        /// ⚠️ Klassen normaliseras med <c>ShootingClasses.NormalizeKey</c>. Startlistan
        /// lagrar klassens ID ("C_Vet_Y") medan <c>PrecisionShooterResult.ShootingClass</c>
        /// bär visningsNAMNET ("C Vet Y"). En rak jämförelse ser rätt ut i all testning och
        /// missar exakt veteran-, dam-, junior- och optikklasserna.
        /// </summary>
        /// <remarks>
        /// Read regardless of <c>isOfficialFinalsStartList</c> on purpose: that flag is reset
        /// to false on every generation and only flipped when the organiser presses Publicera,
        /// so gating on it would make a perfectly good list look like it has no finalists.
        ///
        /// ⚠️ UNION ÖVER ALLA FINALSTARTLISTOR. Sedan 2026-09-08 finns en lista per
        /// vapengrupp — C:s final på lördagen, A:s på söndagen. Läser man bara den första
        /// blir alla andra gruppers finalister "inte finalister", och särskjutningsgrinden
        /// slutar erbjuda särskjutning om deras medaljer. Att läsa EN lista var exakt rätt
        /// före den ändringen, vilket är varför det är värt en varning.
        /// </remarks>
        public HashSet<string> GetFinalistStarts(int competitionId)
        {
            var finalists = new HashSet<string>();
            try
            {
                // Alla finalstartlistor, även de under den äldre hubben (genom IStartListStore).
                var finalsNodes = _startLists.List(competitionId, HpskSite.Models.StartLists.StartListKind.Finals);

                foreach (var finalsNode in finalsNodes)
                {
                    var configData = finalsNode.ConfigurationData;
                    if (string.IsNullOrWhiteSpace(configData)) continue;

                    var config = Newtonsoft.Json.JsonConvert
                        .DeserializeObject<CompetitionTypes.Precision.Models.StartListConfiguration>(configData);

                    var teams = config?.Teams;
                    if (teams == null) continue;

                    foreach (var team in teams)
                    {
                        var shooters = team.Shooters;
                        if (shooters == null) continue;
                        foreach (var shooter in shooters)
                        {
                            if (shooter.MemberId <= 0) continue;
                            // Saknar raden klass kan starten inte identifieras. Den räknas
                            // ändå in på medlemsnivå ("*"), så en trasig eller mycket gammal
                            // lista degraderar till det tidigare beteendet i stället för att
                            // tappa en finalist helt.
                            var cls = ShootingClasses.NormalizeKey(shooter.WeaponClass);
                            finalists.Add(FinalistKey(shooter.MemberId, cls.Length > 0 ? cls : "*"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A broken finals list must never take the result list down with it. An empty
                // set is the safe answer — the caller then refuses to award medals by
                // särskjutning rather than awarding them on incomplete data.
                _logger.LogWarning(ex, "Could not read finals start list for competition {CompetitionId}", competitionId);
            }
            return finalists;
        }

        /// <summary>Nyckelformen för finalistmängden: medlem + normaliserad klass.</summary>
        private static string FinalistKey(int memberId, string normalizedClass) =>
            memberId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + normalizedClass;

        /// <summary>
        /// Är den här STARTEN en finalist? Faller tillbaka på medlemsnivå ("*") för listor
        /// vars rader saknar klass — se <see cref="GetFinalistStarts"/>.
        /// </summary>
        public static bool IsFinalistStart(HashSet<string> finalistStarts, int memberId, string? shootingClass) =>
            finalistStarts.Contains(FinalistKey(memberId, ShootingClasses.NormalizeKey(shootingClass)))
            || finalistStarts.Contains(FinalistKey(memberId, "*"));

        public async Task<FinalResults> BuildAsync(List<PrecisionResultEntry> results, int competitionId, List<ClassMergeAction>? merges = null)
        {
            if (!results.Any())
            {
                return new FinalResults
                {
                    CompetitionId = competitionId,
                    UpdatedAt = DateTime.Now,
                    IsOfficial = true,
                    ClassGroups = new List<ClassGroup>()
                };
            }

            // Get competition configuration for finals
            var competition = _competitions.GetSaved(competitionId);
            var numberOfSeriesOrStations = competition?.NumberOfSeriesOrStations ?? 0;
            var numberOfFinalSeries = competition?.NumberOfFinalSeries ?? 0;
            var hasFinalsRound = numberOfFinalSeries > 0;
            var qualificationSeriesCount = hasFinalsRound ? (numberOfSeriesOrStations - numberOfFinalSeries) : numberOfSeriesOrStations;

            // Namn och klubb för alla skyttar i ETT svep — se BuildShooterLookupAsync för
            // varför den här raden en gång var en åtta sekunder lång loop.
            //
            // ⚠️ Loggade dessutom en rad PER SKYTT (145 rader per uppdatering). Loggen är
            // till för att felsoka, och 145 rader per klick dänker det som går att felsoka.
            var uniqueMemberIds = results.Select(r => r.MemberId).Distinct().ToList();
            var rawShooterLookup = await BuildShooterLookupAsync(competitionId, uniqueMemberIds);
            var shooterLookup = rawShooterLookup.ToDictionary(
                kv => kv.Key,
                kv => (kv.Value.Name, Club: Helpers.ClubNameHelper.Shorten(kv.Value.Club)));

            // DNS / DNF. Loaded once for the whole competition and probed per shooter below.
            // The status table stores the class *Id* ("C1", "A_opt_1") while the rows here carry the
            // display *Name* — ParticipantStatusService.Key folds both onto the same key, so the
            // probe below may use either form.
            var participantStatuses = await _participantStatusService.GetForCompetitionAsync(competitionId);
            var statusLookup = ParticipantStatusService.BuildLookup(participantStatuses);

            // ⚠️ Group on the CANONICAL class, not the raw column. A shooter whose series were
            // entered from two different surfaces can have some rows stored as the Id ("C_Vet_Y")
            // and some as the Name ("C Vet Y") — the same class, two different strings. Grouping on
            // the raw value split such a shooter into two entries that both rendered "C Vet Y",
            // one holding the grundserier and one the finalserier (klubbmästerskapet 2026-08-25).
            // Writes are canonicalised in SaveResult, so this only has to cover rows already in the
            // table; it stays because the read side must never be the thing that trusts the column.
            var shooterResults = results
                .GroupBy(r => new { r.MemberId, ShootingClass = ShootingClasses.ToCanonicalName(r.ShootingClass) })
                .Select(g =>
                {
                    var memberId = g.Key.MemberId;
                    var shootingClass = g.Key.ShootingClass;
                    var memberResults = g.OrderBy(r => r.SeriesNumber).ToList();

                    // Use cached lookup instead of expensive method call
                    var (name, club) = shooterLookup.TryGetValue(memberId, out var shooterInfo)
                        ? shooterInfo
                        : ("Unknown Shooter", "Unknown Club");

                    statusLookup.TryGetValue(
                        ParticipantStatusService.Key(memberId, shootingClass), out var participantStatus);

                    return new ShooterResult
                    {
                        MemberId = memberId,
                        Name = name,
                        Club = club,
                        ShootingClass = shootingClass, // already the display Name — see the group key
                        Results = memberResults,
                        ParticipationStatus = participantStatus?.Status,
                        ParticipationStatusLabel = participantStatus == null
                            ? null
                            : CompetitionParticipantStatus.DisplayLabel(participantStatus.Status)
                    };
                })
                .ToList();

            // Build merge group lookup: maps original class name → combined group name.
            // Uses union-find so multiple merges into the same target (or chains) collapse
            // into one combined group with one shared name.
            var mergeGroupLookup = ClassMergingService.BuildMergeGroupLookup(merges);
            // Helper to resolve which group a shooter belongs to
            string GetGroupName(string shootingClass) =>
                mergeGroupLookup.TryGetValue(shootingClass, out var group) ? group : shootingClass;

            // Class order: from the registry (C first, then B, A, the A family, R, L, M).
            // The hand-written table this replaced knew neither C Jun, L, M nor AM/AP/AG.
            int ClassOrderKey(string shootingClass) =>
                ShootingClassOrder.Key(shootingClass, ShootingClassOrder.ResultList);

            // Determine competition type for type-specific logic
            var competitionTypeId = competition?.CompetitionType ?? "Precision";
            var isMilsnabb = competitionTypeId.Equals("Milsnabb", StringComparison.OrdinalIgnoreCase);
            var isDuell = competitionTypeId.Equals("Duell", StringComparison.OrdinalIgnoreCase);
            var isNationellHelmatch = competitionTypeId.Equals("NationellHelmatch", StringComparison.OrdinalIgnoreCase);
            var isMagnumPrecision = competitionTypeId.Equals("MagnumPrecision", StringComparison.OrdinalIgnoreCase);
            // Standardpistol och Sportpistol: placeringsmedaljer enbart. SHB publicerar ingen
            // fastpoängstabell för dem som jag kunnat läsa, och att hitta på trösklar är värre än
            // att inte ha några — grundregeln (1/9 silver, 1/3 brons) gäller alltid.
            var isPlacementOnlyNewDiscipline =
                competitionTypeId.Equals("Standardpistol", StringComparison.OrdinalIgnoreCase)
                || competitionTypeId.Equals("Sportpistol", StringComparison.OrdinalIgnoreCase);

            // Select tiebreaker based on competition type
            // Duell uses the same tiebreaker as Precision (series count back)
            //
            // ⚠️ Nationell Helmatch har en EGEN regel och delade tidigare Milsnabbs återräkning
            // på tiopoängspar. SHB särskiljer grenen på delmoment C (fält) och därefter B (duell)
            // — se NationellHelmatchTieBreaker för varför innertiorna inte får komma före.
            IComparer<ShooterResult> comparer =
                isNationellHelmatch ? new NationellHelmatchTieBreaker()
                : isMilsnabb ? new MilsnabbTieBreaker()
                : new SeriesCountBackComparer(hasFinalsRound, qualificationSeriesCount, numberOfFinalSeries);

            // Ordningen inom en klass — se PrecisionResultOrdering för varför den ligger där och
            // inte som ett uttryck här: tre ytor behöver samma svar.
            List<ShooterResult> OrderShooters(IEnumerable<ShooterResult> group) =>
                PrecisionResultOrdering.Order(group, competitionTypeId, comparer);

            // Group by shooting class (using merge lookup if merges were applied).
            // Sort key is the MIN classOrder among the group's constituent shooters' classes,
            // so merged groups (e.g. "C2+Dam", "A2+3", "C Vet") land next to their lowest member
            // instead of falling through to the unknown-class bucket.
            var classGroups = shooterResults
                .GroupBy(s => GetGroupName(s.ShootingClass))
                .OrderBy(g => g.Min(s => ClassOrderKey(s.ShootingClass)))
                .ThenBy(g => g.Key) // stable secondary sort for any unknown classes
                .Select(classGroup => new ClassGroup
                {
                    ClassName = classGroup.Key,
                    Shooters = OrderShooters(classGroup)
                })
                .ToList();

            // Championship Särskjutning override: for medal-tier ties (rank ≤ 3 in any class)
            // in Championship competitions, re-rank the tied slice using shoot-off entries.
            // Other ranks and non-championship competitions fall through to the existing
            // X-count + countback already applied above.
            //
            // A särskjutning decides a MEDAL, so it can only ever follow the round that
            // produces the final standings — never a qualifying round. Two shooters level
            // after the grundomgång are not tied for a medal; they simply both go through
            // to the final, which is what separates them.
            var competitionScope = competition?.CompetitionScope ?? "";
            // Arrangörens val: EN uppsättning medaljer per vapengrupp, eller delade
            // mästerskapsklasser? Nivåspärren (bara klubb och krets) ligger i MedalGrouping.
            var championshipRule = ChampionshipRule.For(competition);
            var medalsPerWeaponGroup = championshipRule.MedalsPerWeaponGroup;
            var medalCategoryTies = new List<PrecisionMedalCategoryTies>();
            var medalAwards = new List<PrecisionMedalCategoryAwards>();
            var medalAwardsComputed = false;
            if (championshipRule.IsChampionshipRaw)
            {
                // Who was taken to the final. Only looked up when there is a final to be taken
                // to; an empty set means no finals start list exists yet, which is "unknown"
                // rather than "nobody" — see the fallback in HasShotEverythingDue below.
                var finalistStarts = hasFinalsRound ? GetFinalistStarts(competitionId) : new HashSet<string>();
                var finalistsKnown = finalistStarts.Count > 0;

                // A tied shooter can be given a medal only once they have shot everything they
                // were due to shoot: the qualifying series for everyone, plus the finals series
                // for those taken to the final. Checked per shooter and not per class on
                // purpose — shooters cut before the final never shoot those series at all, and
                // must not hold back the medal decision for the finalists.
                bool HasShotEverythingDue(ShooterResult shooter)
                {
                    // DNS / DNF settles the question outright: no further results are coming, so
                    // the shooter is as finished as they will ever be. Without this the gate would
                    // wait forever for series that will never be entered.
                    if (shooter.ParticipationStatus != null) return true;

                    // With a finals round but no finals start list to read, we cannot tell who
                    // is due the finals series, so nobody counts as finished. That is what
                    // keeps a plain qualifying-round tie from being mistaken for a medal tie.
                    // ⚠️ Frågan är om DEN HÄR STARTEN gick till final, inte om skytten gjorde
                    // det i någon klass. En skytt som är finalist i A men gallrad i C ska
                    // bedömas på sju serier i sin C-start och på tio i sin A-start.
                    int lastSeriesDue = !hasFinalsRound
                        || (finalistsKnown && !IsFinalistStart(finalistStarts, shooter.MemberId, shooter.ShootingClass))
                        ? qualificationSeriesCount
                        : numberOfSeriesOrStations;

                    for (int series = 1; series <= lastSeriesDue; series++)
                    {
                        if (!shooter.Results.Any(r => r.SeriesNumber == series)) return false;
                    }
                    return true;
                }

                var shootOffEntries = await _shootOffService.GetEntriesForCompetitionAsync(competitionId);
                var entriesByMember = shootOffEntries.ToLookup(e => e.MemberId);

                // ── Vilka grupper avgörs en medalj i, och bland vilka? ───────────────
                //
                // ⚠️ INTE resultatlistans klassgrupper. Medaljen delas ut per FINALKLASS
                // (mästerskapskategori): C, C Dam, C Vet Y, C Vet Ä, C Jun, A, B — aldrig per
                // skicklighetsklass. Klasserna 1–3 är kompetensnivåer, inte egna
                // mästerskapsklasser. Rapporterat 2026-09-07: sidan krävde särskjutning i C2,
                // B2 och A1, och i C3/C3 Dam där striden i själva verket står i C respektive
                // C Dam.
                //
                // ⚠️ Och bara bland dem som kan VINNA en medalj. En skytt som gallrats bort
                // före finalen kan aldrig få medalj, så två sådana som står lika är inte tiade
                // om något. Tre av de fem felaktiga grupperna var precis det: gallrade skyttar
                // som stod lika efter grundomgången.
                //
                // ⚠️ UTAN FINALRUNDA GÄLLER SAMMA KATEGORIER. Det här var tidigare gränsen
                // `useMedalCategories = hasFinalsRound`, och den gjorde att ett mästerskap utan
                // final aldrig fick några medaljörer uträknade alls: `medalAwardsComputed` sattes
                // bara i grenen nedan, så prisutdelningssidan fick "vet inte" och skyllde det på
                // en för gammal resultatlista. Rapporterat efter klubbmästerskapet i Nationell
                // Helmatch 2026-09-19 (tävling 7075), som går i 12 serier rakt igenom.
                //
                // Vad som ändras med finalrunda är bara VILKA som är medaljkandidater: med final
                // bara finalisterna, utan final alla startande — där finns ingen gallring, så en
                // skytt kan inte vara bortsorterad från medaljen.
                var useMedalCategories = true;

                var detectionGroups = new List<(string Key, List<PrecisionShooterResult> Shooters, bool PerCategory)>();

                {
                    // Bara finalister är medaljkandidater. Är finalisterna okända ("ingen
                    // finalstartlista än") är ingen det — samma försiktighet som
                    // HasShotEverythingDue, annars är vi tillbaka i kvalrundefelet.
                    var contenders = !hasFinalsRound
                        ? classGroups.SelectMany(cg => cg.Shooters).ToList()
                        : finalistsKnown
                            ? classGroups.SelectMany(cg => cg.Shooters)
                                .Where(s => IsFinalistStart(finalistStarts, s.MemberId, s.ShootingClass))
                                .ToList()
                            : new List<PrecisionShooterResult>();

                    var splitC = championshipRule.SplitsChampionshipClassC;
                    foreach (var cat in contenders
                                 .GroupBy(s => ChampionshipCategory.For(s.ShootingClass, splitC))
                                 .Where(gr => !string.IsNullOrWhiteSpace(gr.Key)))
                    {
                        // Samma ordningsregel som resultatlistan — annars kan medaljlistan och
                        // tabellen namnge olika personer på samma plats.
                        detectionGroups.Add((cat.Key, OrderShooters(cat), true));
                    }
                }

                foreach (var (groupKey, groupShooters, perCategory) in detectionGroups)
                {
                    var tiedRaw = ShootOffService.DetectTiedMedalGroups(groupShooters, groupKey)
                        .Where(g => g.Shooters.All(HasShotEverythingDue))
                        .ToList();
                    if (tiedRaw.Count == 0) continue;

                    ShootOffService.ApplyShootOffOverride(groupShooters, tiedRaw, entriesByMember);

                    // Var landar grupperna? Per kategori på tävlingsnivå, annars som förut på
                    // klassgruppen. Skytteobjekten är desamma i båda fallen, så ShootOffScore
                    // -annoteringen (den publika SS-brickan) fungerar oavsett.
                    var categoryTies = perCategory
                        ? new PrecisionMedalCategoryTies { CategoryName = groupKey }
                        : null;
                    var classGroup = perCategory
                        ? null
                        : classGroups.First(cg => cg.ClassName == groupKey);

                    // Project DTO (decouple admin payload from the live model)
                    foreach (var g in tiedRaw)
                    {
                        var dto = new PrecisionTiedMedalGroup
                        {
                            MedalTier = g.MedalTier,
                            FirstRank = g.FirstRank,
                            LastRank = g.LastRank,
                            TotalScore = g.TotalScore,
                            RoundsCompleted = g.RoundsCompleted,
                            Resolved = g.Resolved
                        };
                        foreach (var s in g.Shooters)
                        {
                            var rounds = entriesByMember[s.MemberId]
                                .Where(e => string.Equals(e.ShootingClass, s.ShootingClass, StringComparison.OrdinalIgnoreCase))
                                .GroupBy(e => e.Round)
                                .OrderBy(grp => grp.Key)
                                .Select(grp =>
                                {
                                    int total = 0, x = 0;
                                    string rawShots = grp.First().Shots; // round = single 5-shot series in this version
                                    try
                                    {
                                        var shotsList = Newtonsoft.Json.JsonConvert
                                            .DeserializeObject<List<string>>(rawShots) ?? new();
                                        total = (int)CompetitionTypes.Common.Utilities.ScoringUtilities.CalculateTotal(shotsList);
                                        x = CompetitionTypes.Common.Utilities.ScoringUtilities.CountInnerTens(shotsList);
                                    }
                                    catch { /* leave 0/0 */ }
                                    return new PrecisionShootOffRoundEntry
                                    {
                                        Round = grp.Key,
                                        Shots = rawShots,
                                        Total = total,
                                        XCount = x
                                    };
                                })
                                .ToList();

                            dto.Shooters.Add(new PrecisionTiedMedalShooter
                            {
                                MemberId = s.MemberId,
                                Name = s.Name,
                                Club = s.Club,
                                ShootingClass = s.ShootingClass,
                                TotalScore = s.TotalScore,
                                XCount = s.TotalXCount,
                                IsResolved = s.ShootOffIsResolved,
                                NextRound = s.ShootOffNextRound,
                                Rounds = rounds
                            });
                        }
                        if (categoryTies != null) categoryTies.Groups.Add(dto);
                        else classGroup!.TiedMedalGroups.Add(dto);

                        // Build a public-friendly footnote when the group is resolved.
                        if (g.Resolved && g.Shooters.Count >= 2)
                        {
                            var ordered = g.Shooters
                                .Where(s => s.ShootOffScore.HasValue)
                                .OrderBy(s => g.Shooters.IndexOf(s))
                                .ToList();
                            if (ordered.Count >= 2)
                            {
                                var medalNouns = ShootOffService.MedalNounsForRange(g.FirstRank, g.LastRank);
                                var parts = ordered.Select(s => $"{s.Name} {s.ShootOffScore}");
                                var note = $"Särskjutning avgjorde {medalNouns} i {groupKey}: {string.Join(" vs ", parts)}";
                                if (categoryTies != null)
                                {
                                    categoryTies.ShootOffNotes.Add(note);

                                    // Den publika resultatsidan renderar fotnoter PER KLASSTABELL.
                                    // Ligger noten bara på kategorin blir en avgjord särskjutning
                                    // osynlig för läsaren, så den upprepas under varje klass som
                                    // en av de tiade skyttarna står i. Noten namnger finalklassen,
                                    // så det framgår att medaljen avgjordes där och inte i klassen.
                                    foreach (var cg in classGroups.Where(cg =>
                                                 cg.Shooters.Any(sh => g.Shooters.Any(ts => ts.MemberId == sh.MemberId
                                                     && ts.ShootingClass == sh.ShootingClass))))
                                    {
                                        if (!cg.ShootOffNotes.Contains(note)) cg.ShootOffNotes.Add(note);
                                    }
                                }
                                else classGroup!.ShootOffNotes.Add(note);
                            }
                        }
                    }

                    if (categoryTies != null && categoryTies.Groups.Count > 0)
                        medalCategoryTies.Add(categoryTies);
                }

                // ── Vem ska ha vilken medalj? ────────────────────────────────────────
                //
                // Byggs HÄR och inte av prisutdelningssidan, för här är listorna redan
                // rätt: detectionGroups håller finalisterna per mästerskapskategori i
                // poängordning, och ApplyShootOffOverride har just skrivit om den tiade
                // delen av varje lista till särskjutningens utfall. En konsument som
                // rankade om själv skulle behöva återskapa fyra saker samtidigt
                // (kategorin, finalistfiltret, innertiorna, särskjutningen) — och den
                // förväxlingen har uppstått tre gånger i den här kodbasen.
                //
                // ⚠️ TVÅ OLIKA MÄNGDER, som inte får blandas:
                //   ANTALET medaljer mäts på ALLA startande i kategorin (SHB C.3.4.1:
                //     "antalet deltagare i en vapengrupp"),
                //   MEDALJÖRERNA hämtas bara ur finalisterna (en gallrad skytt kan
                //     aldrig få medalj).
                if (useMedalCategories)
                {
                    var splitCForAwards = championshipRule.SplitsChampionshipClassC;
                    var participantsByCategory = classGroups
                        .SelectMany(cg => cg.Shooters)
                        .GroupBy(sh => ChampionshipCategory.For(sh.ShootingClass, splitCForAwards))
                        .Where(gr => !string.IsNullOrWhiteSpace(gr.Key))
                        .ToDictionary(
                            gr => gr.Key,
                            gr => gr.Select(sh => sh.MemberId).Distinct().Count(),
                            StringComparer.OrdinalIgnoreCase);

                    foreach (var (categoryKey, orderedContenders, perCategory) in detectionGroups)
                    {
                        if (!perCategory) continue;

                        var participants = participantsByCategory.TryGetValue(categoryKey, out var pc)
                            ? pc
                            : orderedContenders.Count;
                        var isJunCategory = categoryKey.Contains("Jun", StringComparison.OrdinalIgnoreCase);
                        var (medalCount, medalCountText) = ChampionshipMedals(participants, isJunCategory);

                        var categoryAwards = new PrecisionMedalCategoryAwards
                        {
                            CategoryName = categoryKey,
                            WeaponGroup = orderedContenders.Count > 0
                                ? ChampionshipCategory.WeaponGroupFor(orderedContenders[0].ShootingClass)
                                : "",
                            Participants = participants,
                            MedalCount = medalCount,
                            MedalCountText = medalCountText,
                            Reduced = medalCount < 3
                        };

                        var categoryTieGroups = medalCategoryTies
                            .FirstOrDefault(t => string.Equals(t.CategoryName, categoryKey, StringComparison.OrdinalIgnoreCase))
                            ?.Groups ?? new List<PrecisionTiedMedalGroup>();

                        for (int place = 1; place <= medalCount && place <= orderedContenders.Count; place++)
                        {
                            var shooter = orderedContenders[place - 1];
                            var tie = categoryTieGroups.FirstOrDefault(g => place >= g.FirstRank && place <= g.LastRank);

                            // Oavgjord medaljstrid → INGEN medaljör utses för platsen. Att
                            // skriva ut den som poängordningen råkar ge just nu vore att
                            // namnge fel person i uppropet; C.4.3.1.11 kräver kontrollerade
                            // resultat före prisutdelningen.
                            if (tie != null && !tie.Resolved)
                            {
                                var contenderNames = tie.Shooters
                                    .Select(x => x.Name)
                                    .Where(n => !string.IsNullOrWhiteSpace(n))
                                    .ToList();
                                var between = contenderNames.Count switch
                                {
                                    0 => "",
                                    1 => contenderNames[0],
                                    _ => string.Join(", ", contenderNames.Take(contenderNames.Count - 1))
                                         + " och " + contenderNames.Last()
                                };
                                var line = between.Length > 0
                                    ? $"{MedalNameForPlace(place)} — särskjutning krävs mellan {between}"
                                    : $"{MedalNameForPlace(place)} — särskjutning krävs";
                                if (!categoryAwards.Unresolved.Contains(line)) categoryAwards.Unresolved.Add(line);
                                continue;
                            }

                            // Avgjord särskjutning skrivs ut, dels för att det ofta nämns i
                            // uppropet, dels för att det visar att ordningen inte är gissad.
                            string? decidedBy = null;
                            if (tie != null && tie.Resolved && tie.Shooters.Count >= 2)
                            {
                                int SumRounds(PrecisionTiedMedalShooter x) => x.Rounds?.Sum(r => r.Total) ?? 0;
                                var mine = tie.Shooters.FirstOrDefault(x => x.MemberId == shooter.MemberId
                                    && string.Equals(x.ShootingClass, shooter.ShootingClass, StringComparison.OrdinalIgnoreCase));
                                var others = tie.Shooters
                                    .Where(x => !(x.MemberId == shooter.MemberId
                                        && string.Equals(x.ShootingClass, shooter.ShootingClass, StringComparison.OrdinalIgnoreCase)))
                                    .ToList();
                                if (mine != null && others.Count > 0)
                                {
                                    decidedBy = $"avgjord på särskjutning: {SumRounds(mine)} mot "
                                        + string.Join(" och ", others.Select(o => SumRounds(o).ToString()));
                                }
                            }

                            categoryAwards.Awards.Add(new PrecisionMedalAward
                            {
                                Place = place,
                                Medal = MedalNameForPlace(place),
                                MemberId = shooter.MemberId,
                                Name = shooter.Name,
                                Club = shooter.Club,
                                ShootingClass = shooter.ShootingClass,
                                TotalScore = shooter.TotalScore,
                                XCount = shooter.TotalXCount,
                                DecidedBy = decidedBy
                            });
                        }

                        medalAwards.Add(categoryAwards);
                    }

                    medalAwards = medalAwards
                        .OrderBy(a => a.WeaponGroup, StringComparer.CurrentCulture)
                        .ThenBy(a => a.CategoryName, StringComparer.CurrentCulture)
                        .ToList();

                    // Sätts även när listan blev tom: "beräknat, inga medaljer" är ett annat
                    // svar än "vet inte", och prisutdelningssidan måste kunna skilja dem åt.
                    medalAwardsComputed = true;
                }

                // ── Låt särskjutningen slå igenom i RESULTATTABELLEN också ───────────
                //
                // Särskjutningen detekteras och sorteras om per MÄSTERSKAPSKATEGORI, och den
                // listan är en annan lista än resultattabellens klassgrupper — omsorteringen
                // landade alltså aldrig i tabellen. Läsaren såg då två skyttar i poängordning
                // fast särskjutningen redan skilt dem åt, och bara den lilla SS-brickan
                // antydde att ordningen inte betydde något.
                //
                // Skyttobjekten är desamma, så det räcker att läsa annoteringen: inom en följd
                // med samma totalpoäng sorteras om på särskjutningens rundor i tur och ordning.
                // En skytt utan noterad runda bidrar med 0 för den rundan — exakt samma regel
                // som ShootOffService.ApplyShootOffOverride använder när den sorterar sin egen
                // lista, så de två kan inte ge olika svar.
                foreach (var cg in classGroups)
                {
                    var list = cg.Shooters;
                    int i = 0;
                    while (i < list.Count)
                    {
                        int j = i + 1;
                        while (j < list.Count && list[j].TotalScore == list[i].TotalScore) j++;

                        var run = list.GetRange(i, j - i);
                        if (run.Count >= 2 && run.Count(s => s.ShootOffRoundTotals?.Count > 0) >= 2)
                        {
                            int maxRound = run.Max(s => s.ShootOffRoundTotals?.Count ?? 0);
                            int RoundTotal(ShooterResult s, int round) =>
                                (s.ShootOffRoundTotals != null && s.ShootOffRoundTotals.Count >= round)
                                    ? s.ShootOffRoundTotals[round - 1]
                                    : 0;

                            var slice = run.OrderByDescending(s => RoundTotal(s, 1));
                            for (int r = 2; r <= maxRound; r++)
                            {
                                int round = r;
                                slice = slice.ThenByDescending(s => RoundTotal(s, round));
                            }
                            var ordered = slice.ToList();
                            for (int k = 0; k < ordered.Count; k++) list[i + k] = ordered[k];
                        }

                        i = j;
                    }
                }
            }

            // Calculate Standard Medal Awards if enabled
            var isAwardingStandardMedals = competition?.IsAwardingStandardMedals ?? false;
            var isClubOnly = competition?.IsClubOnly ?? false;

            // Per BR-PS.1.3: Standard medals may not be awarded at Föreningstävlingar (club competitions).
            // The `isClubOnly` flag is the authoritative marker for "this is a club competition" —
            // it covers Klubbmästerskap, regular club competitions, and any other club-internal event.
            if (isAwardingStandardMedals && !isClubOnly)
            {
                var allShooters = classGroups.SelectMany(g => g.Shooters).ToList();
                var shouldSplitGroupC = championshipRule.SplitsStandardMedalGroupC;

                // Per BR-PS.2.3: Standard medals are determined from the qualification round only,
                // not the finals. PrecisionShooterResult.TotalScore sums *all* Results, so when finals
                // exist we must run the calculation on shooters whose Results are filtered to
                // qualification series (SeriesNumber <= qualificationSeriesCount). Without this,
                // 10-series totals get compared against 7-series fixed-score thresholds and false
                // Silver medals are awarded.
                var medalShooters = hasFinalsRound
                    ? allShooters.Select(s => new ShooterResult
                    {
                        MemberId = s.MemberId,
                        Name = s.Name,
                        Club = s.Club,
                        ShootingClass = s.ShootingClass,
                        Results = s.Results.Where(r => r.SeriesNumber <= qualificationSeriesCount).ToList()
                    }).ToList()
                    : allShooters;

                if (isMilsnabb)
                {
                    // Milsnabb has its own fixed-score thresholds (includes R group)
                    var milsnabbMedalService = new MilsnabbStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    milsnabbMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isDuell)
                {
                    // Duell uses placement-only medals (no fixed-score thresholds)
                    var duellMedalService = new DuellStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    duellMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isNationellHelmatch)
                {
                    // Nationell Helmatch uses placement-only medals (same pattern as Duell)
                    var nhMedalService = new NationellHelmatchStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    nhMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isPlacementOnlyNewDiscipline)
                {
                    // Delad, parametriserad tjänst — se PlacementOnlyStandardMedalService för varför
                    // Duells och NationellHelmatchs egna kopior lämnades orörda.
                    // 12 serier = full match; A-familjen poolas INTE, eftersom AM/AP/AG inte ens
                    // erbjuds för dessa grenar (se CLAUDE.md: AM/AP/AG-omfattningen).
                    var placementService = new HpskSite.CompetitionTypes.Common.PlacementOnlyStandardMedalService
                    {
                        MinimumSeriesCount = 12,
                        PoolAFamily = false
                    };
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    placementService.CalculateStandardMedals(medalShooters, config);
                }
                else if (isMagnumPrecision)
                {
                    // Magnum Precision uses percentage + fixed-score medals with M-class thresholds
                    var mpMedalService = new MagnumPrecisionStandardMedalService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    mpMedalService.CalculateStandardMedals(medalShooters, config);
                }
                else
                {
                    // Precision and other types use standard medal service
                    var medalService = new StandardMedalCalculationService();
                    var config = new StandardMedalConfig
                    {
                        SeriesCount = qualificationSeriesCount,
                        ShouldSplitGroupC = shouldSplitGroupC
                    };
                    medalService.CalculateStandardMedals(medalShooters, config);
                }

                // When we used a filtered copy, copy the medals back to the originals.
                if (hasFinalsRound)
                {
                    var medalLookup = medalShooters.ToDictionary(
                        s => (s.MemberId, s.ShootingClass),
                        s => s.StandardMedal);
                    foreach (var shooter in allShooters)
                    {
                        if (medalLookup.TryGetValue((shooter.MemberId, shooter.ShootingClass), out var medal))
                        {
                            shooter.StandardMedal = medal;
                        }
                    }
                }

                _logger.LogInformation("Calculated standard medals for {Count} shooters in competition {CompetitionId} (Type: {Type}, Scope: {Scope}, Series: {SeriesCount})",
                    classGroups.SelectMany(g => g.Shooters).Count(), competitionId, competitionTypeId, competitionScope, qualificationSeriesCount);
            }

            return new FinalResults
            {
                CompetitionId = competitionId,
                UpdatedAt = DateTime.Now,
                IsOfficial = true,
                ClassGroups = classGroups,
                MedalCategoryTies = medalCategoryTies,
                MedalAwards = medalAwards,
                MedalAwardsComputed = medalAwardsComputed,
                MedalsPerWeaponGroup = medalsPerWeaponGroup,
                ParticipantCount = classGroups
                    .SelectMany(g => g.Shooters)
                    .Select(sh => sh.MemberId)
                    .Distinct()
                    .Count()
            };
        }

    }
}
