using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Precision.ViewModels
{
    public class PrecisionMixedTeamsGenerator
    {
        private readonly List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration> _registrations;
        private readonly int _maxPerTeam;
        private readonly TimeSpan _startTime;
        private readonly int _intervalMinutes;
        private readonly string _memberSortOrder;

        public PrecisionMixedTeamsGenerator(List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration> registrations, int maxPerTeam, TimeSpan startTime, int intervalMinutes, string memberSortOrder)
        {
            _registrations = registrations;
            _maxPerTeam = maxPerTeam;
            _startTime = startTime;
            _intervalMinutes = intervalMinutes;
            // ⚠️ Stod `= memberSortOrder = "FirstName"` — parametern skrevs över, så sorteringsvalet
            //    ignorerades alltid. Förvalet är fortfarande förnamn när inget anges.
            _memberSortOrder = string.IsNullOrWhiteSpace(memberSortOrder) ? "FirstName" : memberSortOrder;
        }

        public List<StartListTeam> GenerateStartLists()
        {
            List<StartListTeam> teams = new List<StartListTeam>();

            List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration> regs = new List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration>();
            regs.AddRange(this.GetRegsOrderedByRegs(_registrations));
            var teamNumber = 1;

            // ⚠️⚠️ STARTTIDEN FLYTTAS FRAM ETT INTERVALL PER SKJUTLAG. Stod förut
            //    StartTime = _startTime och EndTime = _startTime + 0 för VARJE lag, så alla skjutlag
            //    fick samma tid och intervallet användes aldrig (Michael Henriksson, Åmåls PK
            //    2026-09-24: "intervall 2h men bägge skjutlagen får samma starttid"). Samma regel som
            //    de andra formaten i StartListGenerator: start, slut = start + intervall, nästa start
            //    = slut. ⚠️ Särskilt viktigt här: med blandade vapengrupper står samma skytt i FLERA
            //    lag, och samma tid betyder att hen ska skjuta två gånger samtidigt.
            var currentStart = _startTime;

            while (regs.Count > 0)
            {
                regs = this.GetRegsOrderedByRegs(regs);
                var shooters = this.GetStartListShootersTeam(ref regs, _maxPerTeam);

                if (shooters?.Count > 0)
                {
                    var end = currentStart.Add(TimeSpan.FromMinutes(_intervalMinutes));
                    var team = new StartListTeam
                    {
                        TeamNumber = teamNumber,
                        StartTime = FormatTime(currentStart),
                        EndTime = FormatTime(end),
                        ShooterCount = shooters.Count,
                        WeaponClasses = shooters.Select(r => r.WeaponClass).Distinct().OrderBy(c => c).ToList(),
                        Shooters = shooters
                    };

                    teams.Add(team);
                    teamNumber++;
                    currentStart = end;
                }
            }

            return teams;
        }

        /// <summary>
        /// Returns <paramref name="registrations"/> ordered by number of registrations per person. Persons with the most number of registrations are first
        /// </summary>
        /// <param name="registrations"></param>
        /// <returns></returns>
        private List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration> GetRegsOrderedByRegs(List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration> registrations)
        {
            return registrations
            .GroupBy(reg => reg.MemberId)
            .OrderByDescending(group => group.Count())
            .SelectMany(group => group)
            .ToList();
        }

        private List<StartListShooter>? GetStartListShootersTeam(ref List<HpskSite.Models.ViewModels.Competition.CompetitionRegistration> registrations, int maxPerTeam)
        {
            var selectedRegistrations = registrations
            .DistinctBy(reg => reg.MemberId)
            .Take(maxPerTeam)
            .ToList();

            // Sort within team
            selectedRegistrations = _memberSortOrder switch
            {
                "FirstName" => selectedRegistrations.OrderBy(r => r.MemberName?.Split(' ').FirstOrDefault() ?? "").ToList(),
                "LastName" => selectedRegistrations.OrderBy(r => r.MemberName?.Split(' ').LastOrDefault() ?? "").ToList(),
                "ClubName" => selectedRegistrations.OrderBy(r => r.MemberClub ?? "").ToList(),
                "Class" => selectedRegistrations.OrderBy(r => r.MemberClass ?? "").ToList(),
                _ => selectedRegistrations.OrderBy(r => r.MemberName ?? "").ToList()
            };

            var position = 1;
            var shooters = selectedRegistrations.Select(reg => new StartListShooter
            {
                Position = position++,
                Name = reg.MemberName ?? "Okänd deltagare",
                Club = reg.MemberClub ?? "",
                WeaponClass = reg.MemberClass,
                MemberId = reg.MemberId
            }).ToList();

            // Remove selected registrations from input list
            foreach (var selected in selectedRegistrations)
            {
                registrations.Remove(selected);
            }

            return shooters;
        }

        private string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}";
        }

    }
}
