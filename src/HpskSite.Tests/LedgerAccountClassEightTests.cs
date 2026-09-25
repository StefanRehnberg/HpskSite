using FluentAssertions;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ Klass 8 är DELAD enligt BAS: 8000–8399 finansiella intäkter, 8400–8999 kostnader.
    /// Fram till 2026-09-25 var hela klassen intäkt — räntekostnaden stod under intäkterna i
    /// budgeten (Michael Henriksson), som en negativ intäkt i resultaträkningen och som "I" i SIE.
    /// Ingen befintlig test föll när det rättades; de här pinnar gränsen på varje ställe den bor.
    /// </summary>
    public class LedgerAccountClassEightTests
    {
        [Theory]
        [InlineData(8310, true)]    // Ränteintäkter
        [InlineData(8399, true)]
        [InlineData(8400, false)]
        [InlineData(8410, false)]   // Räntekostnader
        [InlineData(8999, false)]
        [InlineData(3010, true)]
        [InlineData(4010, false)]
        public void Klass_8_delas_vid_8400(int account, bool revenue)
            => LedgerAccountClass.IsRevenueDirected(account).Should().Be(revenue);

        [Fact]
        public void En_rantekostnad_ar_positiv_i_egen_riktning_som_en_kostnad()
            => LedgerAccountClass.InOwnDirection(8410, debit: 250m, credit: 0m).Should().Be(250m);

        [Fact]
        public void Budgeten_lagger_rantekostnaden_bland_kostnaderna()
        {
            LedgerBudgetService.IsIncome(8410).Should().BeFalse();
            LedgerBudgetService.IsIncome(8310).Should().BeTrue();
        }

        [Theory]
        [InlineData(8310, "I")]
        [InlineData(8410, "K")]
        [InlineData(8999, "K")]
        [InlineData(3040, "I")]
        [InlineData(1930, "T")]
        [InlineData(2440, "S")]
        public void SIE_kontotypen_foljer_samma_grans(int account, string ktyp)
            => SieFormat.AccountType(account).Should().Be(ktyp);

        [Fact]
        public void Kontoplanens_intaktsflagga_foljer_samma_grans()
        {
            new LedgerChartRow { Number = 8410 }.IsIncome.Should().BeFalse();
            new LedgerChartRow { Number = 8310 }.IsIncome.Should().BeTrue();
        }
    }
}
