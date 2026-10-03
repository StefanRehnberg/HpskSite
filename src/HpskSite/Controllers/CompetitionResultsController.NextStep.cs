using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using PrecisionResultEntry = HpskSite.CompetitionTypes.Precision.Models.PrecisionResultEntry;

namespace HpskSite.Controllers
{
    /// <summary>
    /// "Nästa steg"-kortet överst på tävlingssidan (precisionsfamiljen). Endpointen samlar ihop
    /// talen; <see cref="CompetitionNextStep"/> avgör fasen.
    ///
    /// ⚠️ Läser bara det som redan lagras — anmälningar, startlistornas publiceringsflaggor,
    /// inmatade serier, finalstartlistorna per vapengrupp, DNS/DNF och resultatlistans
    /// <c>isOfficial</c>. Ingen ny tabell, och ingen yta skriver något här.
    ///
    /// ⚠️ Ett fel får ALDRIG bli ett tomt svar (lärdomen från KM 3468, där finalkortet visade
    /// "inga resultat" på ett serverfel). Kortet visar då ett eget felläge.
    /// </summary>
    public partial class CompetitionResultsController
    {
        [HttpGet]
        public async Task<IActionResult> GetNextStep(int competitionId)
        {
            if (competitionId <= 0) return Json(new { success = false, message = "Ogiltig tävling." });
            if (!await CanManageCompetitionResults(competitionId))
                return Json(new { success = false, denied = true, message = "Du har inte behörighet att se tävlingens läge." });

            try
            {
                return Json(await BuildNextStepAsync(competitionId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetNextStep failed for competition {CompetitionId}", competitionId);
                return Json(new { success = false, failed = true, message = "Tävlingens läge kunde inte läsas." });
            }
        }

        private sealed record NsStart(int MemberId, string ClassKey, string Group, int TeamNumber, string RawClass = "");

        private async Task<object> BuildNextStepAsync(int competitionId)
        {
            var comp = _contentService.GetById(competitionId)
                       ?? throw new InvalidOperationException("Tävlingen finns inte.");
            var typeId = GetCompetitionTypeId(competitionId);
            if (!CompetitionSurfaces.HasFunktionarerHub(typeId) || typeId.Contains("Spring", StringComparison.OrdinalIgnoreCase))
                return new { success = true, supported = false };

            int numSeries = SafeInt(comp, "numberOfSeriesOrStations");
            int finalSeries = CompetitionFinals.Effective(typeId, SafeInt(comp, "numberOfFinalSeries"));
            int qual = PrecisionFamilyResultReader.QualifyingSeriesCount(numSeries, finalSeries);

            // ── Anmälningar ────────────────────────────────────────────────────────────
            // ⚠️ Anmälningsnoderna är OPUBLICERADE (Save, aldrig Publish) — räknas i SQL.
            int registrations;
            using (var db = _umbracoDatabaseFactory.CreateDatabase())
            {
                registrations = db.ExecuteScalar<int>(@"
                    SELECT COUNT(*) FROM umbracoNode reg
                    JOIN umbracoContent c ON c.nodeId = reg.id
                    JOIN umbracoNode hub ON hub.id = reg.parentId
                    WHERE hub.parentId = @0 AND reg.trashed = 0
                      AND c.contentTypeId IN (SELECT nodeId FROM cmsContentType WHERE alias = 'competitionRegistration')",
                    competitionId);
            }
            DateTime? closes = null;
            try
            {
                var raw = comp.GetValue<DateTime?>("registrationCloseDate");
                if (raw.HasValue && raw.Value > DateTime.MinValue.AddYears(1)) closes = raw;
            }
            catch { /* ett oläsbart datum ska inte fälla kortet */ }

            // ── Startlistorna ──────────────────────────────────────────────────────────
            var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out _).ToList();
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
            if (hub != null) children.AddRange(_contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _));

            var qualNodes = children.Where(c => c.ContentType.Alias is "precisionStartList" or "PrecisionStartList").ToList();
            bool hasStartList = qualNodes.Count > 0;
            bool startListPublished = qualNodes.Any(n => SafeBool(n, "isOfficialStartList"));
            var qualCfg = LoadOfficialStartListConfig(competitionId);

            var qualStarts = (qualCfg?.Teams ?? new List<StartListTeam>())
                .SelectMany(t => (t.Shooters ?? new List<StartListShooter>())
                    .Where(s => s.MemberId > 0)
                    .Select(s => new NsStart(s.MemberId, ShootingClasses.NormalizeKey(s.WeaponClass),
                        ChampionshipCategory.WeaponGroupFor(s.WeaponClass), t.TeamNumber, s.WeaponClass ?? "")))
                .GroupBy(s => s.MemberId + "|" + s.ClassKey).Select(g => g.First())
                .ToList();
            int teamCount = qualCfg?.Teams?.Count(t => (t.Shooters?.Count ?? 0) > 0) ?? 0;
            string firstStartTime = qualCfg?.Teams?.OrderBy(t => t.TeamNumber).Select(t => t.StartTime)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

            // ── Resultat och DNS/DNF ──────────────────────────────────────────────────
            var rows = hasStartList ? await GetCompetitionResultsInternal(competitionId) : new List<PrecisionResultEntry>();
            var entered = new HashSet<string>(rows.Select(r =>
                r.MemberId + "|" + ShootingClasses.NormalizeKey(r.ShootingClass) + "|" + r.SeriesNumber));
            var statuses = new Dictionary<string, CompetitionParticipantStatus>();
            try
            {
                foreach (var s in await _participantStatusService.GetForCompetitionAsync(competitionId))
                    statuses[s.MemberId + "|" + ShootingClasses.NormalizeKey(s.ShootingClass)] = s;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "NextStep: DNS/DNF kunde inte läsas för {Id}", competitionId); }

