using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️⚠️ TRE KLUBBSORTER, OCH DEN TREDJE FÅR INGEN EXPORT.
    ///
    /// <para>Skiljelinjen mellan de två sista formerna är <b>inte storleken</b> utan om
    /// föreningens bokföringsprogram läser bankkontot självt. Gör det det, bokförs
    /// anmälningsavgifterna redan därifrån — och en SIE-fil från oss skulle bokföra <b>samma
    /// pengar en andra gång</b>. Källa: underlaget till Varbergs PK (<c>/ekonomifragor/wf</c>),
    /// byggt på Fredriks eget svar.</para>
    ///
    /// <para>Felet vore tyst: exporten skulle se ut att fungera, filen skulle importeras utan
    /// protest, och dubbleringen upptäcks först när någon stämmer av ett saldo. Därför pinnas
    /// regeln här och inte bara i en vy.</para>
    /// </summary>
    public class LedgerIssuerShapeTests
    {
        [Fact]
        public void Bara_FullLedger_bokfor_hos_oss()
        {
            Assert.True(LedgerIssuerShape.KeepsBooks(LedgerIssuerShape.FullLedger));
            Assert.False(LedgerIssuerShape.KeepsBooks(LedgerIssuerShape.FeesAndExport));
            Assert.False(LedgerIssuerShape.KeepsBooks(LedgerIssuerShape.FeesOnly));

            // Ingen vald form bokför inte — samma riktning som allt annat i den här kedjan.
            Assert.False(LedgerIssuerShape.KeepsBooks(""));
            Assert.False(LedgerIssuerShape.KeepsBooks(null));
        }

        [Fact]
        public void Bankkopplad_klubb_erbjuds_INGEN_export()
        {
            // Det här är hela skälet att formen finns.
            Assert.False(LedgerIssuerShape.OffersExport(LedgerIssuerShape.FeesOnly));

            // Kontrollprov: de andra två får den, annars hade påståendet ovan varit grönt
            // även om OffersExport svarade nej på allting.
            Assert.True(LedgerIssuerShape.OffersExport(LedgerIssuerShape.FeesAndExport));
            Assert.True(LedgerIssuerShape.OffersExport(LedgerIssuerShape.FullLedger));
        }

        [Fact]
        public void Formerna_ar_tre_och_alla_ar_giltiga()
        {
            Assert.Equal(3, LedgerIssuerShape.All.Length);

            foreach (var shape in LedgerIssuerShape.All)
                Assert.True(LedgerIssuerShape.IsValid(shape), shape);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        [InlineData("Full")]      // fel skiftläge — nycklarna ligger i databasen, de är exakta
        [InlineData("full-ledger")]
        [InlineData("speedledger")]
        public void Okanda_varden_ar_inte_giltiga_former(string? shape)
        {
            Assert.False(LedgerIssuerShape.IsValid(shape));

            // ⚠️ Och viktigast: ett oigenkänt värde bokför ALDRIG. Riktningen är enkelriktad
            // mot att inte bokföra — ett felaktigt "bokförd" är det enda av felen som är svårt
            // att upptäcka i efterhand.
            Assert.False(LedgerIssuerShape.KeepsBooks(shape));
        }

        [Fact]
        public void Nycklarna_ar_last_mot_databasen()
        {
            // Mappningen förening → form ligger i LedgerIssuerSettings.Shape som TEXT. Döps en
            // nyckel om blir varje befintlig rad ett okänt värde — vilket tyst stänger av
            // bokföringen för alla som valt den. Testet är en låsning, inte en beskrivning.
            Assert.Equal("full", LedgerIssuerShape.FullLedger);
            Assert.Equal("export", LedgerIssuerShape.FeesAndExport);
            Assert.Equal("fees", LedgerIssuerShape.FeesOnly);
        }
    }
}
