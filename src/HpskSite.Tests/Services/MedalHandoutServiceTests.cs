using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Årsmötets medaljsammanställning läser valören ur en oavgjord medaljplats text — den enda
    /// plats i <see cref="MedalHandoutService"/> där en sträng tolkas i stället för läsas ur ett
    /// fält. Raderna skrivs av tre oberoende ställen (precisionens medaljräkning, fältskyttets
    /// artefakt och lagmedaljerna i <c>PrizeGivingService</c>), alla på formen "Guld — ...".
    ///
    /// ⚠️ Prövas här eftersom en felläsning här ger en FELRÄKNAD BESTÄLLNING. Fallbacken måste
    /// vara tom sträng och aldrig en gissning: raden står kvar bland de oavgjorda med hela sin
    /// text, alltså en synlig lucka i stället för ett tyst fel antal.
    /// </summary>
    public class MedalHandoutServiceTests
    {
        [Theory]
        [InlineData("Guld — särskjutning krävs mellan Ivan Slabiak och Markus Henningsson", "Guld")]
        [InlineData("Silver — särskjutning krävs", "Silver")]
        [InlineData("Brons — lagen står lika: Varberg 1 och Falkenberg 2. Ordningen måste avgöras av arrangören.", "Brons")]
        public void LaserValoren_UrDeTreProducenternasForm(string line, string expected) =>
            MedalHandoutService.MedalOf(line).Should().Be(expected);

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Plats 4 — särskjutning krävs")]
        [InlineData("Särskjutning krävs mellan två skyttar")]
        public void OkandForm_GerTomStrang_AldrigEnGissning(string? line) =>
            MedalHandoutService.MedalOf(line).Should().BeEmpty();

        /// <summary>
        /// ⚠️ Formen kommer från <see cref="ChampionshipMedalCount.MedalNameForPlace"/>. Byter den
        /// ord slutar valören läsas — det här påståendet är det som gör en sådan ändring högljudd
        /// i stället för tyst.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void ForstaLedet_MatcharMedaljnamnetForPlatsen(int place)
        {
            var name = ChampionshipMedalCount.MedalNameForPlace(place);
            MedalHandoutService.MedalOf($"{name} — särskjutning krävs").Should().Be(name);
        }

        /// <summary>Guld före silver före brons; okänd valör sist, så en rad aldrig försvinner.</summary>
        [Fact]
        public void Sorteringen_ArGuldSilverBrons_MedOkantSist()
        {
            MedalHandoutService.MedalSort("Guld").Should().BeLessThan(MedalHandoutService.MedalSort("Silver"));
            MedalHandoutService.MedalSort("Silver").Should().BeLessThan(MedalHandoutService.MedalSort("Brons"));
            MedalHandoutService.MedalSort("Brons").Should().BeLessThan(MedalHandoutService.MedalSort(""));
        }

        // ── Graveringstexten ────────────────────────────────────────────────────
        //
        // Beställningslistan var en summering per valör och var därmed oanvändbar för gravören
        // (Stefan 2026-09-20). Texten är ett FÖRSLAG — delarna finns som egna kolumner — men den
        // måste vara läsbar ensam, eftersom det är den som följer med till gravyren.

        [Fact]
        public void Gravyrtext_BarTavlingAr_Kategori_OchNamn()
        {
            var item = new MedalHandoutItem
            {
                Medal = "Guld",
                Category = "C Dam",
                CompetitionName = "KM Precision",
                CompetitionDate = new DateTime(2026, 5, 9)
            };

            item.Engraving("Anna Andersson").Should().Be("KM Precision 2026 · C Dam · Anna Andersson");
        }

        /// <summary>⚠️ Laget måste med — annars är två lagmedaljer i samma klass oskiljbara.</summary>
        [Fact]
        public void Gravyrtext_ForLagmedalj_BarLagetsNamn()
        {
            var item = new MedalHandoutItem
            {
                Medal = "Silver",
                Category = "Lag C Öppen",
                CompetitionName = "KM Fält",
                CompetitionDate = new DateTime(2026, 8, 17),
                IsTeam = true,
                TeamName = "Harplinge 1"
            };

            item.Engraving("Bo Bengtsson")
                .Should().Be("KM Fält 2026 · Lag C Öppen · Harplinge 1 · Bo Bengtsson");
        }

        /// <summary>
        /// ⚠️⚠️ ÅRET KOMMER UR TÄVLINGENS DATUM, aldrig ur dagens. Medaljerna graveras oftast
        /// månaderna efter säsongen — ett årtal ur "nu" hade satt fel år på en graverad medalj,
        /// och det går inte att rätta.
        /// </summary>
        [Fact]
        public void Gravyrtext_AnvanderTavlingensAr_InteDagensAr()
        {
            var item = new MedalHandoutItem
            {
                Medal = "Guld",
                Category = "A",
                CompetitionName = "KM Magnum",
                CompetitionDate = new DateTime(2019, 9, 1)
            };

            item.Engraving("Cecilia Ceder").Should().StartWith("KM Magnum 2019 ·");
            item.Engraving("Cecilia Ceder").Should().NotContain(DateTime.Today.Year.ToString());
        }

        /// <summary>Tomma delar hoppas över — aldrig en text med hängande avdelare.</summary>
        [Theory]
        [InlineData("", "C", "Dan Dahl", "C · Dan Dahl")]
        [InlineData("KM", "", "Dan Dahl", "KM · Dan Dahl")]
        [InlineData("KM", "C", "", "KM · C")]
        public void Gravyrtext_HoppasOverTommaDelar(string comp, string cat, string name, string expected)
        {
            var item = new MedalHandoutItem { CompetitionName = comp, Category = cat };
            item.Engraving(name).Should().Be(expected);
        }
    }
}
