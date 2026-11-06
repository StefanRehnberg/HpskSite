using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// "Utanför mästerskapet" i precisionsfamiljens resultatlista (rena funktioner i <see cref="PrecisionOutOfCompetition"/>). Reglerna är fältets:
    /// en skytt utanför får resultatet men ingen plats, ingen mästerskapskategori och aldrig en finalplats; platsen räknas över de tävlande.
    /// </summary>
    public class PrecisionOutOfCompetitionTests
    {
        private static PrecisionShooterResult S(int memberId, string cls = "C1") =>
            new() { MemberId = memberId, Name = "Skytt " + memberId, ShootingClass = cls };

        private static PrecisionClassGroup G(string name, params PrecisionShooterResult[] shooters) =>
            new() { ClassName = name, Shooters = shooters.ToList() };

        // ── Mark ──────────────────────────────────────────────────────────────────
        [Fact]
        public void Mark_satter_flaggan_per_skytt_och_klass()
        {
            var a = S(1, "C1"); var b = S(1, "B1"); var c = S(2, "C1");
            // Skytt 1 är utanför i C1 men tävlar i B1 (en start är per skytt OCH klass).
            PrecisionOutOfCompetition.Mark(new[] { a, b, c }, OutOfCompetitionSet.None.With(1, "C1"));
            Assert.True(a.OutOfCompetition);
            Assert.False(b.OutOfCompetition);
            Assert.False(c.OutOfCompetition);
        }

        [Fact]
        public void Mark_med_tom_mangd_lamnar_alla_tavlande()
        {
            var a = S(1); var b = S(2);
            PrecisionOutOfCompetition.Mark(new[] { a, b }, OutOfCompetitionSet.None);
            Assert.False(a.OutOfCompetition);
            Assert.False(b.OutOfCompetition);
        }

        [Fact]
        public void Mark_matchar_klassen_oavsett_om_den_ar_id_eller_visningsnamn()
        {
            // Startlistan lagrar klassens id ("C_Vet_Y"), resultatraden visningsnamnet ("C Vet Y").
            var s = S(1, "C Vet Y");
            PrecisionOutOfCompetition.Mark(new[] { s }, OutOfCompetitionSet.None.With(1, "C_Vet_Y"));
            Assert.True(s.OutOfCompetition);
        }

        // ── AssignPlacements ──────────────────────────────────────────────────────
        [Fact]
        public void Placering_raknas_over_de_tavlande_och_gasten_far_ingen()
        {
            var first = S(1); var guest = S(2); var third = S(3);
            guest.OutOfCompetition = true;
            // Gästen står tvåa i poängordning; de som står efter flyttas upp.
            var group = G("C1", first, guest, third);
            PrecisionOutOfCompetition.AssignPlacements(new[] { group });
            Assert.Equal(1, first.Placement);
            Assert.Null(guest.Placement);
            Assert.Equal(2, third.Placement);
        }

        [Fact]
        public void Placering_raknas_per_klassgrupp()
        {
            var a1 = S(1, "C1"); var a2 = S(2, "C1"); var b1 = S(3, "B1");
            PrecisionOutOfCompetition.AssignPlacements(new[] { G("C1", a1, a2), G("B1", b1) });
            Assert.Equal(1, a1.Placement);
            Assert.Equal(2, a2.Placement);
            Assert.Equal(1, b1.Placement);   // B1 börjar om på 1
        }

        [Fact]
        public void Placering_foljer_ordningen_i_listan_alltsa_efter_sarskjutningens_omsortering()
        {
            // AssignPlacements läser listans ORDNING; byggaren anropar den efter att särskjutningen omsorterat klassgrupperna.
            var x = S(1); var y = S(2);
            var group = G("C1", y, x);          // särskjutningen har lagt y före x
            PrecisionOutOfCompetition.AssignPlacements(new[] { group });
            Assert.Equal(1, y.Placement);
            Assert.Equal(2, x.Placement);
        }

        [Fact]
        public void Placering_ar_null_for_alla_i_en_klass_dar_alla_ar_utanfor()
        {
            var a = S(1); var b = S(2); a.OutOfCompetition = true; b.OutOfCompetition = true;
            PrecisionOutOfCompetition.AssignPlacements(new[] { G("C1", a, b) });
            Assert.Null(a.Placement);
            Assert.Null(b.Placement);
        }

        // ── Finalen: en gäst står aldrig i en finalstartlista ─────────────────────
        private static PrecisionResultEntry R(int memberId, string cls = "C1") => new() { MemberId = memberId, ShootingClass = cls, SeriesNumber = 1 };

        [Fact]
        public void ExcludeGuests_resultat_tar_bort_gasten_fore_rankingen()
        {
            var rows = new List<PrecisionResultEntry> { R(1), R(2), R(3) };
            var kept = PrecisionOutOfCompetition.ExcludeGuests(rows, OutOfCompetitionSet.None.With(2, "C1"));
            Assert.Equal(new[] { 1, 3 }, kept.Select(r => r.MemberId).ToArray());
        }

        [Fact]
        public void OnlyGuests_ar_motsatsen_till_ExcludeGuests()
        {
            var rows = new List<PrecisionResultEntry> { R(1), R(2), R(3) };
            var outside = OutOfCompetitionSet.None.With(2, "C1");
            Assert.Equal(new[] { 2 }, PrecisionOutOfCompetition.OnlyGuests(rows, outside).Select(r => r.MemberId).ToArray());
            Assert.Equal(rows.Count, PrecisionOutOfCompetition.OnlyGuests(rows, outside).Count + PrecisionOutOfCompetition.ExcludeGuests(rows, outside).Count);
            Assert.Empty(PrecisionOutOfCompetition.OnlyGuests(rows, OutOfCompetitionSet.None));
        }

        [Fact]
        public void ExcludeGuests_resultat_ar_per_skytt_och_klass()
        {
            var rows = new List<PrecisionResultEntry> { R(1, "C1"), R(1, "B1") };
            var kept = PrecisionOutOfCompetition.ExcludeGuests(rows, OutOfCompetitionSet.None.With(1, "C1"));
            Assert.Single(kept);
            Assert.Equal("B1", kept[0].ShootingClass);
        }

        [Fact]
        public void ExcludeGuests_resultat_med_tom_mangd_ger_samma_lista_tillbaka()
        {
            var rows = new List<PrecisionResultEntry> { R(1), R(2) };
            Assert.Same(rows, PrecisionOutOfCompetition.ExcludeGuests(rows, OutOfCompetitionSet.None));
        }

        [Fact]
        public void Finalistgransen_raknas_over_de_tavlande_inte_gasterna()
        {
            // 60 startande varav 1 gäst: gränsen är ceil(59/6) = 10 → max(10, 10) = 10; med gästen hade den varit ceil(60/6) = 10.
            // Välj antal där ceil ändras: 67 startande → 12, utan en gäst (66) → 11.
            var qualification = new PrecisionFinalsQualificationService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PrecisionFinalsQualificationService>.Instance);
            var withGuest = Enumerable.Range(1, 67).Select(i => R(i)).ToList();
            var outside = OutOfCompetitionSet.None.With(67, "C1");
            var withoutGuest = PrecisionOutOfCompetition.ExcludeGuests(withGuest, outside);
            Assert.Equal(12, qualification.CalculateQualificationCutoff(withGuest.Count));
            Assert.Equal(11, qualification.CalculateQualificationCutoff(withoutGuest.Count));
        }

        private static StartListConfiguration Cfg() => new()
        {
            Teams = new List<StartListTeam>
            {
                new() { TeamNumber = 1, Shooters = new List<StartListShooter> { new() { MemberId = 1, WeaponClass = "C1" }, new() { MemberId = 2, WeaponClass = "C1" } } },
                new() { TeamNumber = 2, Shooters = new List<StartListShooter> { new() { MemberId = 3, WeaponClass = "C1" } } },
            }
        };

        [Fact]
        public void ExcludeGuests_startlista_tar_bort_gasten_och_behaller_skjutlagsnumren()
        {
            var cfg = Cfg();
            var removed = PrecisionOutOfCompetition.ExcludeGuests(cfg, OutOfCompetitionSet.None.With(3, "C1"));
            Assert.Equal(1, removed);
            Assert.Single(cfg.Teams);                       // skjutlag 2 blev tomt och föll bort
            Assert.Equal(1, cfg.Teams[0].TeamNumber);       // numret behålls
            Assert.Equal(2, cfg.Teams[0].ShooterCount);
        }

        [Fact]
        public void ExcludeGuests_startlista_lamnar_en_lucka_efter_gasten()
        {
            var cfg = Cfg();
            PrecisionOutOfCompetition.ExcludeGuests(cfg, OutOfCompetitionSet.None.With(1, "C1"));
            Assert.Equal(new[] { 2 }, cfg.Teams.First(t => t.TeamNumber == 1).Shooters!.Select(s => s.MemberId).ToArray());
        }

        [Fact]
        public void ExcludeGuests_startlista_utan_gaster_ar_oforandrad()
        {
            var cfg = Cfg();
            Assert.Equal(0, PrecisionOutOfCompetition.ExcludeGuests(cfg, OutOfCompetitionSet.None));
            Assert.Equal(2, cfg.Teams.Count);
        }

        [Fact]
        public void Manuellt_tillagg_pa_final_vagras_for_gast()
        {
            var outside = OutOfCompetitionSet.None.With(2, "C1");
            Assert.NotNull(PrecisionOutOfCompetition.FinalsAddRefusal(outside, 2, "C1", "Anna"));
            Assert.Contains("Anna", PrecisionOutOfCompetition.FinalsAddRefusal(outside, 2, "C1", "Anna")!);
            Assert.Null(PrecisionOutOfCompetition.FinalsAddRefusal(outside, 3, "C1", "Bo"));
            Assert.Null(PrecisionOutOfCompetition.FinalsAddRefusal(outside, 2, "B1", "Anna"));   // tävlar i en annan klass
        }

        [Fact]
        public void Manuellt_tillagg_utan_klass_prövas_pa_medlemsniva()
        {
            var outside = OutOfCompetitionSet.None.With(2, "C1");
            Assert.NotNull(PrecisionOutOfCompetition.FinalsAddRefusal(outside, 2, null, "Anna"));
            Assert.NotNull(PrecisionOutOfCompetition.FinalsAddRefusal(outside, 2, "", "Anna"));
            Assert.Null(PrecisionOutOfCompetition.FinalsAddRefusal(outside, 9, null, "Bo"));
        }

        // ── Hederspriser och klassammanslagning: en gäst är inte med i tävlingen och räknas inte in ──
        [Fact]
        public void SplitByOutside_delar_upp_de_tavlande_och_gasterna()
        {
            var a = S(1); var guest = S(2); guest.OutOfCompetition = true; var c = S(3, "B1");
            var (competing, anyOutside) = PrecisionOutOfCompetition.SplitByOutside(new[] { G("C1", a, guest), G("B1", c) });
            Assert.Equal(new[] { 1, 3 }, competing.Select(s => s.MemberId).ToArray());
            Assert.True(anyOutside);
        }

        [Fact]
        public void SplitByOutside_utan_gaster_ar_oforandrad()
        {
            var (competing, anyOutside) = PrecisionOutOfCompetition.SplitByOutside(new[] { G("C1", S(1), S(2)) });
            Assert.Equal(2, competing.Count);
            Assert.False(anyOutside);
        }

        [Theory]
        [InlineData("Services/PrizeGivingService.cs", "PrecisionOutOfCompetition.SplitByOutside(classGroups)")]
        [InlineData("Controllers/CompetitionResultsController.cs", "PrecisionOutOfCompetition.ExcludeGuests(results, _outOfCompetition.Load(competition, analysisRule))")]
        [InlineData("Controllers/CompetitionResultsController.cs", "g.Shooters.Count(s => !s.OutOfCompetition)")]
        [InlineData("Controllers/CompetitionResultsController.cs", "cg.Shooters.Where(s => !s.OutOfCompetition)")]
        [InlineData("CompetitionTypes/Faltskytte/Controllers/FaltskytteController.cs", "filteredResults.Where(r => !outsideForMerge.Contains(r.MemberId, r.ShootingClass))")]
        [InlineData("CompetitionTypes/Common/SeriesCalculation/ScoreSources/PrecisionFamilySeriesScoreSource.cs", "OutOfCompetition = outside.Contains(g.Key.MemberId, g.Key.ShootingClass)")]
        [InlineData("Controllers/ResultReviewController.cs", "(bool?)s[\"OutOfCompetition\"] == true")]
        [InlineData("CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs", "outsideCount = outsideByGroup.TryGetValue(group, out var oc) ? oc : 0")]
        public void Hederspriser_och_sammanslagningsanalys_raknar_inte_gaster(string relativeFile, string call) =>
            AssertWired(relativeFile, call);

        // ── Textspärr: finalvägarna är kopplade till gästfiltret (kommentarsrader räknas inte) ──
        [Theory]
        [InlineData("CompetitionTypes/Precision/Services/PrecisionQualifyingResultsService.cs", "PrecisionOutOfCompetition.ExcludeGuests(results,")]
        [InlineData("CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs", "PrecisionOutOfCompetition.ExcludeGuests(qualConfig,")]
        [InlineData("CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs", "PrecisionOutOfCompetition.FinalsAddRefusal(")]
        [InlineData("CompetitionTypes/Precision/Services/PrecisionResultsBuilder.cs", "PrecisionOutOfCompetition.Mark(")]
        [InlineData("CompetitionTypes/Precision/Services/PrecisionResultsBuilder.cs", "PrecisionOutOfCompetition.AssignPlacements(")]
        public void Gastfiltret_ar_kopplat_pa_varje_finalvag(string relativeFile, string call) =>
            AssertWired(relativeFile, call);

        private static void AssertWired(string relativeFile, string call)
        {
            var path = System.IO.Path.Combine(SiteRoot(), relativeFile.Replace('/', System.IO.Path.DirectorySeparatorChar));
            var src = System.IO.File.ReadAllText(path);
            var i = 0;
            var found = false;
            while ((i = src.IndexOf(call, i, StringComparison.Ordinal)) >= 0)
            {
                var lineStart = src.LastIndexOf('\n', i) + 1;
                if (!src.Substring(lineStart, i - lineStart).Contains("//", StringComparison.Ordinal)) { found = true; break; }
                i += call.Length;
            }
            Assert.True(found, $"{relativeFile} anropar inte längre {call} (utanför en kommentar) — en gäst kunde då hamna i finalen.");
        }

        private static string SiteRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "") =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(thisFile))!, "HpskSite");

        // ── Competing (medaljkandidater, särskjutning, medaljantal) ───────────────
        [Fact]
        public void Competing_utesluter_gaster()
        {
            var a = S(1); var guest = S(2); guest.OutOfCompetition = true; var c = S(3);
            var ids = PrecisionOutOfCompetition.Competing(new[] { a, guest, c }, _ => true).Select(s => s.MemberId).ToList();
            Assert.Equal(new[] { 1, 3 }, ids);
        }

        [Fact]
        public void Competing_utesluter_vapengrupper_som_mästerskapet_inte_gäller()
        {
            var c1 = S(1, "C1"); var a1 = S(2, "A1");
            var ids = PrecisionOutOfCompetition.Competing(new[] { c1, a1 }, cls => cls.StartsWith("C", StringComparison.Ordinal))
                .Select(s => s.MemberId).ToList();
            Assert.Equal(new[] { 1 }, ids);
        }

        [Fact]
        public void Competing_ar_oforandrat_nar_ingen_ar_utanfor_och_alla_vapengrupper_galler()
        {
            var all = new[] { S(1), S(2), S(3) };
            Assert.Equal(3, PrecisionOutOfCompetition.Competing(all, _ => true).Count());
        }
    }
}
