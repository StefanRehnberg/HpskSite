using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// Root config object stored as JSON on competition property "stationConfig".
    /// Contains per-weapon-class station configurations.
    /// </summary>
    public class FaltskytteCompetitionConfig
    {
        /// <summary>
        /// Keyed by weapon class: "A", "B", "C", "R", "M1"-"M9".
        /// </summary>
        public Dictionary<string, FaltskytteWeaponClassConfig> WeaponConfigs { get; set; } = new();

        /// <summary>Gets station config for a specific weapon class. Falls back to first available if not found.</summary>
        public FaltskytteWeaponClassConfig? GetForWeaponClass(string weaponClass)
        {
            if (WeaponConfigs.TryGetValue(weaponClass, out var config))
                return config;
            // Try first character (e.g. "C2" → "C")
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
    /// Configuration for a single station in a Fältskytte competition.
    /// Includes Förutsättningar (shooting rules) and Målgrupper (target groups).
    /// </summary>
    public class FaltskytteStationConfig
    {
        /// <summary>Station number (1-based)</summary>
        public int Station { get; set; }

        /// <summary>Shooting time in seconds (typically 10-45)</summary>
        public int ShootingTimeSec { get; set; }

        /// <summary>Stående, Knästående, Sittande, Liggande, Valfri</summary>
        public string ShooterStartPosition { get; set; } = "";

        /// <summary>45 grader, Riktning tillåten</summary>
        public string WeaponStartPosition { get; set; } = "";

        /// <summary>Stödhand tillåten, Ej stödhand</summary>
        public string SupportHand { get; set; } = "";

        /// <summary>Max counted shots per figure in Normal mode. Ignored in Poäng mode.</summary>
        public int MaxShotsPerFigure { get; set; } = 6;

        /// <summary>Target groups at this station</summary>
        public List<FaltskytteTargetGroup> TargetGroups { get; set; } = new();

        // Computed from target groups
        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TotalFigures => TargetGroups.Sum(g => g.Figures);

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public int TotalPoangmal => TargetGroups.Sum(g => g.PoangmalCount);

        [System.Text.Json.Serialization.JsonIgnore]
        [JsonIgnore]
        public bool HasPoangmal => TotalPoangmal > 0;
    }

    /// <summary>A target group (Målgrupp) within a station.</summary>
    public class FaltskytteTargetGroup
    {
        /// <summary>Group number (1-based)</summary>
        public int Group { get; set; }

        /// <summary>Number of target figures in this group</summary>
        public int Figures { get; set; }

        /// <summary>How many figures have scoring rings (poångmål)</summary>
        public int PoangmalCount { get; set; }

        /// <summary>Fast, Framsvängande, Bortsvängande</summary>
        public string Behavior { get; set; } = "Fast";

        // ── Framsvängande fields ──
        /// <summary>Seconds delay before target appears (Framsvängande only)</summary>
        public int? DelayBeforeShowSec { get; set; }

        /// <summary>Seconds the target is visible (Framsvängande only)</summary>
        public int? ShowTimeSec { get; set; }

        // ── Bortsvängande fields ──
        /// <summary>Seconds before target swings away (Bortsvängande only)</summary>
        public int? HideAfterSec { get; set; }

        /// <summary>Seconds until target reappears briefly (Bortsvängande, 0 or null = no reappear)</summary>
        public int? ReappearSec { get; set; }

        /// <summary>URL to uploaded image of the target figures used in this group</summary>
        public string? ImageUrl { get; set; }
    }

    /// <summary>
    /// Helper to parse station config from JSON, handling both old flat format and new per-weapon-class format.
    /// </summary>
    public static class FaltskytteConfigParser
    {
        /// <summary>
        /// Parses the stationConfig JSON string, auto-migrating old flat format.
        /// </summary>
        public static FaltskytteCompetitionConfig Parse(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new FaltskytteCompetitionConfig();

            var trimmed = json.TrimStart();

            // Object format: starts with {
            if (trimmed.StartsWith("{"))
            {
                // Try wrapped format first: { "WeaponConfigs": { "C": {...} } }
                var wrapped = JsonConvert.DeserializeObject<FaltskytteCompetitionConfig>(json);
                if (wrapped?.WeaponConfigs?.Any() == true)
                    return wrapped;

                // Direct format from JS: { "C": { "stations": [...] }, "A": {...} }
                var direct = JsonConvert.DeserializeObject<Dictionary<string, FaltskytteWeaponClassConfig>>(json);
                if (direct?.Any() == true)
                {
                    return new FaltskytteCompetitionConfig { WeaponConfigs = direct };
                }

                return new FaltskytteCompetitionConfig();
            }

            // Old flat format: starts with [ (array of stations)
            if (trimmed.StartsWith("["))
            {
                // Try to deserialize as new-style station list first
                var stations = JsonConvert.DeserializeObject<List<FaltskytteStationConfig>>(json);
                if (stations != null && stations.Any())
                {
                    return new FaltskytteCompetitionConfig
                    {
                        WeaponConfigs = new Dictionary<string, FaltskytteWeaponClassConfig>
                        {
                            ["C"] = new FaltskytteWeaponClassConfig { Stations = stations }
                        }
                    };
                }

                // Try legacy flat format (just station number + figures + poangmalCount)
                try
                {
                    var legacy = JsonConvert.DeserializeObject<List<LegacyStationConfig>>(json);
                    if (legacy != null && legacy.Any())
                    {
                        var migrated = legacy.Select(l => new FaltskytteStationConfig
                        {
                            Station = l.Station,
                            ShootingTimeSec = 30,
                            ShooterStartPosition = "Valfri",
                            WeaponStartPosition = "45 grader",
                            SupportHand = "Ej stödhand",
                            MaxShotsPerFigure = 6,
                            TargetGroups = new List<FaltskytteTargetGroup>
                            {
                                new FaltskytteTargetGroup
                                {
                                    Group = 1,
                                    Figures = l.Figures,
                                    PoangmalCount = l.PoangmalCount,
                                    Behavior = "Fast"
                                }
                            }
                        }).ToList();

                        return new FaltskytteCompetitionConfig
                        {
                            WeaponConfigs = new Dictionary<string, FaltskytteWeaponClassConfig>
                            {
                                ["C"] = new FaltskytteWeaponClassConfig { Stations = migrated }
                            }
                        };
                    }
                }
                catch { }
            }

            return new FaltskytteCompetitionConfig();
        }

        // Legacy format from the original flat station config
        private class LegacyStationConfig
        {
            public int Station { get; set; }
            public int Figures { get; set; }
            public int PoangmalCount { get; set; }
        }
    }
}
