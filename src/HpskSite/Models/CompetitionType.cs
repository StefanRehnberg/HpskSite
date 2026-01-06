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
            new CompetitionType("Nationell_Helmatch", "Nationell Helmatch", "Precision, Snabbskytte och Fält "),
            new CompetitionType("Springskytte", "Springskytte", "Springskytte med Springskytte mål"),
            new CompetitionType("Faltkytte", "Fältskytte", "Fältskytte"),
            new CompetitionType("Faltkytte_Norsk", "PoängFältskytte", "PoängFältskytte"),
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

    }
}
