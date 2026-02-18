namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Models
{
    public class StrategyParameter
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "int"; // "int", "bool"
        public object? DefaultValue { get; set; }
    }
}
