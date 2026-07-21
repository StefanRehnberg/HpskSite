namespace HpskSite.CompetitionTypes.Faltskytte.Models
{
    /// <summary>
    /// Bill-of-materials (materiellista) for a Fältskytte competition, derived from the attached station
    /// configuration: what target figures each station needs, with visuals, plus a competition-wide order
    /// roll-up. Since a station's config can differ per weapon class but the station is built ONCE, counts
    /// aggregate per-station as the MAX any single weapon class needs of each figure type (union/max).
    /// </summary>
    public class FaltskytteBomFigure
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";      // "Älg (röd)" / "Figur (Fast)"
        public string? ImageUrl { get; set; }
        public int? SizeGroup { get; set; }           // from the Figurkatalog (FieldTarget), when resolvable
        public bool IsPoangmal { get; set; }
        public int Figures { get; set; }              // number of physical figures
        public int Targets { get; set; }              // scoring targets (figures × TargetsPerFigure)
    }

    public class FaltskytteBomStation
    {
        public int Station { get; set; }
        public string? Name { get; set; }
        public List<string> WeaponClasses { get; set; } = new();   // classes this station is configured for
        public List<FaltskytteBomFigure> Figures { get; set; } = new();
        public int TotalFigures => Figures.Sum(f => f.Figures);
        public int TotalTargets => Figures.Sum(f => f.Targets);
    }

    public class FaltskytteBomResult
    {
        public string CompetitionName { get; set; } = "";
        public List<FaltskytteBomStation> Stations { get; set; } = new();
        public List<FaltskytteBomFigure> Rollup { get; set; } = new();   // competition-wide order list
        public int GrandTotalFigures => Rollup.Sum(f => f.Figures);
        public int GrandTotalTargets => Rollup.Sum(f => f.Targets);
        public bool HasConfig => Stations.Count > 0;
    }

    public static class FaltskytteBom
    {
        /// <summary>
        /// Build the BOM from a parsed competition config. <paramref name="sizeByName"/> is an optional
        /// Figurkatalog lookup (target Name → SizeGroup) so BOM rows can show the storleksgrupp.
        /// </summary>
        public static FaltskytteBomResult Build(FaltskytteCompetitionConfig config, IReadOnlyDictionary<string, int>? sizeByName = null)
        {
            var result = new FaltskytteBomResult();

            // Station numbers = union across weapon classes, excluding shoot-off-only stations.
            var stationNumbers = config.WeaponConfigs.Values
                .SelectMany(wc => wc.Stations)
                .Where(s => !s.IsShootOffOnly)
                .Select(s => s.Station)
                .Distinct().OrderBy(n => n).ToList();

            foreach (var n in stationNumbers)
            {
                string? name = null;
                var classes = new List<string>();
                // Per key, keep the weapon-class instance that needs the MOST of that figure (union/max).
                var best = new Dictionary<string, FaltskytteBomFigure>();

                foreach (var kv in config.WeaponConfigs)
                {
                    var station = kv.Value.Stations.FirstOrDefault(s => s.Station == n && !s.IsShootOffOnly);
                    if (station == null) continue;
                    if (!classes.Contains(kv.Key)) classes.Add(kv.Key);
                    if (string.IsNullOrEmpty(name) && !string.IsNullOrWhiteSpace(station.Name)) name = station.Name;

                    // Local tally for THIS weapon class.
                    var local = new Dictionary<string, FaltskytteBomFigure>();
                    foreach (var fig in station.TargetGroups.SelectMany(g => g.Figures))
                    {
                        var key = KeyFor(fig);
                        if (!local.TryGetValue(key, out var row))
                        {
                            row = new FaltskytteBomFigure
                            {
                                Key = key,
                                Label = LabelFor(fig),
                                ImageUrl = fig.ImageUrl,
                                IsPoangmal = fig.IsPoangmal,
                                SizeGroup = ResolveSize(fig, sizeByName),
                            };
                            local[key] = row;
                        }
                        row.Figures += 1;
                        row.Targets += Math.Max(1, fig.TargetsPerFigure);
                        row.IsPoangmal |= fig.IsPoangmal;
                    }

                    // Merge into the station's best-of (max figures per key across classes).
                    foreach (var lkv in local)
                    {
                        if (!best.TryGetValue(lkv.Key, out var b) || lkv.Value.Figures > b.Figures)
                        {
                            // carry poängmål if seen in any class
                            if (b != null) lkv.Value.IsPoangmal |= b.IsPoangmal;
                            best[lkv.Key] = lkv.Value;
                        }
                        else
                        {
                            b.IsPoangmal |= lkv.Value.IsPoangmal;
                        }
                    }
                }

                result.Stations.Add(new FaltskytteBomStation
                {
                    Station = n,
                    Name = name,
                    WeaponClasses = classes,
                    Figures = best.Values.OrderByDescending(f => f.IsPoangmal).ThenBy(f => f.Label).ToList(),
                });
            }

            // Competition-wide roll-up: sum the per-station (union) counts across all stations.
            var roll = new Dictionary<string, FaltskytteBomFigure>();
            foreach (var st in result.Stations)
            {
                foreach (var f in st.Figures)
                {
                    if (!roll.TryGetValue(f.Key, out var r))
                    {
                        r = new FaltskytteBomFigure { Key = f.Key, Label = f.Label, ImageUrl = f.ImageUrl, SizeGroup = f.SizeGroup, IsPoangmal = f.IsPoangmal };
                        roll[f.Key] = r;
                    }
                    r.Figures += f.Figures;
                    r.Targets += f.Targets;
                    r.IsPoangmal |= f.IsPoangmal;
                }
            }
            result.Rollup = roll.Values.OrderByDescending(f => f.Figures).ThenBy(f => f.Label).ToList();
            return result;
        }

        private static string KeyFor(FaltskytteFigure f)
        {
            if (!string.IsNullOrWhiteSpace(f.TargetName))
                return "t:" + f.TargetName.Trim().ToLowerInvariant() + "|" + (f.TargetColor ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(f.ImageUrl))
                return "i:" + f.ImageUrl.Trim().ToLowerInvariant();
            return "b:" + (f.Behavior ?? "").Trim().ToLowerInvariant();
        }

        private static string LabelFor(FaltskytteFigure f)
        {
            if (!string.IsNullOrWhiteSpace(f.TargetName))
                return string.IsNullOrWhiteSpace(f.TargetColor) ? f.TargetName.Trim() : $"{f.TargetName.Trim()} ({f.TargetColor.Trim()})";
            var beh = string.IsNullOrWhiteSpace(f.Behavior) ? "" : $" ({f.Behavior})";
            return "Figur" + beh;
        }

        private static int? ResolveSize(FaltskytteFigure f, IReadOnlyDictionary<string, int>? sizeByName)
        {
            if (sizeByName == null || string.IsNullOrWhiteSpace(f.TargetName)) return null;
            if (sizeByName.TryGetValue(f.TargetName.Trim(), out var sg) && sg >= 1 && sg <= 14) return sg;
            return null;
        }
    }
}
