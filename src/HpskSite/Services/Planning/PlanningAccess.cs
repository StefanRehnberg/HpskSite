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
    /// <param name="ViaLink">Kom in genom en signerad länk i ett mejl (utan inloggning).</param>
    public sealed record PlanningCaller(PlanningMemberRow? Participant, bool ViaLink)
    {
        public static readonly PlanningCaller Nobody = new(null, false);
        public bool IsParticipant => Participant is { IsActive: true };
        public int ParticipantId => Participant?.Id ?? 0;
        public string Access => IsParticipant ? Participant!.Access : "";
    }

    /// <summary>
    /// Planeringens behörighet som RENA regler, så att de går att pröva utan databas.
    ///
    /// <para><b>⚠️ Planeringsgruppen är sin egen enhet</b> (Stefan 2026-10-23): den som är inbjuden
    /// är med — ingen annan. Ingen förening, styrelse eller roll ger läsrätt; en krets som vill vara med
    /// utser personer, och vad var och en företräder står på deltagarraden.</para>
    ///
    /// <para><b>⚠️ Ingen underförstådd rätt för sajtadministratören.</b> Meddelandena är
    /// deltagarnas — samma princip som vapenregistret. En sajtadmin som behöver hjälpa till bjuds in.</para>
    ///
    /// <para><b>⚠️ Alla som deltar får skapa och boka möten</b> (Stefan 2026-10-21) — en
    /// representant ska kunna föreslå tider utan att be den sammankallande. <b>Följer</b> läser allt
    /// men skriver inte, röstar inte och räknas inte bland dem vi väntar på.</para>
    /// </summary>
    public static class PlanningAccess
    {
        private static bool AtLeast(PlanningCaller c, string level) =>
            c.IsParticipant && PlanningAccessLevel.Rank(c.Access) >= PlanningAccessLevel.Rank(level);

        public static bool CanReadContent(PlanningCaller c) => c.IsParticipant;
        public static bool CanReadMessages(PlanningCaller c) => c.IsParticipant;

        /// <summary>Skriva meddelanden. Deltar och uppåt — Följer läser bara.</summary>
        public static bool CanWriteMessages(PlanningCaller c) => AtLeast(c, PlanningAccessLevel.Participant);

        /// <summary>Föreslå, rösta, boka och påminna. Deltar och uppåt.</summary>
        public static bool CanUseMeetings(PlanningCaller c) => AtLeast(c, PlanningAccessLevel.Participant);

        /// <summary>
        /// Är hen kallad till mötena — väntar vi på hens svar om tiderna, och får hen mötesmejlen? Inte den
        /// som följer: hen ser mötena på sidan och får kvällsmejlet med meddelandena, men inga mötesmejl.
        /// </summary>
        public static bool IsCalledToMeetings(PlanningMemberRow p) =>
            p.IsActive && PlanningAccessLevel.Rank(p.Access) >= PlanningAccessLevel.Rank(PlanningAccessLevel.Participant);

        /// <summary>Namn, mål i tid. Kan ändra och Sammankallande.</summary>
        public static bool CanEditPlanning(PlanningCaller c) => AtLeast(c, PlanningAccessLevel.Editor) && !c.ViaLink;

        /// <summary>Arkivering, borttagning. Bara Sammankallande.</summary>
        public static bool CanManagePlanning(PlanningCaller c) =>
            c.IsParticipant && c.Access == PlanningAccessLevel.Owner && !c.ViaLink;

        /// <summary>Bjuda in. Kan ändra och Sammankallande.</summary>
        public static bool CanInvite(PlanningCaller c) => CanEditPlanning(c);

        /// <summary>
        /// Får den som frågar ge den här nivån? Kan ändra ger högst Kan ändra;
        /// bara Sammankallande gör någon till Sammankallande.
        /// </summary>
        public static bool CanGrant(PlanningCaller c, string access) =>
            CanInvite(c) && PlanningAccessLevel.Rank(access) > 0
            && (access != PlanningAccessLevel.Owner || c.Access == PlanningAccessLevel.Owner);

        /// <summary>Ändra en annans nivå eller vad hen företräder, byta ut eller ta bort hen. Bara Sammankallande.</summary>
        public static bool CanManageParticipants(PlanningCaller c) => CanManagePlanning(c);

        /// <summary>Ändra eller ställa in ett möte: den som skapade det, eller den som kan ändra planeringen.</summary>
        public static bool CanEditMeeting(PlanningCaller c, PlanningMeetingRow m) =>
            CanUseMeetings(c) && (m.CreatedBy == c.ParticipantId || AtLeast(c, PlanningAccessLevel.Editor));

        // ---------------------------------------------------------------- uppgifter, punkter, roller
        // (Stefan 2026-10-23) Uppgifter fördelas på mötena av dem som deltar; den som har uppgiften säger
        // själv hur långt hen kommit; om den är UTFÖRD bestämmer mötet — eller den sammankallande.

        /// <summary>Skapa en uppgift: Deltar och uppåt (det är så det går till på ett möte).</summary>
        public static bool CanCreateTasks(PlanningCaller c) => CanWriteMessages(c);

        /// <summary>Ändra en uppgifts innehåll, ansvarig och datum, eller ta bort den: den som skapade den, eller Kan ändra och uppåt.</summary>
        public static bool CanEditTask(PlanningCaller c, PlanningTaskRow t) =>
            c.IsParticipant && (t.CreatedBy == c.ParticipantId || AtLeast(c, PlanningAccessLevel.Editor));

        /// <summary>
        /// Ändra läget (fram till Klar att pröva) och skriva hur det går: den som har uppgiften — även den
        /// som följer — eller Kan ändra och uppåt.
        /// </summary>
        public static bool CanReportOnTask(PlanningCaller c, PlanningTaskRow t) =>
            c.IsParticipant && (t.AssigneeParticipantId == c.ParticipantId || AtLeast(c, PlanningAccessLevel.Editor));

        /// <summary>
        /// Besluta Utförd / Fortsätter / ångra. På ett pågående möte: alla som deltar (mötet beslutar).
        /// Mellan mötena: bara den sammankallande.
        /// </summary>
        public static bool CanDecideTask(PlanningCaller c, bool atCurrentMeeting) =>
            atCurrentMeeting ? CanUseMeetings(c) : CanManagePlanning(c);

        /// <summary>Lägga till något att prata om på ett möte: Deltar och uppåt.</summary>
        public static bool CanAddTopic(PlanningCaller c) => CanUseMeetings(c);

        /// <summary>Ta bort en punkt: den som lade till den, eller Kan ändra och uppåt.</summary>
        public static bool CanRemoveTopic(PlanningCaller c, PlanningMeetingTopicRow t) =>
            c.IsParticipant && (t.AddedBy == c.ParticipantId || AtLeast(c, PlanningAccessLevel.Editor));

        /// <summary>Ge och ta bort roller: den sammankallande (som nivån och vad man företräder).</summary>
        public static bool CanManageRoles(PlanningCaller c) => CanManagePlanning(c);

        /// <summary>Redigera eller ta bort ett meddelande: bara författaren.</summary>
        public static bool CanEditMessage(PlanningCaller c, PlanningMessageRow m) =>
            c.IsParticipant && m.Kind == "Message" && m.ParticipantId == c.ParticipantId && m.DeletedAt == null;

        /// <summary>
        /// Lämnar en ändring planeringen utan sammankallande? Den sista får varken tas bort
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
