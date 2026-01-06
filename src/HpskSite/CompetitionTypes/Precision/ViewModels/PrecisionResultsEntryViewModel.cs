using System.ComponentModel.DataAnnotations;

namespace HpskSite.CompetitionTypes.Precision.ViewModels
{
    public class PrecisionResultsEntryViewModel
    {
        public int RegistrationId { get; set; }
        public int SeriesId { get; set; }
        public int SeriesNumber { get; set; }
        public string SeriesName { get; set; } = "";
        public List<ShooterEntryViewModel> Shooters { get; set; } = new List<ShooterEntryViewModel>();
    }

    public class ShooterEntryViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string Class { get; set; } = "";
        public List<int> Shots { get; set; } = new List<int>();
        
        // Calculated properties
        public int Total => Shots?.Sum() ?? 0;
        public int XCount => Shots?.Count(s => s == 10) ?? 0;
        public bool IsComplete => Shots?.Count == 5 && Shots.All(s => s >= 0 && s <= 10);
    }
}
