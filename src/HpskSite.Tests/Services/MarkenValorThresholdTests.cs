using HpskSite.Models;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Krav per serie för ALLA valörer — <see cref="Marken.ThresholdFor"/> och
    /// <see cref="Marken.ValorFor"/>.
    ///
    /// <para>SHB kap 5, fordringstabellen under punkt 1: Brons 32/33/34, Silver 38/39/40,
    /// Guld 43/45/46 (A/B/C). Meningen om reducerade krav står under HELA tabellen, så eftergiften
    /// (året efter fyllda 55: −1, efter 65: −2) gäller varje valör — beslut 2026-10-02. Fram till dess
    /// fanns bara guld i koden.</para>
    ///
    /// <para>⚠️ Tabellen i den PDF-utvunna texten ser ut som att Guld-raden är 38/39/40. Det är
    /// Silver. Testerna pinnar raderna så att en läsning kolumn för kolumn faller direkt.</para>
    /// </summary>
    public class MarkenValorThresholdTests
    {
        private const int Year = 2026;
        private const int Young = Year - 40;

        // ── Grundkrav per valör och vapengrupp ────────────────────────
        [Theory]
        [InlineData(Marken.LevelBrons, "A", 32)]
        [InlineData(Marken.LevelBrons, "B", 33)]
        [InlineData(Marken.LevelBrons, "C", 34)]
        [InlineData(Marken.LevelBrons, "R", 34)]
        [InlineData(Marken.LevelSilver, "A", 38)]
        [InlineData(Marken.LevelSilver, "B", 39)]
        [InlineData(Marken.LevelSilver, "C", 40)]
        [InlineData(Marken.LevelSilver, "R", 40)]
        [InlineData(Marken.LevelGuld, "A", 43)]
        [InlineData(Marken.LevelGuld, "B", 45)]
        [InlineData(Marken.LevelGuld, "C", 46)]
        [InlineData(Marken.LevelGuld, "R", 46)]
        public void Grundkrav(string level, string group, int expected)
        {
            Assert.Equal(expected, Marken.BaseThreshold(level, group));
            Assert.Equal(expected, Marken.ThresholdFor(level, group, Year, Young));
        }

        [Fact]
        public void OkandVapengrupp_LasesSomC()
        {
            Assert.Equal(34, Marken.BaseThreshold(Marken.LevelBrons, ""));
            Assert.Equal(46, Marken.BaseThreshold(Marken.LevelGuld, "M"));
        }

        [Fact]
        public void OkandValor_GerNoll_InteEttKravSomAllaKlarar()
        {
            Assert.Equal(0, Marken.BaseThreshold("Platina", "C"));
            Assert.Equal(0, Marken.ThresholdFor("Platina", "C", Year, Year - 70));
        }

        // ── Eftergiften gäller ALLA valörer ───────────────────────────
        [Theory]
        [InlineData(Marken.LevelBrons, "A", 31, 30)]
        [InlineData(Marken.LevelBrons, "C", 33, 32)]
        [InlineData(Marken.LevelSilver, "B", 38, 37)]
        [InlineData(Marken.LevelSilver, "C", 39, 38)]
        [InlineData(Marken.LevelGuld, "A", 42, 41)]
        [InlineData(Marken.LevelGuld, "C", 45, 44)]
        public void Eftergift_PaVarjeValor(string level, string group, int at56, int at66)
        {
            Assert.Equal(at56, Marken.ThresholdFor(level, group, Year, Year - 56));
            Assert.Equal(at66, Marken.ThresholdFor(level, group, Year, Year - 66));
        }

        // ── Kanterna: "föregående år fyllt" = fyller 56 / 66 i år ─────
        [Theory]
        [InlineData(55, 0)]
        [InlineData(56, 1)]
        [InlineData(60, 1)]   // veteran yngre, men märket ger −1 — inte en tävlingsklassgräns
        [InlineData(65, 1)]
        [InlineData(66, 2)]
        [InlineData(70, 2)]
        public void AgeConcession_Kanter(int ageThisYear, int expected)
        {
            Assert.Equal(expected, Marken.AgeConcession(Year, Year - ageThisYear));
        }

        [Fact]
        public void AgeConcession_OkantFodelsear_IngenEftergift()
        {
            Assert.Equal(0, Marken.AgeConcession(Year, 0));
            Assert.Equal(34, Marken.ThresholdFor(Marken.LevelBrons, "C", Year, 0));
        }

        [Fact]
        public void AgeConcession_FoljerSeriensAr_InteDagens()
        {
            // Född 1970: fyller 56 år 2026 men bara 55 år 2025. En serie skjuten 2025 får ingen eftergift.
            Assert.Equal(0, Marken.AgeConcession(2025, 1970));
            Assert.Equal(1, Marken.AgeConcession(2026, 1970));
        }

        // ── PrecisionThreshold är oförändrad: guld med eftergift ──────
        [Theory]
        [InlineData("A", 40)]
        [InlineData("C", 56)]
        [InlineData("C", 66)]
        [InlineData("B", 0)]
        public void PrecisionThreshold_ArGuldkravet(string group, int age)
        {
            int birthYear = age == 0 ? 0 : Year - age;
            Assert.Equal(Marken.ThresholdFor(Marken.LevelGuld, group, Year, birthYear),
                         Marken.PrecisionThreshold(group, Year, birthYear));
        }

        // ── ValorFor ──────────────────────────────────────────────────
        [Theory]
        [InlineData(50, "C", Marken.LevelGuld)]
        [InlineData(46, "C", Marken.LevelGuld)]
        [InlineData(45, "C", Marken.LevelSilver)]
        [InlineData(40, "C", Marken.LevelSilver)]
        [InlineData(39, "C", Marken.LevelBrons)]
        [InlineData(34, "C", Marken.LevelBrons)]
        [InlineData(33, "C", null)]
        [InlineData(43, "A", Marken.LevelGuld)]
        [InlineData(42, "A", Marken.LevelSilver)]
        [InlineData(32, "A", Marken.LevelBrons)]
        [InlineData(31, "A", null)]
        public void ValorFor_UtanEftergift(int total, string group, string? expected)
        {
            Assert.Equal(expected, Marken.ValorFor(total, group, Year, Young));
        }

        [Theory]
        [InlineData(44, Marken.LevelGuld)]    // 66+ i C: guld 44
        [InlineData(43, Marken.LevelSilver)]
        [InlineData(38, Marken.LevelSilver)]  // silver 40 − 2
        [InlineData(37, Marken.LevelBrons)]
        [InlineData(32, Marken.LevelBrons)]   // brons 34 − 2
        [InlineData(31, null)]
        public void ValorFor_MedEftergiftPaAllaValorer(int total, string? expected)
        {
            Assert.Equal(expected, Marken.ValorFor(total, "C", Year, Year - 66));
        }

        [Fact]
        public void ValorFor_BronsKantMedFemtiofemPlusEftergift()
        {
            // 56 år, C: brons 33. Före 2026-10-02 hade 33 inte räknats som brons alls.
            Assert.Equal(Marken.LevelBrons, Marken.ValorFor(33, "C", Year, Year - 56));
            Assert.Null(Marken.ValorFor(32, "C", Year, Year - 56));
        }
    }
}
