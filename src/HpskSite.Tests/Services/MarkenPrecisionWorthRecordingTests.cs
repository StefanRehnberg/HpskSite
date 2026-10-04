using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// <see cref="MarkenCompetitionSeriesSync.PrecisionWorthRecording"/> — which competition precision
    /// series the sync materialises (beslut 2026-10-03).
    ///
    /// <para>⚠️ Det som prövas mest är att valören jämförs mot vad medlemmen höll FÖRE seriens år.
    /// Synken raderar rader vars skäl försvunnit; jämförd mot dagens innehav skulle bronsserierna
    /// raderas samma stund bronsmärket de bevisar delas ut.</para>
    /// </summary>
    public class MarkenPrecisionWorthRecordingTests
    {
        private const int None = 0;
        private static readonly int Brons = Marken.LevelOrdinal(Marken.LevelBrons);
        private static readonly int Silver = Marken.LevelOrdinal(Marken.LevelSilver);
        private static readonly int Guld = Marken.LevelOrdinal(Marken.LevelGuld);

        // C, 40 år: guld 46, silver 40, brons 34.
        private static bool C(int total, int held) =>
            MarkenCompetitionSeriesSync.PrecisionWorthRecording(
                total, 46, Marken.ValorFor(total, "C", 2026, 1986), held);

        [Fact]
        public void GuldOchElitgransen_RegistrerasAlltid()
        {
            Assert.True(C(46, Guld));
            Assert.True(C(45, Guld));   // Elit brons, under guldkravet
        }

        [Fact]
        public void BronsSerie_RegistrerasForDenSomSaknarBrons()
        {
            Assert.True(C(34, None));
            Assert.True(C(39, None));
        }

        [Fact]
        public void BronsSerie_RegistrerasInteForDenSomRedanHarBrons()
        {
            Assert.False(C(34, Brons));
            Assert.False(C(39, Silver));
        }

        [Fact]
        public void SilverSerie_RegistrerasForDenSomBaraHarBrons()
        {
            Assert.True(C(40, Brons));
            Assert.True(C(44, Brons));
            Assert.False(C(44, Silver));
        }

        [Fact]
        public void UnderBrons_RegistrerasAldrig()
        {
            Assert.False(C(33, None));
            Assert.False(C(10, None));
        }

        [Fact]
        public void Eftergiften_SankerBronsgransen()
        {
            // 66+ i C: brons 32. En 32:a är en bronsserie för den som saknar brons.
            string? valor = Marken.ValorFor(32, "C", 2026, 1960);
            Assert.Equal(Marken.LevelBrons, valor);
            Assert.True(MarkenCompetitionSeriesSync.PrecisionWorthRecording(32, 44, valor, None));
        }
    }
}
