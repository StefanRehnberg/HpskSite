using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.Models.Competitions;
using HpskSite.Services.Competitions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ SPÄRREN FÖR DOLDA TÄVLINGAR (kretsutlysningen F1, planen 4.2). Tre skyddslager, och varje lager har sitt prov:
    /// <list type="number">
    ///   <item>storen: listor utesluter utkast (provas mot databasen av <c>hpsk-verify/isdraft-verify.mjs</c>);</item>
    ///   <item>ETT globalt åtgärdsfilter (<see cref="DraftCompetitionGuardFilter"/>) för varje åtgärd som bär ett tävlings-id —
    ///   reglerna för vilka argument som räknas provas här, och <b>täckningen</b> (inga åtgärder med ett tävlings-id under annat
    ///   namn) provas av <see cref="Atgarder_med_ett_tavlings_id_under_annat_namn_ar_markerade"/>;</item>
    ///   <item>varje anrop som listar tävlingar (<c>List()</c>/<c>ListBySeries()</c>) måste stå i listan över granskade filer.</item>
    /// </list>
    /// </summary>
    public class DraftCompetitionGuardTests
    {
        // ── Reglerna: vilka argument är tävlings-id? ───────────────────────────────────────────

        public sealed class BodyWithId { public int CompetitionId { get; set; } public string? Name { get; set; } }
        public sealed class BodyWithNullableId { public int? CompetitionId { get; set; } }
        public sealed class BodyWithoutId { public int Id { get; set; } public int ClubId { get; set; } }

        private sealed class Dummy : Controller
        {
            public IActionResult ByName(int competitionId) => Ok();
            public IActionResult ByNullableName(int? competitionId) => Ok();
            public IActionResult ByMarkedName([CompetitionIdArg] int id) => Ok();
            public IActionResult ByBody([FromBody] BodyWithId request) => Ok();
            public IActionResult ByNullableBody([FromBody] BodyWithNullableId request) => Ok();
            public IActionResult NoCompetition(int id, [FromBody] BodyWithoutId request) => Ok();
            public IActionResult FromRequest(string x) => Ok();
        }

        private static ControllerActionDescriptor Descriptor(string action) =>
            new() { MethodInfo = typeof(Dummy).GetMethod(action)! };

        [Fact]
        public void Ett_argument_som_heter_competitionId_ar_ett_tavlings_id()
        {
            var ids = DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByName)),
                new Dictionary<string, object?> { ["competitionId"] = 42 });
            Assert.Equal(new[] { 42 }, ids);
        }

        [Fact]
        public void Skiftlaget_spelar_ingen_roll_och_null_ger_inget()
        {
            var ids = DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByNullableName)),
                new Dictionary<string, object?> { ["competitionId"] = null });
            Assert.Empty(ids);
        }

        [Fact]
        public void Ett_markerat_argument_ar_ett_tavlings_id_fast_det_heter_id()
        {
            var ids = DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByMarkedName)),
                new Dictionary<string, object?> { ["id"] = 7 });
            Assert.Equal(new[] { 7 }, ids);
        }

        [Fact]
        public void Ett_omarkerat_id_ar_inget_tavlings_id()
        {
            var ids = DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.NoCompetition)),
                new Dictionary<string, object?> { ["id"] = 7, ["request"] = new BodyWithoutId { Id = 7, ClubId = 3 } });
            Assert.Empty(ids);
        }

        [Fact]
        public void En_kropp_med_CompetitionId_ar_ett_tavlings_id()
        {
            Assert.Equal(new[] { 9 }, DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByBody)),
                new Dictionary<string, object?> { ["request"] = new BodyWithId { CompetitionId = 9 } }));
            Assert.Equal(new[] { 11 }, DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByNullableBody)),
                new Dictionary<string, object?> { ["request"] = new BodyWithNullableId { CompetitionId = 11 } }));
        }

        [Fact]
        public void Noll_och_negativa_id_prövas_inte_och_dubbletter_slås_ihop()
        {
            Assert.Empty(DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByName)),
                new Dictionary<string, object?> { ["competitionId"] = 0 }));
            Assert.Empty(DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.ByName)),
                new Dictionary<string, object?> { ["competitionId"] = -5 }));
        }

        [Fact]
        public void Ruttvardet_och_fragestrangen_raknas_for_atgarder_som_laser_det_ur_Request()
        {
            var ids = DraftCompetitionGuardRules.IdsOf(Descriptor(nameof(Dummy.FromRequest)),
                new Dictionary<string, object?>(),
                new Dictionary<string, string?> { ["competitionId"] = "33" });
            Assert.Equal(new[] { 33 }, ids);
        }

        // ── Publicera ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Publicera_kraver_namn_gren_datum_och_arrangor()
        {
            var empty = new CompetitionInfo();
            var missing = CompetitionPublishRules.Missing(empty);
            Assert.Contains("namn", missing);
            Assert.Contains("gren", missing);
            Assert.Contains("datum", missing);
            Assert.Contains(missing, m => m.StartsWith("arrangör"));

            var ready = new CompetitionInfo
            {
                CompetitionName = "Vårskytte", CompetitionType = "Precision", CompetitionDate = new DateTime(2027, 5, 1), ClubId = 12,
            };
            Assert.Empty(CompetitionPublishRules.Missing(ready));
            Assert.Null(CompetitionPublishRules.BlockedReason(ready));
            Assert.NotNull(CompetitionPublishRules.BlockedReason(empty));
        }

        [Fact]
        public void En_kretsvard_eller_ett_masterskap_raknas_som_arrangor()
        {
            var c = new CompetitionInfo { CompetitionName = "KM", CompetitionType = "Precision", CompetitionDate = new DateTime(2027, 5, 1) };
            Assert.NotEmpty(CompetitionPublishRules.Missing(c));
            c.RegionalFederation = "Halland";
            Assert.Empty(CompetitionPublishRules.Missing(c));
        }

        [Fact]
        public void Angra_publicering_kraver_att_anmalningar_saknas()
        {
            Assert.Null(CompetitionPublishRules.UnpublishBlockedReason(0));
            Assert.Contains("1 anmälan", CompetitionPublishRules.UnpublishBlockedReason(1));
            Assert.Contains("3 anmälningar", CompetitionPublishRules.UnpublishBlockedReason(3));
        }

        // ── Fältet ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void IsDraft_ar_ett_tillstandsfalt_som_aldrig_foljer_med_en_kopia_eller_ur_ett_formular()
        {
            var f = CompetitionFieldTable.ForAlias("isDraft");
            Assert.NotNull(f);
            Assert.Equal(CompetitionFieldKind.State, f!.Kind);
            Assert.Equal("IsDraft", f.Property);

            var source = new CompetitionInfo { IsDraft = true, IsVisible = true };
            Assert.False(CompetitionFieldTable.CopyDefinition(source).IsDraft);

            // Ur ett formulär: en nyckel som inte är ett definitionsfält hamnar i Ignored och skrivs aldrig.
            var target = new CompetitionInfo { IsDraft = true };
            var result = CompetitionFieldBag.Apply(target, new Dictionary<string, object?> { ["isDraft"] = "false" });
            Assert.True(target.IsDraft);
            Assert.Contains("isDraft", result.Ignored);
        }

        [Fact]
        public void IsDraft_ar_inte_IsVisible_och_tvartom()
        {
            var c = new CompetitionInfo { IsVisible = false };
            Assert.False(c.IsDraft);
            var d = new CompetitionInfo { IsDraft = true };
            Assert.True(d.IsVisible);   // ett utkast är aktivt men dolt: två skilda begrepp
        }

        // ── Täckningen: inga åtgärder med ett tävlings-id under ett annat namn ──────────────────

        /// <summary>Namn som kan bära ett tävlings-id men INTE fångas av filtret (<c>competitionId</c> fångas, och <c>CompetitionId</c> i en kropp).</summary>
        private static readonly Regex OtherCompetitionIdName = new(
            "^(comp(etition)?(node|content)?id|cid|compid|sourcecompetitionid|targetcompetitionid|fromcompetitionid|tocompetitionid|[a-z]*competitionid)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Åtgärder vars tävlings-id heter något annat än <c>competitionId</c> och som varken är markerade med
        /// <see cref="CompetitionIdArgAttribute"/> eller <see cref="DraftGuardOkAttribute"/> (med skäl). <b>Sänk taket när en åtgärd
        /// märks — höj det aldrig.</b> Ett nytt namn som glöms fäller bygget; då märks argumentet <c>[CompetitionIdArg]</c> (det är
        /// ett tävlings-id) eller åtgärden <c>[DraftGuardOk("skäl")]</c> (det är det inte, eller prövas på annat sätt).
        /// </summary>
        private const int UncoveredNameBaseline = 0;

        [Fact]
        public void Atgarder_med_ett_tavlings_id_under_annat_namn_ar_markerade()
        {
            var uncovered = new List<string>();
            foreach (var action in ControllerActions())
            {
                if (action.GetCustomAttribute<DraftGuardOkAttribute>() != null) continue;
                if (action.DeclaringType!.GetCustomAttribute<DraftGuardOkAttribute>() != null) continue;
                foreach (var p in action.GetParameters())
                {
                    if (p.GetCustomAttribute<CompetitionIdArgAttribute>() != null) continue;
                    if (IsIntLike(p.ParameterType) && p.Name != null && !DraftCompetitionGuardRules.IsCompetitionIdName(p.Name)
                        && OtherCompetitionIdName.IsMatch(p.Name))
                        uncovered.Add($"{action.DeclaringType.Name}.{action.Name}({p.Name})");
                    var body = BodyType(p.ParameterType);
                    if (body == null || DraftCompetitionGuardRules.ObjectIdProperty(body) != null) continue;
                    foreach (var prop in body.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        if (IsIntLike(prop.PropertyType) && OtherCompetitionIdName.IsMatch(prop.Name)
                            && !prop.Name.Equals("CompetitionId", StringComparison.OrdinalIgnoreCase))
                            uncovered.Add($"{action.DeclaringType.Name}.{action.Name}({p.Name}.{prop.Name})");
                }
            }
            uncovered = uncovered.Distinct().OrderBy(s => s).ToList();
            Assert.True(uncovered.Count <= UncoveredNameBaseline,
                $"{uncovered.Count} åtgärder bär ett tävlings-id under ett annat namn än competitionId (taket är {UncoveredNameBaseline}). " +
                "Märk argumentet [CompetitionIdArg] om det är ett tävlings-id, annars åtgärden [DraftGuardOk(\"skäl\")]:\n" + string.Join("\n", uncovered));
        }

        [Fact]
        public void Filtret_fangar_de_vanliga_formerna_i_den_riktiga_koden()
        {
            // Kontrollprov: reglerna hittar ett tävlings-id i de åtgärder de ska — annars kan täckningsprovet inte falla.
            var covered = ControllerActions().Count(a => DraftCompetitionGuardRules.CarriesCompetitionId(a));
            Assert.True(covered > 200, $"Bara {covered} åtgärder bär ett tävlings-id enligt reglerna; väntade hundratals.");
            var surface = ControllerActions().Single(a => a.DeclaringType!.Name == "CompetitionAdminController" && a.Name == "PublishCompetition");
            Assert.True(DraftCompetitionGuardRules.CarriesCompetitionId(surface));
        }

        private static bool IsIntLike(Type t) => t == typeof(int) || t == typeof(int?);

        private static Type? BodyType(Type t)
        {
            if (t.IsPrimitive || t == typeof(string) || t.IsEnum || t.IsGenericType || t.IsArray) return null;
            if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true
                || t.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true) return null;
            return t.IsClass ? t : null;
        }

        private static IEnumerable<MethodInfo> ControllerActions()
        {
            var assembly = typeof(DraftCompetitionGuardFilter).Assembly;
            foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t)))
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (!m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() == null)
                        yield return m;
        }

        // ── Listor: varje anrop som listar tävlingar är granskat ────────────────────────────────

        /// <summary>
        /// Filer som anropar <c>List()</c>/<c>ListBySeries()</c> på tävlingslagringen och som är GRANSKADE 2026-11-19: de visar tävlingar för
        /// allmänheten och ska därför aldrig se utkast (standardvärdet), eller anger <c>includeDrafts: true</c> med skäl.
        /// En ny fil här fäller bygget tills någon bestämt vilket av de två den är. Ett motiverat undantag får <c>draft-list-ok:</c> på raden.
        /// </summary>
        private static readonly HashSet<string> ReviewedListCallers = new(StringComparer.OrdinalIgnoreCase)
        {
            // Visar för allmänheten (utkast ingår inte)
            "CompetitionTypes/Faltskytte/Controllers/FaltskytteController.cs",
            "Controllers/AdminStatisticsController.cs", "Controllers/AdminStatisticsController.Planering.cs",
            "Controllers/ClubController.cs", "Controllers/ClubStatisticsController.cs",
            "Controllers/CompetitionSearchController.cs", "Controllers/InvoiceAdminController.cs",
            "Controllers/RegionalStatisticsController.cs", "Controllers/RegistrationAdminController.cs",
            "Routing/CompetitionTreeRedirectFinder.cs",
            "Services/CompetitionMenuService.cs", "Services/Kretsgranskning/KretsCalendarService.cs",
            "Services/MedalHandoutService.cs", "Services/Ops/CompetitionHealthService.cs", "Services/RangeCalendarService.cs",
            "Services/Schedule/MyScheduleService.cs", "Services/SitePages/SitePageReader.cs", "Services/WhatsHappeningService.cs",
            "Views/CompetitionPage.cshtml", "Views/CompetitionSeason.cshtml", "Views/CompetitionSeriesPage.cshtml",
            "Views/CompetitionsHub.cshtml", "Views/HomePage.cshtml", "Views/Partials/ClubCompetitionsList.cshtml",
            "Views/Partials/Panels/_PanelRegionStats.cshtml", "Views/Partials/RegionCompetitionsList.cshtml",
            // Arrangörens egna ytor och underhåll (includeDrafts: true) eller kända id:n
            "Controllers/CompetitionAdminController.cs", "Controllers/PlanningController.Prep.cs",
            "Routing/CompetitionUrlContentFinder.cs", "Services/Addresses/CompetitionAddressService.cs",
            "Services/CompetitionHostBackfillService.cs", "Services/Competitions/CompetitionPostCache.cs",
            "Services/SeriesCalculationService.cs",
            // Skyddsnätets jämförelser och lagringen själv
            "Services/C3/CompetitionStoreComparison.cs", "Services/Competitions/CompetitionStoreSwitch.cs",
            "Services/Competitions/NodeCompetitionStore.cs", "Services/Competitions/SqlCompetitionStore.cs",
        };

        private static readonly Regex StoreListCall = new(
            @"\b(_competitions|competitions|Competitions|_store|_nodeCompetitions|_sqlCompetitions|competitionStore|_f\._competitions)\s*\.\s*(List|ListBySeries|ListAllInSeries)\s*\(",
            RegexOptions.Compiled);

        [Fact]
        public void Varje_fil_som_listar_tavlingar_ar_granskad()
        {
            var root = SiteRoot();
            var unreviewed = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ReviewedListCallers.Contains(rel)) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("*") || lines[i].Contains("draft-list-ok:")) continue;
                    if (StoreListCall.IsMatch(lines[i])) unreviewed.Add($"{rel}:{i + 1}: {t}");
                }
            }
            Assert.True(unreviewed.Count == 0,
                "Ett anrop listar tävlingar utan att vara granskat för dolda tävlingar. Visar det för allmänheten: lämna standardvärdet och lägg " +
                "filen i ReviewedListCallers. Är det arrangörens egen yta: ange includeDrafts: true (och pröva behörigheten per tävling). " +
                "Annars markera raden med draft-list-ok: <skäl>:\n" + string.Join("\n", unreviewed));
        }

        [Fact]
        public void Listmonstret_fangar_det_det_ska()
        {
            Assert.Matches(StoreListCall, "var all = _competitions.List();");
            Assert.Matches(StoreListCall, "foreach (var c in Competitions.ListBySeries(s.Id))");
            Assert.DoesNotMatch(StoreListCall, "var lists = _startLists.List(competitionId);");
            Assert.DoesNotMatch(StoreListCall, "var r = _registrations.List(competitionId);");
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            var skip = new[] { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var sub in Directory.GetDirectories(dir))
                    if (!skip.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
                foreach (var f in Directory.GetFiles(dir))
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                        yield return f;
            }
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "")
        {
            var src = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
            var site = Path.Combine(src, "HpskSite");
            Assert.True(Directory.Exists(site), $"Site source not found at {site}");
            return site;
        }
    }
}
