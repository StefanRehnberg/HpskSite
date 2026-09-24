using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Momsdeklarationens rutor ur bokföringen.
    ///
    /// <para><b>⚠️ Ett fel här felar inte — det ger ett trovärdigt fel belopp på en deklaration.</b>
    /// Därför varje form för sig: försäljning, inköp, återbetalning, rättelse, och kontrollen mot
    /// momskontona.</para>
    /// </summary>
    public class LedgerVatReturnTests
    {
        private const int Out = 2610, In = 2640;

        private static LedgerJournalEntryLine L(int acc, decimal debit, decimal credit, decimal? rate = null, decimal? vat = null)
            => new() { AccountNumber = acc, Debit = debit, Credit = credit, VatRate = rate, VatAmount = vat };

        // Kioskförsäljning 112 kr inkl 12 %: bank 112 D, kiosk 100 K (12 %, moms 12), 2610 12 K.
        private static List<LedgerJournalEntryLine> KioskSale() => new()
        {
            L(1930, 112, 0), L(3040, 0, 100, 12m, 12m), L(Out, 0, 12)
        };

        // Inköp 125 kr inkl 25 %: tavlor 100 D (25 %, moms 25), 2640 25 D, bank 125 K.
        private static List<LedgerJournalEntryLine> Purchase() => new()
        {
            L(4020, 100, 0, 25m, 25m), L(In, 25, 0), L(1930, 0, 125)
        };

        private static LedgerVatReturn Run(params (List<LedgerJournalEntryLine> Lines, List<LedgerJournalEntryLine>? Original)[] entries)
        {
            var vat = entries.SelectMany(e => LedgerVatReturnCalculator.LinesOf(e.Lines, e.Original, Out, In)).ToList();
            var all = entries.SelectMany(e => e.Lines).ToList();
            var ledgerOut = all.Where(l => l.AccountNumber == Out).Sum(l => l.Credit - l.Debit);
            var ledgerIn = all.Where(l => l.AccountNumber == In).Sum(l => l.Debit - l.Credit);
            return LedgerVatReturnCalculator.Compute(DateTime.Today, DateTime.Today, vat, ledgerOut, ledgerIn, entries.Length, Out, In);
        }

        [Fact]
        public void Forsaljning_ger_ruta_05_och_11()
        {
            var r = Run((KioskSale(), null));
            r.Box05.Should().Be(100m);
            r.Box11.Should().Be(12m);
            r.Box10.Should().Be(0m);
            r.Box49.Should().Be(12m);
            r.Reconciles.Should().BeTrue();
        }

        [Fact]
        public void Inkop_ger_ruta_48_och_drar_av()
        {
            var r = Run((KioskSale(), null), (Purchase(), null));
            r.Box48.Should().Be(25m);
            r.Box05.Should().Be(100m, "ett inköp är ingen försäljning");
            r.Box49.Should().Be(-13m, "12 att betala minus 25 att dra av = 13 tillbaka");
            r.Reconciles.Should().BeTrue();
        }

        /// <summary>
        /// ⚠️⚠️ En återbetald försäljning MINSKAR ruta 05 och 11 — den blir aldrig ingående moms.
        /// </summary>
        [Fact]
        public void Aterbetald_forsaljning_minskar_ruta_05_och_11()
        {
            var refund = new List<LedgerJournalEntryLine> { L(3040, 100, 0, 12m, 12m), L(Out, 12, 0), L(1930, 0, 112) };
            var r = Run((KioskSale(), null), (KioskSale(), null), (refund, null));

            r.Box05.Should().Be(100m);
            r.Box11.Should().Be(12m);
            r.Box48.Should().Be(0m);
            r.Reconciles.Should().BeTrue();
        }

        /// <summary>
        /// ⚠️⚠️ En rättelse bär ingen moms på källraden (nollad med flit). Den räknas som originalets
        /// momsrader med omvänt tecken — annars står den tillbakatagna försäljningen kvar i ruta 05
        /// medan momsen försvunnit från kontot, och kontrollen faller.
        /// </summary>
        [Fact]
        public void Rattelse_vander_originalets_rutor()
        {
            var original = KioskSale();
            // Rättelsen: samma rader, debet och kredit bytta, INGEN VatRate/VatAmount.
            var correction = original.Select(l => L(l.AccountNumber, l.Credit, l.Debit)).ToList();

            var r = Run((correction, original));

            r.Box05.Should().Be(-100m);
            r.Box11.Should().Be(-12m);
            r.Reconciles.Should().BeTrue();
        }

        [Fact]
        public void Rattelse_utan_originalet_hade_brutit_kontrollen()
        {
            // Kontrollprov: om rättelsen INTE kopplas till originalet syns felet som en differens.
            var original = KioskSale();
            var correction = original.Select(l => L(l.AccountNumber, l.Credit, l.Debit)).ToList();

            var r = Run((correction, null));
            r.Reconciles.Should().BeFalse();
            r.OutputDifference.Should().Be(-12m);
        }

        [Fact]
        public void Blandade_satser_hamnar_i_var_sin_ruta()
        {
            var sponsor = new List<LedgerJournalEntryLine> { L(1930, 1250, 0), L(3050, 0, 1000, 25m, 250m), L(Out, 0, 250) };
            var r = Run((KioskSale(), null), (sponsor, null));

            r.Box10.Should().Be(250m);
            r.Box11.Should().Be(12m);
            r.Box05.Should().Be(1100m);
            r.Rates.Select(x => x.Rate).Should().Equal(25m, 12m);
        }

        /// <summary>
        /// ⚠️ En post direkt på momskontot (SIE-import, handbokförd) kommer inte från en momsrad —
        /// rutorna kan inte veta vilken sats den hör till. Den SÄGS som en differens.
        /// </summary>
        [Fact]
        public void Post_direkt_pa_momskontot_syns_som_differens()
        {
            var direct = new List<LedgerJournalEntryLine> { L(1930, 50, 0), L(Out, 0, 50) };
            var r = Run((KioskSale(), null), (direct, null));

            r.Reconciles.Should().BeFalse();
            r.OutputDifference.Should().Be(50m);
            r.Box11.Should().Be(12m, "rutorna räknar bara det bokföringen själv räknat moms på");
        }

        [Theory]
        [InlineData("12.99", "12")]
        [InlineData("-13.40", "-13")]
        [InlineData("100.00", "100")]
        public void Rutan_skrivs_i_hela_kronor_orena_stryks(string exact, string whole)
            => LedgerVatReturn.WholeKronor(decimal.Parse(exact, System.Globalization.CultureInfo.InvariantCulture))
                .Should().Be(decimal.Parse(whole, System.Globalization.CultureInfo.InvariantCulture));
    }
}
