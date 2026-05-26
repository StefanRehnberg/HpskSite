using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// Root config object stored as JSON on competition property "stationConfig".
    /// Contains per-weapon-class station configurations.
    /// </summary>
    public class FaltskytteCompetitionConfig
    {
        /// <summary>Keyed by weapon class: "A", "A_Opt", "B", "C", "R", "M1"-"M9".</summary>
        public Dictionary<string, FaltskytteWeaponClassConfig> WeaponConfigs { get; set; } = new();

        /// <summary>Gets station config for a specific weapon class. Falls back to first available.</summary>
        public FaltskytteWeaponClassConfig? GetForWeaponClass(string weaponClass)
        {
            if (WeaponConfigs.TryGetValue(weaponClass, out var config))
                return config;
            // Fallback: if weaponClass is a shooting class ID (e.g. "A_opt_2"), look up by weapon group code.
            var groupCode = ShootingClasses.GetWeaponClassCode(weaponClass);
            if (!string.IsNullOrEmpty(groupCode) && WeaponConfigs.TryGetValue(groupCode, out config))
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

        /// <summary>True when this station is reserved for Särskjutning (shoot-off) only.
        /// Such stations are filtered out of patrol generation, the result-list station
        /// count, the public station card, the admin station-entry links, and result
        /// aggregation. They remain selectable in the Särskjutning station picker on
        /// the result-management page. Treated as a station-level decision — all
        /// weapon-class instances of the same Station number should carry the same
        /// flag (the configurator UI propagates the checkbox automatically).</summary>
        public bool IsShootOffOnly { get; set; } = false;

        public List<FaltskytteTargetGroup> TargetGroups { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TotalFigures => TargetGroups.Sum(g => g.FigureCount);

        /// <summary>Total scoring slots (accounts for multi-target figures)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TotalTargets => TargetGroups.Sum(g => g.TargetCount);

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

        /// <summary>Total scoring slots (accounts for multi-target figures)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TargetCount => Figures.Sum(f => Math.Max(1, f.TargetsPerFigure));

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
        /// <summary>Minimum required shots at this figure to score full points
        /// (per SHB D.10.3 — "minsta antal träff skall anges"). Null = no minimum.</summary>
        public int? MinShotsPerFigure { get; set; }
        /// <summary>Maximum allowed shots at this figure (per SHB D.10.3 — "högsta
        /// antal träff skall anges"). Null = falls back to station-level
        /// MaxShotsPerFigure.</summary>
        public int? MaxShotsPerFigure { get; set; }
        public string? ImageUrl { get; set; }
        /// <summary>Optional reference to a catalog target name</summary>
        public string? TargetName { get; set; }
        /// <summary>Optional color variant selected from catalog</summary>
        public string? TargetColor { get; set; }
        /// <summary>Number of individual targets in this figure. Default 1. A multi-target figure (e.g. 3 silhouettes) creates multiple scoring slots.</summary>
        public int TargetsPerFigure { get; set; } = 1;
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
                // Strip metadata keys (e.g. _linkedGroups) before deserializing — they aren't weapon configs
                var jobj = JObject.Parse(json);
                foreach (var key in jobj.Properties().Where(p => p.Name.StartsWith("_")).Select(p => p.Name).ToList())
                    jobj.Remove(key);
                var direct = jobj.ToObject<Dictionary<string, FaltskytteWeaponClassConfig>>();
                if (direct?.Any() == true)
                    return new FaltskytteCompetitionConfig { WeaponConfigs = direct };
            }

            return new FaltskytteCompetitionConfig();
        }
    }
}
