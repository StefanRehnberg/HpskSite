using HpskSite.Models;

namespace HpskSite.Tests.Services
{
    /// <summary><see cref="MarkenBaseValor"/> — brons och silver ur godkända serier (fas A4).</summary>
    public class MarkenBaseValorTests
    {
        private static MarkenBaseValor.YearEvidence Y(int year, int pb = 0, int ps = 0, int tb = 0, int ts = 0,
            bool falt = false, bool guld = false) => new(year, pb, ps, tb, ts, falt, guld);

        private static MarkenBaseValor.HeldBadge H(string level, int year, string status = Marken.StatusVerified)
            => new(level, year, status);

        [Fact]
        public void TreAvVarje_GerBrons()
        {
            var a = MarkenBaseValor.Derive(new[] { Y(2026, pb: 3, tb: 3) }, Array.Empty<MarkenBaseValor.HeldBadge>());
            Assert.Equal(new[] { new MarkenBaseValor.Award(Marken.LevelBrons, 2026) }, a);
        }

        [Fact]
        public void TvaPrecisionsserier_Racker_Inte()
        {
            Assert.Empty(MarkenBaseValor.Derive(new[] { Y(2026, pb: 2, tb: 3) }, Array.Empty<MarkenBaseValor.HeldBadge>()));
        }

        [Fact]
        public void Faltmedalj_ErsatterDel2()
        {
            var a = MarkenBaseValor.Derive(new[] { Y(2026, pb: 3, falt: true) }, Array.Empty<MarkenBaseValor.HeldBadge>());
            Assert.Single(a);
            Assert.Equal(Marken.LevelBrons, a[0].Level);
        }

        [Fact]
        public void EttMarkePerAr_SilverforstaretEfter()
        {
            // Silvernivå båda åren: brons första året, silver det andra.
            var a = MarkenBaseValor.Derive(new[] { Y(2025, 3, 3, 3, 3), Y(2026, 3, 3, 3, 3) },
                Array.Empty<MarkenBaseValor.HeldBadge>());
            Assert.Equal(new[] { new MarkenBaseValor.Award(Marken.LevelBrons, 2025), new MarkenBaseValor.Award(Marken.LevelSilver, 2026) }, a);
        }

        [Fact]
        public void GuldRaknasAldrigFram()
        {
            var a = MarkenBaseValor.Derive(new[] { Y(2024, 3, 3, 3, 3), Y(2025, 3, 3, 3, 3), Y(2026, 3, 3, 3, 3) },
                Array.Empty<MarkenBaseValor.HeldBadge>());
            Assert.Equal(2, a.Count);
            Assert.DoesNotContain(a, x => x.Level == Marken.LevelGuld);
        }

        [Fact]
        public void HalletBrons_GerSilverEttSenareAr()
        {
            var a = MarkenBaseValor.Derive(new[] { Y(2026, 3, 3, 3, 3) }, new[] { H(Marken.LevelBrons, 2010) });
            Assert.Equal(new[] { new MarkenBaseValor.Award(Marken.LevelSilver, 2026) }, a);
        }

        [Fact]
        public void BronsSammaAr_GerIngetSilverSammaAr()
        {
            Assert.Empty(MarkenBaseValor.Derive(new[] { Y(2026, 3, 3, 3, 3) }, new[] { H(Marken.LevelBrons, 2026) }));
        }

        [Fact]
        public void HalletSilverEllerGuld_GerIngenting()
        {
            Assert.Empty(MarkenBaseValor.Derive(new[] { Y(2026, 3, 3, 3, 3) }, new[] { H(Marken.LevelSilver, 2000) }));
            Assert.Empty(MarkenBaseValor.Derive(new[] { Y(2026, 3, 3, 3, 3) }, new[] { H(Marken.LevelGuld, 1998) }));
        }

        [Fact]
        public void AvvisatBrons_SkapasInteIgen_OchStopparSilver()
        {
            var a = MarkenBaseValor.Derive(new[] { Y(2025, 3, 3, 3, 3), Y(2026, 3, 3, 3, 3) },
                new[] { H(Marken.LevelBrons, 2025, Marken.StatusRejected) });
            Assert.Empty(a);
        }

        [Fact]
        public void Guldfodring_StopparAutomatiken()
        {
            Assert.Empty(MarkenBaseValor.Derive(new[] { Y(2026, 3, 3, 3, 3, guld: true) }, Array.Empty<MarkenBaseValor.HeldBadge>()));
            // ...och alla senare år.
            Assert.Empty(MarkenBaseValor.Derive(new[] { Y(2025, 0, 0, 0, 0, guld: true), Y(2026, 3, 3, 3, 3) },
                Array.Empty<MarkenBaseValor.HeldBadge>()));
        }

        [Fact]
        public void Guldfodring_SenareAr_StopparInteTidigareBrons()
        {
            var a = MarkenBaseValor.Derive(new[] { Y(2025, pb: 3, tb: 3), Y(2026, 3, 3, 3, 3, guld: true) },
                Array.Empty<MarkenBaseValor.HeldBadge>());
            Assert.Equal(new[] { new MarkenBaseValor.Award(Marken.LevelBrons, 2025) }, a);
        }
    }
}