            // Sista serien skytten ska skjuta: DNS = inga, DNF = fram till serien före.
            int LastDue(int memberId, string classKey, int upTo)
            {
                if (!statuses.TryGetValue(memberId + "|" + classKey, out var st)) return upTo;
                if (st.Status == CompetitionParticipantStatus.Dns) return 0;
                var from = st.FromSeriesNumber ?? 1;
                return Math.Min(upTo, from - 1);
            }
            bool Has(NsStart s, int series) => entered.Contains(s.MemberId + "|" + s.ClassKey + "|" + series);

            // ── Finalstartlistorna, en per vapengrupp ──────────────────────────────────
            var finalsLists = children.Where(c => c.ContentType.Alias == "finalsStartList").Select(n =>
            {
                var json = SafeString(n, "configurationData");
                StartListConfiguration? cfg = null;
                try { cfg = string.IsNullOrWhiteSpace(json) ? null : JsonConvert.DeserializeObject<StartListConfiguration>(json); }
                catch { /* en trasig lista räknas som saknad i stället för att fälla kortet */ }
                return new
                {
                    Node = n,
                    Group = FinalsWeaponGroup.FromConfiguration(cfg),
                    Published = SafeBool(n, "isOfficialFinalsStartList"),
                    Starts = (cfg?.Teams ?? new List<StartListTeam>())
                        .SelectMany(t => (t.Shooters ?? new List<StartListShooter>())
                            .Where(s => s.MemberId > 0)
                            .Select(s => new NsStart(s.MemberId, ShootingClasses.NormalizeKey(s.WeaponClass),
                                ChampionshipCategory.WeaponGroupFor(s.WeaponClass), t.TeamNumber, s.WeaponClass ?? "")))
                        .ToList()
                };
            }).ToList();

            // Mästerskapsklassernas indelning (C delas i Dam/Vet/Jun eller inte) — samma regel som medaljerna.
            bool splitC = ChampionshipCategory.SplitsGroupC(SafeString(comp, "competitionScope"), MedalGrouping.PerWeaponGroup(comp));

            // ── Per vapengrupp ─────────────────────────────────────────────────────────
            var groupNames = qualStarts.Select(s => s.Group).Distinct()
                .OrderBy(g => ShootingClassOrder.Key(g, ShootingClassOrder.ResultList)).ToList();
            var groupInputs = new List<CompetitionNextStep.GroupInput>();
            var groupRows = new List<object>();
            foreach (var g in groupNames)
            {
                var starts = qualStarts.Where(s => s.Group == g).ToList();
                int qExp = 0, qEnt = 0, lastSeries = 0, gapTeam = 0;
                foreach (var s in starts.OrderBy(s => s.TeamNumber))
                {
                    int due = LastDue(s.MemberId, s.ClassKey, qual);
                    int got = 0;
                    for (int n = 1; n <= qual; n++)
                    {
                        if (!Has(s, n)) continue;
                        lastSeries = Math.Max(lastSeries, n);
                        if (n <= due) got++;
                    }
                    qExp += due; qEnt += got;
                    if (got < due && gapTeam == 0) gapTeam = s.TeamNumber;
                }

                // Gruppens egen lista, annars en äldre heltävlingslista.
                var fl = finalsLists.FirstOrDefault(f => string.Equals(f.Group, g, StringComparison.OrdinalIgnoreCase))
                         ?? finalsLists.FirstOrDefault(f => f.Group.Length == 0);
                int fExp = 0, fEnt = 0, fLast = 0, fGapTeam = 0;
                if (fl != null && finalSeries > 0)
                {
                    foreach (var s in fl.Starts.Where(s => s.Group == g).OrderBy(s => s.TeamNumber))
                    {
                        int due = LastDue(s.MemberId, s.ClassKey, qual + finalSeries) - qual;
                        due = Math.Max(0, Math.Min(finalSeries, due));
                        int got = 0;
                        for (int n = 1; n <= finalSeries; n++)
                        {
                            if (!Has(s, qual + n)) continue;
                            fLast = Math.Max(fLast, n);
                            if (n <= due) got++;
                        }
                        fExp += due; fEnt += got;
                        if (got < due && fGapTeam == 0) fGapTeam = s.TeamNumber;
                    }
                }

                // Mästerskapsklasser med resultat som saknar finalister — se GroupInput.MissingFinalCategories.
                var missingCats = new List<string>();
                if (fl != null && finalSeries > 0)
                {
                    var withResults = starts.Where(s => Enumerable.Range(1, qual).Any(n => Has(s, n)))
                        .Select(s => ChampionshipCategory.For(s.RawClass, splitC)).Where(c => c.Length > 0);
                    var inFinal = new HashSet<string>(fl.Starts.Where(s => s.Group == g)
                        .Select(s => ChampionshipCategory.For(s.RawClass, splitC)), StringComparer.OrdinalIgnoreCase);
                    missingCats = withResults.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Where(c => !inFinal.Contains(c)).OrderBy(c => c).ToList();
                }

                var gi = new CompetitionNextStep.GroupInput
                {
                    Group = g, Starts = starts.Count, QualExpected = qExp, QualEntered = qEnt,
                    HasFinalsList = fl != null, FinalsExpected = fExp, FinalsEntered = fEnt,
                    MissingFinalCategories = missingCats.Count
                };
                groupInputs.Add(gi);
                var gPhase = CompetitionNextStep.ForGroup(gi, finalSeries);
                groupRows.Add(new
                {
                    group = g,
                    starts = starts.Count,
                    phase = gPhase.ToString(),
                    qualExpected = qExp, qualEntered = qEnt, qualLastSeries = lastSeries, qualGapTeam = gapTeam,
                    hasFinalsList = fl != null,
                    missingFinalCategories = missingCats,
                    finalsListId = fl?.Node.Id ?? 0,
                    finalsPublished = fl?.Published ?? false,
                    finalsExpected = fExp, finalsEntered = fEnt, finalsLastSeries = fLast, finalsGapTeam = fGapTeam
                });
            }

