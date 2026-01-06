using HpskSite.Models;
using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using Microsoft.Extensions.Logging;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Service for calculating finals qualifiers based on championship rules
    /// </summary>
    public class PrecisionFinalsQualificationService
    {
        private readonly ILogger<PrecisionFinalsQualificationService> _logger;

        // Championship class groupings as per rules
        private static readonly Dictionary<string, List<string>> ChampionshipClassMappings = new()
        {
            { "A", new List<string> { "A1", "A2", "A3" } },
            { "B", new List<string> { "B1", "B2", "B3" } },
            { "C", new List<string> { "C1", "C2", "C3" } },
            { "C Dam", new List<string> { "C1 Dam", "C2 Dam", "C3 Dam" } },
            { "C Jun", new List<string> { "C Jun" } },
            { "C Vet Y", new List<string> { "C Vet Y" } },
            { "C Vet Ä", new List<string> { "C Vet Ä" } }
        };

        public PrecisionFinalsQualificationService(ILogger<PrecisionFinalsQualificationService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Calculate which shooters qualify for finals based on qualification results
        /// </summary>
        public PrecisionFinalsQualificationViewModel CalculateQualifiers(
            List<PrecisionResultEntry> qualificationResults,
            Dictionary<int, (string Name, string Club)> shooterInfo,
            int maxShootersPerTeam = 20)
        {
            var viewModel = new PrecisionFinalsQualificationViewModel
            {
                MaxShootersPerTeam = maxShootersPerTeam
            };

            // Group results by shooter and calculate qualification scores
            var shooterScores = qualificationResults
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g => new
                {
                    g.Key.MemberId,
                    g.Key.ShootingClass,
                    QualificationScore = g.Sum(r => CalculateSeriesScore(r.Shots)),
                    XCount = g.Sum(r => CalculateXCount(r.Shots)),
                    SeriesCount = g.Count()
                })
                .ToList();

            viewModel.TotalShooters = shooterScores.Count;

            // Process each championship class
            foreach (var (champClass, subClasses) in ChampionshipClassMappings)
            {
                var classShooters = shooterScores
                    .Where(s => subClasses.Contains(s.ShootingClass))
                    .OrderByDescending(s => s.QualificationScore)
                    .ThenByDescending(s => s.XCount)
                    .ToList();

                if (!classShooters.Any())
                    continue;

                var classQual = new ChampionshipClassQualification
                {
                    ChampionshipClass = champClass,
                    SubClasses = subClasses,
                    TotalShooters = classShooters.Count
                };

                // Apply qualification rules
                int cutoff = CalculateQualificationCutoff(classShooters.Count);
                classQual.QualificationRule = GetQualificationRuleDescription(classShooters.Count, cutoff);

                // Get cutoff score
                var cutoffScore = classShooters[Math.Min(cutoff - 1, classShooters.Count - 1)].QualificationScore;

                // Include all with same score as last qualifier (tie-breaking rule)
                var qualifiers = classShooters
                    .Where((s, idx) => idx < cutoff || s.QualificationScore == cutoffScore)
                    .ToList();

                classQual.Qualifiers = qualifiers.Count;

                // Create qualified shooter list
                int rank = 1;
                foreach (var shooter in qualifiers)
                {
                    var info = shooterInfo.GetValueOrDefault(shooter.MemberId, ("Unknown", "Unknown"));
                    classQual.QualifiedShooters.Add(new QualifiedShooter
                    {
                        MemberId = shooter.MemberId,
                        Name = info.Item1,
                        Club = info.Item2,
                        ShootingClass = shooter.ShootingClass,
                        ChampionshipClass = champClass,
                        QualificationScore = shooter.QualificationScore,
                        QualificationRank = rank++,
                        XCount = shooter.XCount
                    });
                }

                classQual.QualifiedShooters = classQual.QualifiedShooters
                    .OrderBy(s => s.QualificationRank)
                    .ToList();

                viewModel.ClassQualifications.Add(classQual);
            }

            viewModel.TotalQualifiers = viewModel.ClassQualifications.Sum(c => c.Qualifiers);

            // Generate proposed team structure
            viewModel.ProposedTeams = GenerateTeamStructure(viewModel.ClassQualifications, maxShootersPerTeam);

            return viewModel;
        }

        /// <summary>
        /// Calculate qualification cutoff based on 1/6 rule with minimum 10
        /// </summary>
        private int CalculateQualificationCutoff(int totalShooters)
        {
            if (totalShooters < 10)
            {
                // All advance if fewer than 10
                return totalShooters;
            }

            // Top 1/6, rounded up, minimum 10
            return Math.Max(10, (int)Math.Ceiling(totalShooters / 6.0));
        }

        private string GetQualificationRuleDescription(int totalShooters, int cutoff)
        {
            if (totalShooters < 10)
                return "All advance (< 10 shooters)";
            if (cutoff == 10)
                return "Minimum 10";
            return $"Top 1/6 ({cutoff} shooters)";
        }

        /// <summary>
        /// Generate team structure for finals based on championship class groupings
        /// </summary>
        private List<FinalsTeamPreview> GenerateTeamStructure(
            List<ChampionshipClassQualification> classQualifications,
            int maxPerTeam)
        {
            var teams = new List<FinalsTeamPreview>();
            int teamNumber = 1;

            // A class - separate team(s)
            var aClass = classQualifications.FirstOrDefault(c => c.ChampionshipClass == "A");
            if (aClass != null && aClass.Qualifiers > 0)
            {
                teams.AddRange(CreateTeamsForClass(aClass, maxPerTeam, ref teamNumber));
            }

            // B class - separate team(s)
            var bClass = classQualifications.FirstOrDefault(c => c.ChampionshipClass == "B");
            if (bClass != null && bClass.Qualifiers > 0)
            {
                teams.AddRange(CreateTeamsForClass(bClass, maxPerTeam, ref teamNumber));
            }

            // C classes - combined with specific order: C, C Dam, C Jun, C Vet Y, C Vet Ä
            var cClasses = new[] { "C", "C Dam", "C Jun", "C Vet Y", "C Vet Ä" }
                .Select(name => classQualifications.FirstOrDefault(c => c.ChampionshipClass == name))
                .Where(c => c != null && c.Qualifiers > 0)
                .ToList();

            if (cClasses.Any())
            {
                teams.AddRange(CreateCombinedCClassTeams(cClasses!, maxPerTeam, ref teamNumber));
            }

            return teams;
        }

        private List<FinalsTeamPreview> CreateTeamsForClass(
            ChampionshipClassQualification classQual,
            int maxPerTeam,
            ref int teamNumber)
        {
            var teams = new List<FinalsTeamPreview>();
            var shooters = classQual.QualifiedShooters;

            for (int i = 0; i < shooters.Count; i += maxPerTeam)
            {
                var teamShooters = shooters.Skip(i).Take(maxPerTeam).ToList();
                var team = new FinalsTeamPreview
                {
                    TeamName = $"Team F{teamNumber}",
                    ChampionshipClasses = classQual.ChampionshipClass,
                    ShooterCount = teamShooters.Count,
                    Positions = teamShooters.Select((s, idx) => new FinalsPosition
                    {
                        Position = i + idx + 1,
                        MemberId = s.MemberId,
                        Name = s.Name,
                        Club = s.Club,
                        ShootingClass = s.ShootingClass,
                        ChampionshipClass = s.ChampionshipClass,
                        QualificationScore = s.QualificationScore,
                        Rank = s.QualificationRank
                    }).ToList()
                };
                teams.Add(team);
                teamNumber++;
            }

            return teams;
        }

        private List<FinalsTeamPreview> CreateCombinedCClassTeams(
            List<ChampionshipClassQualification> cClasses,
            int maxPerTeam,
            ref int teamNumber)
        {
            var teams = new List<FinalsTeamPreview>();
            var allCShooters = new List<QualifiedShooter>();

            // Combine in order: C, C Dam, C Jun, C Vet Y, C Vet Ä
            foreach (var cClass in cClasses)
            {
                allCShooters.AddRange(cClass.QualifiedShooters);
            }

            // Split into teams, trying to keep classes together
            var currentTeam = new FinalsTeamPreview
            {
                TeamName = $"Team F{teamNumber}",
                Positions = new List<FinalsPosition>()
            };

            int globalPosition = 1;
            var classNames = new List<string>();

            foreach (var shooter in allCShooters)
            {
                if (currentTeam.Positions.Count >= maxPerTeam)
                {
                    // Finalize current team
                    currentTeam.ChampionshipClasses = string.Join(" + ", classNames.Distinct());
                    currentTeam.ShooterCount = currentTeam.Positions.Count;
                    teams.Add(currentTeam);
                    teamNumber++;

                    // Start new team
                    currentTeam = new FinalsTeamPreview
                    {
                        TeamName = $"Team F{teamNumber}",
                        Positions = new List<FinalsPosition>()
                    };
                    classNames = new List<string>();
                }

                currentTeam.Positions.Add(new FinalsPosition
                {
                    Position = globalPosition++,
                    MemberId = shooter.MemberId,
                    Name = shooter.Name,
                    Club = shooter.Club,
                    ShootingClass = shooter.ShootingClass,
                    ChampionshipClass = shooter.ChampionshipClass,
                    QualificationScore = shooter.QualificationScore,
                    Rank = shooter.QualificationRank
                });

                if (!classNames.Contains(shooter.ChampionshipClass))
                {
                    classNames.Add(shooter.ChampionshipClass);
                }
            }

            // Add last team
            if (currentTeam.Positions.Any())
            {
                currentTeam.ChampionshipClasses = string.Join(" + ", classNames.Distinct());
                currentTeam.ShooterCount = currentTeam.Positions.Count;
                teams.Add(currentTeam);
            }

            return teams;
        }

        private int CalculateSeriesScore(string shotsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(shotsJson))
                    return 0;

                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(shotsJson);
                if (shots == null || !shots.Any())
                    return 0;

                return shots.Sum(shot =>
                {
                    if (shot == "X")
                        return 10;
                    if (int.TryParse(shot, out int value))
                        return value;
                    return 0;
                });
            }
            catch
            {
                return 0;
            }
        }

        private int CalculateXCount(string shotsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(shotsJson))
                    return 0;

                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(shotsJson);
                if (shots == null)
                    return 0;

                return shots.Count(shot => shot == "X");
            }
            catch
            {
                return 0;
            }
        }
    }
}
