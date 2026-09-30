using System;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Räkenskapsårets datumregler. Född ur felrapporten 2026-09-28: ett år upplagt 1 oktober–31
    /// december gick inte att rätta till kalenderår.
    /// </summary>
    public class LedgerFiscalYearDatesTests
    {
        [Fact]
        public void Kalenderar_ar_giltigt()
            => Assert.Null(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));

        [Fact]
        public void Kort_forsta_ar_ar_giltigt()
            => Assert.Null(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 10, 1), new DateTime(2026, 12, 31)));

        [Fact]
        public void Brutet_ar_ar_giltigt()
            => Assert.Null(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 7, 1), new DateTime(2027, 6, 30)));

        [Fact]
        public void Arton_manader_ar_giltigt()
            => Assert.Null(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 1, 1), new DateTime(2027, 6, 30)));

        [Fact]
        public void En_dag_over_arton_manader_vagras()
            => Assert.NotNull(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 1, 1), new DateTime(2027, 7, 1)));

        [Fact]
        public void Slut_fore_start_vagras()
            => Assert.NotNull(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 12, 31), new DateTime(2026, 1, 1)));

        [Fact]
        public void Samma_dag_vagras()
            => Assert.NotNull(LedgerFiscalYearDates.LengthRefusal(new DateTime(2026, 1, 1), new DateTime(2026, 1, 1)));

        [Fact]
        public void Angransande_ar_overlappar_inte()
            => Assert.False(LedgerFiscalYearDates.Overlaps(
                new DateTime(2025, 1, 1), new DateTime(2025, 12, 31),
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));

        [Fact]
        public void Delad_dag_overlappar()
            => Assert.True(LedgerFiscalYearDates.Overlaps(
                new DateTime(2025, 1, 1), new DateTime(2026, 1, 1),
                new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));
    }
}
