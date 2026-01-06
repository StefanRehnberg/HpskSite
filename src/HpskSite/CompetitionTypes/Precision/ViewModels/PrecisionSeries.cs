namespace HpskSite.CompetitionTypes.Precision.ViewModels
{
    public class PrecisionSeries
    {
        public int Id { get; set; }
        public int RegistrationId { get; set; }
        public int SeriesNumber { get; set; } // 1, 2, 3, etc.
        public string SeriesType { get; set; } = "Precision"; // "Precision", "Standard", etc.
        public DateTime? CompletedDate { get; set; }
        public bool IsCompleted { get; set; }
        public string? Notes { get; set; }

        // Shot details - array of 10 shots
        public List<ShotResult> Shots { get; set; } = new List<ShotResult>();

        // Calculated totals
        public decimal SeriesTotal { get; set; }
        public int InnerTens { get; set; } // Count of X values
        public int Tens { get; set; } // Count of 10 values (including X)
        public decimal MaxPossible { get; set; } = 109.0m; // Maximum possible for precision series (10 shots × 10.9)

        // Helper methods
        public bool IsValidSeries()
        {
            return Shots.Count == 5 && Shots.All(s => s.IsValidShot());
        }

        public void CalculateTotals()
        {
            if (!IsValidSeries()) return;

            SeriesTotal = Shots.Sum(s => s.ShotPoints);
            InnerTens = Shots.Count(s => s.ShotValue == "X");
            Tens = Shots.Count(s => s.ShotValue == "10" || s.ShotValue == "X");
        }

        public decimal GetSeriesPercentage()
        {
            return MaxPossible > 0 ? (SeriesTotal / MaxPossible) * 100 : 0;
        }
    }

    public class ShotResult
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public int ShotNumber { get; set; } // 1-10
        public string ShotValue { get; set; } = "0"; // "0", "1", "2", ..., "10", "X"
        public decimal ShotPoints { get; set; } // 0.0 to 10.9
        public string? Ring { get; set; } // Inner/Outer ring designation for tens
        public int? EnteredBy { get; set; } // Member ID who entered the shot
        public DateTime EnteredDate { get; set; }
        public int? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }

        // Validation
        public bool IsValidShot()
        {
            // Check if shot value is valid
            var validValues = new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "X" };
            if (!validValues.Contains(ShotValue))
                return false;

            // Check if points match the shot value
            return ShotValue switch
            {
                "0" => ShotPoints == 0.0m,
                "1" => ShotPoints == 1.0m,
                "2" => ShotPoints == 2.0m,
                "3" => ShotPoints == 3.0m,
                "4" => ShotPoints == 4.0m,
                "5" => ShotPoints == 5.0m,
                "6" => ShotPoints == 6.0m,
                "7" => ShotPoints == 7.0m,
                "8" => ShotPoints == 8.0m,
                "9" => ShotPoints == 9.0m,
                "10" => ShotPoints == 10.0m,
                "X" => ShotPoints >= 10.1m && ShotPoints <= 10.9m, // X can be 10.1 to 10.9
                _ => false
            };
        }

        // Helper method to convert points to shot value
        public static string GetShotValueFromPoints(decimal points)
        {
            return points switch
            {
                0.0m => "0",
                1.0m => "1",
                2.0m => "2",
                3.0m => "3",
                4.0m => "4",
                5.0m => "5",
                6.0m => "6",
                7.0m => "7",
                8.0m => "8",
                9.0m => "9",
                10.0m => "10",
                >= 10.1m and <= 10.9m => "X",
                _ => "0"
            };
        }

        // Helper method to get display value with decimals for X shots
        public string GetDisplayValue()
        {
            return ShotValue == "X" ? $"X ({ShotPoints:F1})" : ShotValue;
        }
    }
}
