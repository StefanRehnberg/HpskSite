namespace HpskSite.CompetitionTypes.Precision.ViewModels
{
    public class PrecisionShotEntryViewModel
    {
        public int RegistrationId { get; set; }
        public int SeriesId { get; set; }
        public int SeriesNumber { get; set; }
        public string SeriesType { get; set; } = "Precision";
        public string CompetitionName { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string MemberClass { get; set; } = "";

        // Shot entry data
        public List<ShotEntryRow> Shots { get; set; } = new List<ShotEntryRow>();

        // Series summary
        public int CurrentTotal { get; set; }
        public int CurrentInnerTens { get; set; }
        public int CurrentTens { get; set; }
        public int MaxPossible { get; set; } = 50;
        public decimal Percentage => MaxPossible > 0 ? (CurrentTotal / (decimal)MaxPossible) * 100 : 0;

        // UI state
        public bool IsReadOnly { get; set; }
        public bool IsCompleted { get; set; }
        public string? Notes { get; set; }

        // Initialize with empty shots (5 shots per series)
        public void InitializeShots()
        {
            Shots = Enumerable.Range(1, 5).Select(i => new ShotEntryRow
            {
                ShotNumber = i,
                ShotValue = "0",
                ShotPoints = 0,
                IsValid = true
            }).ToList();
        }

        // Calculate totals from current shot values
        public void CalculateTotals()
        {
            CurrentTotal = Shots.Sum(s => s.ShotPoints);
            CurrentInnerTens = Shots.Count(s => s.ShotValue == "X");
            CurrentTens = Shots.Count(s => s.ShotValue == "10" || s.ShotValue == "X");
        }

        // Validate all shots
        public bool ValidateShots()
        {
            foreach (var shot in Shots)
            {
                shot.ValidateShot();
            }
            return Shots.All(s => s.IsValid);
        }

        // Convert to PrecisionSeries model
        public PrecisionSeries ToPrecisionSeries()
        {
            var series = new PrecisionSeries
            {
                Id = SeriesId,
                RegistrationId = RegistrationId,
                SeriesNumber = SeriesNumber,
                SeriesType = SeriesType,
                IsCompleted = IsCompleted,
                Notes = Notes,
                Shots = Shots.Select(s => new ShotResult
                {
                    ShotNumber = s.ShotNumber,
                    ShotValue = s.ShotValue,
                    ShotPoints = (decimal)s.ShotPoints,
                    EnteredDate = DateTime.Now
                }).ToList()
            };

            series.CalculateTotals();
            return series;
        }
    }

    public class ShotEntryRow
    {
        public int ShotNumber { get; set; }
        public string ShotValue { get; set; } = "0";
        public int ShotPoints { get; set; }
        public bool IsValid { get; set; } = true;
        public string? ValidationMessage { get; set; }

        // Auto-calculate points when shot value is set
        public void SetShotValue(string value)
        {
            ShotValue = value?.Trim().ToUpper() ?? "0";

            // Auto-calculate points based on shot value (integer scoring only)
            ShotPoints = ShotValue switch
            {
                "0" => 0,
                "1" => 1,
                "2" => 2,
                "3" => 3,
                "4" => 4,
                "5" => 5,
                "6" => 6,
                "7" => 7,
                "8" => 8,
                "9" => 9,
                "10" => 10,
                "X" => 10, // X = 10 points (inner 10-ring)
                _ => 0
            };

            ValidateShot();
        }

        // Set integer points (no precision scoring)
        public void SetIntegerPoints(int points)
        {
            ShotPoints = points;

            // Update shot value based on points
            if (points >= 0 && points <= 9)
            {
                ShotValue = points.ToString();
            }
            else if (points == 10)
            {
                ShotValue = "10";
            }
            else
            {
                ShotValue = "0";
                ShotPoints = 0;
            }

            ValidateShot();
        }

        // Validate shot value and points
        public void ValidateShot()
        {
            var validValues = new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "X" };

            if (!validValues.Contains(ShotValue))
            {
                IsValid = false;
                ValidationMessage = "Ogiltigt skottvärde. Använd 0-10 eller X.";
                return;
            }

            // Validate points match shot value (integer scoring)
            var expectedPoints = ShotValue switch
            {
                "0" => 0,
                "1" => 1,
                "2" => 2,
                "3" => 3,
                "4" => 4,
                "5" => 5,
                "6" => 6,
                "7" => 7,
                "8" => 8,
                "9" => 9,
                "10" => 10,
                "X" => 10, // X = 10 points (inner 10-ring)
                _ => 0
            };

            if (ShotPoints != expectedPoints)
            {
                IsValid = false;
                ValidationMessage = $"Poäng matchar inte skottvärde. Förväntat: {expectedPoints}";
                return;
            }

            IsValid = true;
            ValidationMessage = null;
        }

        // Get display string for UI
        public string GetDisplayValue()
        {
            return ShotValue == "X" ? "X" : ShotValue;
        }

        // Get CSS class for styling
        public string GetCssClass()
        {
            if (!IsValid) return "shot-invalid";

            return ShotValue switch
            {
                "X" => "shot-inner-ten",
                "10" => "shot-ten",
                "9" => "shot-nine",
                "8" => "shot-eight",
                "7" => "shot-seven",
                _ => "shot-normal"
            };
        }
    }
}
