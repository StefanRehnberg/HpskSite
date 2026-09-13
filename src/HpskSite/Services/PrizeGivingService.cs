using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models.PrizeGiving;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Bygger prisutdelningsvyn ur den SPARADE resultatartefakten.
    ///
    /// ⚠️ TJÄNSTEN RANKAR INTE OM NÅGOT. Medaljörerna läses ur
    /// <see cref="PrecisionFinalResults.MedalAwards"/>, som fylls av CalculateFinalResults där
    /// mästerskapskategorin, finalistfiltret, innertiorna och särskjutningens utfall alla finns
    /// i hand samtidigt. Att räkna om medaljordningen här skulle kräva att alla fyra
    /// återskapades, och just den förväxlingen (mästerskapskategori mot skicklighetsklass) har
    /// uppstått tre gånger i den här kodbasen — i finalgallringen, i särskjutningen och i
    /// medaljräkningen.
    ///
    /// Det ENDA som ordnas här är hederspriskandidaterna, och det är medvetet: den ordningen
    /// följer en annan regel (alla deltagare, inte bara finalisterna) och fördelningen är
    /// arrangörens beslut som ska kunna ändras utan att resultatlistan räknas om.
    /// </summary>
    public class PrizeGivingService
    {
        /// <summary>Egenskapen på tävlingen som säger om hederspriser utgår (SHB C.3.4.2).</summary>
        public const string HonoraryEnabledProperty = "isAwardingHonoraryAward";

        /// <summary>
        /// Egenskapen på <c>competitionResult</c> där arrangörens egen hedersprisfördelning
        /// bor — som JSON, kategorinamn → antal.
        ///
        /// ⚠️ EGEN EGENSKAP, skild från <c>resultData</c>. Fördelningen är ett manuellt beslut
        /// och måste överleva att resultatlistan räknas om. Exakt den lärdomen kostade oss
        /// <c>mergeConfig</c>: klassammanslagningen låg i samma skrivväg som omräkningen, så
        /// varje omräkning utan uttryckligt val nollade arrangörens sammanslagning.
        /// </summary>
        public const string HonoraryConfigProperty = "honoraryAwardConfig";

        private readonly IContentService _contentService;
        private readonly CompetitionTeamService _teamService;
        private readonly ShootOffService _shootOffService;
        private readonly FaltskytteShootOffService _faltShootOffService;
        private readonly ILogger<PrizeGivingService> _logger;

        public PrizeGivingService(
            IContentService contentService,
            CompetitionTeamService teamService,
            ShootOffService shootOffService,
            FaltskytteShootOffService faltShootOffService,
            ILogger<PrizeGivingService> logger)
        {
            _contentService = contentService;
            _teamService = teamService;
            _shootOffService = shootOffService;
            _faltShootOffService = faltShootOffService;
            _logger = logger;
        }

        /// <summary>Resultatnoden för en tävling, eller null när ingen resultatlista finns.</summary>
        public IContent? GetResultNode(int competitionId) =>
            _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

        public async Task<PrizeGivingModel?> BuildAsync(int competitionId, string? weaponGroup, bool canEdit)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return null;

            var scope = competition.GetValue<string>("competitionScope") ?? "";
            var competitionType = competition.GetValue<string>("competitionType") ?? "Precision";
            var model = new PrizeGivingModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                CompetitionType = competitionType,
                IsChampionship = ChampionshipCategory.IsChampionship(scope),
                SelectedWeaponGroup = (weaponGroup ?? "").Trim(),
                CanEdit = canEdit
            };
            // Grenens enheter som de ser ut NU. Lagkorten räknas live och ska ha de här; för
            // individens tal är de bara en reserv — artefakten bär sina egna, se nedan.
            var liveVariant = ApplyDisciplineLabels(model, competition);

            var resultNode = GetResultNode(competitionId);
            model.HasResultList = resultNode != null;

            var artifactJson = resultNode?.GetValue<string>("resultData");
            PrecisionFinalResults? artifact = null;
            if (!string.IsNullOrWhiteSpace(artifactJson))
            {
                try
                {
                    artifact = JsonConvert.DeserializeObject<PrecisionFinalResults>(artifactJson);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kunde inte läsa resultatartefakten för tävling {CompetitionId}", competitionId);
                }
            }

            if (artifact == null)
            {
                model.Warnings.Add("Det finns ingen resultatlista att dela ut priser efter. "
                    + "Skapa och uppdatera resultatlistan på fliken Resultat först.");
                return model;
            }

            model.ResultsUpdatedAt = artifact.UpdatedAt;
            model.MedalsComputed = artifact.MedalAwardsComputed;

            // ⚠️ TALEN OCH ENHETEN KOMMER UR SAMMA KÄLLA. Artefakten bär sedan 2026-09-13 de
            // enheter den räknades i, och de vinner över tävlingens nuvarande konfiguration:
            // siffran i kortet är artefaktens, alltså måste etiketten också vara det. En
            // artefakt skriven före det fältet säger ingenting, och då gäller konfigurationen
            // som förr.
            if (!string.IsNullOrWhiteSpace(artifact.ScoreUnit)) model.ScoreUnit = artifact.ScoreUnit!;
            if (!string.IsNullOrWhiteSpace(artifact.SecondaryUnit)) model.SecondaryUnit = artifact.SecondaryUnit!;

            // ⚠️ OCH NÄR DE SKILJER SIG ÄR LISTAN INAKTUELL. Byter en fälttävling från
            // normalfält till poängfält ändras varje tal i resultatlistan (poäng = träff +
            // figurer), men artefakten ser lika färsk ut som förut. Det är exakt det läge som
            // visade "46 p / 19 pmål" i prislistan där resultatlistan visade "65 p / 22 pm":
            // talen var träff och figurer, räknade före bytet.
            if (liveVariant != null
                && !string.IsNullOrWhiteSpace(artifact.ScoringVariant)
                && !string.Equals(artifact.ScoringVariant, liveVariant, StringComparison.OrdinalIgnoreCase))
            {
                var wasPoints = string.Equals(artifact.ScoringVariant, "Poang", StringComparison.OrdinalIgnoreCase);
                model.Warnings.Insert(0,
                    $"Resultatlistan räknades som {(wasPoints ? "poängfält" : "normalfält")} "
                    + $"({artifact.ScoreUnit}/{artifact.SecondaryUnit}), men tävlingen räknas nu som "
                    + $"{(wasPoints ? "normalfält" : "poängfält")}. Talen nedan är alltså inte de som gäller — "
                    + "klicka Uppdatera på fliken Resultat och ladda om sidan.");
            }

            // ── Individuella medaljer ────────────────────────────────────────────────
            var allAwards = artifact.MedalAwards ?? new List<PrecisionMedalCategoryAwards>();

            // Vapengrupperna kommer ur medaljkategorierna när de finns, annars ur klasserna —
            // annars blir bordsväljaren tom på en tävling vars artefakt är för gammal.
            model.AvailableWeaponGroups = allAwards.Count > 0
                ? allAwards.Select(a => a.WeaponGroup)
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.CurrentCulture)
                    .ToList()
                : (artifact.ClassGroups ?? new List<PrecisionClassGroup>())
                    .SelectMany(cg => cg.Shooters)
                    .Select(sh => ChampionshipCategory.WeaponGroupFor(sh.ShootingClass))
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.CurrentCulture)
                    .ToList();

            model.Individual = FilterByGroup(allAwards, a => a.WeaponGroup, model.SelectedWeaponGroup);

            if (model.IsChampionship && !artifact.MedalAwardsComputed)
            {
                // Skiljer "inga medaljer" från "vet inte". En tom ceremoni för ett mästerskap är
                // värre än ett ärligt "räkna om listan först".
                model.Warnings.Add("Resultatlistan räknades ut innan medaljfunktionen fanns, "
                    + "så medaljörerna är inte beräknade. Klicka Uppdatera på fliken Resultat.");
            }
            else if (!model.IsChampionship)
            {
                model.Warnings.Add("Tävlingen är inte ett mästerskap, så inga placeringsmedaljer "
                    + "delas ut. Hederspriser och lagpriser visas ändå om de utgår.");
            }

            // ── Lagmedaljer ──────────────────────────────────────────────────────────
            model.Teams = FilterByGroup(
                await BuildTeamGroupsAsync(competition, model.IsChampionship),
                t => t.WeaponGroup,
                model.SelectedWeaponGroup);

            // ── Hederspriser ─────────────────────────────────────────────────────────
            model.Honorary = BuildHonorary(competition, resultNode, artifact, scope,
                model.SelectedWeaponGroup, model.ScoreUnit);

            // ── Varningar som gör ceremonin osäker ───────────────────────────────────
            await AddIntegrityWarningsAsync(model, competition, resultNode, artifact);

            return model;
        }

        /// <summary>
        /// Grenens egna ord för resultatet, och om den har lagtävling.
        ///
        /// ⚠️ NORMALFÄLT RÄKNAR TRÄFF OCH FIGUR, poängfält och magnumfält räknar poäng och
        /// poängmål. Samma delning som <c>FaltskytteResultArtifactService.FaltskytteScoreReader</c>
        /// gör när talen SKRIVS — håll dem i takt, annars visar sidan en annan enhet än den
        /// siffran räknades i. Scoringmode läses ur konfigurationen först: egenskapen på
        /// tävlingen är en spegel som kan vara inaktuell.
        /// </summary>
        /// <returns>
        /// "Poang" eller "Normal" för fältskyttefamiljen — räknesättet som gäller NU — och null
        /// för grenar som bara har ett. Anroparen jämför det mot artefaktens egna enheter.
        /// </returns>
        private static string? ApplyDisciplineLabels(PrizeGivingModel model, IContent competition)
        {
            var isFalt = model.CompetitionType is "Faltskytte" or "MagnumFalt";
            if (!isFalt) return null;

            var config = FaltskytteConfigParser.Parse(competition.GetValue<string>("stationConfig"));
            var scoringMode = FaltskytteScoringMode.Resolve(config, competition.GetValue<string>("scoringMode"));
            var usesPoints = model.CompetitionType == "MagnumFalt"
                          || string.Equals(scoringMode, "Poang", StringComparison.OrdinalIgnoreCase);

            model.ScoreUnit = usesPoints ? "p" : "träff";
            // ⚠️ LAGKORTEN RÄKNAS LIVE, inte ur artefakten (CalculateTeamResultsAsync), så de
            // bär alltid tävlingens NUVARANDE enhet. Individens tal kan komma ur en artefakt
            // som räknades i den andra — då ska korten säga olika, för de ÄR olika.
            model.TeamScoreUnit = model.ScoreUnit;
            // ⚠️ Lagets andrahandstal är FIGURER i båda varianterna — SHB D.6.11.2.2 sätter
            // "största sammanlagda antalet träffade figurer" först i kedjan även i poängfält,
            // där individen i stället särskiljs på poängmål. Lag och individ har alltså inte
            // samma andra tal, och det är inte ett förbiseende.
            model.SecondaryUnit = usesPoints ? "pmål" : "fig";
            model.TeamSecondaryUnit = "fig";

            return usesPoints ? FaltskytteScoringMode.Poang : FaltskytteScoringMode.Normal;
        }

        /// <summary>
        /// Filtrerar på vapengrupp. En rad utan känd vapengrupp visas ALLTID — hellre en rad för
        /// mycket vid ett bord än en medalj som ingen ser.
        /// </summary>
        private static List<T> FilterByGroup<T>(List<T> items, Func<T, string> group, string selected)
        {
            if (string.IsNullOrWhiteSpace(selected)) return items;
            return items
                .Where(i => string.IsNullOrWhiteSpace(group(i))
                            || string.Equals(group(i), selected, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private async Task<List<PrizeTeamGroup>> BuildTeamGroupsAsync(IContent competition, bool isChampionship)
        {
            var groups = new List<PrizeTeamGroup>();
            if (!isChampionship) return groups;

            var competitionType = competition.GetValue<string>("competitionType") ?? "Precision";
            var numberOfSeries = competition.GetValue<int>("numberOfSeriesOrStations");

            var teamGroups = await _teamService.CalculateTeamResultsAsync(competition.Id, competitionType, numberOfSeries);

            foreach (var tg in teamGroups)
            {
                // "Startande lag" och "anmälda lag" är inte samma sak, och skillnaden ändrar
                // antalet medaljer. Ett lag utan en enda inmatad serie har inte startat.
                var starting = tg.Teams.Count(t => t.MemberResults.Any(m => m.HasResult));
                var isJun = tg.TeamClass.Contains("Jun", StringComparison.OrdinalIgnoreCase);
                var (medalCount, medalText) = ChampionshipMedalCount.For(starting, isJun, "lag");

                var group = new PrizeTeamGroup
                {
                    TeamClass = tg.TeamClass,
                    WeaponGroup = WeaponGroupOfTeamClass(tg.TeamClass),
                    StartingTeams = starting,
                    MedalCount = medalCount,
                    MedalCountText = medalText,
                    Reduced = medalCount < 3,
                    IsRelay = tg.Teams.Any(t => t.IsRelay)
                };

                var ranked = tg.Teams.Where(t => t.Rank > 0).OrderBy(t => t.Rank).ToList();
                for (int place = 1; place <= medalCount && place <= ranked.Count; place++)
                {
                    var team = ranked[place - 1];

                    // Lagsärskjutning är inte modellerad, så ett oskiljbart lagpar kan inte
                    // avgöras av systemet. Det ska sägas, inte gissas: C.4.3.1.11 pekar särskilt
                    // ut lagtävlingar som det som måste kontrolleras före prisutdelningen.
                    //
                    // ⚠️ FRÅGAN STÄLLS TILL GRENENS EGEN KEDJA, inte till poäng + X. Fältskyttets
                    // kedja är fyra led djup (SHB D.6.11.2.2: figurer, poängmål, sista stationen
                    // och bakåt), så ett tvåledat test hade flaggat lag som regelverket skiljer
                    // åt — och en påstådd oavgjord medaljplats stoppar en ceremoni lika säkert
                    // som en verklig. Nyckeln byggs där resultatet räknas; se
                    // <see cref="TeamResult.TiebreakKey"/>.
                    var tied = ranked
                        .Where(t => t.TeamId != team.TeamId
                                    && TiebreakKeyComparer.Unseparated(t, team))
                        .ToList();
                    if (tied.Count > 0)
                    {
                        var names = string.Join(" och ", new[] { team.TeamName }.Concat(tied.Select(t => t.TeamName)));
                        var line = $"{ChampionshipMedalCount.MedalNameForPlace(place)} — lagen står lika: {names}. "
                                 + "Ordningen måste avgöras av arrangören.";
                        if (!group.Unresolved.Contains(line)) group.Unresolved.Add(line);
                        continue;
                    }

                    group.Awards.Add(new PrizeTeamAward
                    {
                        Place = place,
                        Medal = ChampionshipMedalCount.MedalNameForPlace(place),
                        TeamName = team.TeamName,
                        ClubName = team.ClubName,
                        TotalScore = team.TotalScore,
                        XCount = team.TotalXCount,
                        Members = team.MemberResults
                            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                            .Select(m => m.Name)
                            .ToList()
                    });
                }

                if (group.Awards.Count > 0 || group.Unresolved.Count > 0) groups.Add(group);
            }

            return groups
                .OrderBy(g => g.WeaponGroup, StringComparer.CurrentCulture)
                .ThenBy(g => g.TeamClass, StringComparer.CurrentCulture)
                .ToList();
        }

        /// <summary>
        /// Vapengruppen för en lagklass. Lagklasserna heter "A", "B", "C Öppen", "C Dam",
        /// "C Vet", "C Jun", "R", "M", "A Opt" — vapengruppen är alltid första bokstaven.
        /// </summary>
        private static string WeaponGroupOfTeamClass(string teamClass)
        {
            var t = (teamClass ?? "").Trim();
            if (t.Length == 0) return "";
            var first = char.ToUpperInvariant(t[0]);
            return first is 'A' or 'B' or 'C' or 'R' or 'M' or 'L' ? first.ToString() : "";
        }

        private PrizeHonorarySection BuildHonorary(
            IContent competition,
            IContent? resultNode,
            PrecisionFinalResults artifact,
            string scope,
            string selectedWeaponGroup,
            string scoreUnit)
        {
            var section = new PrizeHonorarySection
            {
                PropertyMissing = !competition.HasProperty(HonoraryEnabledProperty)
            };

            // Saknad egenskap läses som "utgår inte", men sidan säger varför i stället för att tiga.
            section.Enabled = !section.PropertyMissing && competition.GetValue<bool>(HonoraryEnabledProperty);
            if (!section.Enabled) return section;

            var classGroups = artifact.ClassGroups ?? new List<PrecisionClassGroup>();
            var allShooters = classGroups.SelectMany(cg => cg.Shooters).ToList();

            section.ParticipantCount = artifact.ParticipantCount > 0
                ? artifact.ParticipantCount
                : allShooters.Select(s => s.MemberId).Distinct().Count();
            section.Minimum = ChampionshipMedalCount.MinimumHonoraryAwards(section.ParticipantCount);

            var stored = ReadHonoraryConfig(resultNode);
            section.IsCustomised = stored.Count > 0;

            var splitC = ChampionshipCategory.SplitsGroupC(scope);

            // ⚠️ ORDNINGEN HÄR ÄR INTE MEDALJORDNINGEN. Hederspriser tillfaller deltagarna, inte
            // finalisterna, så mängden är en annan än medaljens — och därför får den här
            // ordningen aldrig användas för att peka ut en medaljör. Medaljörerna kommer
            // uteslutande ur artefaktens MedalAwards.
            foreach (var cat in allShooters
                         .GroupBy(s => ChampionshipCategory.For(s.ShootingClass, splitC))
                         .Where(g => !string.IsNullOrWhiteSpace(g.Key))
                         .OrderBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var group = ChampionshipCategory.WeaponGroupFor(cat.First().ShootingClass);
                if (!string.IsNullOrWhiteSpace(selectedWeaponGroup)
                    && !string.IsNullOrWhiteSpace(group)
                    && !string.Equals(group, selectedWeaponGroup, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ordered = cat
                    .OrderByDescending(s => s.TotalScore)
                    .ThenByDescending(s => s.TotalXCount)
                    .ThenBy(s => s.Name, StringComparer.CurrentCulture)
                    .ToList();

                var participants = ordered.Select(s => s.MemberId).Distinct().Count();
                var proposed = ChampionshipMedalCount.MinimumHonoraryAwards(participants);
                var count = stored.TryGetValue(cat.Key, out var manual) ? Math.Max(0, manual) : proposed;

                var category = new PrizeHonoraryCategory
                {
                    CategoryName = cat.Key,
                    WeaponGroup = group,
                    Participants = participants,
                    ProposedCount = proposed,
                    Count = Math.Min(count, ordered.Count)
                };

                for (int i = 0; i < ordered.Count; i++)
                {
                    var s = ordered[i];
                    category.Candidates.Add(new PrizeHonoraryCandidate
                    {
                        Order = i + 1,
                        MemberId = s.MemberId,
                        Name = s.Name,
                        Club = s.Club,
                        ShootingClass = s.ShootingClass,
                        TotalScore = s.TotalScore,
                        XCount = s.TotalXCount
                    });
                }

                MarkBoundaryTie(category, scoreUnit);
                section.Categories.Add(category);
            }

            return section;
        }

        /// <summary>
        /// Flaggar oskiljbara skyttar PRECIS VID GRÄNSEN för sista hederspriset.
        ///
        /// SHB D.6.11.2 avgör ordningen med särskjutning för dem som deltar i en sådan, och
        /// annars med lottning. Det spelar bara roll där gränsen går: två som står lika mitt i
        /// listan får båda ett pris, och att be funktionären lotta där vore påhittat arbete.
        /// </summary>
        private static void MarkBoundaryTie(PrizeHonoraryCategory category, string scoreUnit)
        {
            if (category.Count <= 0 || category.Count >= category.Candidates.Count) return;

            var last = category.Candidates[category.Count - 1];
            var tied = category.Candidates
                .Where(c => c.TotalScore == last.TotalScore && c.XCount == last.XCount)
                .ToList();
            if (tied.Count < 2) return;

            // Sträcker sig gruppen över gränsen? Annars är den helt inom eller helt utanför.
            var spansBoundary = tied.Any(c => c.Order <= category.Count)
                                && tied.Any(c => c.Order > category.Count);
            if (!spansBoundary) return;

            var names = string.Join(", ", tied.Select(c => c.Name));
            // ⚠️ Enheten är GRENENS, inte "p". Ett normalfältsresultat på 32 träff skrivet
            // "32 p" läses som ett annat resultat — samma fälla som medaljkortens etiketter.
            var note = $"Står lika på {last.TotalScore} {scoreUnit} ({tied.Count} skyttar: {names}) och delar den sista "
                     + "hederspriseplatsen. Ordningen avgörs av särskjutning för dem som deltagit i en sådan, "
                     + "annars genom lottning (SHB D.6.11.2).";
            foreach (var c in tied) c.TieNote = note;
        }

        /// <summary>Arrangörens sparade fördelning, kategorinamn → antal. Tom när inget är sparat.</summary>
        public Dictionary<string, int> ReadHonoraryConfig(IContent? resultNode)
        {
            var empty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (resultNode == null || !resultNode.HasProperty(HonoraryConfigProperty)) return empty;

            var json = resultNode.GetValue<string>(HonoraryConfigProperty);
            if (string.IsNullOrWhiteSpace(json)) return empty;
            try
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, int>>(json);
                return parsed == null
                    ? empty
                    : new Dictionary<string, int>(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa hedersprisfördelningen på nod {NodeId}", resultNode.Id);
                return empty;
            }
        }

        /// <summary>True när fördelningen kan sparas — annars saknas doctype-egenskapen.</summary>
        public bool CanStoreHonoraryConfig(IContent? resultNode) =>
            resultNode != null && resultNode.HasProperty(HonoraryConfigProperty);

        private async Task AddIntegrityWarningsAsync(
            PrizeGivingModel model,
            IContent competition,
            IContent? resultNode,
            PrecisionFinalResults artifact)
        {
            // ⚠️ ÄR ARTEFAKTEN ÄLDRE ÄN SÄRSKJUTNINGEN? Då beskriver medaljlistan ett
            // annat utfall än det som faktiskt skjutits, och det syns inte på något annat
            // sätt — en oavgjord medaljplats ser likadan ut oavsett om striden är oavgjord
            // eller om artefakten bara inte hunnit med. Rapporterat 2026-09-08: artefakt
            // skriven 13:10, det avgörande resultatet inmatat 14:06.
            //
            // Orsaken är åtgärdad (SaveShootOffEntry räknar om artefakten), men vakten
            // står kvar: den täcker artefakter sparade före den fixen, och den gör en
            // misslyckad omräkning synlig i stället för tyst.
            //
            // Medvetet SMAL — bara särskjutningen, inte resultatrader i allmänhet. En
            // allmän "resultaten är nyare än listan" skulle larma oavbrutet medan
            // resultaten matas in, och en varning som alltid lyser slutar betyda något.
            try
            {
                // ⚠️ FÄLTSKYTTE HAR EN EGEN SÄRSKJUTNINGSTABELL. Läses bara precisionsfamiljens
                // svarar den tomt för en fälttävling, och vakten tiger just på den gren där den
                // först behövdes.
                var newest = model.CompetitionType is "Faltskytte" or "MagnumFalt"
                    ? (await _faltShootOffService.GetEntriesForCompetitionAsync(model.CompetitionId))
                        .Select(e => (DateTime?)e.LastModified).Max()
                    : (await _shootOffService.GetEntriesForCompetitionAsync(model.CompetitionId))
                        .Select(e => (DateTime?)e.LastModified).Max();

                if (newest.HasValue)
                {
                    if (newest.Value > artifact.UpdatedAt)
                    {
                        model.Warnings.Insert(0,
                            $"Särskjutningsresultat matades in {newest:d MMM HH:mm}, efter att den här "
                            + $"listan räknades ut ({artifact.UpdatedAt:d MMM HH:mm}). "
                            + "Medaljerna nedan kan vara felaktiga — klicka Uppdatera på fliken Resultat "
                            + "och ladda om den här sidan.");
                    }
                }
            }
            catch (Exception ex)
            {
                // En vakt som inte kan läsas får inte ta ner ceremonin. Men den får inte
                // heller tiga: kan vi inte avgöra om listan är aktuell ska det sägas.
                _logger.LogWarning(ex, "Kunde inte jämföra särskjutningen mot resultatlistan för tävling {CompetitionId}",
                    model.CompetitionId);
                model.Warnings.Add("Kunde inte kontrollera om listan är räknad efter de senaste "
                    + "särskjutningsresultaten. Klicka Uppdatera på fliken Resultat om en särskjutning nyligen matats in.");
            }

            // C.4.3.1.11: "Tävlingsresultaten skall före prisutdelningen vara noga kontrollerade,
            // inte minst i lagtävlingar." En opublicerad lista är per definition inte den
            // kontrollerade listan.
            if (resultNode != null && !resultNode.GetValue<bool>("isOfficial"))
            {
                model.Warnings.Add("Resultatlistan är inte publicerad som officiell. "
                    + "Kontrollera och publicera den innan prisutdelningen.");
            }

            var numberOfSeries = competition.GetValue<int>("numberOfSeriesOrStations");
            var numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries");
            var qualificationSeries = numberOfFinalSeries > 0
                ? numberOfSeries - numberOfFinalSeries
                : numberOfSeries;

            if (qualificationSeries > 0)
            {
                // Bara grundomgången kontrolleras. Vilka som var finalister vet den här sidan
                // inte, och en skytt som gallrats bort ska inte flaggas för serier hen aldrig
                // var uttagen att skjuta.
                var incomplete = (artifact.ClassGroups ?? new List<PrecisionClassGroup>())
                    .SelectMany(cg => cg.Shooters)
                    .Count(s => s.ParticipationStatus == null
                                && s.Results.Count(r => r.SeriesNumber <= qualificationSeries) < qualificationSeries);
                if (incomplete > 0)
                {
                    model.Warnings.Add($"{incomplete} "
                        + (incomplete == 1 ? "skytt saknar" : "skyttar saknar")
                        + $" serier i grundomgången ({qualificationSeries} serier). "
                        + "Kontrollera resultaten innan medaljerna delas ut.");
                }
            }

            if (model.TotalUnresolved > 0)
            {
                model.Warnings.Add($"{model.TotalUnresolved} medaljplats"
                    + (model.TotalUnresolved == 1 ? "" : "er")
                    + " kan inte delas ut än — särskjutningen är inte avgjord. "
                    + "De är markerade i rött nedan.");
            }
        }
    }
}
