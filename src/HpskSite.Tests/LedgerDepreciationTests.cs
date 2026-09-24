using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Avskrivningsreglerna.
    ///
    /// <para><b>⚠️ Rena funktioner, därför enhetstest.</b> En regel som bara går att pröva genom
    /// hela stacken blir inte prövad — och den här räknar fram belopp som hamnar i en förenings
    /// bokslut.</para>
    /// </summary>
    public class LedgerDepreciationTests
    {
        private static LedgerAsset Asset(
            string inUse = "2026-01-01", decimal amount = 60000m, int years = 5,
            decimal residual = 0m, string? disposed = null) => new()
            {
                Name = "Tavelställ",
                InUseDate = DateTime.Parse(inUse),
                AcquisitionAmount = amount,
                UsefulLifeYears = years,
                ResidualValue = residual,
                DisposedDate = disposed is null ? null : DateTime.Parse(disposed)
            };

        private static (DateTime From, DateTime To) Year(int y) =>
            (new DateTime(y, 1, 1), new DateTime(y, 12, 31));

        // ── Grundfallet ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Helt_ar_ger_rak_avskrivning()
        {
            var (f, t) = Year(2026);
            LedgerDepreciation.ForPeriod(Asset(), f, t).Should().Be(12000m);
        }

        [Fact]
        public void Alla_ar_summerar_till_anskaffningsvardet()
        {
            var a = Asset();

            var total = Enumerable.Range(2026, 5)
                .Sum(y => { var (f, t) = Year(y); return LedgerDepreciation.ForPeriod(a, f, t); });

            // ⚠️ Summan måste bli EXAKT. Att den "ungefär" stämmer betyder ören kvar på kontot
            //    i evighet, och en balansräkning som inte går att nolla.
            total.Should().Be(60000m);
        }

        [Fact]
        public void Efter_perioden_skrivs_ingenting_av()
        {
            var (f, t) = Year(2031);
            LedgerDepreciation.ForPeriod(Asset(), f, t).Should().Be(0m);
        }

        [Fact]
        public void Fore_ibruktagandet_skrivs_ingenting_av()
        {
            var (f, t) = Year(2025);
            LedgerDepreciation.ForPeriod(Asset(), f, t).Should().Be(0m);
        }

        // ── Månadsproportioneringen ──────────────────────────────────────────────────────

        [Fact]
        public void Kop_i_november_skrivs_av_tva_manader_forsta_aret()
        {
            // ⚠️⚠️ HELA POÄNGEN med månadsproportionering. Ett helt år här hade kostnadsfört
            //    12000 kr för en tillgång föreningen ägt i två månader.
            var a = Asset(inUse: "2026-11-15");
            var (f, t) = Year(2026);

            LedgerDepreciation.ForPeriod(a, f, t).Should().Be(2000m);
        }

        [Fact]
        public void Manaden_for_ibruktagandet_raknas_med()
        {
            var a = Asset(inUse: "2026-12-31");
            var (f, t) = Year(2026);

            // December räknas, trots att bara en dag återstår av den.
            LedgerDepreciation.ForPeriod(a, f, t).Should().Be(1000m);
        }

        [Fact]
        public void Sista_aret_tar_resten_och_inte_mer()
        {
            var a = Asset(inUse: "2026-11-01");

            var total = Enumerable.Range(2026, 6)
                .Sum(y => { var (f, t) = Year(y); return LedgerDepreciation.ForPeriod(a, f, t); });

            total.Should().Be(60000m);
        }

        // ── Brutet räkenskapsår ──────────────────────────────────────────────────────────

        [Fact]
        public void Brutet_rakenskapsar_far_sina_egna_manader()
        {
            // ⚠️ Perioden är RÄKENSKAPSÅRETS, inte kalenderårets. Ett halvår ger halva beloppet.
            var a = Asset();

            LedgerDepreciation
                .ForPeriod(a, new DateTime(2026, 7, 1), new DateTime(2026, 12, 31))
                .Should().Be(6000m);
        }

        // ── Restvärde ────────────────────────────────────────────────────────────────────

        [Fact]
        public void Restvardet_skrivs_aldrig_av()
        {
            var a = Asset(amount: 60000m, residual: 10000m);

            var total = Enumerable.Range(2026, 5)
                .Sum(y => { var (f, t) = Year(y); return LedgerDepreciation.ForPeriod(a, f, t); });

            total.Should().Be(50000m);
            LedgerDepreciation.BookValue(a, new DateTime(2030, 12, 31)).Should().Be(10000m);
        }

        // ── Utrangering ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Utrangering_stoppar_avskrivningen()
        {
            // ⚠️ Att fortsätta skriva av något föreningen gjort sig av med hade byggt upp en
            //    kostnad för en tillgång som inte finns.
            var a = Asset(disposed: "2026-06-20");
            var (f, t) = Year(2026);

            LedgerDepreciation.ForPeriod(a, f, t).Should().Be(6000m);
        }

        [Fact]
        public void Efter_utrangering_skrivs_ingenting_av()
        {
            var a = Asset(disposed: "2026-06-20");
            var (f, t) = Year(2027);

            LedgerDepreciation.ForPeriod(a, f, t).Should().Be(0m);
        }

        // ── Bokfört värde ────────────────────────────────────────────────────────────────

        [Fact]
        public void Bokfort_varde_sjunker_mot_restvardet()
        {
            var a = Asset();

            LedgerDepreciation.BookValue(a, new DateTime(2026, 12, 31)).Should().Be(48000m);
            LedgerDepreciation.BookValue(a, new DateTime(2028, 12, 31)).Should().Be(24000m);
            LedgerDepreciation.BookValue(a, new DateTime(2030, 12, 31)).Should().Be(0m);
        }

        [Fact]
        public void Bokfort_varde_gar_aldrig_under_restvardet()
        {
            var a = Asset();

            // Långt efter periodens slut ska värdet ligga kvar på noll, aldrig bli negativt.
            LedgerDepreciation.BookValue(a, new DateTime(2040, 12, 31)).Should().Be(0m);
        }

        // ── Gränsfall som annars blir division med noll ───────────────────────────────────

        [Fact]
        public void Nyttjandeperiod_noll_ger_ingen_avskrivning()
        {
            var (f, t) = Year(2026);
            LedgerDepreciation.ForPeriod(Asset(years: 0), f, t).Should().Be(0m);
        }

        [Fact]
        public void Restvarde_over_anskaffningsvardet_ger_ingen_avskrivning()
        {
            var (f, t) = Year(2026);
            LedgerDepreciation.ForPeriod(Asset(amount: 5000m, residual: 9000m), f, t).Should().Be(0m);
        }

        [Fact]
        public void Udda_belopp_summerar_anda_exakt()
        {
            // 10 000 / 36 månader går inte jämnt ut. Summan måste ändå bli precis 10 000.
            var a = Asset(amount: 10000m, years: 3);

            var total = Enumerable.Range(2026, 4)
                .Sum(y => { var (f, t) = Year(y); return LedgerDepreciation.ForPeriod(a, f, t); });

            total.Should().Be(10000m);
        }

        // ── Minuskontot (ändrat 2026-09-24) ─────────────────────────────────────────────

        [Theory]
        [InlineData(1220, 1229)]
        [InlineData(1221, 1229)]   // klubbvapen är ett underkonto till inventarierna
        [InlineData(1110, 1119)]
        [InlineData(1150, 1159)]
        public void Minuskontot_foreslas_som_tillgangskontot_med_nio_sist(int asset, int expected)
            => LedgerDepreciation.AccumulatedAccountFor(asset).Should().Be(expected);

        /// <summary>
        /// ⚠️ Varje tillgångskonto i mallen måste ha sitt minuskonto I mallen. Annars föreslår
        /// formuläret ett konto som inte finns, och `EnsureAccount` vägrar skapa det.
        /// </summary>
        [Fact]
        public void Varje_anlaggningskonto_i_mallen_har_sitt_minuskonto_i_mallen()
        {
            var numbers = LedgerChartTemplate.Accounts.Select(a => a.Number).ToHashSet();

            var missing = LedgerChartTemplate.Accounts
                .Where(a => a.Number is >= 1100 and < 1300 && a.Number % 10 != 9)
                .Where(a => !numbers.Contains(LedgerDepreciation.AccumulatedAccountFor(a.Number)))
                .Select(a => a.Number)
                .ToList();

            missing.Should().BeEmpty();
        }

        [Fact]
        public void Forlustkontot_for_utrangering_finns_i_mallen_och_ar_en_kostnad()
        {
            LedgerChartTemplate.Find(LedgerChartTemplate.DisposalLossAccount).Should().NotBeNull();
            LedgerAccountClass.Of(LedgerChartTemplate.DisposalLossAccount).Should().BeInRange(4, 7);
        }

        // ── Utrangeringen ───────────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️⚠️ Utrangeringen måste gå jämnt upp: det ackumulerade + det bokförda värdet =
        /// hela anskaffningsvärdet. Annars står något kvar på 1220 eller 1229 för en tillgång
        /// föreningen inte har.
        /// </summary>
        [Fact]
        public void Utrangering_mitt_i_planen_delar_anskaffningsvardet_jamnt()
        {
            // 60 000 över 5 år = 1 000/mån. I bruk jan 2026, utrangerad juni 2027 = 18 månader.
            var (acc, book) = LedgerDepreciation.Disposal(Asset(), new DateTime(2027, 6, 15));

            acc.Should().Be(18000m);
            book.Should().Be(42000m);
            (acc + book).Should().Be(60000m);
        }

        [Fact]
        public void Fardigavskriven_utrangering_har_inget_bokfort_varde()
        {
            var (acc, book) = LedgerDepreciation.Disposal(Asset(), new DateTime(2035, 1, 1));

            acc.Should().Be(60000m);
            book.Should().Be(0m);
        }

        [Fact]
        public void Restvardet_ar_kvar_som_bokfort_varde_vid_utrangering()
        {
            // Restvärdet skrivs aldrig av — det är det som återstår när planen är slut.
            var (acc, book) = LedgerDepreciation.Disposal(
                Asset(amount: 60000m, residual: 6000m), new DateTime(2035, 1, 1));

            acc.Should().Be(54000m);
            book.Should().Be(6000m);
        }

        [Fact]
        public void Utrangeringens_kopia_ror_inte_originalet()
        {
            var a = Asset();
            LedgerDepreciation.Disposal(a, new DateTime(2027, 6, 15));
            a.DisposedDate.Should().BeNull();
        }

        /// <summary>
        /// Utrangeringsårets återstående avskrivning räknas på den utrangerade kopian: planen
        /// kapas vid utrangeringen, så juni-utrangering ger sex månader, inte tolv.
        /// </summary>
        [Fact]
        public void Utrangeringsaret_skrivs_av_fram_till_utrangeringen()
        {
            var copy = LedgerDepreciation.CopyDisposedOn(Asset(), new DateTime(2027, 6, 15));
            var (f, t) = Year(2027);
            LedgerDepreciation.ForPeriod(copy, f, t).Should().Be(6000m);
        }
    }
}
