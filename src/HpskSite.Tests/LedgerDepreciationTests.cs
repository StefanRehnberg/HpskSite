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
    }
}
