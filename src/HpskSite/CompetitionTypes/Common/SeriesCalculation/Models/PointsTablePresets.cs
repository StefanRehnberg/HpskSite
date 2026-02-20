namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Models
{
    /// <summary>
    /// Common points table presets for strategy parameter dropdowns.
    /// </summary>
    public static class PointsTablePresets
    {
        /// <summary>Presets for individual shooter placement scoring.</summary>
        public static List<SelectOption> Individual => new()
        {
            new() { Value = "25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1", Label = "F1-poäng (25, 20, 16, 13... 15 platser)" },
            new() { Value = "12, 10, 8, 6, 5, 4, 3, 2, 1", Label = "Topp 9 (12, 10, 8, 6...)" },
            new() { Value = "10, 8, 6, 5, 4, 3, 2, 1", Label = "Topp 8 (10, 8, 6, 5...)" },
            new() { Value = "7, 5, 4, 3, 2, 1", Label = "Topp 6 (7, 5, 4, 3, 2, 1)" },
            new() { Value = "5, 4, 3, 2, 1", Label = "Topp 5 (5, 4, 3, 2, 1)" },
            new() { Value = "3, 2, 1", Label = "Prispall (3, 2, 1)" },
            new() { Value = "custom", Label = "Egen tabell..." },
        };

        /// <summary>Presets for club team placement scoring (typically fewer participants).</summary>
        public static List<SelectOption> Club => new()
        {
            new() { Value = "10, 8, 6, 5, 4, 3, 2, 1", Label = "Topp 8 (10, 8, 6, 5...)" },
            new() { Value = "7, 5, 4, 3, 2, 1", Label = "Topp 6 (7, 5, 4, 3, 2, 1)" },
            new() { Value = "5, 4, 3, 2, 1", Label = "Topp 5 (5, 4, 3, 2, 1)" },
            new() { Value = "3, 2, 1", Label = "Prispall (3, 2, 1)" },
            new() { Value = "25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1", Label = "F1-poäng (25, 20, 16, 13... 15 platser)" },
            new() { Value = "custom", Label = "Egen tabell..." },
        };
    }
}
