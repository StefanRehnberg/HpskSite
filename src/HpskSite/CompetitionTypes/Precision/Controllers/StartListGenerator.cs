using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class StartListGenerator
    {
        private readonly UmbracoStartListRepository _repository;
        private readonly ILogger<StartListGenerator> _logger;

        public StartListGenerator(UmbracoStartListRepository repository, ILogger<StartListGenerator> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public StartListConfiguration GenerateStartListData(List<CompetitionRegistration> registrations, StartListGenerationRequest request)
        {
            var teams = new List<StartListTeam>();
            var currentTime = TimeSpan.Parse(request.FirstStartTime);
            var intervalMinutes = request.StartInterval;

            switch (request.TeamFormat)
            {
                case "Mixade Skjutlag":
                    teams = GenerateMixedTeams(registrations, request.MaxShootersPerTeam, currentTime, intervalMinutes, request.MemberSortOrder);
                    break;
                case "En vapengrupp per Skjutlag":
                    teams = GenerateSeparatedTeamsWithClassOrder(registrations, request.MaxShootersPerTeam, currentTime, intervalMinutes, request.ClassStartOrder, request.MemberSortOrder);
                    break;
                case "A och B i samma skjutlag":
                    teams = GenerateABCombinedTeamsWithClassOrder(registrations, request.MaxShootersPerTeam, currentTime, intervalMinutes, request.ClassStartOrder, request.MemberSortOrder);
                    break;
                case "B och C i samma skjutlag":
                    teams = GenerateBCCombinedTeamsWithClassOrder(registrations, request.MaxShootersPerTeam, currentTime, intervalMinutes, request.ClassStartOrder, request.MemberSortOrder);
                    break;
                default:
                    teams = GenerateMixedTeams(registrations, request.MaxShootersPerTeam, currentTime, intervalMinutes, request.MemberSortOrder);
                    break;
            }

            return new StartListConfiguration
            {
                Settings = new StartListSettings
                {
                    Format = request.TeamFormat,
                    MaxShootersPerTeam = request.MaxShootersPerTeam,
                    StartInterval = $"{intervalMinutes / 60}:{intervalMinutes % 60:D2}",
                    FirstStartTime = request.FirstStartTime,
                    Generated = DateTime.Now
                },
                Teams = teams
            };
        }

        private List<StartListTeam> GenerateMixedTeams(List<CompetitionRegistration> registrations, int maxPerTeam, TimeSpan startTime, int intervalMinutes, string memberSortOrder = "FirstName")
        {
            // PERFORMANCE FIX: Removed redundant AddClubNames() call
            // Club names are now resolved in GetCompetitionRegistrations()
            var mixer = new PrecisionMixedTeamsGenerator(registrations, maxPerTeam, startTime, intervalMinutes, memberSortOrder);
            return mixer.GenerateStartLists();
        }

        private List<StartListTeam> GenerateSeparatedTeams(List<CompetitionRegistration> registrations, int maxPerTeam, TimeSpan startTime, int intervalMinutes)
        {
            var teams = new List<StartListTeam>();
            var teamNumber = 1;

            var classesByLevel = registrations
                .GroupBy(r => GetWeaponClassLevel(r.MemberClass))
                .OrderBy(g => g.Key);

            foreach (var classLevelGroup in classesByLevel)
            {
                var classRegistrations = classLevelGroup.OrderBy(r => r.MemberClass).ToList();

                for (int i = 0; i < classRegistrations.Count; i += maxPerTeam)
                {
                    var teamRegistrations = classRegistrations.Skip(i).Take(maxPerTeam).ToList();
                    var endTime = startTime.Add(TimeSpan.FromMinutes(intervalMinutes));
                    var position = 1;

                    var team = new StartListTeam
                    {
                        TeamNumber = teamNumber,
                        StartTime = FormatTime(startTime),
                        EndTime = FormatTime(endTime),
                        ShooterCount = teamRegistrations.Count,
                        WeaponClasses = teamRegistrations.Select(r => r.MemberClass).Distinct().OrderBy(c => c).ToList(),
                        Shooters = teamRegistrations.Select(reg => new StartListShooter
                        {
                            Position = position++,
                            Name = reg.MemberName ?? "Okänd deltagare",
                            Club = UmbracoStartListRepository.IsUnknownClub(reg.MemberClub) 
                                ? _repository.GetMemberClub(reg.MemberId)
                                : reg.MemberClub,
                            WeaponClass = reg.MemberClass,
                            MemberId = reg.MemberId
                        }).ToList()
                    };

                    teams.Add(team);
                    teamNumber++;
                    startTime = endTime;
                }
            }

            return teams;
        }

        private List<StartListTeam> GenerateSeparatedTeamsWithClassOrder(List<CompetitionRegistration> registrations, int maxPerTeam, TimeSpan startTime, int intervalMinutes, string classStartOrder, string memberSortOrder = "FirstName")
        {
            var teams = new List<StartListTeam>();
            var teamNumber = 1;

            if (string.IsNullOrEmpty(classStartOrder))
            {
                return GenerateSeparatedTeams(registrations, maxPerTeam, startTime, intervalMinutes);
            }

            var orderParts = classStartOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToUpper())
                .ToList();

            var registrationsByClass = new Dictionary<string, List<CompetitionRegistration>>();
            foreach (var prefix in orderParts)
            {
                registrationsByClass[prefix] = registrations
                    .Where(r => r.MemberClass.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.MemberClass)
                    .ThenBy(r => r.MemberName)
                    .ToList();
            }

            var currentClassIndex = 0;
            var currentClassPosition = 0;

            while (currentClassIndex < orderParts.Count)
            {
                var currentClass = orderParts[currentClassIndex];
                var classRegistrations = registrationsByClass[currentClass];

                if (currentClassPosition >= classRegistrations.Count)
                {
                    currentClassIndex++;
                    currentClassPosition = 0;
                    continue;
                }

                var teamRegistrations = classRegistrations
                    .Skip(currentClassPosition)
                    .Take(maxPerTeam)
                    .ToList();

                if (teamRegistrations.Count == 0)
                {
                    currentClassIndex++;
                    currentClassPosition = 0;
                    continue;
                }

                teamRegistrations = SortAndDeduplicateWithinTeam(teamRegistrations, memberSortOrder);

                var endTime = startTime.Add(TimeSpan.FromMinutes(intervalMinutes));
                var position = 1;

                var team = new StartListTeam
                {
                    TeamNumber = teamNumber,
                    StartTime = FormatTime(startTime),
                    EndTime = FormatTime(endTime),
                    ShooterCount = teamRegistrations.Count,
                    WeaponClasses = teamRegistrations.Select(r => r.MemberClass).Distinct().OrderBy(c => c).ToList(),
                    Shooters = teamRegistrations.Select(reg => new StartListShooter
                    {
                        Position = position++,
                        Name = reg.MemberName ?? "Okänd deltagare",
                        Club = UmbracoStartListRepository.IsUnknownClub(reg.MemberClub)
                            ? _repository.GetMemberClub(reg.MemberId)
                            : reg.MemberClub,
                        WeaponClass = reg.MemberClass,
                        MemberId = reg.MemberId
                    }).ToList()
                };

                teams.Add(team);
                teamNumber++;
                startTime = endTime;
                currentClassPosition += teamRegistrations.Count;
            }

            return teams;
        }

        private List<CompetitionRegistration> ApplyClassStartOrder(List<CompetitionRegistration> registrations, string classStartOrder)
        {
            if (string.IsNullOrEmpty(classStartOrder))
                return registrations;

            var orderParts = classStartOrder.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToUpper())
                .ToList();

            var orderedRegistrations = new List<CompetitionRegistration>();

            foreach (var classPrefix in orderParts)
            {
                var classRegistrations = registrations
                    .Where(r => r.MemberClass.StartsWith(classPrefix, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.MemberClass)
                    .ThenBy(r => r.MemberName)
                    .ToList();

                orderedRegistrations.AddRange(classRegistrations);
            }

            var remainingRegistrations = registrations
                .Where(r => !orderParts.Any(prefix => r.MemberClass.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.MemberClass)
                .ThenBy(r => r.MemberName)
                .ToList();

            orderedRegistrations.AddRange(remainingRegistrations);

            return orderedRegistrations;
        }

        private List<StartListTeam> GenerateABCombinedTeamsWithClassOrder(List<CompetitionRegistration> registrations, int maxPerTeam, TimeSpan startTime, int intervalMinutes, string classStartOrder, string memberSortOrder = "FirstName")
        {
            var orderedRegistrations = ApplyClassStartOrder(registrations, classStartOrder);

            var abRegistrations = orderedRegistrations
                .Where(r => r.MemberClass.StartsWith("A", StringComparison.OrdinalIgnoreCase) ||
                           r.MemberClass.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var cRegistrations = orderedRegistrations
                .Where(r => r.MemberClass.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var teams = new List<StartListTeam>();
            var teamNumber = 1;

            teams.AddRange(GenerateTeamsFromList(abRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder));
            teams.AddRange(GenerateTeamsFromList(cRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder));

            return teams;
        }

        private List<StartListTeam> GenerateBCCombinedTeamsWithClassOrder(List<CompetitionRegistration> registrations, int maxPerTeam, TimeSpan startTime, int intervalMinutes, string classStartOrder, string memberSortOrder = "FirstName")
        {
            var orderedRegistrations = ApplyClassStartOrder(registrations, classStartOrder);

            var bcRegistrations = orderedRegistrations
                .Where(r => r.MemberClass.StartsWith("B", StringComparison.OrdinalIgnoreCase) ||
                           r.MemberClass.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var aRegistrations = orderedRegistrations
                .Where(r => r.MemberClass.StartsWith("A", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var teams = new List<StartListTeam>();
            var teamNumber = 1;

            teams.AddRange(GenerateTeamsFromList(bcRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder));
            teams.AddRange(GenerateTeamsFromList(aRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder));

            return teams;
        }

        private List<StartListTeam> GenerateTeamsFromList(List<CompetitionRegistration> registrations, int maxPerTeam, ref TimeSpan startTime, ref int teamNumber, int intervalMinutes, string memberSortOrder)
        {
            var teams = new List<StartListTeam>();

            for (int i = 0; i < registrations.Count; i += maxPerTeam)
            {
                var teamRegistrations = registrations.Skip(i).Take(maxPerTeam).ToList();
                teamRegistrations = SortAndDeduplicateWithinTeam(teamRegistrations, memberSortOrder);
                var endTime = startTime.Add(TimeSpan.FromMinutes(intervalMinutes));
                var position = 1;

                var team = new StartListTeam
                {
                    TeamNumber = teamNumber,
                    StartTime = FormatTime(startTime),
                    EndTime = FormatTime(endTime),
                    ShooterCount = teamRegistrations.Count,
                    WeaponClasses = teamRegistrations.Select(r => r.MemberClass).Distinct().OrderBy(c => c).ToList(),
                    Shooters = teamRegistrations.Select(reg => new StartListShooter
                    {
                        Position = position++,
                        Name = reg.MemberName ?? "Okänd deltagare",
                        Club = UmbracoStartListRepository.IsUnknownClub(reg.MemberClub)
                            ? _repository.GetMemberClub(reg.MemberId)
                            : reg.MemberClub,
                        WeaponClass = reg.MemberClass,
                        MemberId = reg.MemberId
                    }).ToList()
                };

                teams.Add(team);
                teamNumber++;
                startTime = endTime;
            }

            return teams;
        }

        private List<CompetitionRegistration> SortAndDeduplicateWithinTeam(List<CompetitionRegistration> registrations, string memberSortOrder)
        {
            var uniqueRegistrations = registrations
                .GroupBy(r => r.MemberId)
                .Select(g => g.First())
                .ToList();

            return memberSortOrder switch
            {
                "FirstName" => uniqueRegistrations.OrderBy(r => r.MemberName?.Split(' ').FirstOrDefault() ?? "").ToList(),
                "LastName" => uniqueRegistrations.OrderBy(r => r.MemberName?.Split(' ').LastOrDefault() ?? "").ToList(),
                "ClubName" => uniqueRegistrations.OrderBy(r => r.MemberClub ?? "").ToList(),
                "Class" => uniqueRegistrations.OrderBy(r => r.MemberClass ?? "").ToList(),
                _ => uniqueRegistrations.OrderBy(r => r.MemberName ?? "").ToList()
            };
        }

        private string GetWeaponClassLevel(string weaponClass)
        {
            if (weaponClass.StartsWith("A")) return "A";
            if (weaponClass.StartsWith("B")) return "B";
            if (weaponClass.StartsWith("C")) return "C";
            if (weaponClass.StartsWith("R")) return "R";
            if (weaponClass.StartsWith("M")) return "M";
            if (weaponClass.StartsWith("L")) return "L";
            return "Z";
        }

        private string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}";
        }
    }
}
