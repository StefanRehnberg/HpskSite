using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using Microsoft.Extensions.Logging;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Builds a finals StartListConfiguration from a per-class QualifyingResultsSnapshot
    /// plus the admin's per-class config (skjutlag assignment + optional cut overrides).
    ///
    /// Skjutlag semantics: multiple championship classes may share a skjutlag. Within the
    /// skjutlag, classes are ordered by OrderInSkjutlag and laid out as contiguous
    /// position blocks. Each class's leader sits at the start of its block; positions
    /// within the class follow that class's leaderboard. There is NO score-based
    /// re-ranking across classes — C-class leader is at position 1, C-Vet-Y leader is at
    /// (e.g.) position 11, regardless of who has the higher absolute score.
    ///
    /// Output is the regular StartListConfiguration so all existing editor endpoints
    /// (MoveShooterToTeam, AddShooterToStartList, UpdateTeamTimes, …) work transparently
    /// on the finals start list.
    /// </summary>
    public class PrecisionFinalsStartListBuilder
    {
        private readonly ILogger<PrecisionFinalsStartListBuilder> _logger;
        private readonly PrecisionFinalsQualificationService _qualificationService;

        public PrecisionFinalsStartListBuilder(
            ILogger<PrecisionFinalsStartListBuilder> logger,
            PrecisionFinalsQualificationService qualificationService)
        {
            _logger = logger;
            _qualificationService = qualificationService;
        }

        public BuildResult Build(
            QualifyingResultsSnapshot snapshot,
            Dictionary<string, FinalsClassConfig> perClassConfig,
            FinalsStartListSettings settings)
        {
            // Resolve which result-list groups actually participate. A group participates
            // iff it's frozen in the snapshot AND not marked Skip in the config.
            var participating = new List<ClassPlacement>();
            int autoSkjutlag = 1;
            foreach (var (group, classSnap) in snapshot.ClassSnapshots)
            {
                if (!perClassConfig.TryGetValue(group, out var cfg))
                    cfg = new FinalsClassConfig { SkjutlagNumber = autoSkjutlag++ };
                if (cfg.Skip) continue;

                var shooters = ApplyCut(classSnap.QualifiedShooters, cfg);
                if (shooters.Count == 0) continue;

                participating.Add(new ClassPlacement
                {
                    GroupName = group,
                    SkjutlagNumber = cfg.SkjutlagNumber > 0 ? cfg.SkjutlagNumber : 1,
                    OrderInSkjutlag = cfg.OrderInSkjutlag,
                    Shooters = shooters
                });
            }

            if (participating.Count == 0)
            {
                return new BuildResult
                {
                    Ok = false,
                    Message = "Inga grupper låsta — lås minst en grupp innan generering."
                };
            }

            // Group by skjutlag number, order groups within each skjutlag by
            // OrderInSkjutlag (stable; defaults are zero, so name order breaks ties).
            var grouped = participating
                .GroupBy(p => p.SkjutlagNumber)
                .OrderBy(g => g.Key)
                .Select(g => new SkjutlagBucket
                {
                    SkjutlagNumber = g.Key,
                    Classes = g.OrderBy(p => p.OrderInSkjutlag)
                                .ThenBy(p => p.GroupName)
                                .ToList()
                })
                .ToList();

            var teams = new List<StartListTeam>();
            string currentStart = settings.FirstStartTime;
            int teamNumber = 1;

            foreach (var bucket in grouped)
            {
                var team = BuildTeam(teamNumber++, currentStart, settings.StartInterval, bucket);
                teams.Add(team);
                currentStart = team.EndTime;
            }

            var config = new StartListConfiguration
            {
                Settings = new StartListSettings
                {
                    Format = "Championship Finals",
                    MaxShootersPerTeam = settings.MaxShootersPerTeam,
                    StartInterval = settings.StartInterval,
                    FirstStartTime = settings.FirstStartTime,
                    Generated = DateTime.Now
                },
                Teams = teams
            };

            return new BuildResult
            {
                Ok = true,
                Message = $"Genererade {teams.Count} skjutlag med {teams.Sum(t => t.Shooters?.Count ?? 0)} finalister.",
                Configuration = config,
                ClassesPerSkjutlag = grouped.ToDictionary(b => b.SkjutlagNumber, b => b.Classes.Select(c => c.GroupName).ToList()),
                ShooterCountPerClass = participating.ToDictionary(p => p.GroupName, p => p.Shooters.Count)
            };
        }

        public PreviewResult PreviewBuckets(
            QualifyingResultsSnapshot snapshot,
            Dictionary<string, FinalsClassConfig> perClassConfig)
        {
            var result = new PreviewResult();
            int autoSkjutlag = 1;
            foreach (var (group, classSnap) in snapshot.ClassSnapshots)
            {
                if (!perClassConfig.TryGetValue(group, out var cfg))
                    cfg = new FinalsClassConfig { SkjutlagNumber = autoSkjutlag++ };
                if (cfg.Skip)
                {
                    result.PerClass[group] = new PreviewLine { Skjutlag = null, FinalistCount = 0 };
                    continue;
                }
                var shooters = ApplyCut(classSnap.QualifiedShooters, cfg);
                result.PerClass[group] = new PreviewLine
                {
                    Skjutlag = cfg.SkjutlagNumber > 0 ? cfg.SkjutlagNumber : 1,
                    FinalistCount = shooters.Count,
                    TotalInClass = classSnap.QualifiedShooters.Count
                };
            }
            return result;
        }

        private List<QualifiedShooter> ApplyCut(List<QualifiedShooter> all, FinalsClassConfig cfg)
        {
            if (all.Count == 0) return all;
            if (cfg.IncludeAllShooters) return all.ToList();

            int rawCutoff = cfg.FinalistCountOverride
                ?? _qualificationService.CalculateQualificationCutoff(all.Count);
            if (rawCutoff < 1) rawCutoff = 1;
            if (rawCutoff >= all.Count) return all.ToList();

            // Tie-extension: include everyone tied at the cutoff score.
            var cutoffScore = all[rawCutoff - 1].QualificationScore;
            return all.Where((s, idx) => idx < rawCutoff || s.QualificationScore == cutoffScore).ToList();
        }

        private StartListTeam BuildTeam(int teamNumber, string startTime, string interval, SkjutlagBucket bucket)
        {
            // Each group fills a contiguous position block. Group leader sits at the
            // first position of its block.
            var shooters = new List<StartListShooter>();
            int position = 1;
            foreach (var grp in bucket.Classes)
            {
                foreach (var qs in grp.Shooters)
                {
                    shooters.Add(new StartListShooter
                    {
                        Position = position++,
                        Name = qs.Name,
                        Club = qs.Club,
                        WeaponClass = qs.ShootingClass,
                        MemberId = qs.MemberId,
                        QualificationRank = qs.QualificationRank,
                        QualificationScore = qs.QualificationScore,
                        QualificationXCount = qs.XCount,
                        ChampionshipClass = qs.ChampionshipClass
                    });
                }
            }

            var groupNames = bucket.Classes.Select(c => c.GroupName).ToList();
            var label = groupNames.Count > 1
                ? $"Final ({string.Join(" + ", groupNames)})"
                : $"Final {groupNames[0]}";

            return new StartListTeam
            {
                TeamNumber = teamNumber,
                StartTime = startTime,
                EndTime = AddInterval(startTime, interval),
                Label = label,
                WeaponClasses = shooters.Select(s => s.WeaponClass).Distinct().OrderBy(c => c).ToList(),
                ShooterCount = shooters.Count,
                Shooters = shooters,
                ChampionshipClasses = string.Join(" + ", groupNames)
            };
        }

        private static string AddInterval(string startTime, string interval)
        {
            // Interval is hours:minutes (h:mm) — matches the convention used by the regular
            // StartListGenerator. "1:45" = 1 hour 45 minutes per skjutlag.
            if (!TimeSpan.TryParse(startTime, out var ts)) return startTime;
            var parts = (interval ?? "").Split(':');
            int hours = 0, minutes = 0;
            if (parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            {
                hours = h;
                minutes = m;
            }
            ts = ts.Add(new TimeSpan(hours, minutes, 0));
            return $"{ts.Hours:D2}:{ts.Minutes:D2}";
        }

        private class ClassPlacement
        {
            public string GroupName { get; set; } = "";
            public int SkjutlagNumber { get; set; }
            public int OrderInSkjutlag { get; set; }
            public List<QualifiedShooter> Shooters { get; set; } = new();
        }

        private class SkjutlagBucket
        {
            public int SkjutlagNumber { get; set; }
            public List<ClassPlacement> Classes { get; set; } = new();
        }

        public class BuildResult
        {
            public bool Ok { get; set; }
            public string Message { get; set; } = "";
            public StartListConfiguration? Configuration { get; set; }
            public Dictionary<int, List<string>> ClassesPerSkjutlag { get; set; } = new();
            public Dictionary<string, int> ShooterCountPerClass { get; set; } = new();
        }

        public class PreviewResult
        {
            public Dictionary<string, PreviewLine> PerClass { get; set; } = new();
        }

        public class PreviewLine
        {
            public int? Skjutlag { get; set; }
            public int FinalistCount { get; set; }
            public int TotalInClass { get; set; }
        }
    }
}
