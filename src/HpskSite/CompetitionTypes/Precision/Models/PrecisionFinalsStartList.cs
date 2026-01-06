using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    /// <summary>
    /// Precision Finals Start List document type for championship competitions.
    /// Contains the qualified finalists organized by championship class with proper start order.
    /// </summary>
    public class PrecisionFinalsStartList : HpskSite.Models.BasePage
    {
        public PrecisionFinalsStartList(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Basic properties
        public int CompetitionId => this.Value<int>("competitionId", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        public int QualificationStartListId => this.Value<int>("qualificationStartListId", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        public DateTime GeneratedDate => this.Value<DateTime>("generatedDate", fallback: Fallback.ToDefaultValue, defaultValue: DateTime.Now);
        public string GeneratedBy => this.Value<string>("generatedBy") ?? "";
        public bool IsOfficialFinalsStartList => this.Value<bool>("isOfficialFinalsStartList", fallback: Fallback.ToDefaultValue, defaultValue: false);
        
        // Configuration data stored as JSON
        public string ConfigurationData => this.Value<string>("configurationData") ?? "";
        
        // Team format (e.g., "Championship Finals")
        public string TeamFormat => this.Value<string>("teamFormat") ?? "Championship Finals";
        
        // Number of qualified finalists
        public int TotalFinalists => this.Value<int>("totalFinalists", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        
        // Max shooters per team
        public int MaxShootersPerTeam => this.Value<int>("maxShootersPerTeam", fallback: Fallback.ToDefaultValue, defaultValue: 20);

        // Get parent competition
        public IPublishedContent? Competition
        {
            get
            {
                var parent = this.Parent();
                // Finals start list should be under a "Start Lists Hub" which is under Competition
                if (parent?.ContentType.Alias == "competitionStartListsHub")
                {
                    return parent.Parent();
                }
                // Or could be directly under competition
                if (parent?.ContentType.Alias == "competition")
                {
                    return parent;
                }
                return null;
            }
        }

        // Display helpers
        public string GetStatusDisplay()
        {
            return IsOfficialFinalsStartList ? "Officiell Finalstartlista" : "Preliminär Finalstartlista";
        }

        public string GetStatusBadgeClass()
        {
            return IsOfficialFinalsStartList ? "badge bg-success" : "badge bg-warning text-dark";
        }
    }
}
