using System.Globalization;
using HpskSite.Services.Staffing;

namespace HpskSite.Services
{
    /// <summary>Home-page summary of a member's functionary commitments — mirrors BoardHubService, powering
    /// the "Dina funktionärsuppdrag" card next to "Ditt styrelsearbete".</summary>
    public class StaffHubService
    {
        /// <summary>How long a finished uppdrag lingers on the card. Functionaries routinely look
        /// something up in the days after a comp (who worked what, what was logged), so cutting it off
        /// at midnight is too sharp. Past ones render muted and never count as "att svara på".
        /// Shared with /mina-uppdrag, which uses it to auto-expand its "Tidigare" section.</summary>
        public const int RecentGraceDays = 3;

        private readonly StaffingService _staffing;
        private readonly StaffingSignupService _signup;

        public StaffHubService(StaffingService staffing, StaffingSignupService signup)
        {
            _staffing = staffing;
            _signup = signup;
        }

        public StaffHubSummary GetSummary(int memberId)
        {
            var s = new StaffHubSummary();
            var today = DateTime.Today;
            var cutoff = today.AddDays(-RecentGraceDays);
            try
            {
                foreach (var g in _staffing.GetMyAssignments(memberId))
                {
                    if (g.Assignments.Count == 0) continue;
                    var day = ParseDay(g.CompDate);
                    if (day != null && day < cutoff) continue;      // long over — lives on /mina-uppdrag
                    var isPast = day != null && day < today;
                    s.Assignments.Add(new StaffHubItem
                    {
                        CompetitionId = g.CompetitionId,
                        CompName = g.CompName,
                        CompDate = g.CompDate,
                        IsPast = isPast,
                        RoleSummary = string.Join(", ", g.Assignments.Select(a => a.RoleName).Distinct()),
                        // Answering an invitation after the comp is meaningless — don't nag about it.
                        NeedsResponse = !isPast && g.Assignments.Any(a =>
                            string.Equals(a.Status, Models.Staffing.StaffAssignmentStatus.Invited, StringComparison.OrdinalIgnoreCase)),
                    });
                }
                // Volunteering for a finished comp is pointless, so open sign-ups get no grace window.
                var open = _signup.GetOpenSignups(memberId)
                    .Where(o => { var d = ParseDay(o.CompDate); return d == null || d >= today; }).ToList();
                s.OpenCount = open.Count;
                s.OpenFirstCompetitionId = open.FirstOrDefault()?.CompetitionId;

                // Upcoming first (in GetMyAssignments' order), the recent-past tail newest-first.
                s.Assignments = s.Assignments.Where(a => !a.IsPast)
                    .Concat(s.Assignments.Where(a => a.IsPast).OrderByDescending(a => a.CompDate, StringComparer.Ordinal))
                    .ToList();
            }
            catch { /* summary is best-effort; never break the home page */ }
            s.HasAny = s.Assignments.Count > 0 || s.OpenCount > 0;
            return s;
        }

        /// <summary>The competition day, or null when undated. Undated comps are always kept — we can't
        /// tell whether they're over, and silently hiding a real commitment is the worse failure.</summary>
        private static DateTime? ParseDay(string? compDate) =>
            DateTime.TryParseExact(compDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.Date : null;
    }

    public class StaffHubSummary
    {
        public bool HasAny { get; set; }
        public List<StaffHubItem> Assignments { get; set; } = new();
        public int OpenCount { get; set; }
        public int? OpenFirstCompetitionId { get; set; }
        public int NeedsResponseCount => Assignments.Count(a => a.NeedsResponse);
    }

    public class StaffHubItem
    {
        public int CompetitionId { get; set; }
        public string CompName { get; set; } = "";
        public string? CompDate { get; set; }
        public string RoleSummary { get; set; } = "";
        public bool NeedsResponse { get; set; }
        /// <summary>Comp day has passed but it's still inside the grace window — show it muted.</summary>
        public bool IsPast { get; set; }
    }
}
