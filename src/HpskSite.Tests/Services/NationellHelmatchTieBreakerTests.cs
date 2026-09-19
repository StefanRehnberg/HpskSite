using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.NationellHelmatch.Services;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Tests.TestDataBuilders;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// SHB:s särskiljning i Nationell Helmatch: <i>"Vid särskiljning går den som har högst poäng i
    /// delmoment C före, därefter i delmoment B."</i> Delmoment A är precision (serie 1–4),
    /// B duell (5–8) och C fält-/figurskjutning (9–12).
    ///
    /// Grenen delade tidigare Milsnabbs återräkning på tiopoängspar, och den kördes dessutom EFTER
    /// innertiorna. Testerna här är skrivna så att båda de gamla reglerna svarar fel: paren läses
    /// bakifrån i steg om två (serie 11+12 först), medan delmoment C är alla fyra serierna 9–12.
    /// </summary>
    public class NationellHelmatchTieBreakerTests
    {
        private static readonly NationellHelmatchTieBreaker Sut = new();

        /// <summary>
        /// Tolv serier i ordning: delmoment A (4), B (4), C (4). Varje serie är 5 skott, alltså
        /// högst 50 poäng — testdatat håller sig under taket så byggarens skottsträngar summerar
        /// till precis de tal testet påstår.
        /// </summary>
        private static PrecisionShooterResult Shooter(string name, int[] a, int[] b, int[] c)
        {
            var scores = new List<(int score, int xCount)>();
            foreach (var s in a.Concat(b).Concat(c)) scores.Add((s, 0));
            return new ShooterResultBuilder().WithName(name).WithSeriesAndXCounts(scores).Build();
        }

        /// <summary>Samma skytt, men med en innertia i första serien — för att pröva var i kedjan
        /// innertiorna läses. Serien behåller sin poängsumma (X räknas som 10).</summary>
        private static PrecisionShooterResult ShooterWithX(string name, int[] a, int[] b, int[] c)
        {
            var scores = new List<(int score, int xCount)>();
            foreach (var s in a.Concat(b).Concat(c)) scores.Add((s, 0));
            scores[0] = (scores[0].score, 1);
            return new ShooterResultBuilder().WithName(name).WithSeriesAndXCounts(scores).Build();
        }

        [Fact]
        public void DelmomentC_Decides_BeforeAnythingElse()
        {
            // Samma total (555). Lis har 4 poäng mer i FÄLT och ska gå före.
            var lis  = Shooter("Lis",  new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 46 }, new[] { 44, 44, 44, 47 });
            var karl = Shooter("Karl", new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 50 }, new[] { 44, 44, 44, 43 });

            lis.TotalScore.Should().Be(karl.TotalScore, "testet ska pröva särskiljningen, inte totalen");
            Sut.Compare(lis, karl).Should().BePositive("Lis har högst poäng i delmoment C");
            Sut.Compare(karl, lis).Should().BeNegative();
        }

        [Fact]
        public void DelmomentC_Decides_EvenWhenTheLastTwoSeriesFavourTheOther()
        {
            // ⚠️ Fällan den gamla regeln gick i: Milsnabbs återräkning läser serie 11+12 FÖRST,
            // och där leder Karl (48+48 mot 40+40). Delmoment C är ändå Lis (180 mot 176).
            var lis  = Shooter("Lis",  new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 46 }, new[] { 50, 50, 40, 40 });
            var karl = Shooter("Karl", new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 50 }, new[] { 40, 40, 48, 48 });

            lis.TotalScore.Should().Be(karl.TotalScore);
            Sut.Compare(lis, karl).Should().BePositive("delmoment C är alla fyra fältserierna, inte det sista paret");
        }

        [Fact]
        public void DelmomentB_Decides_WhenDelmomentCIsEqual()
        {
            // Delmoment C lika (180 vs 180). Delmoment B: A har 180, B har 170 — A ska gå före.
            // ⚠️ Serie 7+8 pekar åt ANDRA hållet (80 mot 90), vilket är exakt det par Milsnabbs
            // återräkning hade landat på när fältparen tagit slut. Testet skiljer alltså de två
            // reglerna åt i stället för att bara råka svara likadant.
            var a = Shooter("A", new[] { 40, 40, 40, 40 }, new[] { 50, 50, 40, 40 }, new[] { 45, 45, 45, 45 });
            var b = Shooter("B", new[] { 45, 45, 40, 40 }, new[] { 40, 40, 45, 45 }, new[] { 45, 45, 45, 45 });

            a.TotalScore.Should().Be(b.TotalScore);
            Sut.Compare(a, b).Should().BePositive("delmoment C är lika, då avgör delmoment B");
        }

        [Fact]
        public void EqualInBothDelmoment_IsGenuinelyTied()
        {
            // Lika i C och B, olika i A — men då är totalen olika, så en verklig oavgjord kräver
            // att även A är lika. Svaret ska vara 0: det är särskjutning eller lottning som
            // gäller därefter, och det är inte sorteringens beslut.
            var a = Shooter("A", new[] { 45, 45, 45, 45 }, new[] { 45, 45, 45, 45 }, new[] { 45, 45, 45, 45 });
            var b = Shooter("B", new[] { 46, 44, 45, 45 }, new[] { 45, 45, 45, 45 }, new[] { 44, 46, 45, 45 });

            Sut.Compare(a, b).Should().Be(0);
        }

        [Fact]
        public void MissingSeries_CountAsZero_RatherThanThrowing()
        {
            var full = Shooter("Full", new[] { 45, 45, 45, 45 }, new[] { 45, 45, 45, 45 }, new[] { 45, 45, 45, 45 });
            var partial = new ShooterResultBuilder().WithName("Partial").WithSeries(45, 45, 45, 45).Build();

            Sut.Compare(full, partial).Should().BePositive("skytten utan fältserier har 0 i delmoment C");
        }

        // ── Ordningen stegen läses i ──────────────────────────────────────────

        [Fact]
        public void Delmoment_IsReadBeforeXCount()
        {
            // Rapporten från tävling 7075: skytten med högst FÄLTRESULTAT hamnade under den med
            // flest innertior. Lika total, Karl har en innertia mer, Lis har 4 poäng mer i
            // delmoment C — och SHB:s särskiljning för grenen nämner inte innertior alls.
            var lis  = Shooter("Lis",  new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 46 }, new[] { 44, 44, 44, 47 });
            var karl = ShooterWithX("Karl", new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 50 }, new[] { 44, 44, 44, 43 });

            lis.TotalScore.Should().Be(karl.TotalScore);
            karl.TotalXCount.Should().BeGreaterThan(lis.TotalXCount, "testet ska pröva just innertiornas plats i kedjan");

            var ordered = PrecisionResultOrdering.Order(
                new[] { karl, lis }, "NationellHelmatch", Sut);

            ordered[0].Name.Should().Be("Lis", "delmoment C avgör före innertior i Nationell Helmatch");
        }

        [Fact]
        public void OtherDisciplines_StillReadXCountFirst()
        {
            // Samma data, men som en precisionstävling: där ÄR innertiorna nästa steg efter
            // poängen, och den här ändringen får inte flytta den gränsen.
            var lis  = Shooter("Lis",  new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 46 }, new[] { 44, 44, 44, 47 });
            var karl = ShooterWithX("Karl", new[] { 48, 48, 48, 48 }, new[] { 46, 46, 46, 50 }, new[] { 44, 44, 44, 43 });

            PrecisionResultOrdering.DelmomentBeforeXCount("Precision").Should().BeFalse();

            var ordered = PrecisionResultOrdering.Order(
                new[] { lis, karl }, "Precision", Sut);

            ordered[0].Name.Should().Be("Karl", "utanför helmatchen avgör innertiorna närmast efter poängen");
        }

        // ── Kartan särskiljningen läser delmomenten ur ────────────────────────

        [Fact]
        public void SeriesSegments_NationellHelmatch_IsThreeDelmomentOfFour()
        {
            var segs = SeriesSegments.For("NationellHelmatch", 12);

            segs.Select(s => (s.FirstSeries, s.LastSeries, s.ShortLabel, s.Delmoment))
                .Should().Equal(
                    (1, 4, "Prec", "A"),
                    (5, 8, "Duell", "B"),
                    (9, 12, "Fält", "C"));
        }

        [Fact]
        public void SeriesSegments_AreDroppedWhenTheCompetitionIsNotTwelveSeries()
        {
            SeriesSegments.For("NationellHelmatch", 10).Should().BeEmpty(
                "indelningen är skriven för 12-seriesformatet; en annan uppsättning serier har ingen");
        }

        [Fact]
        public void SeriesSegments_TimeBasedDisciplines_CarryNoDelmomentLetter()
        {
            // En bokstav här vore en särskiljningsregel SHB inte har gett för grenen.
            SeriesSegments.For("Milsnabb", 12).Should().OnlyContain(s => s.Delmoment == null);
            SeriesSegments.For("Standardpistol", 12).Should().OnlyContain(s => s.Delmoment == null);
            SeriesSegments.For("Sportpistol", 12).Should().OnlyContain(s => s.Delmoment == null);
            SeriesSegments.For("Precision", 12).Should().BeEmpty();
        }
    }
}
