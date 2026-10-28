using System;
using Microsoft.AspNetCore.DataProtection;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Opaque, non-forgeable token for an external (non-member) helper to accept/decline a StaffAssignment
    /// without logging in — the token IS the authorization (mirrors the board-justering / Märken-verify
    /// IDataProtector pattern). Keys are persisted in Program.cs so links survive an app recycle.
    /// <para>Omgång 3 (Expertgruppen K3): v1 was bound only to the row id, so a link sent to the person who fell ill kept
    /// working after the work was handed over — and showed the new person's name. v2 also carries the row's
    /// <see cref="StaffAssignment.InviteNonce"/>, which every handover replaces. v1 is still accepted for a row that has
    /// never been handed over (nonce null). ⚠️ Never change v1's purpose string — it would void every link already sent.</para>
    /// </summary>
    public static class StaffingInviteToken
    {
        public const string Purpose = "Staffing.AssignmentInvite.v1";
        public const string PurposeV2 = "Staffing.AssignmentInvite.v2";

        public static string Protect(IDataProtectionProvider provider, StaffAssignment a)
            => a.InviteNonce is Guid n
                ? provider.CreateProtector(PurposeV2).Protect($"{a.Id}|{n:N}")
                : provider.CreateProtector(Purpose).Protect(a.Id.ToString());

        /// <summary>The row id and the nonce the link was issued with (null = a v1 link). Null = not a valid link.</summary>
        public static (int Id, Guid? Nonce)? Read(IDataProtectionProvider provider, string token)
        {
            try
            {
                var parts = provider.CreateProtector(PurposeV2).Unprotect(token).Split('|');
                return (int.Parse(parts[0]), Guid.ParseExact(parts[1], "N"));
            }
            catch { }
            try { return (int.Parse(provider.CreateProtector(Purpose).Unprotect(token)), null); }
            catch { return null; }
        }

        /// <summary>Does a link with <paramref name="tokenNonce"/> still belong to the row's current person?</summary>
        public static bool StillValid(Guid? tokenNonce, Guid? rowNonce) => rowNonce == tokenNonce;
    }

    /// <summary>En placering på den skrivskyddade sidan "Din placering".</summary>
    public class InvitePlacement
    {
        public string RoleName { get; set; } = "";
        public string ScopeLabel { get; set; } = "";
        /// <summary>"lördag 14 november 08:00–13:00" eller "… hela dagen"; tom när raden saknar dag.</summary>
        public string When { get; set; } = "";
    }

    /// <summary>Model for the chromeless external READ-ONLY page "Din placering" (/mina-uppdrag/svar?t=…). Äldre länkar hade ja/nej — de visar nu
    /// samma skrivskyddade sida (Stefan 2026-10-28: placeringar tilldelas, de godkänns inte).</summary>
    public class InviteResponseModel
    {
        /// <summary>Ett vänligt besked överst (till exempel när en gammal sida skickat ett svar som inte längre behövs).</summary>
        public string? Notice { get; set; }
        /// <summary>Alla personens placeringar i samma planeringsfamilj (den länkade först).</summary>
        public List<InvitePlacement> Placements { get; set; } = new();
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
