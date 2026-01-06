namespace HpskSite.CompetitionTypes.Precision.ViewModels
{
    public class PrecisionTotal
    {
        public int Id { get; set; }
        public int RegistrationId { get; set; }
        public int? SeriesId { get; set; } // NULL for overall total
        public string TotalType { get; set; } = "Series"; // "Series", "Overall"
        public decimal TotalPoints { get; set; }
        public int InnerTens { get; set; } // Count of X values
        public int Tens { get; set; } // Count of 10 values (including X)
        public decimal MaxPossible { get; set; }
        public DateTime CalculatedDate { get; set; }
        public bool IsOfficial { get; set; }

        // Helper properties
        public decimal Percentage => MaxPossible > 0 ? (TotalPoints / MaxPossible) * 100 : 0;
        public bool IsSeriesTotal => TotalType == "Series" && SeriesId.HasValue;
        public bool IsOverallTotal => TotalType == "Overall" && !SeriesId.HasValue;

        // Static helper methods for calculating totals
        public static PrecisionTotal CalculateSeriesTotal(PrecisionSeries series)
        {
            series.CalculateTotals();

            return new PrecisionTotal
            {
                RegistrationId = series.RegistrationId,
                SeriesId = series.Id,
                TotalType = "Series",
                TotalPoints = series.SeriesTotal,
                InnerTens = series.InnerTens,
                Tens = series.Tens,
                MaxPossible = series.MaxPossible,
                CalculatedDate = DateTime.Now,
                IsOfficial = series.IsCompleted
            };
        }

        public static PrecisionTotal CalculateOverallTotal(List<PrecisionSeries> allSeries, int registrationId)
        {
            var completedSeries = allSeries.Where(s => s.IsCompleted).ToList();

            // Calculate totals
            foreach (var series in completedSeries)
            {
                series.CalculateTotals();
            }

            return new PrecisionTotal
            {
                RegistrationId = registrationId,
                SeriesId = null,
                TotalType = "Overall",
                TotalPoints = completedSeries.Sum(s => s.SeriesTotal),
                InnerTens = completedSeries.Sum(s => s.InnerTens),
                Tens = completedSeries.Sum(s => s.Tens),
                MaxPossible = completedSeries.Sum(s => s.MaxPossible),
                CalculatedDate = DateTime.Now,
                IsOfficial = completedSeries.Count > 0 && completedSeries.All(s => s.IsCompleted)
            };
        }

        // Member class calculation based on total percentage
        public string GetMemberClass()
        {
            return Percentage switch
            {
                >= 95.0m => "Mästarklass",      // Master class - 95%+
                >= 90.0m => "Expertklass",     // Expert class - 90-94.9%
                >= 85.0m => "Avancerad",       // Advanced - 85-89.9%
                >= 80.0m => "Medelklass",      // Intermediate - 80-84.9%
                >= 75.0m => "Nybörjarklass",   // Beginner - 75-79.9%
                _ => "Träningsklass"           // Training class - below 75%
            };
        }

        // Get class description with requirements
        public string GetClassDescription()
        {
            return GetMemberClass() switch
            {
                "Mästarklass" => "Mästarklass (95%+) - Tävlingsnivå",
                "Expertklass" => "Expertklass (90-94.9%) - Avancerad skjutning",
                "Avancerad" => "Avancerad (85-89.9%) - Erfaren skytt",
                "Medelklass" => "Medelklass (80-84.9%) - Stabila resultat",
                "Nybörjarklass" => "Nybörjarklass (75-79.9%) - Grundläggande färdigheter",
                _ => "Träningsklass (<75%) - Lära grunderna"
            };
        }

        // Color coding for UI display
        public string GetClassColor()
        {
            return GetMemberClass() switch
            {
                "Mästarklass" => "#FFD700",    // Gold
                "Expertklass" => "#C0C0C0",    // Silver
                "Avancerad" => "#CD7F32",      // Bronze
                "Medelklass" => "#4CAF50",     // Green
                "Nybörjarklass" => "#2196F3",  // Blue
                _ => "#9E9E9E"                 // Gray
            };
        }

        // Calculate improvement from previous total
        public decimal? CalculateImprovement(PrecisionTotal? previousTotal)
        {
            if (previousTotal == null) return null;
            return TotalPoints - previousTotal.TotalPoints;
        }

        // Calculate trend (improving, declining, stable)
        public string GetTrend(PrecisionTotal? previousTotal)
        {
            var improvement = CalculateImprovement(previousTotal);
            if (!improvement.HasValue) return "Ny";

            return improvement.Value switch
            {
                > 5.0m => "Starkt Stigande",
                > 1.0m => "Stigande",
                > -1.0m => "Stabil",
                > -5.0m => "Fallande",
                _ => "Starkt Fallande"
            };
        }
    }
}
