namespace HpskSite.Models.ViewModels.Competition
{
    public class CompetitionManagementViewModel
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public string CompetitionType { get; set; } = "Precision";
        public DateTime CompetitionDate { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Description { get; set; }

        // Registration management
        public List<CompetitionRegistration> Registrations { get; set; } = new List<CompetitionRegistration>();
        public List<MemberOption> AvailableMembers { get; set; } = new List<MemberOption>();

        // Statistics
        public int TotalRegistrations => Registrations.Count;
        public int CompletedRegistrations => Registrations.Count(r => r.IsCompleted);
        public int ActiveRegistrations => Registrations.Count(r => r.IsActive);
        public decimal AverageScore => Registrations.Where(r => r.IsCompleted).Any()
            ? Registrations.Where(r => r.IsCompleted).Average(r => r.OverallTotal)
            : 0;

        // Member class breakdown
        public Dictionary<string, int> MemberClassDistribution =>
            Registrations.Where(r => r.IsActive)
                        .GroupBy(r => r.MemberClass)
                        .ToDictionary(g => g.Key, g => g.Count());

        // Results and rankings
        public List<CompetitionResult> Results { get; set; } = new List<CompetitionResult>();

        // Get top performers
        public List<CompetitionResult> GetTopPerformers(int count = 10)
        {
            return Results.Where(r => r.IsCompleted)
                         .OrderByDescending(r => r.TotalScore)
                         .ThenByDescending(r => r.InnerTens)
                         .Take(count)
                         .ToList();
        }

        // Get results by class
        public List<CompetitionResult> GetResultsByClass(string memberClass)
        {
            return Results.Where(r => r.MemberClass == memberClass && r.IsCompleted)
                         .OrderByDescending(r => r.TotalScore)
                         .ThenByDescending(r => r.InnerTens)
                         .ToList();
        }

        // Get available member classes
        public List<string> GetAvailableClasses()
        {
            return Registrations.Where(r => r.IsActive)
                               .Select(r => r.MemberClass)
                               .Distinct()
                               .OrderBy(c => c)
                               .ToList();
        }
    }

    public class MemberOption
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Club { get; set; } = "";
        public bool IsAlreadyRegistered { get; set; }
    }

    public class CompetitionResult
    {
        public int RegistrationId { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string MemberClass { get; set; } = "";
        public string Club { get; set; } = "";
        public int? StartNumber { get; set; }

        // Scores
        public decimal TotalScore { get; set; }
        public int InnerTens { get; set; }
        public int Tens { get; set; }
        public decimal Percentage { get; set; }
        public decimal MaxPossible { get; set; }

        // Series breakdown
        public List<SeriesResult> SeriesResults { get; set; } = new List<SeriesResult>();

        // Status
        public bool IsCompleted { get; set; }
        public int CompletedSeries => SeriesResults.Count(s => s.IsCompleted);
        public int TotalSeries => SeriesResults.Count;

        // Ranking (set externally)
        public int Position { get; set; }
        public int ClassPosition { get; set; }

        // Helper methods
        public string GetStatusDisplay()
        {
            if (!IsCompleted && CompletedSeries == 0) return "Inte påbörjad";
            if (!IsCompleted) return $"Pågående ({CompletedSeries}/{TotalSeries})";
            return "Avslutad";
        }

        public string GetScoreDisplay()
        {
            return $"{TotalScore:F1} ({Percentage:F1}%)";
        }

        public string GetPerformanceClass()
        {
            return Percentage switch
            {
                >= 95.0m => "Excellent",
                >= 90.0m => "Very Good",
                >= 85.0m => "Good",
                >= 80.0m => "Average",
                >= 75.0m => "Below Average",
                _ => "Needs Improvement"
            };
        }
    }

    public class SeriesResult
    {
        public int SeriesNumber { get; set; }
        public decimal SeriesScore { get; set; }
        public int SeriesInnerTens { get; set; }
        public int SeriesTens { get; set; }
        public bool IsCompleted { get; set; }
        public DateTime? CompletedDate { get; set; }

        public string GetDisplayScore()
        {
            return IsCompleted ? $"{SeriesScore:F1}" : "-";
        }
    }

    // ViewModel for competition creation/editing
    public class CompetitionEditViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string CompetitionType { get; set; } = "Precision";
        public DateTime CompetitionDate { get; set; } = DateTime.Today;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        public int MaxParticipants { get; set; } = 50;
        public DateTime? RegistrationDeadline { get; set; }

        // Validation
        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(Name) &&
                   CompetitionDate >= DateTime.Today &&
                   MaxParticipants > 0;
        }

        public List<string> GetValidationErrors()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("Tävlingsnamn krävs");

            if (CompetitionDate < DateTime.Today)
                errors.Add("Tävlingsdatum måste vara idag eller senare");

            if (MaxParticipants <= 0)
                errors.Add("Max deltagare måste vara större än 0");

            if (RegistrationDeadline.HasValue && RegistrationDeadline.Value > CompetitionDate)
                errors.Add("Anmälningsdeadline kan inte vara efter tävlingsdatum");

            return errors;
        }
    }
}