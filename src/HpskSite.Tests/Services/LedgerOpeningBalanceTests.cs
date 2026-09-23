using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Ingående balanser och tidigare års resultat — de två sakerna som avgör om balansräkningen
    /// går ihop för en förening som funnits längre än sitt första år här.
    ///
    /// <para><b>⚠️ Testerna anropar den riktiga koden</b> (<c>BuildLines</c> är
    /// <c>internal static</c>, <c>BalanceDifference</c> är en ren egenskap).</para>
    /// </summary>
    public class LedgerOpeningBalanceTests
    {
        private static decimal Debit(List<LedgerPostingLine> l) => l.Sum(x => x.Debit);
        private static decimal Credit(List<LedgerPostingLine> l) => l.Sum(x => x.Credit);

        [Fact]
        public void Tillgangar_i_debet_och_eget_kapital_raknas_ut()
        {
            var lines = LedgerOpeningBalanceService.BuildLines(new[]
            {
                new OpeningBalanceLine { AccountNumber = 1930, Amount = 48312m },
                new OpeningBalanceLine { AccountNumber = 1910, Amount = 1500m },
            }, 2060);

            Assert.Equal(49812m, Debit(lines));
            Assert.Equal(Debit(lines), Credit(lines));
            var eq = Assert.Single(lines, x => x.AccountNumber == 2060);
            Assert.Equal(49812m, eq.Credit);
        }

        [Fact]
        public void En_skuld_minskar_eget_kapital()
        {
            var lines = LedgerOpeningBalanceService.BuildLines(new[]
            {
                new OpeningBalanceLine { AccountNumber = 1930, Amount = 10000m },
                new OpeningBalanceLine { AccountNumber = 2440, Amount = 2500m },
            }, 2060);

            Assert.Equal(2500m, lines.Single(x => x.AccountNumber == 2440).Credit);
            Assert.Equal(7500m, lines.Single(x => x.AccountNumber == 2060).Credit);
            Assert.Equal(Debit(lines), Credit(lines));
        }

        [Fact]
        public void Ett_overtrasserat_konto_byter_sida()
        {
            var lines = LedgerOpeningBalanceService.BuildLines(new[]
            {
                new OpeningBalanceLine { AccountNumber = 1930, Amount = -300m },
            }, 2060);

            Assert.Equal(300m, lines.Single(x => x.AccountNumber == 1930).Credit);
            // Mer skuld än tillgångar: eget kapital blir negativt, alltså debet.
            Assert.Equal(300m, lines.Single(x => x.AccountNumber == 2060).Debit);
        }

        [Fact]
        public void Nollrader_bokfors_inte_och_inget_eget_kapital_utan_belopp()
        {
            var lines = LedgerOpeningBalanceService.BuildLines(new[]
            {
                new OpeningBalanceLine { AccountNumber = 1930, Amount = 0m },
            }, 2060);

            Assert.Empty(lines);
        }

        [Fact]
        public void Balansrakningen_gar_ihop_ar_tva_med_tidigare_ars_resultat()
        {
            // År 1 gav 5 000 kr i överskott. Pengarna står på kontot, men inget har bokförts mot
            // eget kapital — liggaren gör ingen årsskiftesöverföring.
            var s = new LedgerFinancialStatements { PriorResult = 5000m };
            s.Assets.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 1930, Amount = 55000m });
            s.EquityAndLiabilities.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 2060, Amount = 50000m });

            Assert.Equal(0m, s.BalanceDifference);
            Assert.True(s.Balances);
        }

        [Fact]
        public void Utan_tidigare_ars_resultat_ar_differensen_exakt_forra_arets_overskott()
        {
            // Kontrollprov: samma siffror utan PriorResult ger den differens som låste år två.
            var s = new LedgerFinancialStatements();
            s.Assets.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 1930, Amount = 55000m });
            s.EquityAndLiabilities.Add(new LedgerFinancialStatements.StatementRow { AccountNumber = 2060, Amount = 50000m });

            Assert.Equal(5000m, s.BalanceDifference);
            Assert.False(s.Balances);
        }
    }
}
