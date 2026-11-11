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
    /// <param name="ParentReader">
    /// Kan ändra i huvudplaneringen som läser en delplanering utan att vara med i den (<see cref="PlanningFamily"/>). Läser
    /// allt som en deltagare läser — men är ingen deltagare: skriver inget, får inga notiser, räknas inte någonstans.
    /// (Huvudplaneringens sammankallande är sammankallande i delplaneringen — en deltagare, inte en läsare.)
    /// </param>
    public sealed record PlanningCaller(PlanningMemberRow? Participant, bool ViaLink, bool ParentReader = false)
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
    /// deltagarnas — samma princip som vapenregistret. Sajtadmin öppnar som LÄSARE (support) och kan, med ett uttryckligt
    /// klick, gå med som support (<c>PlanningController.JoinAsSupport</c>, Stefan 2026-11-11): en vanlig deltagarrad som
    /// Sammankallande, märkt "(support)", utan mejl — så varje åtgärd har en författare och gruppen ser vem som hjälpt till.</para>
    ///
    /// <para><b>⚠️ Alla som deltar får skapa och boka möten</b> (Stefan 2026-10-21) — en
    /// representant ska kunna föreslå tider utan att be den sammankallande. <b>Följer</b> läser allt
    /// men skriver inte, röstar inte och räknas inte bland dem vi väntar på.</para>
    /// </summary>
    public static class PlanningAccess
    {
        private static bool AtLeast(PlanningCaller c, string level) =>
            c.IsParticipant && PlanningAccessLevel.Rank(c.Access) >= PlanningAccessLevel.Rank(level);

        /// <summary>Läsa planeringen. Deltagarna — och Kan ändra i huvudplaneringen, i en delplanering (bara läsa).</summary>
        public static bool CanReadContent(PlanningCaller c) => c.IsParticipant || (c.ParentReader && !c.ViaLink);
        public static bool CanReadMessages(PlanningCaller c) => CanReadContent(c);

        /// <summary>
        /// Stefan 2026-10-25: skapa en delplanering — den sammankallande, inloggad, och bara i en planering som inte själv
        /// är en delplanering (en nivå).
        /// </summary>
        public static bool CanCreateSub(PlanningCaller c, PlanningRow p) => CanManagePlanning(c) && !(p.ParentPlanningId > 0);

        /// <summary>
        /// Flytta ett område till en annan planering i familjen: den sammankallande här. Att hen också leder målet prövas av
        /// anroparen (huvudplaneringens sammankallande leder varje delplanering).
        /// </summary>
        public static bool CanMoveAreas(PlanningCaller c) => CanManagePlanning(c);

        /// <summary>
        /// Stefan 2026-10-25 (Expertgruppen B5, K2 §3.9): områdets ANSVARIGE (Deltar och uppåt, inloggad) leder områdets
        /// uppgifter — tar dem, ändrar dem och sätter läget. Att besluta <i>Utförd</i> ingår inte. <paramref name="areaLeadPid"/>
        /// = uppgiftens områdes ansvarige (deltagar-id), eller null.
        /// </summary>
        public static bool LeadsArea(PlanningCaller c, int? areaLeadPid) =>
            areaLeadPid > 0 && areaLeadPid == c.ParticipantId && AtLeast(c, PlanningAccessLevel.Participant) && !c.ViaLink;

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

        /// <summary>
        /// P2b: planeringens bemanning (roller, dagar, pass, personer). Kan ändra och Sammankallande — samma
        /// som planeringen i övrigt.
        /// </summary>
        public static bool CanEditStaffing(PlanningCaller c) => CanEditPlanning(c);

        /// <summary>
        /// Stefan B1 (2026-10-25, Expertgruppen K2): vem som får ändra bemanningen i en planering — den som får ändra
        /// i planeringen själv (<paramref name="own"/>: Kan ändra eller Sammankallande, aktiv), ELLER Sammankallande/Kan
        /// ändra i huvudplaneringen (<paramref name="root"/>: aktiv, inte en rad som bara kom via tävlingen). Härleds vid
        /// varje anrop — förut krävdes en egen rad i delen, som B4 bara lade in när delen skapades eller öppnades, så en
        /// sammankallande som tillkom senare var utelåst. Den som fått behörighet i tävlingen genom bemanningen räknas
        /// INTE (B2). <paramref name="root"/> är null för en huvudplanering (eller en planering utan familj).
        /// </summary>
        public static bool CanEditStaffingIn(PlanningMemberRow? own, PlanningMemberRow? root) =>
            (own is { IsActive: true } && CanEditStaffing(new PlanningCaller(own, false)))
            || PlanningFamily.ReadsSubViaParent(root);

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

        /// <summary>
        /// Kan ändra och uppåt — men bara inloggad. Omgång 3 (Expertgruppen K2): en vidarebefordrad mejllänk ska inte bära
        /// avsändarens rätt att ändra ANDRAS saker. Det egna (min uppgift, mitt möte, min punkt) går fortfarande via länken.
        /// </summary>
        private static bool EditorSignedIn(PlanningCaller c) => AtLeast(c, PlanningAccessLevel.Editor) && !c.ViaLink;

        /// <summary>Ändra eller ställa in ett möte: den som skapade det, eller den som kan ändra planeringen (inloggad).</summary>
        public static bool CanEditMeeting(PlanningCaller c, PlanningMeetingRow m) =>
            CanUseMeetings(c) && (m.CreatedBy == c.ParticipantId || EditorSignedIn(c));

        // ---------------------------------------------------------------- uppgifter, punkter, roller
        // (Stefan 2026-10-23) Uppgifter fördelas på mötena av dem som deltar; den som har uppgiften säger
        // själv hur långt hen kommit; om den är UTFÖRD bestämmer mötet — eller den sammankallande.

        /// <summary>Skapa en uppgift: Deltar och uppåt (det är så det går till på ett möte).</summary>
        public static bool CanCreateTasks(PlanningCaller c) => CanWriteMessages(c);

        /// <summary>
        /// Ändra en uppgifts innehåll, ansvarig och datum, eller ta bort den: den som skapade den, Kan ändra och uppåt — eller
        /// områdets ansvarige (<see cref="LeadsArea"/>).
        /// </summary>
        public static bool CanEditTask(PlanningCaller c, PlanningTaskRow t, int? areaLeadPid = null) =>
            c.IsParticipant && (t.CreatedBy == c.ParticipantId || EditorSignedIn(c) || LeadsArea(c, areaLeadPid));

        /// <summary>
        /// Ändra läget (fram till Klar att pröva) och skriva hur det går: den som har uppgiften — även den
        /// som följer — eller Kan ändra och uppåt (inloggad).
        /// </summary>
        public static bool CanReportOnTask(PlanningCaller c, PlanningTaskRow t, int? areaLeadPid = null) =>
            c.IsParticipant && (t.AssigneeParticipantId == c.ParticipantId || EditorSignedIn(c) || LeadsArea(c, areaLeadPid));

        /// <summary>
        /// Besluta Utförd / Fortsätter / ångra. På ett pågående möte: alla som deltar (mötet beslutar) — inloggade;
        /// via mejllänken bara den egna uppgiften (Stefan 2026-10-24). Mellan mötena: bara den sammankallande.
        /// </summary>
        public static bool CanDecideTask(PlanningCaller c, bool atCurrentMeeting) =>
            atCurrentMeeting ? CanUseMeetings(c) && !c.ViaLink : CanManagePlanning(c);

        private static bool DecidesAtMeeting(PlanningCaller c, PlanningTaskRow? t) =>
            CanUseMeetings(c) && (!c.ViaLink || (t != null && t.AssigneeParticipantId == c.ParticipantId));

        /// <summary>
        /// P2: samma beslut med planeringens val "Utförd beslutas på mötena". Är valet AV (tävlingens egen
        /// planering) får den ansvarige och Kan ändra markera Utförd direkt, även utanför ett möte.
        /// </summary>
        public static bool CanDecideTask(PlanningCaller c, bool atCurrentMeeting, PlanningRow p, PlanningTaskRow? t)
        {
            if (atCurrentMeeting) return DecidesAtMeeting(c, t);
            if (p.DoneAtMeetingsOnly) return CanManagePlanning(c);
            return EditorSignedIn(c)
                || (t != null && c.IsParticipant && t.AssigneeParticipantId == c.ParticipantId);
        }

        /// <summary>Lägga upp, ändra och ta bort områden; mallar, kretsens checklista och stationer: Kan ändra och uppåt (inte via mejllänk).</summary>
        public static bool CanManageAreas(PlanningCaller c) => CanEditPlanning(c);

        /// <summary>Kommentera en uppgift: Deltar och uppåt, och den som har uppgiften.</summary>
        public static bool CanCommentOnTask(PlanningCaller c, PlanningTaskRow t) =>
            CanWriteMessages(c) || (c.IsParticipant && t.AssigneeParticipantId == c.ParticipantId);

        /// <summary>
        /// Omgång 3 steg 10 (K5): via mejllänken arbetar bara en GÄST (utan konto) med dokument — hen har ingen annan
        /// väg in. Den som har ett konto loggar in, som för allt annat som skriver.
        /// </summary>
        private static bool DocumentsViaLinkOk(PlanningCaller c) => !c.ViaLink || c.Participant is { IsGuest: true };

        /// <summary>Lägga till ett dokument eller en länk: Deltar och uppåt; inloggad, eller gäst via länken.</summary>
        public static bool CanAddDocument(PlanningCaller c) => CanWriteMessages(c) && DocumentsViaLinkOk(c);

        /// <summary>Ta bort ett dokument: den som lade till det, eller Kan ändra och uppåt (en gäst bara sina egna).</summary>
        public static bool CanRemoveDocument(PlanningCaller c, PlanningDocumentRow d) =>
            c.IsParticipant && DocumentsViaLinkOk(c)
            && (d.AddedBy == c.ParticipantId || (!c.ViaLink && AtLeast(c, PlanningAccessLevel.Editor)));

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
            c.IsParticipant && DocumentsViaLinkOk(c)
            && (r.AddedBy == c.ParticipantId || (!c.ViaLink && AtLeast(c, PlanningAccessLevel.Editor)));

        /// <summary>Koppla och koppla loss en tävling: den sammankallande (plus tävlingens redigerare, prövas av anroparen).</summary>
        public static bool CanLinkCompetition(PlanningCaller c) => CanManagePlanning(c);

        /// <summary>Lägga till något att prata om på ett möte: Deltar och uppåt.</summary>
        public static bool CanAddTopic(PlanningCaller c) => CanUseMeetings(c);

        /// <summary>Ta bort en punkt: den som lade till den, eller Kan ändra och uppåt.</summary>
        public static bool CanRemoveTopic(PlanningCaller c, PlanningMeetingTopicRow t) =>
            c.IsParticipant && (t.AddedBy == c.ParticipantId || EditorSignedIn(c));

        /// <summary>Ge och ta bort roller: den sammankallande (som nivån och vad man företräder).</summary>
        public static bool CanManageRoles(PlanningCaller c) => CanManagePlanning(c);

        // ---------------------------------------------------------------- förslag och beslut (P3)

        /// <summary>Lägga fram ett förslag: Deltar och uppåt.</summary>
        public static bool CanProposeDecision(PlanningCaller c) => CanWriteMessages(c);

        /// <summary>Svara Vi kan / Vi kan inte: de som kallas till mötena (Deltar och uppåt), även via mejllänken.</summary>
        public static bool CanRespondToDecision(PlanningCaller c) => CanUseMeetings(c);

        /// <summary>Ändra eller dra tillbaka ett öppet förslag: den som lade fram det, eller Kan ändra och uppåt.</summary>
        public static bool CanEditDecision(PlanningCaller c, PlanningDecisionRow d) =>
            c.IsParticipant && d.IsOpen && (d.ProposedBy == c.ParticipantId || EditorSignedIn(c));

        /// <summary>Registrera eller ångra beslutet, och skicka förslaget till en styrelse: Kan ändra och uppåt, inloggad.</summary>
        public static bool CanDecideDecision(PlanningCaller c) => CanEditPlanning(c);

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
