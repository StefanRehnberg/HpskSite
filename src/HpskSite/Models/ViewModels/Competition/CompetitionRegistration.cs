using HpskSite.CompetitionTypes.Precision.ViewModels;

namespace HpskSite.Models.ViewModels.Competition
{
    public class CompetitionRegistration
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int CompetitionId { get; set; }
        public string MemberClass { get; set; } = "";
        public DateTime RegistrationDate { get; set; }
        public int? StartNumber { get; set; }
        public string? Notes { get; set; }
        public bool IsActive { get; set; } = true;

        public override string ToString()
        {
            return $"{this.MemberName} - {this.MemberClass}";
        }

        // Navigation properties (populated when needed)
        public string? MemberName { get; set; }
        public string? MemberEmail { get; set; }
        public string? MemberClub { get; set; }
        public string? CompetitionName { get; set; }
        public List<PrecisionSeries> Series { get; set; } = new List<PrecisionSeries>();
        public List<PrecisionTotal> Totals { get; set; } = new List<PrecisionTotal>();

        // Calculated properties
        public bool HasStarted => Series.Any(s => s.Shots.Any());
        public bool IsCompleted => Series.All(s => s.IsCompleted) && Series.Count > 0;
        public decimal OverallTotal => Totals.FirstOrDefault(t => t.IsOverallTotal)?.TotalPoints ?? 0;
        public decimal OverallPercentage => Totals.FirstOrDefault(t => t.IsOverallTotal)?.Percentage ?? 0;
        public int TotalInnerTens => Totals.FirstOrDefault(t => t.IsOverallTotal)?.InnerTens ?? 0;
        public int TotalTens => Totals.FirstOrDefault(t => t.IsOverallTotal)?.Tens ?? 0;

        // Helper methods
        public string GetStatusDisplay()
        {
            if (!HasStarted) return "Inte påbörjad";
            if (IsCompleted) return "Avslutad";
            return "Pågående";
        }

        public string GetStatusColor()
        {
            return GetStatusDisplay() switch
            {
                "Inte påbörjad" => "#9E9E9E", // Gray
                "Pågående" => "#FF9800",       // Orange
                "Avslutad" => "#4CAF50",       // Green
                _ => "#9E9E9E"
            };
        }

        public PrecisionSeries? GetCurrentSeries()
        {
            return Series.FirstOrDefault(s => !s.IsCompleted) ?? Series.LastOrDefault();
        }

        public int GetCompletedSeriesCount()
        {
            return Series.Count(s => s.IsCompleted);
        }

        public void UpdateMemberClass()
        {
            var overallTotal = Totals.FirstOrDefault(t => t.IsOverallTotal);
            if (overallTotal != null)
            {
                MemberClass = overallTotal.GetMemberClass();
            }
        }

        // Validation
        public bool IsValidRegistration()
        {
            return MemberId > 0 &&
                   CompetitionId > 0 &&
                   !string.IsNullOrWhiteSpace(MemberClass) &&
                   RegistrationDate != default;
        }

        // Create a new series for this registration
        public PrecisionSeries CreateNewSeries(int seriesNumber, string seriesType = "Precision")
        {
            var newSeries = new PrecisionSeries
            {
                RegistrationId = Id,
                SeriesNumber = seriesNumber,
                SeriesType = seriesType,
                IsCompleted = false,
                Shots = Enumerable.Range(1, 10).Select(i => new ShotResult
                {
                    ShotNumber = i,
                    ShotValue = "0",
                    ShotPoints = 0.0m,
                    EnteredDate = DateTime.Now
                }).ToList()
            };

            return newSeries;
        }
    }
}