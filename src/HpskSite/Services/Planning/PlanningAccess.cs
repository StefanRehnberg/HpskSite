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

        /// <summary>
        /// P2: samma beslut med planeringens val "Utförd beslutas på mötena". Är valet AV (tävlingens egen
        /// planering) får den ansvarige och Kan ändra markera Utförd direkt, även utanför ett möte.
        /// </summary>
        public static bool CanDecideTask(PlanningCaller c, bool atCurrentMeeting, PlanningRow p, PlanningTaskRow? t)
        {
            if (atCurrentMeeting) return CanUseMeetings(c);
            if (p.DoneAtMeetingsOnly) return CanManagePlanning(c);
            return AtLeast(c, PlanningAccessLevel.Editor)
                || (t != null && c.IsParticipant && t.AssigneeParticipantId == c.ParticipantId);
        }

        /// <summary>Områden, mallar, kretsens checklista och stationer: Kan ändra och uppåt (inte via mejllänk).</summary>
        public static bool CanManageAreas(PlanningCaller c) => CanEditPlanning(c);

        /// <summary>Kommentera en uppgift: Deltar och uppåt, och den som har uppgiften.</summary>
        public static bool CanCommentOnTask(PlanningCaller c, PlanningTaskRow t) =>
            CanWriteMessages(c) || (c.IsParticipant && t.AssigneeParticipantId == c.ParticipantId);

        /// <summary>Lägga till ett dokument eller en länk: Deltar och uppåt, inloggad.</summary>
        public static bool CanAddDocument(PlanningCaller c) => CanWriteMessages(c) && !c.ViaLink;

        /// <summary>Ta bort ett dokument: den som lade till det, eller Kan ändra och uppåt.</summary>
        public static bool CanRemoveDocument(PlanningCaller c, PlanningDocumentRow d) =>
            c.IsParticipant && !c.ViaLink && (d.AddedBy == c.ParticipantId || AtLeast(c, PlanningAccessLevel.Editor));

        /// <summary>Ändra namn, beskrivning och kategori på ett dokument: samma som att ta bort det.</summary>
        public static bool CanEditDocument(PlanningCaller c, PlanningDocumentRow d) => CanRemoveDocument(c, d);

        /// <summary>Skapa, byta namn på och ta bort kategorier i dokumentarkivet: Kan ändra och uppåt.</summary>
        public static bool CanManageDocumentCategories(PlanningCaller c) => CanEditPlanning(c);

        /// <summary>
        /// Bifoga ett dokument till ett möte, en punkt, en uppgift eller ett område: Deltar och uppåt.
        /// Till ett meddelande bifogar bara den som skrev det (prövas av anroparen).
        /// </summary>
        public static bool CanAttachDocument(PlanningCaller c) => CanAddDocument(c);

        /// <summary>Ta bort en bifogning: den som bifogade, eller Kan ändra och uppåt. Dokumentet står kvar i arkivet.</summary>
        public static bool CanDetachDocument(PlanningCaller c, PlanningDocumentRefRow r) =>
            c.IsParticipant && !c.ViaLink && (r.AddedBy == c.ParticipantId || AtLeast(c, PlanningAccessLevel.Editor));

        /// <summary>Koppla och koppla loss en tävling: den sammankallande (plus tävlingens redigerare, prövas av anroparen).</summary>
        public static bool CanLinkCompetition(PlanningCaller c) => CanManagePlanning(c);

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
