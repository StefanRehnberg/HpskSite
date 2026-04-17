using System.Text.Json;

namespace HpskSite.Models
{
    /// <summary>
    /// Configuration for Direktplacering (self-service team/time booking at registration).
    /// Stored as JSON in the competition's "direktplaceringConfig" property.
    /// </summary>
    public class DirektplaceringConfig
    {
        public bool Enabled { get; set; }
        public bool AllowMixedClasses { get; set; } = true;
        public List<DirektplaceringTeam> Teams { get; set; } = new();

        /// <summary>
        /// Parse the JSON config from a competition's property value.
        /// Returns null if not enabled or invalid.
        /// </summary>
        public static DirektplaceringConfig? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var config = JsonSerializer.Deserialize<DirektplaceringConfig>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return config?.Enabled == true ? config : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public class DirektplaceringTeam
    {
        public int TeamNumber { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public int Positions { get; set; }
        public string Label { get; set; } = "";
        public List<string> AllowedWeaponClasses { get; set; } = new();
    }
}
