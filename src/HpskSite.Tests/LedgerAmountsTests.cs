using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Beloppsräkningen är det enda i bokföringen som RÄKNAR, och därmed det enda som kan räkna
    /// fel. Den ligger som ren funktion just för att kunna prövas utan att skriva verifikationer.
    ///
    /// <para>⚠️ Ett fel här syns inte som ett fel: verifikationen går ihop, kvittot ser rätt ut, och
    /// felet upptäcks först i en momsdeklaration eller av en revisor. Därför prövas talet och dess
    /// följder som ett par — att delarna summerar till bruttot är lika viktigt som att momsen är
    /// rätt.</para>
    /// </summary>
    public class LedgerAmountsTests
    {
        // ── Momsdelningen ───────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(250, 25, 200.00, 50.00)]
        [InlineData(125, 25, 100.00, 25.00)]
        [InlineData(112, 12, 100.00, 12.00)]
        [InlineData(106, 6, 100.00, 6.00)]
        public void SplitGross_delar_ett_jamnt_belopp_ratt(
            decimal gross, decimal rate, decimal expectedNet, decimal expectedVat)
        {
            var (net, vat) = LedgerAmounts.SplitGross(gross, rate);

            Assert.Equal(expectedNet, net);
            Assert.Equal(expectedVat, vat);
        }

        [Fact]
        public void SplitGross_behandlar_beloppet_som_BRUTTO_inte_netto()
        {
            // Skytten swishade 250 kr. Det är summan på kontoutdraget, och den kan inte ändras av
            // hur vi väljer att bokföra. Tolkades den som netto skulle verifikationen säga 312:50
            // och avstämningen mot banken vara omöjlig.
            var (net, vat) = LedgerAmounts.SplitGross(250m, 25m);

            Assert.Equal(250m, net + vat);
            Assert.True(net < 250m);
        }

        [Theory]
        [InlineData(100, 25)]
        [InlineData(33.33, 25)]
        [InlineData(0.01, 25)]
        [InlineData(999.99, 6)]
        [InlineData(1, 12)]
        public void SplitGross_delarna_summerar_alltid_exakt_till_bruttot(decimal gross, decimal rate)
        {
            // Räknades netto och moms var för sig kunde de skilja ett öre från det som betalades.
            var (net, vat) = LedgerAmounts.SplitGross(gross, rate);

            Assert.Equal(gross, net + vat);
        }

        [Fact]
        public void SplitGross_avrundar_momsen_till_oren()
        {
            var (_, vat) = LedgerAmounts.SplitGross(100m, 25m);

            Assert.Equal(20.00m, vat);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void SplitGross_utan_momssats_lamnar_beloppet_orort(decimal rate)
        {
            var (net, vat) = LedgerAmounts.SplitGross(250m, rate);

            Assert.Equal(250m, net);
            Assert.Equal(0m, vat);
        }

        // ── Balansen ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Imbalance_ar_noll_for_en_balanserad_verifikation()
        {
            var lines = Lines((250m, 0m), (0m, 250m));

            Assert.Equal(0m, LedgerAmounts.Imbalance(lines));
        }

        [Fact]
        public void Imbalance_ar_positiv_nar_debet_overstiger_kredit()
        {
            var lines = Lines((250m, 0m), (0m, 200m));

            Assert.Equal(50m, LedgerAmounts.Imbalance(lines));
        }

        [Fact]
        public void Imbalance_hanterar_en_momsdelad_post()
        {
            // Bank 250 debet, intäkt 200 kredit, utgående moms 50 kredit.
            var lines = Lines((250m, 0m), (0m, 200m), (0m, 50m));

            Assert.Equal(0m, LedgerAmounts.Imbalance(lines));
        }

        // ── Avrundningen ────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(0.01)]
        [InlineData(-0.01)]
        [InlineData(0.02)]
        [InlineData(-0.02)]
        public void IsRoundable_tillater_oresdifferenser(decimal imbalance)
        {
            Assert.True(LedgerAmounts.IsRoundable(imbalance));
        }

        [Theory]
        [InlineData(0.03)]
        [InlineData(1)]
        [InlineData(-50)]
        [InlineData(250)]
        public void IsRoundable_avvisar_allt_storre_an_oren(decimal imbalance)
        {
            // ⚠️ En krona kommer ur ett räknefel eller en felskrivning. Göms den i en
            // avrundningsrad är felet borta ur synhåll men kvar i böckerna.
            Assert.False(LedgerAmounts.IsRoundable(imbalance));
        }

        [Fact]
        public void IsRoundable_ar_falsk_for_en_balanserad_verifikation()
        {
            // Noll ska inte ge en avrundningsrad på noll kronor.
            Assert.False(LedgerAmounts.IsRoundable(0m));
        }

        // ── Momsens riktning ────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(3010)]
        [InlineData(3020)]
        [InlineData(3040)]
        [InlineData(3890)]
        public void IsOutgoingVatAccount_ar_sann_for_intaktskonton(int account)
        {
            Assert.True(LedgerAmounts.IsOutgoingVatAccount(account));
        }

        [Theory]
        [InlineData(1930)]
        [InlineData(2440)]
        [InlineData(4010)]
        [InlineData(6980)]
        [InlineData(8410)]
        public void IsOutgoingVatAccount_ar_falsk_for_allt_annat(int account)
        {
            Assert.False(LedgerAmounts.IsOutgoingVatAccount(account));
        }

        // ── Formen på mallen ────────────────────────────────────────────────────────────────

        [Fact]
        public void Varje_kontoroll_har_ett_forval_i_mallen()
        {
            // ⚠️ En roll utan konto är en betalning som inte går att bokföra. Det ska upptäckas
            // här och inte av en kassör mitt i en tävlingsdag.
            Assert.Empty(LedgerChartTemplate.RolesWithoutDefault());
        }

        [Fact]
        public void Varje_forvalskonto_finns_i_mallens_kontoplan()
        {
            var numbers = LedgerChartTemplate.Accounts.Select(a => a.Number).ToHashSet();
            var missing = LedgerChartTemplate.RoleDefaults
                .Where(kv => !numbers.Contains(kv.Value))
                .Select(kv => $"{kv.Key} -> {kv.Value}")
                .ToList();

            Assert.Empty(missing);
        }

        [Fact]
        public void Mallens_kontonummer_ar_unika()
        {
            var duplicates = LedgerChartTemplate.Accounts
                .GroupBy(a => a.Number)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicates);
        }

        private static List<LedgerJournalEntryLine> Lines(params (decimal Debit, decimal Credit)[] rows)
            => rows.Select(r => new LedgerJournalEntryLine { Debit = r.Debit, Credit = r.Credit })
                   .ToList();
    }
}
