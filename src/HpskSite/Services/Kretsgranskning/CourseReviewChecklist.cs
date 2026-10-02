using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Bangranskarens automatiska checklista (fas 3), ur tävlingens EGEN stationConfig.
    ///
    /// <para>Det mesta finns redan i konfiguratorn, och reglerna är dess regler —
    /// <see cref="ShbFieldRules"/> speglar konfiguratorns JavaScript (och ett paritetstest håller dem
    /// lika). Checklistan säger vad den kunde mäta och vad den inte kunde: ett saknat avstånd eller en
    /// figur utan storleksgrupp redovisas som just det, aldrig som godkänt.</para>
    ///
    /// <para>Läser stationConfig som rå JSON: tävlingens modellklass saknar målgruppens avstånd, så
    /// <c>FaltskytteConfigParser</c> hade tyst tappat det.</para>
    /// </summary>
    public class CourseReviewChecklist
    {
        public const int MinimumStations = 6;

        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CourseReviewChecklist> _logger;

        public CourseReviewChecklist(IScopeProvider scopeProvider, ILogger<CourseReviewChecklist> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public record DistanceIssue(string Group, int Distance, int Max);
        public record StationRow(string WeaponClass, int Station, string? Name, int ShootingTimeSec, int? ShbMinimumSec,
            int? DifficultyPercent, bool BelowMinimum, List<DistanceIssue> TooFar, int GroupsWithoutDistance,
            int FiguresWithoutSizeGroup, int Figures, int TargetGroups);
        public record WeaponClassSummary(string WeaponClass, int Stations, bool EnoughStations);
        public record Approval(int ConfigId, string Name, string Status, string? ApprovedBy, DateTime? ApprovedAt, bool ContentMatches);
        public record Result(List<WeaponClassSummary> WeaponClasses, List<StationRow> Stations, List<string> CustomTargets,
            bool Morker, bool PoangMode, Approval? BanlaggareApproval, string? Error);

        public Result Build(string? stationConfig, Func<int, string?> memberName)
        {
            var wcs = new List<WeaponClassSummary>();
            var rows = new List<StationRow>();
            var custom = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            JObject root;
            try { root = JObject.Parse(string.IsNullOrWhiteSpace(stationConfig) ? "{}" : stationConfig); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bangranskningens checklista kunde inte läsa stationConfig.");
                return new Result(wcs, rows, new(), false, false, null, "Stationsbeskrivningen gick inte att läsa.");
            }
            var morker = root.Value<bool?>("_morker") == true;
            var poang = string.Equals(root.Value<string>("_scoringMode"), "Poang", StringComparison.OrdinalIgnoreCase);
            var catalogue = Catalogue();

            // Formerna: { "C": { stations: [...] } } (konfiguratorn) eller { WeaponConfigs: { "C": {...} } }.
            var weaponsObj = root["WeaponConfigs"] as JObject ?? root;
            foreach (var prop in weaponsObj.Properties().Where(p => !p.Name.StartsWith("_")))
            {
                var stationsArr = prop.Value is JArray ja ? ja : (prop.Value["stations"] ?? prop.Value["Stations"]) as JArray;
                if (stationsArr == null) continue;
                var wc = prop.Name;
                var count = 0;
                foreach (var s in stationsArr.OfType<JObject>())
                {
                    if (Bool(s, "isShootOffOnly")) continue;
                    count++;
                    var groups = new List<ShbFieldRules.TargetGroup>();
                    int noDistance = 0, noSize = 0, figs = 0;
                    var gi = 0;
                    foreach (var g in (Arr(s, "targetGroups")).OfType<JObject>())
                    {
                        var figures = new List<ShbFieldRules.Figure>();
                        foreach (var f in Arr(g, "figures").OfType<JObject>())
                        {
                            figs++;
                            var name = (Str(f, "targetName") ?? "").Trim();
                            int? size = Int(f, "sizeGroup");
                            if ((size is null or 0) && name.Length > 0 && catalogue.TryGetValue(name, out var cat)) size = cat;
                            // Mål utanför SHB:s förteckning: inte i katalogen, eller "ej grupperad" (grupp 15).
                            if (name.Length > 0 && (!catalogue.ContainsKey(name) || size == 15)) custom.Add(name);
                            if (size is null or 0 or 15) noSize++;
                            figures.Add(new ShbFieldRules.Figure(size is 15 ? null : size, Int(f, "targetsPerFigure") ?? 1));
                        }
                        var dist = Int(g, "distance");
                        if (dist == null) noDistance++;
                        groups.Add(new ShbFieldRules.TargetGroup(dist, figures));
                        gi++;
                    }
                    var station = new ShbFieldRules.Station(Int(s, "shootingTimeSec") ?? 0, Str(s, "supportHand"), Str(s, "weaponStartPosition"),
                        Int(s, "minShotsPerFigure") ?? 0, Int(s, "maxShotsPerFigure") ?? 6, groups);
                    var tooFar = new List<DistanceIssue>();
                    for (var i = 0; i < groups.Count; i++)
                    {
                        if (groups[i].Distance is not int d) continue;
                        var max = ShbFieldRules.GroupMaxDistance(groups[i], wc, station, morker);
                        if (max != null && d > max) tooFar.Add(new DistanceIssue(((char)('A' + i)).ToString(), d, max.Value));
                    }
                    var min = ShbFieldRules.MinimumShootingTime(station, wc, morker, poang);
                    int? pct = min != null && station.ShootingTimeSec > 0 ? (int)Math.Round(100.0 * min.Value / station.ShootingTimeSec) : null;
                    rows.Add(new StationRow(wc, Int(s, "station") ?? count, Str(s, "name"), station.ShootingTimeSec, min, pct,
                        min != null && station.ShootingTimeSec < min.Value, tooFar, noDistance, noSize, figs, groups.Count));
                }
                wcs.Add(new WeaponClassSummary(wc, count, count >= MinimumStations));
            }

            return new Result(wcs, rows, custom.ToList(), morker, poang, BanlaggareApproval(root, stationConfig, memberName), null);
        }

        /// <summary>
        /// Banläggarens godkännande av den sparade konfiguration tävlingens bana kom ifrån — en punkt i
        /// checklistan, inget krav. ⚠️ Säger ingenting om tävlingens kopia har ändrats efter anslutningen,
        /// därför jämförs innehållet (<see cref="Approval.ContentMatches"/>).
        /// </summary>
        private Approval? BanlaggareApproval(JObject root, string? stationConfig, Func<int, string?> memberName)
        {
            var id = root.Value<int?>("_attachedConfigId");
            if (id is null or <= 0) return null;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var cfg = scope.Database.SingleOrDefaultById<FaltskytteConfiguration>(id.Value);
                if (cfg == null) return null;
                var same = Models.Kretsgranskning.CourseReviewRules.Checksum(cfg.JsonBlob) == Models.Kretsgranskning.CourseReviewRules.Checksum(stationConfig);
                return new Approval(cfg.Id, cfg.Name, cfg.ApprovalStatus ?? "Draft",
                    cfg.ApprovedByMemberId is > 0 ? memberName(cfg.ApprovedByMemberId.Value) : null, cfg.ApprovedDate, same);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Konfiguration {Id} kunde inte läsas för bangranskningen.", id);
                return null;
            }
        }

        private Dictionary<string, int> Catalogue()
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in scope.Database.Fetch<FieldTarget>("SELECT * FROM FieldTarget"))
                    if (!string.IsNullOrWhiteSpace(t.Name)) d[t.Name.Trim()] = t.SizeGroup;
                return d;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Figurkatalogen kunde inte läsas för bangranskningen.");
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static JArray Arr(JObject o, string key) => (o[key] ?? o[char.ToUpper(key[0]) + key[1..]]) as JArray ?? new JArray();
        private static string? Str(JObject o, string key) => (o[key] ?? o[char.ToUpper(key[0]) + key[1..]])?.Type is JTokenType.String
            ? (string?)(o[key] ?? o[char.ToUpper(key[0]) + key[1..]]) : null;
        private static int? Int(JObject o, string key)
        {
            var t = o[key] ?? o[char.ToUpper(key[0]) + key[1..]];
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type is JTokenType.Integer or JTokenType.Float) return (int)Math.Round((double)t);
            return int.TryParse(t.ToString(), out var v) ? v : null;
        }
        private static bool Bool(JObject o, string key) => (o[key] ?? o[char.ToUpper(key[0]) + key[1..]])?.Type == JTokenType.Boolean
            && (bool)(o[key] ?? o[char.ToUpper(key[0]) + key[1..]])!;
    }
}
