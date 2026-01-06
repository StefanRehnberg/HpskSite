using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.ViewModels;

namespace HpskSite.CompetitionTypes.Precision.Tests
{
    /// <summary>
    /// Tests for precision scoring calculations
    /// Run these tests to verify that the competition scoring system works correctly
    /// </summary>
    public static class PrecisionScoringTests
    {
        public static void RunAllTests()
        {
            Console.WriteLine("=== PRECISION SCORING TESTS ===");
            Console.WriteLine();

            TestShotValidation();
            TestShotValueCalculation();
            TestSeriesCalculation();
            TestPrecisionTotalCalculation();
            TestMemberClassCalculation();

            Console.WriteLine("=== ALL TESTS COMPLETED ===");
        }

        private static void TestShotValidation()
        {
            Console.WriteLine("Testing Shot Validation:");

            // Test valid shots
            var validShots = new[]
            {
                new { Value = "0", Points = 0.0m, Expected = true },
                new { Value = "5", Points = 5.0m, Expected = true },
                new { Value = "10", Points = 10.0m, Expected = true },
                new { Value = "X", Points = 10.5m, Expected = true },
                new { Value = "X", Points = 10.9m, Expected = true }
            };

            foreach (var test in validShots)
            {
                var shot = new ShotResult { ShotValue = test.Value, ShotPoints = test.Points };
                var isValid = shot.IsValidShot();
                Console.WriteLine($"  {test.Value} ({test.Points}p) -> {(isValid ? "✓" : "✗")} {(isValid == test.Expected ? "PASS" : "FAIL")}");
            }

            // Test invalid shots
            var invalidShots = new[]
            {
                new { Value = "X", Points = 9.5m, Expected = false },
                new { Value = "10", Points = 9.0m, Expected = false },
                new { Value = "11", Points = 11.0m, Expected = false },
                new { Value = "X", Points = 11.0m, Expected = false }
            };

            foreach (var test in invalidShots)
            {
                var shot = new ShotResult { ShotValue = test.Value, ShotPoints = test.Points };
                var isValid = shot.IsValidShot();
                Console.WriteLine($"  {test.Value} ({test.Points}p) -> {(isValid ? "✓" : "✗")} {(!isValid == test.Expected ? "PASS" : "FAIL")}");
            }

            Console.WriteLine();
        }

        private static void TestShotValueCalculation()
        {
            Console.WriteLine("Testing Shot Value Calculation:");

            var pointsToValues = new[]
            {
                new { Points = 0.0m, Expected = "0" },
                new { Points = 5.0m, Expected = "5" },
                new { Points = 10.0m, Expected = "10" },
                new { Points = 10.1m, Expected = "X" },
                new { Points = 10.5m, Expected = "X" },
                new { Points = 10.9m, Expected = "X" },
                new { Points = 11.0m, Expected = "0" } // Invalid, should default to 0
            };

            foreach (var test in pointsToValues)
            {
                var result = ShotResult.GetShotValueFromPoints(test.Points);
                Console.WriteLine($"  {test.Points}p -> {result} {(result == test.Expected ? "PASS" : "FAIL")}");
            }

            Console.WriteLine();
        }

        private static void TestSeriesCalculation()
        {
            Console.WriteLine("Testing Series Calculation:");

            // Create a test series with known values
            var series = new PrecisionSeries
            {
                SeriesNumber = 1,
                SeriesType = "Precision"
            };

            // Add 10 shots: 5x10.0, 3x10.5 (X), 2x9.0
            series.Shots = new List<ShotResult>
            {
                new ShotResult { ShotNumber = 1, ShotValue = "10", ShotPoints = 10.0m },
                new ShotResult { ShotNumber = 2, ShotValue = "10", ShotPoints = 10.0m },
                new ShotResult { ShotNumber = 3, ShotValue = "10", ShotPoints = 10.0m },
                new ShotResult { ShotNumber = 4, ShotValue = "10", ShotPoints = 10.0m },
                new ShotResult { ShotNumber = 5, ShotValue = "10", ShotPoints = 10.0m },
                new ShotResult { ShotNumber = 6, ShotValue = "X", ShotPoints = 10.5m },
                new ShotResult { ShotNumber = 7, ShotValue = "X", ShotPoints = 10.5m },
                new ShotResult { ShotNumber = 8, ShotValue = "X", ShotPoints = 10.5m },
                new ShotResult { ShotNumber = 9, ShotValue = "9", ShotPoints = 9.0m },
                new ShotResult { ShotNumber = 10, ShotValue = "9", ShotPoints = 9.0m }
            };

            series.CalculateTotals();

            var expectedTotal = (5 * 10.0m) + (3 * 10.5m) + (2 * 9.0m); // 50 + 31.5 + 18 = 99.5
            var expectedInnerTens = 3; // 3 X values
            var expectedTens = 8; // 5 tens + 3 X values

            Console.WriteLine($"  Expected Total: {expectedTotal}, Actual: {series.SeriesTotal} {(series.SeriesTotal == expectedTotal ? "PASS" : "FAIL")}");
            Console.WriteLine($"  Expected Inner Tens: {expectedInnerTens}, Actual: {series.InnerTens} {(series.InnerTens == expectedInnerTens ? "PASS" : "FAIL")}");
            Console.WriteLine($"  Expected Tens: {expectedTens}, Actual: {series.Tens} {(series.Tens == expectedTens ? "PASS" : "FAIL")}");
            Console.WriteLine($"  Series Valid: {series.IsValidSeries()} {(series.IsValidSeries() ? "PASS" : "FAIL")}");

            Console.WriteLine();
        }

