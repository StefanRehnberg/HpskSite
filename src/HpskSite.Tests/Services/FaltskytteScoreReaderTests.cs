using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Talen OCH enheterna i fältskyttets resultatartefakt.
    ///
    /// ⚠️ Poängen med sviten är PARET, inte talet. Prisutdelningssidan läste talet ur artefakten
    /// men räknade fram etiketten på nytt ur tävlingens konfiguration, och de två kan inte hållas
    /// i takt över tid: artefakten står still, konfigurationen kan ändras. Mätt 2026-09-13 på ett
    /// klubbmästerskap i R-fält — prislistan sa "46 p / 19 pmål" där resultatlistan sa
    /// "65 p / 22 pm". Talen var träff och figurer, räknade innan tävlingen blev poängfält.
    ///
    /// Varje test nedan prövar därför talet och dess enhet tillsammans.
    /// </summary>
    public class FaltskytteScoreReaderTests
    {
        /// <summary>Lars-Inge Larssons runda ur rapporten: 8 stationer, 46 träff, 19 figurer, 22 poängmål.</summary>
        private static FaltskytteShooterResult Shooter() => new()
        {
            MemberId = 1,
            Name = "Lars-Inge Larsson",
            TotalHits = 46,
            TotalFigures = 19,
            TotalPoints = 65,          // poängfält: träff + figurer
            TotalTiebreakerScore = 22  // poängmålssumman
        };

        [Fact]
        public void Normalfalt_RaknarTraffOchFigur()
        {
            var scoring = new FaltskytteResultArtifactService.FaltskytteScoreReader("Faltskytte", "Normal");
            var s = Shooter();

            scoring.Primary(s).Should().Be(46);
            scoring.PrimaryUnit.Should().Be("träff");
            scoring.Secondary(s).Should().Be(19);
            scoring.SecondaryUnit.Should().Be("fig");
            scoring.Variant.Should().Be(FaltskytteScoringMode.Normal);
        }

        [Fact]
        public void Poangfalt_RaknarPoangOchPoangmal()
        {
            var scoring = new FaltskytteResultArtifactService.FaltskytteScoreReader("Faltskytte", "Poang");
            var s = Shooter();

            scoring.Primary(s).Should().Be(65);
            scoring.PrimaryUnit.Should().Be("p");
            scoring.Secondary(s).Should().Be(22);
            scoring.SecondaryUnit.Should().Be("pmål");
            scoring.Variant.Should().Be(FaltskytteScoringMode.Poang);
        }

        [Fact]
        public void MagnumFalt_RaknarPoang_OavsettScoringMode()
        {
            // Magnumfält räknar poäng även när konfigurationen säger "Normal" — vapnet avgör.
            var scoring = new FaltskytteResultArtifactService.FaltskytteScoreReader("MagnumFalt", "Normal");
            var s = Shooter();

            scoring.Primary(s).Should().Be(65);
            scoring.PrimaryUnit.Should().Be("p");
            scoring.Variant.Should().Be(FaltskytteScoringMode.Poang);
        }

        [Fact]
        public void OkantScoringMode_FallerTillbakaPaNormalfalt()
        {
            // En artefakt skriven ur en konfiguration utan tävlingstyp får inte tyst bli poängfält:
            // poäng = träff + figurer är alltid ett HÖGRE tal, så gissningen skulle överdriva varje
            // resultat i listan.
            var scoring = new FaltskytteResultArtifactService.FaltskytteScoreReader("Faltskytte", null);

            scoring.Primary(Shooter()).Should().Be(46);
            scoring.PrimaryUnit.Should().Be("träff");
        }

        /// <summary>
        /// ⚠️ Det här är påståendet som faller när enheten och talet kommer ur skilda källor:
        /// paret måste följas åt, i båda varianterna.
        /// </summary>
        [Theory]
        [InlineData("Normal", 46, "träff", 19, "fig")]
        [InlineData("Poang", 65, "p", 22, "pmål")]
        public void TaletOchEnhetenFoljsAt(string mode, int primary, string primaryUnit, int secondary, string secondaryUnit)
        {
            var scoring = new FaltskytteResultArtifactService.FaltskytteScoreReader("Faltskytte", mode);
            var s = Shooter();

            (scoring.Primary(s), scoring.PrimaryUnit).Should().Be((primary, primaryUnit));
            (scoring.Secondary(s), scoring.SecondaryUnit).Should().Be((secondary, secondaryUnit));
        }
    }
}
