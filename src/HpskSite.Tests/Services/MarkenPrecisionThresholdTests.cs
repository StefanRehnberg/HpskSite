using HpskSite.Models;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Guldfodringens precisionströskel per serie — <see cref="Marken.PrecisionThreshold"/>.
    ///
    /// <para>Regeln, ordagrant ur SHB kap 5 ("Reducerade krav", under fordringstabellen):
    /// <i>"Reducerade krav för äldre skyttar som föregående år fyllt 55 år gäller 1 poäng mindre per
    /// serie, och för personer som ett föregående år fyllt 65 år, 2 poäng mindre per serie."</i>
    /// Grundkraven är A 43 / B 45 / C 46.</para>
    ///
    /// <para>⚠️ Koden läste tidigare 5.1.2.2 som att 65+ skjuter mot hela <b>silvertabellen</b>
    /// (A 38 / B 39 / C 40). Följden var att en 40-poängsserie i vapengrupp C stod som en godkänd
    /// guldserie i det underlag en klubbstyrelse signerar ett föreningsintyg på. Varje test nedan som
    /// rör 66+ faller om den läsningen sätts tillbaka — det är hela poängen med dem.</para>
    ///
    /// <para>⚠️ 55/65 är INTE veteranklassernas gränser (60 respektive 70). Testerna prövar därför
    /// åldrarna runt 56 och 66 explicit, så ingen kan "rätta" dem till tävlingsklassernas gränser
    /// utan att något går sönder.</para>
    /// </summary>
    public class MarkenPrecisionThresholdTests
    {
        private const int Year = 2026;

        // ── Grundkrav, ingen eftergift ────────────────────────────────
        [Theory]
        [InlineData("A", 43)]
        [InlineData("B", 45)]
        [InlineData("C", 46)]
        [InlineData("R", 46)]
        public void FullGuldkrav_UnderFemtiosex(string group, int expected)
        {
            // Fyller 40 i år — långt under varje eftergift.
            Assert.Equal(expected, Marken.PrecisionThreshold(group, Year, Year - 40));
        }

        // ── 65+ → guldkrav − 2 (det som var fel) ──────────────────────
        [Theory]
        [InlineData("A", 41)]
        [InlineData("B", 43)]
        [InlineData("C", 44)]
        [InlineData("R", 44)]
        public void SextiofemPlus_GerTvaPoangsAvdrag_InteSilverkravet(string group, int expected)
        {
            // Född 1960 → 66 år 2026. Det är Torbjörn Andreasson-fallet.
            int threshold = Marken.PrecisionThreshold(group, Year, 1960);
            Assert.Equal(expected, threshold);

            // Och uttryckligen INTE silvertabellen (A 38 / B 39 / C 40) — den gamla läsningen.
            int silver = group == "A" ? 38 : group == "B" ? 39 : 40;
            Assert.NotEqual(silver, threshold);
        }

        [Fact]
        public void FyrtioPoangIVapengruppC_ArIngenGuldserie_ForEnSextiosexaring()
        {
            // Raden som rapporterades: "C: 40 (krav 40)" märkt som godkänd guldserie.
            int threshold = Marken.PrecisionThreshold("C", Year, 1960);
            Assert.True(40 < threshold, $"40 p i C ska inte nå kravet {threshold} för en 66-åring.");
            Assert.True(44 >= threshold, "44 p i C ska däremot nå kravet för en 66-åring.");
        }

        // ── 55+ → guldkrav − 1 ────────────────────────────────────────
        [Theory]
        [InlineData("A", 42)]
        [InlineData("B", 44)]
        [InlineData("C", 45)]
        public void FemtiofemPlus_GerEttPoangsAvdrag(string group, int expected)
        {
            // Född 1965 → 61 år 2026: mellan de två eftergifterna.
            Assert.Equal(expected, Marken.PrecisionThreshold(group, Year, 1965));
        }

        // ── Gränserna: "fyllt 55/65 ett FÖREGÅENDE år" = ålder ≥ 56/66 ──
        [Fact]
        public void Gransen_FemtiofemFyllsIAr_GerIngetAvdrag()
        {
            // Fyller 55 i år → eftergiften gäller först nästa år.
            Assert.Equal(46, Marken.PrecisionThreshold("C", Year, Year - 55));
        }

        [Fact]
        public void Gransen_FemtiosexIAr_GerEttPoang()
        {
            Assert.Equal(45, Marken.PrecisionThreshold("C", Year, Year - 56));
        }

        [Fact]
        public void Gransen_SextiofemFyllsIAr_GerFortfarandeBaraEttPoang()
        {
            // Fyller 65 i år: fortfarande bara −1, eftersom 65 fylldes i år och inte ett föregående.
            Assert.Equal(45, Marken.PrecisionThreshold("C", Year, Year - 65));
        }

        [Fact]
        public void Gransen_SextiosexIAr_GerTvaPoang()
        {
            Assert.Equal(44, Marken.PrecisionThreshold("C", Year, Year - 66));
        }

        // ── Veterangränserna får inte styra tröskeln ──────────────────
        [Fact]
        public void VeteranYngre_MenUnderFemtiosex_GerIngetAvdrag()
        {
            // Veteran yngre börjar det år man fyller 60 — men SHB:s märkesavdrag börjar redan vid 56,
            // så åldrarna 60 och 61 måste ge −1 och inte −2 (och inget alls vore också fel).
            Assert.Equal(45, Marken.PrecisionThreshold("C", Year, Year - 60));
            Assert.Equal(45, Marken.PrecisionThreshold("C", Year, Year - 61));
        }

        [Fact]
        public void VeteranAldre_BorjarVidSjuttio_MenTvapoangsavdraget_RedanVidSextiosex()
        {
            // Skulle någon binda tröskeln till veteranklasserna skulle 66–69 ge −1 här. De ger −2.
            Assert.Equal(44, Marken.PrecisionThreshold("C", Year, Year - 67));
            Assert.Equal(44, Marken.PrecisionThreshold("C", Year, Year - 70));
        }

        // ── Okänt födelseår: fail safe ────────────────────────────────
        [Fact]
        public void OkantFodelsear_GerFulltGuldkrav()
        {
            // Ingen eftergift utan belägg — en gissning här sänker kravet på ett intygsunderlag.
            Assert.Equal(46, Marken.PrecisionThreshold("C", Year, 0));
            Assert.Equal(43, Marken.PrecisionThreshold("A", Year, 0));
        }

        // ── Basvärdet som ytan använder för brickan "ålderseftergift" ──
        [Fact]
        public void GuldPerSeriesBase_ArOreducerat_SaAldersefergiftGarAttUpptacka()
        {
            // Klubbadminens serierad jämför Threshold mot det här för att kunna märka en rad som
            // godkänd enbart tack vare en eftergift. Följer basen med eftergiften blir brickan aldrig
            // satt, och raden ser ut som en vanlig guldserie igen.
            Assert.Equal(46, Marken.GuldPerSeriesBase("C"));
            Assert.Equal(43, Marken.GuldPerSeriesBase("A"));
            Assert.True(Marken.PrecisionThreshold("C", Year, 1960) < Marken.GuldPerSeriesBase("C"));
        }
    }
}