        private static void TestPrecisionTotalCalculation()
        {
            Console.WriteLine("Testing Precision Total Calculation:");

            // Create multiple series
            var series1 = CreateTestSeries(1, 95.5m, 2, 8); // Series 1: 95.5 points
            var series2 = CreateTestSeries(2, 98.0m, 3, 9); // Series 2: 98.0 points
            var series3 = CreateTestSeries(3, 97.2m, 1, 7); // Series 3: 97.2 points

            var allSeries = new List<PrecisionSeries> { series1, series2, series3 };

            // Calculate overall total
            var overallTotal = PrecisionTotal.CalculateOverallTotal(allSeries, 1);

            var expectedTotal = 95.5m + 98.0m + 97.2m; // 290.7
            var expectedInnerTens = 2 + 3 + 1; // 6
            var expectedTens = 8 + 9 + 7; // 24
            var expectedMaxPossible = 3 * 109.0m; // 327.0

            Console.WriteLine($"  Expected Overall Total: {expectedTotal}, Actual: {overallTotal.TotalPoints} {(overallTotal.TotalPoints == expectedTotal ? "PASS" : "FAIL")}");
            Console.WriteLine($"  Expected Inner Tens: {expectedInnerTens}, Actual: {overallTotal.InnerTens} {(overallTotal.InnerTens == expectedInnerTens ? "PASS" : "FAIL")}");
            Console.WriteLine($"  Expected Tens: {expectedTens}, Actual: {overallTotal.Tens} {(overallTotal.Tens == expectedTens ? "PASS" : "FAIL")}");
            Console.WriteLine($"  Expected Max Possible: {expectedMaxPossible}, Actual: {overallTotal.MaxPossible} {(overallTotal.MaxPossible == expectedMaxPossible ? "PASS" : "FAIL")}");

            var expectedPercentage = (expectedTotal / expectedMaxPossible) * 100;
            Console.WriteLine($"  Expected Percentage: {expectedPercentage:F1}%, Actual: {overallTotal.Percentage:F1}% {(Math.Abs(overallTotal.Percentage - expectedPercentage) < 0.1m ? "PASS" : "FAIL")}");

            Console.WriteLine();
        }

        private static void TestMemberClassCalculation()
        {
            Console.WriteLine("Testing Member Class Calculation:");

            var testCases = new[]
            {
                new { Percentage = 96.0m, Expected = "Mästarklass" },
                new { Percentage = 92.0m, Expected = "Expertklass" },
                new { Percentage = 87.0m, Expected = "Avancerad" },
                new { Percentage = 82.0m, Expected = "Medelklass" },
                new { Percentage = 77.0m, Expected = "Nybörjarklass" },
                new { Percentage = 70.0m, Expected = "Träningsklass" }
            };

            foreach (var test in testCases)
            {
                var total = new PrecisionTotal
                {
                    TotalPoints = test.Percentage,
                    MaxPossible = 100.0m // This gives us the percentage directly
                };

                var memberClass = total.GetMemberClass();
                Console.WriteLine($"  {test.Percentage}% -> {memberClass} {(memberClass == test.Expected ? "PASS" : "FAIL")}");
            }

            Console.WriteLine();
        }

        private static PrecisionSeries CreateTestSeries(int seriesNumber, decimal totalPoints, int innerTens, int tens)
        {
            var series = new PrecisionSeries
            {
                Id = seriesNumber,
                SeriesNumber = seriesNumber,
                SeriesType = "Precision",
                IsCompleted = true,
                SeriesTotal = totalPoints,
                InnerTens = innerTens,
                Tens = tens,
                MaxPossible = 109.0m
            };

            // Create dummy shots that would result in the specified totals
            series.Shots = Enumerable.Range(1, 10).Select(i => new ShotResult
            {
                ShotNumber = i,
                ShotValue = i <= innerTens ? "X" : (i <= tens ? "10" : "9"),
                ShotPoints = i <= innerTens ? 10.5m : (i <= tens ? 10.0m : 9.0m)
            }).ToList();

            return series;
        }

        /// <summary>
        /// Test precision scoring edge cases
        /// </summary>
        public static void TestEdgeCases()
        {
            Console.WriteLine("=== TESTING EDGE CASES ===");

            // Test X-shot boundaries
            Console.WriteLine("Testing X-shot boundaries:");
            var xBoundaries = new[]
            {
                new { Points = 10.0m, ShouldBeX = false },
                new { Points = 10.1m, ShouldBeX = true },
                new { Points = 10.5m, ShouldBeX = true },
                new { Points = 10.9m, ShouldBeX = true },
                new { Points = 11.0m, ShouldBeX = false }
            };

            foreach (var test in xBoundaries)
            {
                var shotValue = ShotResult.GetShotValueFromPoints(test.Points);
                var isX = shotValue == "X";
                Console.WriteLine($"  {test.Points}p -> {shotValue} {(isX == test.ShouldBeX ? "PASS" : "FAIL")}");
            }

            // Test incomplete series
            Console.WriteLine("\nTesting incomplete series:");
            var incompleteSeries = new PrecisionSeries();
            incompleteSeries.Shots = Enumerable.Range(1, 5).Select(i => new ShotResult
            {
                ShotNumber = i,
                ShotValue = "10",
                ShotPoints = 10.0m
            }).ToList();

            var isValid = incompleteSeries.IsValidSeries();
            Console.WriteLine($"  5-shot series valid: {isValid} {(!isValid ? "PASS" : "FAIL")}");

            Console.WriteLine("=== EDGE CASE TESTS COMPLETED ===");
        }
    }
}