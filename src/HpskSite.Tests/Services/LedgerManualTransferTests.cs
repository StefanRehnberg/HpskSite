using System.Linq;
using HpskSite.Models.Ledger;
using HpskSite.Services.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// "Vi flyttade pengar" — en överföring mellan föreningens egna konton (kassörsrapport
    /// 2026-09-29: från placeringskontot 1940 till föreningskontot 1930 gick inte att bokföra).
    /// </summary>
    public class LedgerManualTransferTests
    {
        // Från 1940 till 1930: accountNumber = TILL, paymentAccountNumber = FRÅN.
        private static ManualEntryRequest Transfer() => new()
        {
            IssuerType = 0,
            IssuerId = 2604,
            Amount = 20000m,
            Date = new DateTime(2026, 9, 29),
            Description = "Överföring från sparkontot",
            IsTransfer = true,
            AccountNumber = 1930,
            PaymentAccountNumber = 1940
        };

        [Fact]
        public void En_overforing_mellan_tva_pengakonton_duger()
            => Assert.Null(LedgerManualPostingService.Validate(Transfer()));

        [Fact]
        public void Till_kontot_i_debet_fran_kontot_i_kredit()
        {
            var lines = LedgerManualPostingService.BuildLines(Transfer());

            Assert.Equal(2, lines.Count);
            var debit = lines.Single(l => l.Debit > 0);
            var credit = lines.Single(l => l.Credit > 0);
            Assert.Equal(1930, debit.AccountNumber);
            Assert.Equal(1940, credit.AccountNumber);
            Assert.Equal(20000m, debit.Debit);
            Assert.Equal(20000m, credit.Credit);
        }

        [Fact]
        public void Riktningsknappen_betyder_ingenting_for_en_overforing()
        {
            var a = Transfer(); a.WeReceived = true;
            var b = Transfer(); b.WeReceived = false;

            var la = LedgerManualPostingService.BuildLines(a);
            var lb = LedgerManualPostingService.BuildLines(b);
            Assert.Equal(la.Single(l => l.Debit > 0).AccountNumber, lb.Single(l => l.Debit > 0).AccountNumber);
        }

        [Fact]
        public void Ingen_moms_pa_nagon_rad()
        {
            var r = Transfer();
            r.VatRate = 25;   // även om något skickar en sats
            Assert.All(LedgerManualPostingService.BuildLines(r), l => Assert.Equal(0m, l.VatRate));
        }

        [Theory]
        [InlineData(4010, 1930)]   // till ett kostnadskonto — det är en utgift
        [InlineData(1930, 3010)]   // från ett intäktskonto
        [InlineData(2350, 1930)]   // lån — utanför 19xx tills vidare
        [InlineData(1930, 1510)]
        public void Bara_pengakonton_19xx(int to, int from)
        {
            var r = Transfer();
            r.AccountNumber = to;
            r.PaymentAccountNumber = from;
            Assert.NotNull(LedgerManualPostingService.Validate(r));
        }

        [Fact]
        public void Samma_konto_pa_bada_sidor_vagras()
        {
            var r = Transfer();
            r.PaymentAccountNumber = 1930;
            Assert.Contains("samma konto", LedgerManualPostingService.Validate(r));
        }

        [Theory]
        [InlineData(1899, false)]
        [InlineData(1900, true)]
        [InlineData(1940, true)]
        [InlineData(1999, true)]
        [InlineData(2000, false)]
        public void Pengakontots_granser(int n, bool expected)
            => Assert.Equal(expected, LedgerMoneyAccount.Is(n));

        [Fact]
        public void En_vanlig_betalning_ar_oforandrad()
        {
            var r = Transfer();
            r.IsTransfer = false;
            r.AccountNumber = 4010;
            r.PaymentAccountNumber = 1930;
            r.WeReceived = false;

            var lines = LedgerManualPostingService.BuildLines(r);
            Assert.Equal(4010, lines.Single(l => l.Debit > 0).AccountNumber);
            Assert.Null(lines.Single(l => l.Debit > 0).VatRate);   // null = kontots sats, som förut
        }
    }
}
