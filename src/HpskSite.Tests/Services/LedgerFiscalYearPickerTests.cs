using System;
using System.Collections.Generic;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Vilket räkenskapsår arbetet sker i.
    ///
    /// <para><b>⚠️⚠️ FÖDD UR ETT FALSKT LARM I SKARP ANVÄNDNING.</b> Hallands krets lade upp
    /// räkenskapsåret 2025 i en sandlåda och möttes av <i>"Det finns inget räkenskapsår som
    /// omfattar 2026-09-22. Lägg upp året i ekonomiinställningarna först."</i> — trots att året
    /// fanns, var öppet och gick alldeles utmärkt att bokföra i. Beredskapsfrågan ställdes med
    /// <c>DateTime.Today</c> i stället för med året föreningen arbetade i.</para>
    ///
    /// <para><b>Att mata in ett PASSERAT år är normalfallet när någon provar oss</b> — man lägger
    /// in förra årets bokföring för att jämföra med den man redan har. Det är alltså inte ett
    /// kantfall utan den vanligaste första handlingen.</para>
    /// </summary>
    public class LedgerFiscalYearPickerTests
    {
        private static readonly DateTime Today = new(2026, 9, 22);

        private static LedgerFiscalYear Year(int year, string status = LedgerFiscalYearStatus.Open)
            => new()
            {
                Id = year,
                Year = year,
                StartDate = new DateTime(year, 1, 1),
                EndDate = new DateTime(year, 12, 31),
                Status = status
            };

        // ── Working ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Utan_ar_finns_inget_arbetsar()
            => Assert.Null(LedgerFiscalYearPicker.Working(new List<LedgerFiscalYear>(), Today));

        [Fact]
        public void Null_ar_ett_giltigt_svar_inte_en_krasch()
            => Assert.Null(LedgerFiscalYearPicker.Working(null, Today));

        [Fact]
        public void Aret_som_omfattar_i_dag_vinner()
        {
            var working = LedgerFiscalYearPicker.Working(new[] { Year(2025), Year(2026) }, Today);
            Assert.Equal(2026, working!.Year);
        }

        /// <summary>⚠️ Rapporten från Hallands krets, ordagrant som data.</summary>
        [Fact]
        public void Ett_ensamt_passerat_ar_ar_arbetsaret()
        {
            var working = LedgerFiscalYearPicker.Working(new[] { Year(2025) }, Today);
            Assert.Equal(2025, working!.Year);
        }

        [Fact]
        public void Senaste_oppna_aret_valjs_framfor_ett_aldre()
        {
            var working = LedgerFiscalYearPicker.Working(new[] { Year(2023), Year(2025) }, Today);
            Assert.Equal(2025, working!.Year);
        }

        /// <summary>
        /// ⚠️ Ett FASTSTÄLLT år som omfattar i dag vinner ändå — svaret ska bli "året är
        /// fastställt", inte "året finns inte". Två helt olika besked till kassören.
        /// </summary>
        [Fact]
        public void Ett_faststallt_ar_som_omfattar_i_dag_vinner_anda()
        {
            var working = LedgerFiscalYearPicker.Working(
                new[] { Year(2026, LedgerFiscalYearStatus.Established), Year(2024) }, Today);

            Assert.Equal(2026, working!.Year);
        }

        /// <summary>Bara fastställda år kvar: spärren ska ändå få något att uttala sig om.</summary>
        [Fact]
        public void Bara_faststallda_ar_ger_det_senaste()
        {
            var working = LedgerFiscalYearPicker.Working(
                new[] { Year(2024, LedgerFiscalYearStatus.Established),
                        Year(2025, LedgerFiscalYearStatus.Established) }, Today);

            Assert.Equal(2025, working!.Year);
        }

        [Fact]
        public void Brutet_rakenskapsar_omfattar_i_dag()
        {
            var broken = new LedgerFiscalYear
            {
                Year = 2026,
                StartDate = new DateTime(2026, 7, 1),
                EndDate = new DateTime(2027, 6, 30),
                Status = LedgerFiscalYearStatus.Open
            };

            Assert.Equal(2026, LedgerFiscalYearPicker.Working(new[] { broken }, Today)!.Year);
        }

        // ── ProbeDate ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ Utan räkenskapsår svarar den I DAG, med flit: då ÄR "lägg upp året" rätt besked,
        /// och det kommer ur spärren själv.
        /// </summary>
        [Fact]
        public void Utan_ar_provas_dagens_datum()
            => Assert.Equal(Today, LedgerFiscalYearPicker.ProbeDate(new List<LedgerFiscalYear>(), Today));

        [Fact]
        public void Ligger_i_dag_inne_i_aret_provas_i_dag()
            => Assert.Equal(Today, LedgerFiscalYearPicker.ProbeDate(new[] { Year(2026) }, Today));

        /// <summary>⚠️ Kärnan i buggen: 2025 ska prövas med ett datum i 2025.</summary>
        [Fact]
        public void Ett_passerat_ar_provas_med_ett_datum_inuti_aret()
        {
            var probe = LedgerFiscalYearPicker.ProbeDate(new[] { Year(2025) }, Today);

            Assert.Equal(new DateTime(2025, 12, 31), probe);
            Assert.InRange(probe, new DateTime(2025, 1, 1), new DateTime(2025, 12, 31));
        }

        [Fact]
        public void Ett_framtida_ar_provas_med_sin_forsta_dag()
            => Assert.Equal(new DateTime(2027, 1, 1),
                            LedgerFiscalYearPicker.ProbeDate(new[] { Year(2027) }, Today));

        // ── DateNote ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void Ingen_upplysning_nar_i_dag_ryms()
            => Assert.Null(LedgerFiscalYearPicker.DateNote(new[] { Year(2026) }, Today));

        [Fact]
        public void Ingen_upplysning_utan_ar()
            => Assert.Null(LedgerFiscalYearPicker.DateNote(new List<LedgerFiscalYear>(), Today));

        /// <summary>
        /// ⚠️ Upplysningen måste NAMNGE året och intervallet. "Datumet är fel" utan att säga vilket
        /// spann som gäller lämnar kassören att gissa.
        /// </summary>
        [Fact]
        public void Upplysningen_namnger_aret_och_intervallet()
        {
            var note = LedgerFiscalYearPicker.DateNote(new[] { Year(2025) }, Today);

            Assert.NotNull(note);
            Assert.Contains("2025", note);
            Assert.Contains("2025-01-01", note);
            Assert.Contains("2025-12-31", note);
        }

        /// <summary>
        /// ⚠️ Den är en UPPLYSNING, inte en spärr — den får aldrig läsa som att bokföringen är
        /// stängd. Det var precis den sammanblandningen som gav det falska larmet.
        /// </summary>
        [Fact]
        public void Upplysningen_sager_inte_att_aret_saknas()
        {
            var note = LedgerFiscalYearPicker.DateNote(new[] { Year(2025) }, Today)!;

            Assert.DoesNotContain("finns inget räkenskapsår", note);
        }

        // ── Clamp ────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("2025-06-15", "2025-06-15")]   // inuti
        [InlineData("2024-12-31", "2025-01-01")]   // före
        [InlineData("2026-01-01", "2025-12-31")]   // efter
        public void Clamp_hallar_datumet_inuti_aret(string input, string expected)
            => Assert.Equal(DateTime.Parse(expected),
                            LedgerFiscalYearPicker.Clamp(DateTime.Parse(input), Year(2025)));

        /// <summary>Kanterna räknas MED — 1 januari och 31 december ligger i året.</summary>
        [Fact]
        public void Kanterna_ligger_inuti_aret()
        {
            Assert.Equal(new DateTime(2025, 1, 1),
                LedgerFiscalYearPicker.Clamp(new DateTime(2025, 1, 1), Year(2025)));
            Assert.Equal(new DateTime(2025, 12, 31),
                LedgerFiscalYearPicker.Clamp(new DateTime(2025, 12, 31), Year(2025)));
        }
    }
}
