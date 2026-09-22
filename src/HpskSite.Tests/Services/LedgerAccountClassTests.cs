using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ Teckenregeln fanns i tre SQL-frågor innan den fick ett hem. Ett minustecken åt fel
    /// håll i en handling som går till årsmötet är den sortens fel som ingen upptäcker förrän
    /// revisorn gör det.
    /// </summary>
    public class LedgerAccountClassTests
    {
        [Theory]
        [InlineData(1930, 1)]
        [InlineData(2010, 2)]
        [InlineData(3010, 3)]
        [InlineData(8410, 8)]
        public void Klassen_ar_forsta_siffran(int account, int expected)
            => LedgerAccountClass.Of(account).Should().Be(expected);

        [Theory]
        [InlineData(1930, true)]
        [InlineData(2350, true)]
        [InlineData(3010, false)]
        [InlineData(8310, false)]
        public void Balanskonton_ar_klass_1_och_2(int account, bool expected)
            => LedgerAccountClass.IsBalance(account).Should().Be(expected);

        [Theory]
        [InlineData(3010, true)]
        [InlineData(4010, true)]
        [InlineData(7510, true)]
        [InlineData(8410, true)]
        [InlineData(1930, false)]
        public void Resultatkonton_ar_klass_3_till_8(int account, bool expected)
            => LedgerAccountClass.IsResult(account).Should().Be(expected);

        /// <summary>
        /// ⚠️ Klass 8 räknas som intäkt-riktad, precis som 3 — det är vad budgeten redan gör.
        /// Ändras det måste budgeten ändras i samma andetag, annars visar Rapport och Bokslut
        /// olika resultat för samma år.
        /// </summary>
        [Theory]
        [InlineData(3010, true)]
        [InlineData(8310, true)]
        [InlineData(4010, false)]
        [InlineData(7010, false)]
        public void Klass_3_och_8_ar_intaktsriktade(int account, bool expected)
            => LedgerAccountClass.IsRevenueDirected(account).Should().Be(expected);

        // ── Egen riktning ───────────────────────────────────────────────────────────────────

        /// <summary>En intäkt på 146 000 ska bli +146000, inte −146000.</summary>
        [Fact]
        public void Intakt_ar_positiv_i_egen_riktning()
            => LedgerAccountClass.InOwnDirection(3010, debit: 0, credit: 146000)
                .Should().Be(146000);

        /// <summary>Och en kostnad på 95 000 ska också bli +95000 — så de går att jämföra.</summary>
        [Fact]
        public void Kostnad_ar_positiv_i_egen_riktning()
            => LedgerAccountClass.InOwnDirection(4010, debit: 95000, credit: 0)
                .Should().Be(95000);

        /// <summary>En kreditering av en intäkt drar ner den.</summary>
        [Fact]
        public void Rattelse_drar_ner()
            => LedgerAccountClass.InOwnDirection(3010, debit: 500, credit: 146000)
                .Should().Be(145500);

        // ── Balansräkningens riktning ───────────────────────────────────────────────────────

        [Fact]
        public void Tillgang_ar_debetpositiv()
            => LedgerAccountClass.BalanceAmount(1930, debit: 12500, credit: 0).Should().Be(12500);

        /// <summary>
        /// ⚠️⚠️ Eget kapital är normalt en KREDITPOST. Räknas klass 2 debet-positivt blir
        /// föreningens egna kapital negativt, och balansräkningen påstår att den är skuldsatt
        /// med hela sitt kapital.
        /// </summary>
        [Fact]
        public void Eget_kapital_ar_kreditpositivt()
            => LedgerAccountClass.BalanceAmount(2060, debit: 0, credit: 80000).Should().Be(80000);

        [Fact]
        public void Skuld_ar_kreditpositiv()
            => LedgerAccountClass.BalanceAmount(2440, debit: 0, credit: 3200).Should().Be(3200);

        // ── Balansräkningen går ihop ────────────────────────────────────────────────────────

        /// <summary>
        /// ⚠️⚠️ ÅRETS RESULTAT MÅSTE MED PÅ SKULDSIDAN. Det är inte bokfört mot eget kapital
        /// förrän året stängs, så utan det blir differensen exakt årets resultat — varje år, för
        /// varje förening — och det ser ut som ett fel i bokföringen i stället för i jämförelsen.
        /// </summary>
        [Fact]
        public void Balansen_gar_ihop_nar_arets_resultat_raknas_med()
        {
            var s = new LedgerFinancialStatements();

            // Intäkt 146 000, kostnad 95 000 → resultat 51 000.
            s.Revenue.Add(new() { AccountNumber = 3010, Amount = 146000 });
            s.Costs.Add(new() { AccountNumber = 4010, Amount = 95000 });

            // Bank 131 000, eget kapital 80 000.
            s.Assets.Add(new() { AccountNumber = 1930, Amount = 131000 });
            s.EquityAndLiabilities.Add(new() { AccountNumber = 2060, Amount = 80000 });

            s.Result.Should().Be(51000);
            s.BalanceDifference.Should().Be(0);
            s.Balances.Should().BeTrue();
        }

        /// <summary>Kontrollprov: en verklig obalans ska SYNAS, inte viftas bort.</summary>
        [Fact]
        public void En_verklig_obalans_syns()
        {
            var s = new LedgerFinancialStatements();
            s.Assets.Add(new() { AccountNumber = 1930, Amount = 131000 });
            s.EquityAndLiabilities.Add(new() { AccountNumber = 2060, Amount = 80000 });

            // Inget resultat bokfört → 51 000 saknas.
            s.BalanceDifference.Should().Be(51000);
            s.Balances.Should().BeFalse();
        }

        /// <summary>
        /// ⚠️ Exakt noll, ingen tolerans. Dubbel bokföring balanserar per konstruktion — ett öre
        /// fel är ett genuint fel, och att vifta bort det döljer det.
        /// </summary>
        [Fact]
        public void Ett_ore_fel_ar_ett_fel()
        {
            var s = new LedgerFinancialStatements();
            s.Assets.Add(new() { AccountNumber = 1930, Amount = 100000.01m });
            s.EquityAndLiabilities.Add(new() { AccountNumber = 2060, Amount = 100000m });

            s.Balances.Should().BeFalse();
        }
    }
}
