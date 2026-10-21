using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Vem som frågar. Byggs av <see cref="PlanningService.ResolveCaller"/> ur sessionen eller ur en
    /// signerad mejllänk — reglerna nedan vet inte vilket, och behöver inte veta det.
    /// </summary>
    /// <param name="Participant">Den aktiva deltagarraden, eller null.</param>
    /// <param name="OwnerOrgReader">Styrelseledamot eller administratör i ägarföreningen, när planeringen
    /// är synlig för föreningen (<see cref="PlanningVisibility.OwnerOrg"/>).</param>
    /// <param name="ViaLink">Kom in genom en signerad länk i ett mejl (utan inloggning).</param>
    public sealed record PlanningCaller(PlanningMemberRow? Participant, bool OwnerOrgReader, bool ViaLink)
    {
        public static readonly PlanningCaller Nobody = new(null, false, false);
        public bool IsParticipant => Participant is { IsActive: true };
        public int ParticipantId => Participant?.Id ?? 0;
        public string Access => IsParticipant ? Participant!.Access : "";
    }

    /// <summary>
    /// Planeringens behörighet som RENA regler, så att de går att pröva utan databas.
    ///
    /// <para><b>⚠️ Ingen underförstådd rätt för sajtadministratören.</b> Meddelandena är
    /// deltagarnas — samma princip som vapenregistret. En sajtadmin som behöver hjälpa till bjuds in.</para>
    ///
    /// <para><b>⚠️ Föreningens läsrätt omfattar bara innehållet, aldrig meddelandena</b>
    /// (Stefan 2026-10-21, beslut 10.3).</para>
    ///
    /// <para><b>⚠️ Alla som är med får skapa och boka möten</b> (Stefan 2026-10-21) — en
    /// representant ska kunna föreslå tider utan att be ägaren.</para>
    /// </summary>
    public static class PlanningAccess
    {
        public static bool CanReadContent(PlanningCaller c) => c.IsParticipant || c.OwnerOrgReader;

        public static bool CanReadMessages(PlanningCaller c) => c.IsParticipant;
        public static bool CanWriteMessages(PlanningCaller c) => c.IsParticipant;

        public static bool CanUseMeetings(PlanningCaller c) => c.IsParticipant;

        /// <summary>Namn, mål i tid. Kan ändra och Kan ändra allt.</summary>
        public static bool CanEditPlanning(PlanningCaller c) =>
            c.IsParticipant && PlanningAccessLevel.Rank(c.Access) >= 2 && !c.ViaLink;

        /// <summary>Synlighet, arkivering, borttagning. Bara Kan ändra allt.</summary>
        public static bool CanManagePlanning(PlanningCaller c) =>
            c.IsParticipant && c.Access == PlanningAccessLevel.Owner && !c.ViaLink;

        /// <summary>Bjuda in. Kan ändra och Kan ändra allt.</summary>
        public static bool CanInvite(PlanningCaller c) => CanEditPlanning(c);

        /// <summary>
        /// Får den som frågar ge den här rättigheten? Kan ändra ger högst Kan ändra;
        /// bara Kan ändra allt ger Kan ändra allt.
        /// </summary>
        public static bool CanGrant(PlanningCaller c, string access) =>
            CanInvite(c) && PlanningAccessLevel.Rank(access) > 0
            && (access != PlanningAccessLevel.Owner || c.Access == PlanningAccessLevel.Owner);

        /// <summary>Ändra en annans rättighet eller ta bort hen. Bara Kan ändra allt.</summary>
        public static bool CanManageParticipants(PlanningCaller c) => CanManagePlanning(c);

        /// <summary>Ändra eller ställa in ett möte: den som skapade det, eller den som kan ändra planeringen.</summary>
        public static bool CanEditMeeting(PlanningCaller c, PlanningMeetingRow m) =>
            c.IsParticipant && (m.CreatedBy == c.ParticipantId || PlanningAccessLevel.Rank(c.Access) >= 2);

        /// <summary>Redigera eller ta bort ett meddelande: bara författaren.</summary>
        public static bool CanEditMessage(PlanningCaller c, PlanningMessageRow m) =>
            c.IsParticipant && m.Kind == "Message" && m.ParticipantId == c.ParticipantId && m.DeletedAt == null;

        /// <summary>
        /// Lämnar en ändring planeringen utan någon som kan ändra allt? Den sista får varken tas bort
        /// eller sänkas — annars kan ingen längre bjuda in eller ändra något.
        /// </summary>
        public static bool WouldLeaveNoOwner(IEnumerable<PlanningMemberRow> active, int participantId, string? newAccess)
        {
            var owners = active.Where(p => p.IsActive && p.Access == PlanningAccessLevel.Owner).Select(p => p.Id).ToList();
            if (!owners.Contains(participantId)) return false;
            if (newAccess == PlanningAccessLevel.Owner) return false;
            return owners.Count <= 1;
        }
    }
}
