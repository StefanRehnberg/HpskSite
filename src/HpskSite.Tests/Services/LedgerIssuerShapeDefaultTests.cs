using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ FÖRVALET ÄR EN SPÄRR, INTE EN BEKVÄMLIGHET.
    ///
    /// <para>Beslutet (Stefan 2026-09-15, skärpt 2026-09-19) är att <b>bokföring är opt-in</b>:
    /// en förening hamnar i bokföringsläge bara när någon valt det. Den regeln har redan brutits
    /// två gånger på två olika ställen — först som default-parameter på
    /// <c>LedgerSetupService.EnsureIssuer</c>, sedan som fältförval på
    /// <see cref="LedgerIssuerSettings.Shape"/>. Båda gångerna såg det ut som en trivialitet, och
    /// båda gångerna var följden densamma: en förening som aldrig valt kräver plötsligt
    /// räkenskapsår för att kunna ta emot pengar.</para>
    ///
    /// <para>Den här filen finns för att ett tredje sådant förval ska fälla sviten, inte upptäckas
    /// av en kassör mitt i en tävlingsdag.</para>
    /// </summary>
    public class LedgerIssuerShapeDefaultTests
    {
        [Fact]
        public void En_ny_installningsrad_har_INTE_valt_foreningsform()
        {
            var settings = new LedgerIssuerSettings();

            // ⚠️ Poängen är inte att strängen är tom — det är att den inte är FullLedger.
            // Båda påståendena står kvar: det första faller om någon sätter "export" som
            // förval (också ett val vi inte får göra åt föreningen), det andra om
            // FullLedger kryper tillbaka.
            Assert.Equal("", settings.Shape);
            Assert.NotEqual(LedgerIssuerShape.FullLedger, settings.Shape);
        }

        [Fact]
        public void En_ny_installningsrad_bokfor_inte()
        {
            // Hela poängen, mätt genom den riktiga regeln i stället för genom fältet:
            // en förening ingen satt upp får ingen verifikation.
            var settings = new LedgerIssuerSettings();

            var decision = LedgerPostingDecision.For(settings.Shape, blockedReason: null);

            Assert.False(decision.ShouldPost);

            // Och den säger ingenting till användaren — att inte bokföra är normaltillståndet,
            // inte ett fel som ska visas för varje klubb som aldrig bett om bokföring.
            Assert.Null(decision.SkipReason);
        }

        [Fact]
        public void En_ny_installningsrad_bokfor_inte_ens_nar_allt_annat_ar_klart()
        {
            // Kontrollprov mot det farligaste missförståndet: att ett upplagt räkenskapsår
            // skulle räcka för att bokföring ska starta. Det gör det inte — formen avgör.
            var settings = new LedgerIssuerSettings();

            Assert.False(LedgerPostingDecision.For(settings.Shape, blockedReason: null).ShouldPost);

            // …och kontrollprovets motsats: samma rad med formen VALD bokför. Utan den här
            // raden hade testet ovan varit grönt även om For() svarade nej på allting.
            settings.Shape = LedgerIssuerShape.FullLedger;
            Assert.True(LedgerPostingDecision.For(settings.Shape, blockedReason: null).ShouldPost);
        }

        [Fact]
        public void Momsregistrering_ar_ocksa_AV_tills_nagon_sagt_annat()
        {
            // Samma familj av fel: ett påstående om föreningen som vi inte får gissa. Det här
            // hamnar på en utfärdad handling (kvittot), och ett falskt momspåstående där är
            // fel 8 i den gamla fakturamodellen.
            var settings = new LedgerIssuerSettings();

            Assert.False(settings.IsVatRegistered);
            Assert.Null(settings.VatNumber);
        }
    }
}
