using HpskSite.Services.Firearms;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Nyckeln som avgör vilket tillfälle en vapentaggning hör till.
    ///
    /// <para><b>Varför den är värd egna test:</b> <c>SetUsage</c> och <c>UsageBySourceForMember</c>
    /// kräver en databas och är därför otestade. <see cref="FirearmUsageService.Key"/> är däremot en
    /// ren funktion — och den är det som avgör om resultatlistan hittar sin egen taggning. Faller
    /// den, visar listan tomt på rader som ÄR taggade, vilket läses som att valen inte sparades.</para>
    ///
    /// <para><b>⚠️ Klienten speglar den här funktionen</b> (<c>tagKey</c> i
    /// <c>_FirearmUsageTagger.cshtml</c>, via <c>window.getShootingClassName(...).toLowerCase()</c>).
    /// Ändras formen här måste JS-sidan följa, annars slutar uppslagningen matcha — tyst.</para>
    /// </summary>
    public class FirearmUsageKeyTests
    {
        // ── Grundformen: utan klass är nyckeln oförändrad ─────────────────────────────────────
        // Träningsraderna lagrades innan klasskolumnen fanns. Bytte nyckeln form för dem skulle
        // varje redan gjord taggning bli oåtkomlig utan att något sa ifrån.

        [Fact]
        public void Key_UtanKlass_ArKindOchId()
        {
            Assert.Equal("training:42", FirearmUsageService.Key("training", 42));
            Assert.Equal("comp:2586", FirearmUsageService.Key("comp", 2586));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Key_TomKlass_GerSammaNyckelSomIngenKlass(string? cls)
        {
            Assert.Equal("training:42", FirearmUsageService.Key("training", 42, cls));
        }

        // ── Källan är halva nyckeln ───────────────────────────────────────────────────────────
        // ⚠️ TVÅ OBEROENDE IDENTITETSSERIER. En självrapporterad rad bär TrainingScores-id, en av
        // våra egna tävlingar bär tävlingsnodens id — samma heltal betyder olika saker. På id:t
        // ensamt viker en kollision ihop två skilda tillfällen, tyst. Samma lärdom som SourceTable
        // i märkessynken.

        [Fact]
        public void Key_SammaIdOlikaKalla_KolliderarInte()
        {
            Assert.NotEqual(
                FirearmUsageService.Key("training", 7),
                FirearmUsageService.Key("comp", 7));
        }

        // ── Klassen är tredje delen, för en tävling ───────────────────────────────────────────
        // Detta är hela skälet kolumnen infördes: resultatlistan grupperar en officiell tävling per
        // (tävling, vapenklass), så en skytt anmäld i A1 och C1 har två rader med samma tävlings-id
        // och ska kunna ange två olika vapen.

        [Fact]
        public void Key_TvaKlasserSammaTavling_ArSkildaTillfallen()
        {
            var a1 = FirearmUsageService.Key("comp", 2586, "A1");
            var c1 = FirearmUsageService.Key("comp", 2586, "C1");

            Assert.NotEqual(a1, c1);
        }

        [Fact]
        public void Key_MedKlass_BarKlassenSistOchGement()
        {
            Assert.Equal("comp:2586:c1", FirearmUsageService.Key("comp", 2586, "C1"));
        }

        // ── ⚠️ Id-vs-Namn-fällan ─────────────────────────────────────────────────────────────
        // En klass finns i TVÅ strängformer: id ("C_Vet_Y") och visningsnamn ("C Vet Y"). De är
        // IDENTISKA för C1/C2/C3/A1/B2 och skiljer sig för varje klass med ändelse. En rak
        // strängjämförelse ser därför korrekt ut i all testning och delar just veteran-, dam-,
        // junior- och optikklasserna i två tillfällen — samma bugg som gav dubbla rader i
        // klubbmästerskapet 2026-08-25.

        [Theory]
        [InlineData("C_Vet_Y", "C Vet Y")]
        [InlineData("A_opt_1", "A Opt 1")]
        [InlineData("C1_Dam", "C1 Dam")]
        public void Key_IdOchNamnFormenAvSammaKlass_GerSAMMANyckel(string idForm, string nameForm)
        {
            Assert.Equal(
                FirearmUsageService.Key("comp", 2586, idForm),
                FirearmUsageService.Key("comp", 2586, nameForm));
        }

        [Fact]
        public void Key_KlassensSkiftlage_SpelarIngenRoll()
        {
            Assert.Equal(
                FirearmUsageService.Key("comp", 2586, "c vet y"),
                FirearmUsageService.Key("comp", 2586, "C_Vet_Y"));
        }

        [Fact]
        public void Key_TvaGenuintOlikaKlasser_ForblirOlika()
        {
            // Kontrollprov mot ihopvikningen ovan: en normalisering som viker ALLT samman hade
            // klarat testerna över men gjort kolumnen verkningslös.
            Assert.NotEqual(
                FirearmUsageService.Key("comp", 2586, "C_Vet_Y"),
                FirearmUsageService.Key("comp", 2586, "C_Vet_A"));
        }

        // ── Okänd klass kastas inte ──────────────────────────────────────────────────────────
        // En klass vi inte känner igen ska gruppera med SIG SJÄLV, inte försvinna. Samma regel som
        // ShootingClasses.ToCanonicalName.

        [Fact]
        public void Key_OkandKlass_BehallsOchGrupperarMedSigSjalv()
        {
            var key = FirearmUsageService.Key("comp", 2586, "Zz9");

            Assert.Equal("comp:2586:zz9", key);
            Assert.Equal(key, FirearmUsageService.Key("comp", 2586, " ZZ9 "));
        }
    }
}
