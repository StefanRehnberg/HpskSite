using Microsoft.AspNetCore.DataProtection;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Opaque, non-forgeable token for an external (non-member) helper to accept/decline a StaffAssignment
    /// without logging in — the token IS the authorization (mirrors the board-justering / Märken-verify
    /// IDataProtector pattern). Keys are persisted in Program.cs so links survive an app recycle.
    /// </summary>
    public static class StaffingInviteToken
    {
        public const string Purpose = "Staffing.AssignmentInvite.v1";

        public static string Protect(IDataProtectionProvider provider, int assignmentId)
            => provider.CreateProtector(Purpose).Protect(assignmentId.ToString());

        public static int? Unprotect(IDataProtectionProvider provider, string token)
        {
            try { return int.Parse(provider.CreateProtector(Purpose).Unprotect(token)); }
            catch { return null; }
        }
    }

    /// <summary>Model for the chromeless external accept/decline page (/mina-uppdrag/svar?t=…).</summary>
    public class InviteResponseModel
    {
        public bool Valid { get; set; }
        public string Token { get; set; } = "";
        public string CompName { get; set; } = "";
        public string RoleName { get; set; } = "";
        public string ScopeLabel { get; set; } = "";
        public string? ShiftLabel { get; set; }
        public string PersonName { get; set; } = "";
        public string Status { get; set; } = StaffAssignmentStatus.Planned;
    }
}
