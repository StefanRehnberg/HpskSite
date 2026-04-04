using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// Root config object stored as JSON on competition property "stationConfig".
    /// Contains per-weapon-class station configurations.
    /// </summary>
    public class FaltskytteCompetitionConfig
    {
        /// <summary>Keyed by weapon class: "A", "B", "C", "R", "M1"-"M9".</summary>
        public Dictionary<string, FaltskytteWeaponClassConfig> WeaponConfigs { get; set; } = new();

        /// <summary>Gets station config for a specific weapon class. Falls back to first available.</summary>
        public FaltskytteWeaponClassConfig? GetForWeaponClass(string weaponClass)
        {
            if (WeaponConfigs.TryGetValue(weaponClass, out var config))
                return config;
            if (weaponClass.Length > 0 && WeaponConfigs.TryGetValue(weaponClass.Substring(0, 1), out config))
                return config;
            return WeaponConfigs.Values.FirstOrDefault();
        }
    }

    /// <summary>Station configurations for one weapon class.</summary>
    public class FaltskytteWeaponClassConfig
    {
        public List<FaltskytteStationConfig> Stations { get; set; } = new();
    }

    /// <summary>
    /// Configuration for a single station. Includes Förutsättningar and Målgrupper.
    /// </summary>
    public class FaltskytteStationConfig
    {
        public int Station { get; set; }
        public int ShootingTimeSec { get; set; }
        public string ShooterStartPosition { get; set; } = "";
        public string WeaponStartPosition { get; set; } = "";
        public string SupportHand { get; set; } = "";
        public int MaxShotsPerFigure { get; set; } = 6;
        public List<FaltskytteTargetGroup> TargetGroups { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TotalFigures => TargetGroups.Sum(g => g.FigureCount);

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TotalPoangmal => TargetGroups.Sum(g => g.PoangmalCount);

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public bool HasPoangmal => TotalPoangmal > 0;
    }

    /// <summary>A target group (Målgrupp) containing one or more individual figures.</summary>
    public class FaltskytteTargetGroup
    {
        public int Group { get; set; }
        public List<FaltskytteFigure> Figures { get; set; } = new();
        /// <summary>Photo of the complete target group setup</summary>
        public string? ImageUrl { get; set; }
        /// <summary>Descriptive text about this target group (for station cards)</summary>
        public string? Description { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int FigureCount => Figures.Count;

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int PoangmalCount => Figures.Count(f => f.IsPoangmal);

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public bool HasPoangmal => Figures.Any(f => f.IsPoangmal);
    }

    /// <summary>An individual target figure within a Målgrupp.</summary>
    public class FaltskytteFigure
    {
        public int FigureNumber { get; set; }
        public bool IsPoangmal { get; set; }
        /// <summary>Fast, Framsvängande, Bortsvängande</summary>
        public string Behavior { get; set; } = "Fast";
        public int? DelayBeforeShowSec { get; set; }
        public int? ShowTimeSec { get; set; }
        public int? HideAfterSec { get; set; }
        public int? ReappearSec { get; set; }
        public string? ImageUrl { get; set; }
        /// <summary>Optional reference to a catalog target name</summary>
        public string? TargetName { get; set; }
        /// <summary>Optional color variant selected from catalog</summary>
        public string? TargetColor { get; set; }
    }

    /// <summary>Parses station config JSON. Handles direct format from JS.</summary>
    public static class FaltskytteConfigParser
    {
        public static FaltskytteCompetitionConfig Parse(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new FaltskytteCompetitionConfig();

            var trimmed = json.TrimStart();

            if (trimmed.StartsWith("{"))
            {
                // Try wrapped format: { "WeaponConfigs": { "C": {...} } }
                var wrapped = JsonConvert.DeserializeObject<FaltskytteCompetitionConfig>(json);
                if (wrapped?.WeaponConfigs?.Any() == true)
                    return wrapped;

                // Direct format from JS: { "C": { "stations": [...] } }
                var direct = JsonConvert.DeserializeObject<Dictionary<string, FaltskytteWeaponClassConfig>>(json);
                if (direct?.Any() == true)
                {
                    // Remove metadata keys (e.g. _linkedGroups from configurator UI)
                    foreach (var key in direct.Keys.Where(k => k.StartsWith("_")).ToList())
                        direct.Remove(key);
                    return new FaltskytteCompetitionConfig { WeaponConfigs = direct };
                }
            }

            return new FaltskytteCompetitionConfig();
        }
    }
}
