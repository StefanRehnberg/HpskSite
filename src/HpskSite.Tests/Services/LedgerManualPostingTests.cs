using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Den manuella bokför-ytans två regler: <b>vad som är en giltig post</b>, och <b>hur riktningen
    /// blir en kontering</b>.
    ///
    /// <para><b>⚠️ Testerna anropar den riktiga koden</b> — <c>Validate</c> och <c>BuildLines</c> är
    /// <c>internal static</c> och rör ingen databas. En spegling i testfilen hade varit ett påstående
    /// om testet och inte om produkten.</para>
    ///
    /// <para><b>⚠️⚠️ RIKTNINGEN ÄR DET DYRA.</b> Kastas debet och kredit om hamnar en utgift som en
    /// intäkt. Verifikationen balanserar, liggaren accepterar den, och felet syns först när någon
    /// läser resultatrapporten — eller aldrig.</para>
    /// </summary>
    public class LedgerManualPostingTests
    {
        private static ManualEntryRequest Valid() => new()
        {
            IssuerType = 0,
            IssuerId = 2604,
            Amount = 3150m,
            WeReceived = false,
            Date = new DateTime(2026, 9, 14),
            Description = "Ammunition till måndagsträningen",
            AccountNumber = 4010,
            PaymentAccountNumber = 1930
        };

        [Fact]
        public void En_ifylld_post_duger()
        {
            Assert.Null(LedgerManualPostingService.Validate(Valid()));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-3150)]
        public void Beloppet_maste_vara_positivt(decimal amount)
        {
            var r = Valid();
            r.Amount = amount;

            // ⚠️ Ett negativt belopp skulle "fungera": det vänder bara debet och kredit, och ger
            // en verifikation som balanserar men betyder motsatsen till vad kassören menade.
            // Riktningen anges med knappen, aldrig med ett minustecken.
            Assert.NotNull(LedgerManualPostingService.Validate(r));
        }

        [Fact]
        public void Datum_kravs_och_gissas_inte()
        {
            var r = Valid();
            r.Date = null;

            // Ett gissat datum (i dag) lägger posten i fel månad och ibland i fel räkenskapsår.
            Assert.NotNull(LedgerManualPostingService.Validate(r));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Beskrivningen_kravs(string description)
        {
            var r = Valid();
            r.Description = description;

            // Texten ÄR verifikationens beskrivning — en tom rad går inte att granska i efterhand.
            Assert.NotNull(LedgerManualPostingService.Validate(r));
        }

        [Fact]
        public void Samma_konto_pa_bada_sidor_vagras()
        {
            var r = Valid();
            r.PaymentAccountNumber = r.AccountNumber;

            // ⚠️ Den här balanserar perfekt och betyder ingenting: 3 150 in och 3 150 ut på samma
            // konto. Den passerar varje maskinell kontroll utom den här.
            var error = LedgerManualPostingService.Validate(r);

            Assert.NotNull(error);
            Assert.Contains("samma konto", error!, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Vi_betalade_satter_kostnaden_i_debet()
        {
            var r = Valid();
            r.WeReceived = false;

            var lines = LedgerManualPostingService.BuildLines(r);

            Assert.Equal(2, lines.Count);

            var cost = lines.Single(l => l.AccountNumber == 4010);
            var bank = lines.Single(l => l.AccountNumber == 1930);

            Assert.Equal(3150m, cost.Debit);
            Assert.Equal(0m, cost.Credit);
            Assert.Equal(3150m, bank.Credit);
            Assert.Equal(0m, bank.Debit);
        }

        [Fact]
        public void Vi_fick_in_vander_pa_konteringen()
        {
            var r = Valid();
            r.WeReceived = true;
            r.AccountNumber = 3010;

            var lines = LedgerManualPostingService.BuildLines(r);

            var revenue = lines.Single(l => l.AccountNumber == 3010);
            var bank = lines.Single(l => l.AccountNumber == 1930);

            // Pengarna IN på banken (debet), intäkten i kredit — spegelvänt mot fallet ovan.
            Assert.Equal(3150m, bank.Debit);
            Assert.Equal(3150m, revenue.Credit);
            Assert.Equal(0m, bank.Credit);
            Assert.Equal(0m, revenue.Debit);
        }

        [Fact]
        public void Konteringen_balanserar_at_bada_hallen()
        {
            foreach (var received in new[] { true, false })
            {
                var r = Valid();
                r.WeReceived = received;

                var lines = LedgerManualPostingService.BuildLines(r);

                Assert.Equal(lines.Sum(l => l.Debit), lines.Sum(l => l.Credit));
                Assert.Equal(3150m, lines.Sum(l => l.Debit));
            }
        }

        [Fact]
        public void Betalkontot_bokfors_alltid_momsfritt()
        {
            foreach (var received in new[] { true, false })
            {
                var r = Valid();
                r.WeReceived = received;

                var bank = LedgerManualPostingService.BuildLines(r).Single(l => l.AccountNumber == 1930);

                // ⚠️ Momsen hör till vad posten VAR, inte till pengarnas väg in eller ut. Utan
                // den nollan delas beloppet två gånger och verifikationen blir fel med momsen.
                Assert.Equal(0m, bank.VatRate);
            }
        }

        [Fact]
        public void Kostnadsraden_bar_beskrivningen()
        {
            var r = Valid();

            var cost = LedgerManualPostingService.BuildLines(r).Single(l => l.AccountNumber == 4010);

            // Radtexten är det en revisor läser när kontonumret inte räcker.
            Assert.Equal("Ammunition till måndagsträningen", cost.Text);
        }

        [Fact]
        public void Beskrivningen_trimmas_innan_den_bokfors()
        {
            var r = Valid();
            r.Description = "   Fika till kretsmötet   ";

            var cost = LedgerManualPostingService.BuildLines(r).Single(l => l.AccountNumber == 4010);

            Assert.Equal("Fika till kretsmötet", cost.Text);
        }
    }
}
