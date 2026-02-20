namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Models
{
    public class StrategyParameter
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Type { get; set; } = "int"; // "int", "bool", "string", "select"
        public object? DefaultValue { get; set; }
        public List<SelectOption>? Options { get; set; }
        public string? Placeholder { get; set; }

        /// <summary>Key of another parameter this one depends on. When set, this parameter
        /// is only visible when the other parameter's value equals <see cref="DependsOnValue"/>.</summary>
        public string? DependsOn { get; set; }

        /// <summary>Required value of the <see cref="DependsOn"/> parameter for this parameter to be visible.</summary>
        public string? DependsOnValue { get; set; }
    }

    public class SelectOption
    {
        public string Value { get; set; } = "";
        public string Label { get; set; } = "";
    }
}
