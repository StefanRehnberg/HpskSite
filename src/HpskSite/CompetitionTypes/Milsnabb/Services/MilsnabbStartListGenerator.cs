using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models.ViewModels.Competition;

namespace HpskSite.CompetitionTypes.Milsnabb.Services
{
    /// <summary>
    /// Generates start list teams for Milsnabb competitions with A+R mixed
    /// in the same teams and B+C mixed in other teams.
    /// A member registered in both A1 and R1 will appear in separate teams
    /// (one per registration) so they can shoot both classes.
    /// </summary>
    public class MilsnabbStartListGenerator
    {
        private readonly UmbracoStartListRepository _repository;

        public MilsnabbStartListGenerator(UmbracoStartListRepository repository)
        {
            _repository = repository;
        }

        public List<StartListTeam> Generate(
            List<CompetitionRegistration> registrations,
            int maxPerTeam,
            TimeSpan startTime,
            int intervalMinutes,
            string memberSortOrder = "FirstName")
        {
            var arRegistrations = registrations
                .Where(r => r.MemberClass.StartsWith("A", StringComparison.OrdinalIgnoreCase) ||
                            r.MemberClass.StartsWith("R", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.MemberClass)
                .ThenBy(r => r.MemberName)
                .ToList();

            var bcRegistrations = registrations
                .Where(r => r.MemberClass.StartsWith("B", StringComparison.OrdinalIgnoreCase) ||
                            r.MemberClass.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.MemberClass)
                .ThenBy(r => r.MemberName)
                .ToList();

            // Any remaining weapon classes (M, L, etc.) go after B+C
            var otherRegistrations = registrations
                .Where(r => !r.MemberClass.StartsWith("A", StringComparison.OrdinalIgnoreCase) &&
                            !r.MemberClass.StartsWith("R", StringComparison.OrdinalIgnoreCase) &&
                            !r.MemberClass.StartsWith("B", StringComparison.OrdinalIgnoreCase) &&
                            !r.MemberClass.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.MemberClass)
                .ThenBy(r => r.MemberName)
                .ToList();

            var teams = new List<StartListTeam>();
            var teamNumber = 1;

            GenerateTeamsFromGroup(arRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder, teams);
            GenerateTeamsFromGroup(bcRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder, teams);
            GenerateTeamsFromGroup(otherRegistrations, maxPerTeam, ref startTime, ref teamNumber, intervalMinutes, memberSortOrder, teams);

            return teams;
        }

        /// <summary>
        /// Generates teams from a group of registrations using round-robin picking.
        /// Each team gets at most one registration per member. A member with multiple
        /// registrations (e.g. A1 and R1) will appear in different teams.
        /// </summary>
        private void GenerateTeamsFromGroup(
            List<CompetitionRegistration> registrations,
            int maxPerTeam,
            ref TimeSpan startTime,
            ref int teamNumber,
            int intervalMinutes,
            string memberSortOrder,
            List<StartListTeam> teams)
        {
            // Work on a mutable copy
            var remaining = new List<CompetitionRegistration>(registrations);

            while (remaining.Count > 0)
            {
                // Pick one registration per member (distinct by MemberId), up to maxPerTeam
                var teamRegistrations = remaining
                    .DistinctBy(r => r.MemberId)
                    .Take(maxPerTeam)
                    .ToList();

                if (teamRegistrations.Count == 0)
                    break;

                // Remove picked registrations from the pool
                foreach (var picked in teamRegistrations)
                {
                    remaining.Remove(picked);
                }

                // Sort within team
                teamRegistrations = SortWithinTeam(teamRegistrations, memberSortOrder);

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

        private static List<CompetitionRegistration> SortWithinTeam(
            List<CompetitionRegistration> registrations, string memberSortOrder)
        {
            return memberSortOrder switch
            {
                "FirstName" => registrations.OrderBy(r => r.MemberName?.Split(' ').FirstOrDefault() ?? "").ToList(),
                "LastName" => registrations.OrderBy(r => r.MemberName?.Split(' ').LastOrDefault() ?? "").ToList(),
                "ClubName" => registrations.OrderBy(r => r.MemberClub ?? "").ToList(),
                "Class" => registrations.OrderBy(r => r.MemberClass ?? "").ToList(),
                _ => registrations.OrderBy(r => r.MemberName ?? "").ToList()
            };
        }

        private static string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}";
        }
    }
}
