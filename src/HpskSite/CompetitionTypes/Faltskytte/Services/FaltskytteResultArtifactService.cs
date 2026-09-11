using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Skriver fältskyttets resultatartefakt (<c>resultData</c>) — den form prisutdelningen läser.
    ///
    /// ⚠️ DEN HÄR KLASSEN FINNS FÖR ATT DEN UPPENBARA FIXEN ÄR FEL. Prisutdelningen läser
    /// <see cref="PrecisionFinalResults"/>, och den frestande vägen är att lägga till en
    /// <c>case "Faltskytte"</c> i precisionsfamiljens <c>FetchFamilyResultsAsync</c>. Det går
    /// inte: den metoden returnerar <c>List&lt;PrecisionResultEntry&gt;</c> och CASTAR, medan
    /// <see cref="FaltskytteResultEntry"/> är en egen radform. Och att peka fältskytte mot de
    /// delade endpointarna vore värre än så — se
    /// <see cref="CompetitionResultTables.ForSharedResultEndpoint"/>, som KASTAR för
    /// Faltskytte/MagnumFalt just för att deras DELETE och klassbytets UPDATE annars hade
    /// börjat adressera riktiga fältskytterader med ett klass-skopat WHERE.
    ///
    /// Fältskytte äger sin egen resultatcontroller, sin egen tabell och sin egen radform. Den
    /// här tjänsten är översättningen ut till artefaktens form, och ingenting annat.
    ///
    /// ⚠️ OCH DEN HÄR ÄR FÄLTSKYTTETS ENDA SKRIVVÄG TILL <c>resultData</c>. Allt som ändrar
    /// utfallet måste anropa den — särskjutningens endpoints gjorde det inte i
    /// precisionsfamiljen, och följden var att prisutdelningssidan påstod "särskjutning krävs"
    /// om en strid som just avgjorts (mätt på SSM 2026: artefakt 13:10, avgörande resultat 14:06).
    /// </summary>
    public class FaltskytteResultArtifactService
    {
        private readonly IContentService _contentService;
        private readonly FaltskytteResultsBuilder _builder;
        private readonly ILogger<FaltskytteResultArtifactService> _logger;

        public FaltskytteResultArtifactService(
            IContentService contentService,
            FaltskytteResultsBuilder builder,
            ILogger<FaltskytteResultArtifactService> logger)
        {
            _contentService = contentService;
            _builder = builder;
            _logger = logger;
        }

        /// <summary>
        /// Räknar om och sparar artefakten. Best-effort: en misslyckad omräkning får aldrig
        /// rapportera den lyckade skrivning som utlöste den som misslyckad — anroparen berättar
        /// i stället att listan behöver uppdateras för hand.
        /// </summary>
        public async Task<bool> RefreshAsync(int competitionId, string reason)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return false;

                var resultPage = FindResultNode(competition.Id);
                if (resultPage == null)
                {
                    // Ingen resultatnod = tävlingen har aldrig publicerat en lista. Att skapa en
                    // här vore att publicera något arrangören inte bett om; PublishResults äger
                    // skapandet och anropar oss efteråt.
                    return false;
                }

                var build = await _builder.BuildAsync(competitionId, null, subCompetitionOnly: false);
                if (build.Results == null)
                {
                    // ⚠️ TOM RESULTATMÄNGD SKRIVER INGEN ARTEFAKT. Det är motsatt regel mot
                    // precisionsfamiljens RefreshResultArtifactAsync, och skillnaden är avsiktlig:
                    // där är tomt ett giltigt läge efter att sista raden tagits bort, medan
                    // fältskyttet hit bara kommer från händelser som förutsätter resultat. En
                    // artefakt som påstår att tävlingen saknar resultat är sämre än ingen alls.
                    _logger.LogInformation(
                        "Hoppade över artefaktomräkning för fältskyttetävling {CompetitionId} ({Reason}): {Message}",
                        competitionId, reason, build.Error ?? "inga resultat");
                    return false;
                }

                var artifact = BuildArtifact(competition, build);

                var existingIsOfficial = resultPage.GetValue<bool>("isOfficial");
                resultPage.SetValue("resultData", JsonConvert.SerializeObject(artifact));
                resultPage.SetValue("lastUpdated", DateTime.Now);
                resultPage.SetValue("isOfficial", existingIsOfficial);
                resultPage.SetValue("resultType", "Final Results");

                _contentService.Save(resultPage);
                _contentService.Publish(resultPage, new[] { "*" }, -1);

                _logger.LogInformation("Resultatartefakten omräknad för fältskyttetävling {CompetitionId} ({Reason})",
                    competitionId, reason);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte räkna om resultatartefakten för fältskyttetävling {CompetitionId} ({Reason})",
                    competitionId, reason);
                return false;
            }
        }

        private IContent? FindResultNode(int competitionId) =>
            _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");

        // ── Översättningen ───────────────────────────────────────────────────────

        internal static PrecisionFinalResults BuildArtifact(IContent competition, FaltskytteResultsBuildResult build)
        {
            var results = build.Results!;
            var competitionType = competition.GetValue<string>("competitionType") ?? "Faltskytte";
            var scope = competition.GetValue<string>("competitionScope") ?? "";
            var scoring = new FaltskytteScoreReader(competitionType, results.ScoringMode);

            var artifact = new PrecisionFinalResults
            {
                CompetitionId = results.CompetitionId,
                UpdatedAt = results.UpdatedAt,
                IsOfficial = results.IsOfficial,
                ClassGroups = results.ClassGroups.Select(cg => MapClassGroup(cg, scoring)).ToList(),
                ParticipantCount = results.ClassGroups
                    .SelectMany(cg => cg.Shooters)
                    .Select(s => s.MemberId)
                    .Distinct()
                    .Count()
            };

            if (!ChampionshipCategory.IsChampionship(scope)) return artifact;

            foreach (var standing in build.CategoryStandings)
            {
                var ties = results.MedalCategoryTies
                    .FirstOrDefault(t => string.Equals(t.CategoryName, standing.CategoryName, StringComparison.OrdinalIgnoreCase))
                    ?.Groups ?? new List<FaltskytteTiedMedalGroup>();

                artifact.MedalAwards.Add(BuildCategoryAwards(standing, ties, scoring));
            }

            artifact.MedalAwards = artifact.MedalAwards
                .OrderBy(a => a.WeaponGroup, StringComparer.CurrentCulture)
                .ThenBy(a => a.CategoryName, StringComparer.CurrentCulture)
                .ToList();

            // Sätts även när listan blev tom: "beräknat, inga medaljer" är ett annat svar än
            // "vet inte", och prisutdelningssidan måste kunna skilja dem åt.
            artifact.MedalAwardsComputed = true;
            return artifact;
        }

        private static PrecisionClassGroup MapClassGroup(FaltskytteClassGroup cg, FaltskytteScoreReader scoring) =>
            new()
            {
                ClassName = cg.ClassName,
                DisplayClassName = cg.DisplayClassName,
                ShootOffNotes = new List<string>(cg.ShootOffNotes),
                Shooters = cg.Shooters.Select(s => MapShooter(s, scoring)).ToList()
            };

        private static PrecisionShooterResult MapShooter(FaltskytteShooterResult s, FaltskytteScoreReader scoring) =>
            new()
            {
                MemberId = s.MemberId,
                Name = s.Name,
                Club = s.Club,
                ShootingClass = s.ShootingClass,
                StandardMedal = s.StandardMedal,
                ScoreOverride = scoring.Primary(s),
                XCountOverride = scoring.Secondary(s),

                // En tom serie PER STATION. Skotten kan inte återges — fältskyttets radform är
                // träff och figur — men ANTALET bär den enda fråga prisutdelningen ställer om
                // dem: har skytten skjutit alla stationer? Utan raderna kan sidan inte skilja en
                // fullföljd runda från en halv.
                Results = s.Stations
                    .Select(st => new PrecisionResultEntry { SeriesNumber = st.StationNumber, Shots = "[]" })
                    .ToList()
            };

        private static PrecisionMedalCategoryAwards BuildCategoryAwards(
            FaltskytteCategoryStanding standing,
            List<FaltskytteTiedMedalGroup> ties,
            FaltskytteScoreReader scoring)
        {
            var isJunior = standing.CategoryName.Contains("Jun", StringComparison.OrdinalIgnoreCase);
            var (medalCount, medalCountText) = ChampionshipMedalCount.For(standing.Participants, isJunior);

            var awards = new PrecisionMedalCategoryAwards
            {
                CategoryName = standing.CategoryName,
                WeaponGroup = standing.WeaponGroup,
                Participants = standing.Participants,
                MedalCount = medalCount,
                MedalCountText = medalCountText,
                Reduced = medalCount < 3
            };

            for (int place = 1; place <= medalCount && place <= standing.Ordered.Count; place++)
            {
                var shooter = standing.Ordered[place - 1];
                var tie = ties.FirstOrDefault(g => place >= g.FirstRank && place <= g.LastRank);

                // ⚠️ OAVGJORD MEDALJSTRID → INGEN MEDALJÖR UTSES. Att skriva ut den ordning
                // tiebreakern råkar ge just nu vore att namnge fel person i uppropet; SHB
                // C.4.3.1.11 kräver kontrollerade resultat före prisutdelningen.
                if (tie != null && !tie.Resolved)
                {
                    var line = UnresolvedLine(place, tie);
                    if (!awards.Unresolved.Contains(line)) awards.Unresolved.Add(line);
                    continue;
                }

                awards.Awards.Add(new PrecisionMedalAward
                {
                    Place = place,
                    Medal = ChampionshipMedalCount.MedalNameForPlace(place),
                    MemberId = shooter.MemberId,
                    Name = shooter.Name,
                    Club = shooter.Club,
                    ShootingClass = shooter.ShootingClass,
                    TotalScore = scoring.Primary(shooter),
                    XCount = scoring.Secondary(shooter),
                    DecidedBy = DecidedByText(shooter, tie)
                });
            }

            return awards;
        }

        private static string UnresolvedLine(int place, FaltskytteTiedMedalGroup tie)
        {
            var names = tie.Shooters
                .Select(x => x.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var between = names.Count switch
            {
                0 => "",
                1 => names[0],
                _ => string.Join(", ", names.Take(names.Count - 1)) + " och " + names.Last()
            };
            return between.Length > 0
                ? $"{ChampionshipMedalCount.MedalNameForPlace(place)} — särskjutning krävs mellan {between}"
                : $"{ChampionshipMedalCount.MedalNameForPlace(place)} — särskjutning krävs";
        }

        /// <summary>
        /// "avgjord på särskjutning: 5/4 mot 4/3". Skrivs ut dels för att det ofta nämns i
        /// uppropet, dels för att det visar att ordningen inte är gissad.
        /// </summary>
        private static string? DecidedByText(FaltskytteShooterResult shooter, FaltskytteTiedMedalGroup? tie)
        {
            if (tie == null || !tie.Resolved || tie.Shooters.Count < 2) return null;

            static string? LastRound(FaltskytteTiedMedalShooter x) =>
                x.Rounds == null || x.Rounds.Count == 0
                    ? null
                    : x.Rounds.OrderByDescending(r => r.Round).First().Display;

            bool Same(FaltskytteTiedMedalShooter x) =>
                x.MemberId == shooter.MemberId
                && string.Equals(x.ShootingClass, shooter.ShootingClass, StringComparison.OrdinalIgnoreCase);

            var mine = tie.Shooters.FirstOrDefault(Same);
            var mineDisplay = mine == null ? null : LastRound(mine);
            if (mineDisplay == null) return null;

            var others = tie.Shooters.Where(x => !Same(x)).Select(LastRound).Where(d => d != null).ToList();
            if (others.Count == 0) return null;

            return $"avgjord på särskjutning: {mineDisplay} mot {string.Join(" och ", others)}";
        }

        /// <summary>
        /// Vilka två tal en fältskytteskytt bär, och vad de heter.
        ///
        /// ⚠️ NORMALFÄLT RÄKNAR TRÄFF, POÄNGFÄLT OCH MAGNUMFÄLT RÄKNAR POÄNG — samma gren som
        /// <see cref="FaltskytteShootOffService.DetectTiedMedalGroups"/> använder för att avgöra
        /// vad "lika" betyder. Glider de isär skulle prisutdelningen visa ett annat tal än det
        /// striden avgjordes på.
        /// </summary>
        internal sealed class FaltskytteScoreReader
        {
            private readonly bool _usesPoints;

            public FaltskytteScoreReader(string? competitionType, string? scoringMode)
            {
                _usesPoints = string.Equals(competitionType, "MagnumFalt", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(scoringMode, "Poang", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>Träff (normalfält) eller poäng (poängfält/magnum).</summary>
            public int Primary(FaltskytteShooterResult s) => _usesPoints ? s.TotalPoints : s.TotalHits;

            /// <summary>Figurer (normalfält) eller poängmålssumman (poängfält/magnum).</summary>
            public int Secondary(FaltskytteShooterResult s) => _usesPoints ? s.TotalTiebreakerScore : s.TotalFigures;

            /// <summary>Enheten för <see cref="Primary"/>, som den skrivs ut intill talet.</summary>
            public string PrimaryUnit => _usesPoints ? "p" : "träff";

            /// <summary>Enheten för <see cref="Secondary"/>.</summary>
            public string SecondaryUnit => _usesPoints ? "pmål" : "fig";
        }
    }
}
