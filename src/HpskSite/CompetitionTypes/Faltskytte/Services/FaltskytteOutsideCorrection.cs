using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Competitions;
using HpskSite.Services.Registrations;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Konsekvenserna av att flytta en skytt utanför mästerskapet i en PUBLICERAD lista, uttryckta som rader arrangören kan läsa
    /// upp och som hen kan underrätta de berörda utifrån: platsändringar i klassgrupperna och förlorade/vunna/ändrade
    /// mästerskapsmedaljer. Ren funktion över två artefakter (listan nu, listan efteråt) — all hämtning ligger i
    /// <see cref="FaltskytteOutsideCorrectionService"/>.
    /// </summary>
    public sealed record OutsideConsequences(IReadOnlyList<string> Lines, int PlacementChanges, int MedalChanges);

    public static class OutsideConsequenceCalculator
    {
        /// <param name="movedOutside">Sant (förval): skytten flyttas UT ur mästerskapet. Falsk: skytten flyttas TILLBAKA IN.</param>
        public static OutsideConsequences Compute(PrecisionFinalResults before, PrecisionFinalResults after, int movedMemberId, string movedClass, bool movedOutside = true)
        {
            var movedKey = ShootingClasses.NormalizeKey(movedClass);
            var placementLines = new List<(int sort, string text)>();
            var moved = new List<string>();
            var placementChanges = 0;

            var afterShooters = after.ClassGroups
                .SelectMany(cg => cg.Shooters.Select(s => (Group: cg, Shooter: s)))
                .ToDictionary(x => (x.Group.ClassName, x.Shooter.MemberId), x => x.Shooter);

            foreach (var cg in before.ClassGroups)
            {
                var label = string.IsNullOrWhiteSpace(cg.DisplayClassName) ? cg.ClassName : cg.DisplayClassName;
                foreach (var s in cg.Shooters)
                {
                    if (!afterShooters.TryGetValue((cg.ClassName, s.MemberId), out var a)) continue;
                    if (s.Placement == a.Placement) continue;

                    placementChanges++;
                    var isMoved = s.MemberId == movedMemberId && ShootingClasses.NormalizeKey(s.ShootingClass) == movedKey;
                    if (isMoved && movedOutside)
                    {
                        moved.Add(s.Placement is int was
                            ? $"{s.Name} får ingen placering i {label} (har plats {was} nu)."
                            : $"{s.Name} får ingen placering i {label}.");
                    }
                    else if (isMoved)
                    {
                        // Tillbaka in: skytten får en plats i klassen igen, och de som står efter hen flyttas ned.
                        moved.Add(a.Placement is int now
                            ? (s.Placement is int was2
                                ? $"{s.Name} får plats {now} i {label} (har plats {was2} nu)."
                                : $"{s.Name} får plats {now} i {label} (har ingen placering nu).")
                            : $"{s.Name} får ingen placering i {label}.");
                    }
                    else if (s.Placement is int from && a.Placement is int to)
                    {
                        placementLines.Add((to, $"{s.Name} går från plats {from} till plats {to} i {label}."));
                    }
                    else
                    {
                        placementLines.Add((int.MaxValue, $"{s.Name}: placeringen i {label} ändras ({Describe(s.Placement)} → {Describe(a.Placement)})."));
                    }
                }
            }

            var medalLines = new List<string>();
            var medalChanges = 0;

            static Dictionary<(string Category, int MemberId), (string Medal, string Name)> Medals(PrecisionFinalResults r) =>
                r.MedalAwards
                    .SelectMany(c => c.Awards.Select(w => (c.CategoryName, w)))
                    .GroupBy(x => (x.CategoryName, x.w.MemberId))
                    .ToDictionary(g => g.Key, g => (g.First().w.Medal, g.First().w.Name));

            var mb = Medals(before);
            var ma = Medals(after);
            foreach (var key in mb.Keys.Union(ma.Keys).OrderBy(k => k.Category, StringComparer.CurrentCulture).ThenBy(k => k.MemberId))
            {
                mb.TryGetValue(key, out var b);
                ma.TryGetValue(key, out var a);
                var name = !string.IsNullOrEmpty(b.Name) ? b.Name : a.Name;
                if (b.Medal == a.Medal) continue;
                medalChanges++;
                if (b.Medal != null && a.Medal == null)
                    medalLines.Add($"{name} förlorar mästerskaps{Lower(b.Medal)} i {key.Category}.");
                else if (b.Medal == null && a.Medal != null)
                    medalLines.Add($"{name} får mästerskaps{Lower(a.Medal)} i {key.Category} (har ingen medalj nu).");
                else
                    medalLines.Add($"{name} går från mästerskaps{Lower(b.Medal)} till mästerskaps{Lower(a.Medal)} i {key.Category}.");
            }

            // Reduceringen (SHB C.3.4.1): färre tävlande kan ge färre medaljer i kategorin, även för dem som inte flyttats.
            foreach (var cb in before.MedalAwards)
            {
                var ca = after.MedalAwards.FirstOrDefault(c => string.Equals(c.CategoryName, cb.CategoryName, StringComparison.OrdinalIgnoreCase));
                if (ca == null || ca.MedalCount == cb.MedalCount) continue;
                medalLines.Add($"Medaljantalet i {cb.CategoryName} ändras från {cb.MedalCount} ({cb.MedalCountText}) till {ca.MedalCount} ({ca.MedalCountText}); " +
                               $"tävlande {cb.Participants} → {ca.Participants}.");
            }

            var lines = new List<string>();
            lines.AddRange(moved);
            lines.AddRange(placementLines.OrderBy(p => p.sort).Select(p => p.text));
            lines.AddRange(medalLines);
            return new OutsideConsequences(lines, placementChanges, medalChanges);
        }

        private static string Describe(int? placement) => placement is int p ? "plats " + p : "ingen plats";
        private static string Lower(string? s) => (s ?? "").ToLowerInvariant();
    }

    public sealed class OutsideCorrectionPreview
    {
        public bool Ok { get; init; }
        public string? Message { get; init; }
        public string MemberName { get; init; } = "";
        public string ClassName { get; init; } = "";
        public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
        public int PlacementChanges { get; init; }
        public int MedalChanges { get; init; }

        /// <summary>
        /// Fingeravtryck av exakt dessa konsekvenser. Rättningen skickar tillbaka det, och servern räknar om konsekvenserna när den
        /// tillämpar: skiljer de sig (någon har sedan dess matat in ett resultat, räknat om listan eller rättat en annan skytt) nekas
        /// rättningen. Arrangören rättar alltså aldrig något annat än det hen läste.
        /// </summary>
        public string Token { get; init; } = "";
    }

    public sealed class OutsideCorrectionResult
    {
        public bool Ok { get; init; }
        public string? Message { get; init; }
        public bool ListRefreshed { get; init; }
    }

    /// <summary>
    /// Rätta listan → "Flytta utanför mästerskapet" (S6). En PUBLICERAD lista är fast; det här är den enda vägen att ändra vem som
    /// skjuter utanför mästerskapet efteråt. Förhandsvisningen räknar listan med och utan flaggan (byggaren med en överstyrd mängd) och
    /// visar konsekvenserna; ändringen kräver en anledning och en bekräftelse, skrivs som ett arrangörsbeslut, loggas (beslutsloggen +
    /// tävlingens ändringslogg med anledningen) och räknar om listan så att den frusna bilden följer.
    ///
    /// <para>⚠️ Det finns INGEN spärr på "medaljer beställts eller delats ut": ingenting i systemet lagrar det (medaljlistan i
    /// <c>MedalHandoutService</c> härleds ur resultaten). Därför kräver ändringen i stället en uttrycklig bekräftelse.</para>
    /// </summary>
    public class FaltskytteOutsideCorrectionService
    {
        public const int MinReasonLength = 5;
        public const int MaxReasonLength = 300;
        /// <summary>⚠️ Högst 20 tecken: <c>CompetitionChange.Action</c> är nvarchar(20) och <c>Record</c> sväljer felet tyst (testat).</summary>
        public const string ChangeAction = "outside-correction";

        private readonly FaltskytteResultsBuilder _builder;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly ICompetitionRegistrationStore _registrations;
        private readonly OutOfCompetitionFlags _flags;
        private readonly CompetitionTeamService _teams;
        private readonly FaltskytteResultArtifactService _artifacts;
        private readonly CompetitionChangeRecorder _changes;
        private readonly ILogger<FaltskytteOutsideCorrectionService> _logger;
        private readonly Umbraco.Cms.Infrastructure.Scoping.IScopeProvider _scopeProvider;
        private readonly HpskSite.Services.Results.ICompetitionResultStore _results;

        public FaltskytteOutsideCorrectionService(
            Umbraco.Cms.Infrastructure.Scoping.IScopeProvider scopeProvider,
            FaltskytteResultsBuilder builder,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            ICompetitionRegistrationStore registrations,
            OutOfCompetitionFlags flags,
            CompetitionTeamService teams,
            FaltskytteResultArtifactService artifacts,
            CompetitionChangeRecorder changes,
            ILogger<FaltskytteOutsideCorrectionService> logger,
            HpskSite.Services.Results.ICompetitionResultStore results)
        {
            _results = results;
            _scopeProvider = scopeProvider;
            _builder = builder;
            _competitions = competitions;
            _registrations = registrations;
            _flags = flags;
            _teams = teams;
            _artifacts = artifacts;
            _changes = changes;
            _logger = logger;
        }

        private sealed record Target(
            HpskSite.Models.Competitions.CompetitionInfo Competition, ChampionshipRule Rule,
            HpskSite.Models.Registrations.RegistrationRecord Registration, string ClassRaw);

        /// <summary>
        /// Prövar att flytten går. <paramref name="outside"/> sant = flytta UT ur mästerskapet, falskt = flytta TILLBAKA IN.
        /// Returnerar felet, annars null och målet.
        /// </summary>
        private async Task<(string? Error, Target? Target)> CheckAsync(int competitionId, int registrationId, string classKey, bool outside)
        {
            var competition = competitionId > 0 ? _competitions.GetSaved(competitionId) : null;
            if (competition == null) return ("Tävlingen hittades inte.", null);

            var rule = ChampionshipRule.For(competition);
            if (!rule.OutOfCompetitionApplies)
                return ("Den här tävlingen använder inte \"Utanför mästerskapet\".", null);
            if (!competition.FaltskytteResultsOfficial)
                return ("Resultatlistan är inte publicerad. Ändra kryssrutan \"Utanför mästerskapet\" i Redigera anmälan i stället.", null);

            var registration = _registrations.GetById(registrationId);
            if (registration == null || registration.CompetitionId != competitionId)
                return ("Anmälan hittades inte.", null);

            var key = ShootingClasses.NormalizeKey(classKey);
            var cls = registration.Classes.FirstOrDefault(c => ShootingClasses.NormalizeKey(c.Class) == key);
            if (cls == null) return ("Anmälan har ingen sådan klass.", null);
            if (!rule.AppliesToClass(cls.Class))
                return ("Mästerskapet gäller inte den här vapengruppen, så det finns ingen mästerskapsplacering att stå utanför.", null);

            var current = _flags.States(competition, rule)
                .FirstOrDefault(s => s.RegistrationId == registration.Id && s.ClassKey == key);
            if (outside)
            {
                if (current?.Effective == true)
                    return ($"{registration.MemberName} skjuter redan utanför mästerskapet i den här klassen.", null);

                var team = (await _teams.GetTeamsForCompetitionAsync(competitionId))
                    .FirstOrDefault(t => t.Members.Any(m => m.MemberId == registration.MemberId));
                if (team != null)
                    return ($"{registration.MemberName} ingår i laget {team.Team.TeamName}. Ta ur laget först — en skytt utanför mästerskapet får inte ingå i ett lag.", null);
            }
            else if (current?.Effective != true)
            {
                return ($"{registration.MemberName} skjuter redan i mästerskapet i den här klassen.", null);
            }

            return (null, new Target(competition, rule, registration, cls.Class));
        }

        /// <summary>
        /// Konsekvenserna av flytten: den PUBLICERADE listan (artefakten som skrevs vid publicering/omräkning, annars ett nytt bygge) jämförd
        /// med listan som den blir. Allt som skiljer visas, även sådant som inte hör till flytten (ett resultat som matats in sedan publiceringen):
        /// rättningen räknar om hela listan, och arrangören ska se vad hen faktiskt ändrar.
        /// </summary>
        private async Task<(OutsideCorrectionPreview? Preview, string? Error, Target? Target)> ConsequencesAsync(
            int competitionId, int registrationId, string classKey, bool outside)
        {
            var (error, t) = await CheckAsync(competitionId, registrationId, classKey, outside);
            if (error != null || t == null) return (null, error, null);

            // Utgångspunkten är den PUBLICERADE artefakten (resultData: klassgrupperna med platser och medaljställningarna), som skrivs
            // samtidigt med den frusna bilden — inte den frusna bilden själv, som saknar medaljställningarna. Saknas den räknas listan nytt.
            PrecisionFinalResults? beforeArtifact = null;
            var published = _results.Get(competitionId)?.ResultData;
            if (!string.IsNullOrWhiteSpace(published))
            {
                try { beforeArtifact = Newtonsoft.Json.JsonConvert.DeserializeObject<PrecisionFinalResults>(published); }
                catch (Exception ex) { _logger.LogWarning(ex, "Kunde inte läsa den publicerade artefakten för tävling {CompetitionId}", competitionId); }
            }
            if (beforeArtifact == null)
            {
                var before = await _builder.BuildAsync(competitionId);
                if (before.Results == null)
                    return (null, before.Error ?? "Listan kunde inte räknas.", null);
                beforeArtifact = FaltskytteResultArtifactService.BuildArtifact(t.Competition, before);
            }

            var live = _flags.Load(t.Competition, t.Rule);
            var set = outside ? live.With(t.Registration.MemberId, t.ClassRaw) : live.Without(t.Registration.MemberId, t.ClassRaw);
            var after = await _builder.BuildAsync(competitionId, null, false, set);
            if (after.Results == null)
                return (null, after.Error ?? "Listan kunde inte räknas.", null);

            var c = OutsideConsequenceCalculator.Compute(
                beforeArtifact,
                FaltskytteResultArtifactService.BuildArtifact(t.Competition, after),
                t.Registration.MemberId, t.ClassRaw, outside);

            return (new OutsideCorrectionPreview
            {
                Ok = true,
                MemberName = t.Registration.MemberName,
                ClassName = ShootingClasses.GetById(t.ClassRaw)?.Name ?? t.ClassRaw,
                Lines = c.Lines,
                PlacementChanges = c.PlacementChanges,
                MedalChanges = c.MedalChanges,
                Token = ConsequenceToken(outside, c)
            }, null, t);
        }

        /// <summary>Fingeravtryck av riktning och exakta konsekvenser (rader och antal). Samma konsekvenser ger samma värde.</summary>
        public static string ConsequenceToken(bool outside, OutsideConsequences c)
        {
            var text = (outside ? "ut" : "in") + "|" + c.PlacementChanges + "|" + c.MedalChanges + "|" + string.Join("\n", c.Lines);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
        }

        /// <summary>
        /// När listan senast rättades med "Flytta utanför mästerskapet" (svensk tid), eller null. Tittarnotisen på resultatsidan
        /// ("Listan rättades …") läser den. Ett fel ger null — notisen är en upplysning, aldrig en förutsättning för sidan.
        /// </summary>
        public DateTime? LatestCorrection(int competitionId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var at = scope.Database.ExecuteScalar<DateTime?>(
                    "SELECT MAX(AtUtc) FROM dbo.CompetitionChange WHERE TargetKind = @2 AND TargetId = @0 AND [Action] = @1",
                    competitionId, ChangeAction, HpskSite.Models.Competitions.CompetitionFieldTable.NodeAlias);
                return HpskSite.Services.Hosting.SwedishTime.FromUtc(at);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa rättningsdatum för tävling {CompetitionId}", competitionId);
                return null;
            }
        }

        /// <param name="outside">Sant: flytta skytten UT ur mästerskapet. Falskt: flytta skytten TILLBAKA IN.</param>
        public async Task<OutsideCorrectionPreview> PreviewAsync(int competitionId, int registrationId, string classKey, bool outside = true)
        {
            var (preview, error, _) = await ConsequencesAsync(competitionId, registrationId, classKey, outside);
            return preview ?? new OutsideCorrectionPreview { Ok = false, Message = error };
        }

        /// <param name="expectedToken">Fingeravtrycket från förhandsvisningen arrangören läste (<see cref="OutsideCorrectionPreview.Token"/>).</param>
        /// <param name="outside">Sant: flytta skytten UT ur mästerskapet. Falskt: flytta skytten TILLBAKA IN.</param>
        public async Task<OutsideCorrectionResult> ApplyAsync(
            int competitionId, int registrationId, string classKey, string? reason, bool confirmed, int? decidedByMemberId,
            string? expectedToken = null, bool outside = true)
        {
            var why = (reason ?? "").Trim();
            if (why.Length < MinReasonLength)
                return new OutsideCorrectionResult { Ok = false, Message = "Skriv en anledning (minst några ord) — den loggas." };
            if (why.Length > MaxReasonLength)
                return new OutsideCorrectionResult { Ok = false, Message = $"Anledningen får vara högst {MaxReasonLength} tecken." };
            if (!confirmed)
                return new OutsideCorrectionResult { Ok = false, Message = "Bekräfta att du har kontrollerat att inga medaljer beställts eller delats ut, eller att du kontaktat kretsen." };

            // Konsekvenserna räknas om NU och måste vara exakt de arrangören läste: har någon sedan dess matat in ett resultat, räknat om
            // listan eller rättat en annan skytt, nekas rättningen i stället för att tyst ändra något hen inte såg.
            var (current, error, t) = await ConsequencesAsync(competitionId, registrationId, classKey, outside);
            if (current == null || t == null) return new OutsideCorrectionResult { Ok = false, Message = error };
            if (string.IsNullOrEmpty(expectedToken) || !string.Equals(expectedToken, current.Token, StringComparison.Ordinal))
                return new OutsideCorrectionResult
                {
                    Ok = false,
                    Message = "Listan har ändrats sedan du tittade på konsekvenserna. Visa konsekvenserna igen och kontrollera dem innan du fortsätter."
                };

            var outcome = _registrations.SetOutOfCompetition(competitionId, t.Registration.Id, t.ClassRaw, outside, decidedByMemberId, "correction");
            if (outcome == OutOfCompetitionOutcome.RegistrationNotFound || outcome == OutOfCompetitionOutcome.ClassNotFound)
                return new OutsideCorrectionResult { Ok = false, Message = "Anmälan eller klassen hittades inte." };

            // Anledningen får inte plats i beslutsloggen (40 tecken) — den bor i tävlingens ändringslogg, och den läses också av
            // tittarnotisen ("Listan rättades {datum}"). Varningsnivå: en rättning av en publicerad lista ska gå att spåra i loggen.
            _changes.Record(HpskSite.Models.Competitions.CompetitionFieldTable.NodeAlias, competitionId, ChangeAction, new Dictionary<string, (object?, object?)>
            {
                ["shooter"] = (null, t.Registration.MemberName),
                ["class"] = (null, t.ClassRaw),
                ["to"] = (null, outside ? "utanför" : "i mästerskapet"),
                ["reason"] = (null, why)
            });
            _logger.LogWarning("Publicerad fältlista RÄTTAD: {Member} flyttad {Direction} i {Class}, tävling {CompetitionId}. Anledning: {Reason}",
                t.Registration.MemberName, outside ? "utanför mästerskapet" : "tillbaka in i mästerskapet", t.ClassRaw, competitionId, why);

            var refreshed = await _artifacts.RefreshAsync(competitionId, outside ? "flyttad utanför mästerskapet (rättning)" : "flyttad tillbaka in i mästerskapet (rättning)");
            return new OutsideCorrectionResult { Ok = true, ListRefreshed = refreshed };
        }
    }
}
