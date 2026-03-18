namespace HpskSite.Models
{
    public class CompetitionType
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        public CompetitionType(string id, string name, string description)
        {
            Id = id;
            Name = name;
            Description = description;
        }
    }

    public static class CompetitionTypes
    {
        public static readonly List<CompetitionType> All = new List<CompetitionType>
        {
            new CompetitionType("Precision", "Precision", "Precisions skytte på standard 10-ringad precisionstavla"),
            new CompetitionType("Milsnabb", "Milsnabb", "Militärt Snabbskytte på 10-ringad snabbskjutningstavla"),
            new CompetitionType("Duell", "Duell", "Snabbskytte på 10-ringad snabbskjutningstavla"),
            new CompetitionType("NationellHelmatch", "Nationell Helmatch", "Precision, Snabbskytte och Fält"),
            new CompetitionType("Springskytte", "Springskytte", "Springskytte med Springskytte mål"),
            new CompetitionType("Faltkytte", "Fältskytte", "Fältskytte"),
            new CompetitionType("MagnumPrecision", "Magnum Precision", "Magnum Precision 50 m"),
            new CompetitionType("MagnumFalt", "Magnum Fältskytte", "Magnum Fältskytte"),
        };

        public static CompetitionType? GetById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return All.FirstOrDefault(sc => sc.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        public static CompetitionType? GetByName(string name)
        {
            return All.FirstOrDefault(sc => sc.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Fuzzy lookup: normalizes spaces and Swedish characters before matching against Id and Name.
        /// Handles free-text values like "Magnum Fält" matching model Id "MagnumFalt".
        /// </summary>
        public static CompetitionType? GetFuzzy(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var normalized = Normalize(value);
            return All.FirstOrDefault(sc =>
                Normalize(sc.Id) == normalized
                || Normalize(sc.Name) == normalized
                || Normalize(sc.Id).StartsWith(normalized)
                || Normalize(sc.Name).StartsWith(normalized));
        }

        private static string Normalize(string s)
        {
            return s.Replace(" ", "")
                .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
                .Replace("Å", "A").Replace("Ä", "A").Replace("Ö", "O")
                .Replace("é", "e").Replace("É", "E")
                .ToLowerInvariant();
        }

    }
}
