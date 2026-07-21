using HpskSite.Services.Staffing;

namespace HpskSite.Services
{
    /// <summary>Home-page summary of a member's functionary commitments — mirrors BoardHubService, powering
    /// the "Dina funktionärsuppdrag" card next to "Ditt styrelsearbete".</summary>
    public class StaffHubService
    {
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
            try
            {
                foreach (var g in _staffing.GetMyAssignments(memberId))
                {
                    if (g.Assignments.Count == 0) continue;
                    s.Assignments.Add(new StaffHubItem
                    {
                        CompetitionId = g.CompetitionId,
                        CompName = g.CompName,
                        CompDate = g.CompDate,
                        RoleSummary = string.Join(", ", g.Assignments.Select(a => a.RoleName).Distinct()),
                        NeedsResponse = g.Assignments.Any(a =>
                            string.Equals(a.Status, Models.Staffing.StaffAssignmentStatus.Invited, StringComparison.OrdinalIgnoreCase)),
                    });
                }
                var open = _signup.GetOpenSignups(memberId);
                s.OpenCount = open.Count;
                s.OpenFirstCompetitionId = open.FirstOrDefault()?.CompetitionId;
            }
            catch { /* summary is best-effort; never break the home page */ }
            s.HasAny = s.Assignments.Count > 0 || s.OpenCount > 0;
            return s;
        }
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
    }
}
