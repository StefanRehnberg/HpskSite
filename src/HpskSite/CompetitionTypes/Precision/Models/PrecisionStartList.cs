using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    public class PrecisionStartList : HpskSite.Models.BasePage
    {
        public PrecisionStartList(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Basic properties
        public int CompetitionId => this.Value<int>("competitionId");
        public string TeamFormat => this.Value<string>("teamFormat") ?? "";
        public string StartListContent => this.Value<string>("startListContent") ?? "";
        public string ConfigurationData => this.Value<string>("configurationData") ?? "";
        public DateTime GeneratedDate => this.Value<DateTime>("generatedDate", fallback: Fallback.ToDefaultValue, defaultValue: DateTime.Now);
        public string GeneratedBy => this.Value<string>("generatedBy") ?? "";
        public string Notes => this.Value<string>("notes") ?? "";
        public new bool IsPublished => this.Value<bool>("isOfficialStartList", fallback: Fallback.ToDefaultValue, defaultValue: false);

        // Team format helpers
        public bool IsMixedTeams => TeamFormat == "Mixade Skjutlag";
        public bool IsSeparatedClasses => TeamFormat == "En vapengrupp per Skjutlag";

        // Get linked competition
        public IPublishedContent? LinkedCompetition
        {
            get
            {
                if (CompetitionId == 0) return null;

                // Find competition by ID in the content tree
                var root = this.Root();
                return root.DescendantsOrSelf()
                          .FirstOrDefault(x => x.ContentType.Alias == "competition" && x.Id == CompetitionId);
            }
        }

        public string CompetitionName => LinkedCompetition?.Name ?? "Okänd tävling";

        // Configuration data helpers
        public StartListConfiguration? Configuration
        {
            get
            {
                if (string.IsNullOrEmpty(ConfigurationData))
                    return null;

                try
                {
                    return JsonConvert.DeserializeObject<StartListConfiguration>(ConfigurationData);
                }
                catch
                {
                    return null;
                }
            }
        }

        // Check if start list has been generated
        public bool HasContent => !string.IsNullOrEmpty(StartListContent);

        // Get team count from configuration
        public int TeamCount => Configuration?.Teams?.Count ?? 0;

        // Get total shooters from configuration
        public int TotalShooters => Configuration?.Teams?.Sum(t => t.Shooters?.Count ?? 0) ?? 0;

        // Display helpers
        public string GetTeamFormatDisplay()
        {
            return TeamFormat switch
            {
                "Mixade Skjutlag" => "Mixade vapengrupper per skjutlag",
                "En vapengrupp per Skjutlag" => "En vapengrupp per skjutlag",
                _ => TeamFormat
            };
        }

        public string GetStatusDisplay()
        {
            if (!HasContent) return "Ej genererad";
            return IsPublished ? "Publicerad" : "Ej publicerad";
        }

        public string GetStatusColor()
        {
            if (!HasContent) return "secondary";
            return IsPublished ? "success" : "warning";
        }
    }

    // Configuration classes for JSON data
    public class StartListConfiguration
    {
        public StartListSettings? Settings { get; set; }
        public List<StartListTeam>? Teams { get; set; }
    }

    public class StartListSettings
    {
        public string Format { get; set; } = "";
        public int MaxShootersPerTeam { get; set; } = 30;
        public string StartInterval { get; set; } = "1:45";
        public string FirstStartTime { get; set; } = "09:00";
        public DateTime Generated { get; set; } = DateTime.Now;
    }

    public class StartListTeam
    {
        public int TeamNumber { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public List<string> WeaponClasses { get; set; } = new List<string>();
        public int ShooterCount { get; set; }
        public List<StartListShooter>? Shooters { get; set; }
    }

    public class StartListShooter
    {
        public int Position { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string WeaponClass { get; set; } = "";
        public int MemberId { get; set; }
    }
}
