using HpskSite.Models;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Härledningen av blankettens "Datum för godkänt skjutprov" (Föreningsintyg PM 551.24).
    ///
    /// Regeln som prövas: datumet är den dag guldmärkets fordringar <b>senast uppfylldes</b> — inte
    /// den dag märket togs. Blanketten kräver ett datum inom två år och guldmärket är permanent, så
    /// märkets datum kan inte vara svaret.
    ///
    /// ⚠️ Varje test här beskriver ett utfall som skiljer sig från det naiva. Ett påstående som
    /// inte kan falla är värre än inget.
    /// </summary>
    public class SkjutprovCandidateTests
    {
        private static readonly DateTime Today = new(2026, 09, 01);
        private const int NeedPrecision = 3;
        private const int NeedSpeed = 3;

        private static SkjutprovCandidate Derive(
            int? year,
            DateTime[] precision,
            string? part2Source = Marken.SeriesTypeSpeed,
            DateTime[]? tillampning = null,
            DateTime? medalDate = null,
            DateTime? today = null) =>
            SkjutprovCandidate.Derive(
                year, precision, NeedPrecision, part2Source,
                tillampning ?? Array.Empty<DateTime>(), NeedSpeed,
                medalDate, null, today ?? Today);

        private static DateTime D(int y, int m, int d) => new(y, m, d);

        // ── Fullbordandet, inte första eller sista serien ─────────────

        [Fact]
        public void Part1Date_IsTheThirdSeries_NotTheFirst()
        {
            // Fordringen är uppfylld när den TREDJE serien är skjuten. Att ta den första hade
            // daterat intyget till innan fordringen ens var uppfylld.
            var c = Derive(2026,
                new[] { D(2026, 3, 15), D(2026, 4, 4), D(2026, 5, 2) },
                tillampning: new[] { D(2026, 1, 10), D(2026, 1, 11), D(2026, 1, 12) });

            Assert.Equal("2026-05-02", c.Part1Date);
        }

        [Fact]
        public void Part1Date_IsTheThirdSeries_NotTheLast()
        {
            // Serier EFTER fullbordandet ändrar ingenting — de är inte det som uppfyllde fordringen.
            // Verklig form i dev: en skytt har långt fler kvalificerande serier än de tre som krävs.
            var c = Derive(2026,
                new[] { D(2026, 3, 15), D(2026, 4, 4), D(2026, 5, 2), D(2026, 6, 7), D(2026, 8, 20) },
                tillampning: new[] { D(2026, 1, 10), D(2026, 1, 11), D(2026, 1, 12) });

            Assert.Equal("2026-05-02", c.Part1Date);
        }

        [Fact]
        public void SeriesOrderInInputDoesNotMatter()
        {
            // Anroparen sorterar inte — härledningen gör det. Skulle den lita på inmatningsordningen
            // vore datumet beroende av vilken SQL-ORDER BY som råkar gälla.
            var c = Derive(2026,
                new[] { D(2026, 5, 2), D(2026, 3, 15), D(2026, 4, 4) },
                tillampning: new[] { D(2026, 1, 12), D(2026, 1, 10), D(2026, 1, 11) });

            Assert.Equal("2026-05-02", c.Part1Date);
            Assert.Equal("2026-01-12", c.Part2Date);
        }

        // ── Fordringarna är uppfyllda när BÅDA delarna är ────────────

        [Fact]
        public void FulfilmentDate_IsTheLATERofTheTwoParts()
        {
            var c = Derive(2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) },
                tillampning: new[] { D(2026, 6, 1), D(2026, 6, 2), D(2026, 6, 3) });

            Assert.True(c.Derivable);
            Assert.Equal("2026-06-03", c.Date);   // del 2 blev klar sist
        }

        [Fact]
        public void FulfilmentDate_TakesPart1WhenItIsTheLater()
        {
            // Motsatsprovet — utan det kan koden returnera del 2 alltid och ändå se grön ut.
            var c = Derive(2026,
                new[] { D(2026, 7, 1), D(2026, 7, 2), D(2026, 7, 3) },
                tillampning: new[] { D(2026, 2, 1), D(2026, 2, 2), D(2026, 2, 3) });

            Assert.Equal("2026-07-03", c.Date);
        }

        // ── Del 2 kan vila på en standardmedalj i fält ────────────────

        [Fact]
        public void Part2_ViaStandardMedal_UsesTheCompetitionDate()
        {
            // SHB 5.1.1.1 pt 2: en hållen standardmedalj i fält uppfyller hela del 2. Då är
            // tävlingsdagen datumet, inte en series.
            var c = Derive(2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) },
                part2Source: Marken.PartSourceStandardMedal,
                medalDate: D(2026, 5, 20));

            Assert.True(c.Derivable);
            Assert.Equal("2026-05-20", c.Part2Date);
            Assert.Equal("2026-05-20", c.Date);
        }

        [Fact]
        public void Part2_ViaStandardMedal_WithoutDate_IsNotDerivable_AndSaysWhy()
        {
            var c = Derive(2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) },
                part2Source: Marken.PartSourceStandardMedal,
                medalDate: null);

            Assert.False(c.Derivable);
            Assert.Equal("", c.Date);
            Assert.Contains("standardmedalj", c.NotDerivableReason);
        }

        [Fact]
        public void Part2_ViaStandardMedal_IgnoresSpeedSeries()
        {
            // Vilar del 2 på medaljen får snabbserierna inte smyga in och ge ett annat datum.
            var c = Derive(2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) },
                part2Source: Marken.PartSourceStandardMedal,
                tillampning: new[] { D(2026, 8, 1), D(2026, 8, 2), D(2026, 8, 3) },
                medalDate: D(2026, 5, 20));

            Assert.Equal("2026-05-20", c.Date);
        }

        // ── Historiska år har inga datum ─────────────────────────────

        [Fact]
        public void ManuallyAttestedYear_IsNotDerivable_AndSaysWhy()
        {
            // Mätt i dev: guldfodringar för 2023–2025 är attesterade på plats i efterhand
            // (PartSourceManualAttest) och har NULL i båda datumkolumnerna. Där finns inget att
            // härleda, och det ska sägas — inte gissas.
            var c = Derive(2024,
                new[] { D(2024, 3, 1), D(2024, 3, 2), D(2024, 3, 3) },
                part2Source: Marken.PartSourceManualAttest);

            Assert.False(c.Derivable);
            Assert.Equal("", c.Date);
            Assert.Contains("intygad på plats", c.NotDerivableReason.ToLowerInvariant());
            Assert.Contains("2024", c.NotDerivableReason);
        }

        // ── För få serier ────────────────────────────────────────────

        [Fact]
        public void TooFewPrecisionSeries_IsNotDerivable()
        {
            var c = Derive(2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2) },      // bara två
                tillampning: new[] { D(2026, 1, 1), D(2026, 1, 2), D(2026, 1, 3) });

            Assert.False(c.Derivable);
            Assert.Equal("", c.Part1Date);
            Assert.NotEqual("", c.NotDerivableReason);
        }

        [Fact]
        public void TooFewSpeedSeries_IsNotDerivable()
        {
            var c = Derive(2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) },
                tillampning: new[] { D(2026, 1, 1), D(2026, 1, 2) });   // bara två

            Assert.False(c.Derivable);
            Assert.Equal("", c.Date);
        }

        [Fact]
        public void SeriesWithoutADate_DoesNotCount()
        {
            // En serie utan datum kan inte belägga en dag. Räknades den in skulle default(DateTime)
            // sorteras först och göra år 0001 till fullbordandedatum.
            var c = Derive(2026,
                new[] { default, D(2026, 3, 2), D(2026, 3, 3) },
                tillampning: new[] { D(2026, 1, 1), D(2026, 1, 2), D(2026, 1, 3) });

            Assert.False(c.Derivable);
            Assert.DoesNotContain("0001", c.Part1Date);
        }

        // ── Tvåårsfönstret ──────────────────────────────────────────

        [Fact]
        public void WithinTwoYears_IsNotFlagged()
        {
            var c = Derive(2026,
                new[] { D(2025, 10, 1), D(2025, 10, 2), D(2025, 10, 3) },
                tillampning: new[] { D(2025, 9, 1), D(2025, 9, 2), D(2025, 9, 3) });

            Assert.True(c.Derivable);
            Assert.False(c.OlderThanTwoYears);
        }

        [Fact]
        public void OlderThanTwoYears_IsFlagged()
        {
            // Blanketten kräver ett datum inom den senaste tvåårsperioden. Ett äldre datum är inte
            // ett fel i vår data — det är information styrelsen måste se innan den skriver under.
            var c = Derive(2023,
                new[] { D(2023, 1, 1), D(2023, 1, 2), D(2023, 1, 3) },
                tillampning: new[] { D(2023, 1, 4), D(2023, 1, 5), D(2023, 1, 6) });

            Assert.True(c.Derivable);
            Assert.True(c.OlderThanTwoYears);
            Assert.Equal("2023-01-06", c.Date);
        }

        [Fact]
        public void ExactlyTwoYearsOld_IsStillInsideTheWindow()
        {
            // Gränsen prövas explicit: exakt två år tillbaka ligger INOM fönstret. En strikt
            // jämförelse åt fel håll gör ett giltigt datum till en varning.
            var c = Derive(2024,
                new[] { D(2024, 9, 1), D(2024, 9, 1), D(2024, 9, 1) },
                tillampning: new[] { D(2024, 9, 1), D(2024, 9, 1), D(2024, 9, 1) });

            Assert.Equal("2024-09-01", c.Date);
            Assert.False(c.OlderThanTwoYears);
        }

        [Fact]
        public void OneDayBeforeTheWindow_IsFlagged()
        {
            var c = Derive(2024,
                new[] { D(2024, 8, 31), D(2024, 8, 31), D(2024, 8, 31) },
                tillampning: new[] { D(2024, 8, 31), D(2024, 8, 31), D(2024, 8, 31) });

            Assert.True(c.OlderThanTwoYears);
        }

        // ── Inget att härleda ur ─────────────────────────────────────

        [Fact]
        public void NoFulfilledGuldfodring_SaysSo()
        {
            var c = Derive(null, Array.Empty<DateTime>());

            Assert.False(c.Derivable);
            Assert.Null(c.Year);
            Assert.Contains("guldfodring", c.NotDerivableReason.ToLowerInvariant());
        }

        [Fact]
        public void Part2Basis_IsCarriedThroughForTheIssuer()
        {
            // Intygaren måste kunna se VAD som uppfyllde del 2 — det avgör om datumet är rimligt.
            var c = SkjutprovCandidate.Derive(
                2026,
                new[] { D(2026, 3, 1), D(2026, 3, 2), D(2026, 3, 3) },
                NeedPrecision, Marken.SeriesTypeSpeed,
                new[] { D(2026, 4, 1), D(2026, 4, 2), D(2026, 4, 3) },
                NeedSpeed, null, "3/3 snabbserier", Today);

            Assert.Equal("3/3 snabbserier", c.Part2Basis);
        }

        [Fact]
        public void TheYearIsAlwaysReported_EvenWhenNotDerivable()
        {
            // Året är användbart även utan datum: det säger intygaren var hen ska leta.
            var c = Derive(2025,
                new[] { D(2025, 3, 1) },
                part2Source: Marken.PartSourceManualAttest);

            Assert.Equal(2025, c.Year);
        }
    }
}