            // ── Resultatlistan ─────────────────────────────────────────────────────────
            var resultPage = children.FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
            bool resultsOfficial = resultPage != null && SafeBool(resultPage, "isOfficial");

            // ── Särskjutning — bara när allt är inmatat (uträkningen är tung) ──────────
            bool? unresolvedTies = null;
            var ties = new List<object>();
            bool allEntered = startListPublished && groupInputs.Any(g => g.QualExpected > 0)
                && groupInputs.Where(g => g.QualExpected > 0)
                    .All(g => CompetitionNextStep.ForGroup(g, finalSeries) == CompetitionNextStep.Phase.PublishResults);
            if (allEntered)
            {
                try
                {
                    var calc = await CalculateFinalResults(rows, competitionId, ReadStoredMerges(resultPage, false));
                    var groups = calc.MedalCategoryTies.Count > 0
                        ? calc.MedalCategoryTies.SelectMany(ct => ct.Groups.Select(tg => (ct.CategoryName, tg)))
                        : calc.ClassGroups.SelectMany(cg => (cg.TiedMedalGroups ?? new List<PrecisionTiedMedalGroup>())
                            .Select(tg => (cg.ClassName, tg)));
                    foreach (var (cat, tg) in groups.Where(x => !x.tg.Resolved))
                        ties.Add(new
                        {
                            category = cat,
                            medal = tg.MedalTier,
                            score = tg.TotalScore,
                            names = tg.Shooters.Select(s => s.Name).ToList()
                        });
                    unresolvedTies = ties.Count > 0;
                }
                catch (Exception ex)
                {
                    // "Vet inte" — kortet går då på publiceringsflaggan. Loggas, sväljs inte tyst.
                    _logger.LogWarning(ex, "NextStep: särskjutningsläget kunde inte räknas för {Id}", competitionId);
                }
            }

            var phase = CompetitionNextStep.Resolve(new CompetitionNextStep.Input
            {
                HasStartList = hasStartList,
                StartListPublished = startListPublished,
                FinalSeries = finalSeries,
                Groups = groupInputs,
                ResultsOfficial = resultsOfficial,
                UnresolvedTies = unresolvedTies
            });

            return new
            {
                success = true,
                supported = true,
                phase = phase.ToString(),
                step = CompetitionNextStep.StepIndex(phase),
                serverTime = DateTime.Now.ToString("HH:mm"),
                registrations,
                registrationCloses = closes?.ToString("yyyy-MM-dd"),
                registrationOpen = closes == null || closes.Value >= DateTime.Now,
                hasStartList,
                startListPublished,
                startListId = qualNodes.OrderByDescending(n => SafeBool(n, "isOfficialStartList")).FirstOrDefault()?.Id ?? 0,
                teamCount,
                startCount = qualStarts.Count,
                firstStartTime,
                qualSeries = qual,
                finalSeries,
                hasSkjutledareView = CompetitionSurfaces.HasSkjutledareView(typeId),
                groups = groupRows,
                ties,
                resultsOfficial
            };
        }

        private static int SafeInt(IContent c, string alias)
        {
            try { return c.HasProperty(alias) ? c.GetValue<int>(alias) : 0; } catch { return 0; }
        }

        private static bool SafeBool(IContent c, string alias)
        {
            try { return c.HasProperty(alias) && c.GetValue<bool>(alias); } catch { return false; }
        }

        private static string SafeString(IContent c, string alias)
        {
            try { return c.HasProperty(alias) ? (c.GetValue<string>(alias) ?? "") : ""; } catch { return ""; }
        }
    }
}
