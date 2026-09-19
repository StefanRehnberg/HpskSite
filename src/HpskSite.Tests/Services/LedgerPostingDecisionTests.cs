using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ BOKFÖRING ÄR OPT-IN. Fram till 2026-09-19 vägrade evenemangsbetalningen visa en
    /// Swish-kod när föreningen saknade räkenskapsår — "Klubben kan inte ta emot betalningen än".
    /// Rapporterat i dev: de flesta klubbar vill skapa ett evenemang och kunna ta betalt, och
    /// bokför i sitt eget program.
    ///
    /// <para>Testerna pinnar SJÄLVA BESLUTET, som är den enda regeln i kedjan. Att posta eller
    /// inte avgörs av <see cref="LedgerIssuerShape"/>, inte av om ett räkenskapsår råkar finnas.</para>
    /// </summary>
    public class LedgerPostingDecisionTests
    {
        // ⚠️ ANROPAR DEN RIKTIGA REGELN, inte en spegling. En kopia av villkoren här hade
        // varit ett påstående om testfilen och inte om produkten — den kan glida utan att
        // något faller. LedgerPostingService.DecidePosting anropar samma metod.
        private static LedgerPostingDecision Decide(string? shape, string? blockedReason)
            => LedgerPostingDecision.For(shape, blockedReason);

        [Fact]
        public void Ingen_installningsrad_bokfor_inte_och_har_inget_att_saga()
        {
            var d = Decide(null, blockedReason: null);

            Assert.False(d.ShouldPost);
            // ⚠️ null = helt normalt. En text här hade visats som ett fel för varje klubb som
            // aldrig bett om bokföring.
            Assert.Null(d.SkipReason);
        }

        [Fact]
        public void Ingen_installningsrad_bokfor_inte_ens_nar_rakenskapsaret_saknas()
        {
            // Det var precis det här fallet som blockerade betalningen.
            var d = Decide(null, blockedReason: "Det finns inget räkenskapsår som omfattar 2026-09-19.");

            Assert.False(d.ShouldPost);
            Assert.Null(d.SkipReason);
        }

        [Fact]
        public void Foreningen_bokfor_i_eget_program_bokfor_inte_har()
        {
            var d = Decide(LedgerIssuerShape.FeesAndExport, blockedReason: null);

            Assert.False(d.ShouldPost);
            Assert.Null(d.SkipReason);
        }

        [Fact]
        public void Full_liggare_med_oppet_ar_bokfor()
        {
            var d = Decide(LedgerIssuerShape.FullLedger, blockedReason: null);

            Assert.True(d.ShouldPost);
            Assert.Null(d.SkipReason);
        }

        [Fact]
        public void Full_liggare_utan_rakenskapsar_bokfor_inte_MEN_sager_varfor()
        {
            // ⚠️ Den här föreningen RÄKNAR med bokföring. Tystnad här vore en utebliven
            // verifikation som ingen upptäcker — betalningen tas emot, men skälet måste fram.
            const string reason = "Det finns inget räkenskapsår som omfattar 2026-09-19.";

            var d = Decide(LedgerIssuerShape.FullLedger, blockedReason: reason);

            Assert.False(d.ShouldPost);
            Assert.Equal(reason, d.SkipReason);
        }

        [Fact]
        public void Full_liggare_med_faststallt_ar_bokfor_inte_MEN_sager_varfor()
        {
            const string reason = "Räkenskapsåret 2025 är fastställt av årsmötet och tar inte emot fler poster.";

            var d = Decide(LedgerIssuerShape.FullLedger, blockedReason: reason);

            Assert.False(d.ShouldPost);
            Assert.Equal(reason, d.SkipReason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("nagot-annat")]
        public void Ett_olasbart_shape_bokfor_INTE(string shape)
        {
            // ⚠️ Riktningen är enkelriktad mot att INTE bokföra. Vi påstår hellre att vi inte
            // bokfört än tvärtom — ett felaktigt "bokförd" är det enda av felen som är svårt
            // att upptäcka i efterhand.
            var d = Decide(shape, blockedReason: null);

            Assert.False(d.ShouldPost);
        }

        [Fact]
        public void Formerna_ar_tva_och_bara_den_ena_bokfor()
        {
            // Ett strukturellt prov: läggs en tredje form till ska någon behöva ta ställning
            // till om den bokför, i stället för att ärva "nej" av misstag.
            Assert.NotEqual(LedgerIssuerShape.FullLedger, LedgerIssuerShape.FeesAndExport);
            Assert.True(Decide(LedgerIssuerShape.FullLedger, null).ShouldPost);
            Assert.False(Decide(LedgerIssuerShape.FeesAndExport, null).ShouldPost);
        }
    }
}
