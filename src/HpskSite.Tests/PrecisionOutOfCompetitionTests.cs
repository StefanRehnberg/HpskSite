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
