using HpskSite.Services.Mail;
using NPoco;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Aviseringarna kring en föreningsintygsförfrågan.
    ///
    /// <para><b>⚠️ Den här tjänsten finns för att flödet saknade avisering HELT.</b> En medlem kunde
    /// begära ett intyg och klubben fick aldrig veta något: förfrågan syntes bara som en siffra inne
    /// på den adminflik mottagaren skulle behöva besöka för att upptäcka att det fanns något att
    /// besöka. I praktiken fick sekreteraren höra det av medlemmen på skjutbanan. Ta inte bort
    /// anropen "för att minska mejlbruset" — utan dem är inkorgen osynlig.</para>
    ///
    /// <para><b>Vem som får mejlet:</b> klubbens utsedda föreningsintygsansvariga (de som faktiskt
    /// KAN läsa vapenuppgifterna och alltså skriva intyget). Har klubben ingen utsedd går mejlet till
    /// klubbens kontaktadress i stället — då är det ingen som kan hantera ärendet, och det är just då
    /// någon behöver få veta det. Att tiga i det läget vore det värsta av alla utfall.</para>
    ///
    /// <para><b>⚠️ Aldrig en vapenuppgift i ett mejl.</b> Bara aliaset — medlemmens eget klartextnamn
    /// på vapnet — följer med. Fabrikat, modell och kaliber är krypterade och läses genom grinden med
    /// en loggrad; ett mejl är en okontrollerad kopia och kringgår båda.</para>
    /// </summary>
    public class ForeningsintygNotificationService
    {
        private readonly EmailService _email;
        private readonly FirearmAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubs;
        private readonly ReplyContactResolver _replyContacts;
        private readonly MailReplyLinkService _replyLinks;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<ForeningsintygNotificationService> _logger;

        public ForeningsintygNotificationService(
            EmailService email,
            FirearmAuthorizationService auth,
            IMemberService memberService,
            ClubService clubs,
            ReplyContactResolver replyContacts,
            MailReplyLinkService replyLinks,
            IScopeProvider scopeProvider,
            ILogger<ForeningsintygNotificationService> logger)
        {
            _email = email;
            _auth = auth;
            _memberService = memberService;
            _clubs = clubs;
            _replyContacts = replyContacts;
            _replyLinks = replyLinks;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        // ── Vem av de ansvariga som vill ha mejl ────────────────────────────────────────────────
        //
        // ⚠️⚠️ FRÅNVARO AV RAD BETYDER MEJLA. Tabellen `ForeningsintygNotifySetting` lagrar bara
        // avvikelser. Skälet står i migreringen och tål att upprepas: behörigheten är något klubben
        // AKTIVT utser någon till, så den personen ska rimligen få veta när ett ärende kommer in.
        // Vore standardläget "mejla inte" skulle en nyutsedd person TYST gå miste om aviseringen —
        // och den tystnaden är precis det fel hela det här arbetet handlar om.
        //
        // ⚠️ Läsningen sväljer sina fel och svarar "mejla" vid problem. En trasig tabell ska ge för
        // många mejl, aldrig för få: ett uteblivet mejl är en medlem som väntar i tysthet.

        /// <summary>Medlems-id → vill ha mejl. Bara rader som AVVIKER från standardläget finns.</summary>
        public Dictionary<int, bool> GetNotifySettings(int clubId)
        {
            var map = new Dictionary<int, bool>();
            if (clubId <= 0) return map;
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var rows = uow.Database.Fetch<NotifyRow>(
                    "SELECT MemberId, Enabled FROM ForeningsintygNotifySetting WHERE ClubId = @0", clubId);
                foreach (var r in rows) map[r.MemberId] = r.Enabled;
            }
            catch (Exception ex)
            {
                // Saknad tabell (omigrerad miljö) eller läsfel → tom karta → alla får mejl.
                _logger.LogDebug(ex, "Kunde inte läsa mejlinställningar för klubb {ClubId}.", clubId);
            }
            return map;
        }

        /// <summary>Ska den här ansvariga mejlas? Ingen rad = ja.</summary>
        public bool ShouldNotify(int clubId, int memberId)
            => !GetNotifySettings(clubId).TryGetValue(memberId, out var enabled) || enabled;

        /// <summary>
        /// Sätter inställningen för EN ansvarig.
        ///
        /// <para><b>⚠️ Skriver en rad även när värdet är standardläget (true).</b> Det gör att ett
        /// uttryckligt JA går att skilja från "har aldrig rört inställningen" — vilket en ren
        /// frånvaro inte kan säga något om.</para>
        /// </summary>
        public string? SetNotify(int clubId, int memberId, bool enabled, int actorMemberId)
        {
            if (clubId <= 0 || memberId <= 0) return "Ogiltig klubb eller medlem.";
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var affected = uow.Database.Execute(
                    @"UPDATE ForeningsintygNotifySetting
                         SET Enabled = @0, ChangedAt = @1, ChangedBy = @2
                       WHERE ClubId = @3 AND MemberId = @4",
                    enabled, DateTime.Now, actorMemberId, clubId, memberId);

                if (affected == 0)
                {
                    uow.Database.Execute(
                        @"INSERT INTO ForeningsintygNotifySetting (ClubId, MemberId, Enabled, ChangedAt, ChangedBy)
                          VALUES (@0, @1, @2, @3, @4)",
                        clubId, memberId, enabled, DateTime.Now, actorMemberId);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte spara mejlinställning för medlem {MemberId} i klubb {ClubId}.",
                    memberId, clubId);
                return "Kunde inte spara inställningen.";
            }
        }

        [TableName("ForeningsintygNotifySetting")]
        private class NotifyRow
        {
            public int MemberId { get; set; }
            public bool Enabled { get; set; }
        }

        // ── Utskicksloggen ──────────────────────────────────────────────────────────────────────
        //
        // ⚠️⚠️ FINNS FÖR ATT VI INTE KUNDE SVARA PÅ FRÅGAN. När Stefan 2026-09-09 frågade om det
        // första mejlet också gick till den andra ansvariga gick svaret bara att HÄRLEDA ur
        // tidsstämpeln på en opt-out-rad. Det är ett resonemang, inte ett belägg — och hade någon
        // ändrat något i mellantiden hade resonemanget blivit fel utan att någon märkt det.
        //
        // ⚠️ LOGGAR BÅDE LYCKADE OCH MISSLYCKADE. Ett misslyckat utskick är det viktigaste att
        // kunna se i efterhand: det betyder att en medlem väntar på ett besked som aldrig kom.
        //
        // ⚠️ Skrivningen får ALDRIG fälla utskicket. Mejlet är redan skickat när vi kommer hit;
        // ett loggfel loggas i apploggen och sväljs.
        public static class NotifyKind
        {
            public const string NewRequest = "NyForfragan";
            public const string Reminder = "Paminnelse";
            public const string Decision = "Beslut";

            /// <summary>Klubben bad medlemmen komplettera. Ärendet lever — det är inget avslag.</summary>
            public const string Completion = "Komplettering";

            /// <summary>
            /// Medlemmen svarade i appen och handläggaren aviserades.
            ///
            /// <para><b>⚠️ Det är UTSKICKET till handläggaren som loggas här, inte svaret.</b>
            /// Svaret självt är en rad i <c>MailReply</c>. Loggen svarar bara på frågan "gick
            /// mejlet fram" — samma regel som för de andra sorterna.</para>
            /// </summary>
            public const string MemberReply = "MedlemSvarade";

            /// <summary>
            /// Medlemmen försökte svara men svaret gick INTE att spara.
            ///
            /// <para><b>⚠️ EGEN SORT, inte <see cref="MemberReply"/>.</b> Den som läser loggen i
            /// efterhand måste kunna skilja "medlemmen svarade" från "medlemmen försökte och vi
            /// tappade det" — det andra betyder att det finns ett svar vi aldrig fick, och att
            /// någon måste ringa.</para>
            /// </summary>
            public const string MemberReplyFailed = "MedlemSvarFel";
        }

        private void LogNotify(int clubId, int? requestId, int? memberId, string email,
                               string kind, string? detail, bool succeeded)
        {
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                uow.Database.Execute(
                    @"INSERT INTO ForeningsintygNotifyLog
                          (ClubId, RequestId, MemberId, Email, Kind, Detail, Succeeded, SentAt)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7)",
                    clubId, requestId, memberId, Cut(email, 255), kind, Cut(detail, 200),
                    succeeded, DateTime.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte skriva utskicksloggen (klubb {ClubId}, {Kind}).", clubId, kind);
            }
        }

        private static string Cut(string? v, int max)
            => string.IsNullOrEmpty(v) ? "" : (v.Length <= max ? v : v[..max]);

        /// <summary>Klubbens senaste utskick, nyast först. Tom lista om tabellen saknas.</summary>
        public List<NotifyLogRow> GetNotifyLog(int clubId, int take = 25)
        {
            if (clubId <= 0) return new List<NotifyLogRow>();
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var rows = uow.Database.Fetch<NotifyLogRow>(
                    @"SELECT TOP (@1) Id, ClubId, RequestId, MemberId, Email, Kind, Detail,
                             Succeeded, SentAt
                        FROM ForeningsintygNotifyLog
                       WHERE ClubId = @0
                       ORDER BY SentAt DESC, Id DESC", clubId, take);

                // Namnet loses upp har och lagras INTE i loggen: en medlem kan byta namn, och
                // raden ska visa vem personen ar nu — adressen ar det som maste vara historisk.
                foreach (var r in rows)
                    if (r.MemberId is int mid && mid > 0) r.MemberName = ResolveMemberName(mid);

                return rows;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kunde inte läsa utskicksloggen för klubb {ClubId}.", clubId);
                return new List<NotifyLogRow>();
            }
        }

        [TableName("ForeningsintygNotifyLog")]
        public class NotifyLogRow
        {
            public int Id { get; set; }
            public int ClubId { get; set; }
            public int? RequestId { get; set; }
            public int? MemberId { get; set; }
            public string Email { get; set; } = string.Empty;
            public string Kind { get; set; } = string.Empty;
            public string? Detail { get; set; }
            public bool Succeeded { get; set; }
            public DateTime SentAt { get; set; }

            /// <summary>Visningsfält, inte en kolumn.</summary>
            [Ignore] public string? MemberName { get; set; }
        }

        /// <summary>
        /// Påminner klubbens ansvariga om att det finns obehandlade förfrågningar.
        ///
        /// <para><b>ETT mejl som räknar, inte N mejl som upprepar.</b> Stefans begäran var "funktion
        /// för att skicka mailen på nytt, som påminnelse" — och att skicka om varje ursprungsmejl
        /// hade gett fem likadana brev om fem ärenden väntar. En påminnelse som säger "3 förfrågningar
        /// väntar, den äldsta sedan 7 september" är kortare och mer användbar.</para>
        ///
        /// <para>Returnerar antalet mottagare, eller ett felmeddelande.</para>
        /// </summary>
        /// <returns>
        /// Antal lyckade utskick, MOTTAGARNA (namn + adress), och ett eventuellt felmeddelande.
        ///
        /// <para><b>⚠️ Mottagarna returneras för att ytan ska kunna NAMNGE dem.</b> "Skickad till 1
        /// person" fick oss att gräva i databasen för att ta reda på vilken brevlåda mejlet gick
        /// till — svaret var att det låg i skräpposten på en adress användaren inte läser. Står
        /// adressen på skärmen behövs ingen sådan utgrävning.</para>
        /// </returns>
        public async Task<(int Sent, List<(string Name, string Email)> Recipients, string? Error)>
            SendPendingReminderAsync(int clubId, List<ForeningsintygRequest> openRequests)
        {
            var none = new List<(string, string)>();
            if (openRequests == null || openRequests.Count == 0)
                return (0, none, "Det finns inga obehandlade förfrågningar att påminna om.");

            var club = _clubs.GetClubById(clubId);
            var clubName = club?.Name ?? "din klubb";
            var oldest = openRequests.Min(r => r.CreatedAt);
            var names = openRequests
                .Select(r => ResolveMemberName(r.MemberId))
                .Distinct()
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .ToList();

            var settings = GetNotifySettings(clubId);
            var recipients = _auth.GetViewers(clubId)
                .Where(v => !v.IsDormant)
                .Where(v => !settings.TryGetValue(v.MemberId, out var on) || on)
                .Select(v => (v.Name, Email: EmailOf(v.MemberId), v.MemberId))
                .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                .ToList();

            if (recipients.Count == 0)
            {
                // ⚠️ Två olika orsaker, och de kräver olika åtgärd av den som klickade. Att svara
                // "inga mottagare" på båda hade lämnat hen utan nästa steg.
                var anyViewer = _auth.GetViewers(clubId).Any(v => !v.IsDormant);
                return (0, none, anyViewer
                    ? "Ingen av de ansvariga har mejl påslaget, eller saknar e-postadress. " +
                      "Slå på mejl för minst en i listan ovan."
                    : "Klubben har ingen utsedd föreningsintygsansvarig att påminna.");
            }

            var sent = 0;
            var delivered = new List<(string, string)>();
            var detail = openRequests.Count == 1 ? "1 väntande" : openRequests.Count + " väntande";

            foreach (var (name, toEmail, viewerId) in recipients)
            {
                var ok = false;
                try
                {
                    // ⚠️ RÄKNA BARA LYCKADE. Ytan rapporterar det här talet till användaren, och
                    // dev sa "Påminnelse skickad till 1 person" medan loggen sa "Failed to send
                    // email" — exakt den lögn EmailService egen dokumentation varnar för.
                    ok = await _email.SendForeningsintygReminderAsync(
                        toEmail!, name, clubName, openRequests.Count, oldest, names);
                    if (ok)
                    {
                        sent++;
                        delivered.Add((name, toEmail!));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kunde inte påminna {Name} i klubb {ClubId}.", name, clubId);
                }
                // ⚠️ Loggas OAVSETT utfall — ett misslyckat utskick är det viktigaste att kunna se
                // i efterhand. RequestId är null: påminnelsen gäller flera ärenden på en gång.
                LogNotify(clubId, null, viewerId, toEmail!, NotifyKind.Reminder, detail, ok);
            }

            return sent > 0
                ? (sent, delivered, null)
                : (0, none, "Påminnelsen kunde inte skickas. Kontrollera e-postinställningarna.");
        }

        /// <summary>
        /// Klubben får veta att någon begärt ett intyg.
        ///
        /// <para><b>⚠️ Kastar aldrig vidare.</b> En trasig SMTP får inte fälla själva förfrågan —
        /// medlemmen har gjort sitt, och raden ligger redan i databasen. Felet loggas i stället, och
        /// inkorgen visar ärendet oavsett.</para>
        /// </summary>
        public async Task NotifyClubOfNewRequestAsync(ForeningsintygRequest request)
        {
            try
            {
                var club = _clubs.GetClubById(request.ClubId);
                var clubName = club?.Name ?? "din klubb";
                var memberName = ResolveMemberName(request.MemberId);
                var firearmLabel = FirearmLabel(request);

                // ⚠️ Mejlinställningen filtrerar mottagarlistan, den ERSÄTTER den inte. Grunden är
                // alltid "utsedd och inte vilande" — en avgången ledamot kan alltså inte få mejl
                // genom en kvarglömd rad i inställningstabellen.
                var settings = GetNotifySettings(request.ClubId);
                var recipients = _auth.GetViewers(request.ClubId)
                    .Where(v => !v.IsDormant)
                    .Where(v => !settings.TryGetValue(v.MemberId, out var on) || on)
                    .Select(v => (v.Name, Email: EmailOf(v.MemberId), v.MemberId))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                    .ToList();

                // ⚠️ SVARET GÅR TILL MEDLEMMEN, inte till klubben. Den ansvarige som läser "Kalle
                // har begärt ett föreningsintyg" vill svara Kalle — inte sig själv, och inte
                // klubbens egen brevlåda. Svarsadressen är motparten i samtalet.
                var replyToMember = _replyContacts.ForMember(request.MemberId);

                if (recipients.Count > 0)
                {
                    foreach (var (name, toEmail, viewerId) in recipients)
                    {
                        var ok = await _email.SendForeningsintygRequestSubmittedAsync(
                            toEmail!, name, memberName,
                            ForeningsintygRequestKind.Label(request.Kind), firearmLabel, clubName,
                            replyToMember);
                        LogNotify(request.ClubId, request.Id, viewerId, toEmail!,
                                  NotifyKind.NewRequest, ForeningsintygRequestKind.Label(request.Kind), ok);
                    }
                    return;
                }

                // Ingen MOTTAGARE. ⚠️ Då är ärendet i praktiken obevakat, och tystnad vore det
                // sämsta utfallet — mejlet går till klubbens kontaktadress så att någon över huvud
                // taget får veta att en medlem väntar.
                //
                // ⚠️ Gäller ÄVEN när klubben har utsedda personer som alla stängt av sitt mejl. Att
                // låta inställningen tysta även den här fallbacken hade gjort det möjligt för en
                // klubb att av misstag göra sig helt onåbar för förfrågningar.
                if (!string.IsNullOrWhiteSpace(club?.ContactEmail))
                {
                    var ok = await _email.SendForeningsintygRequestSubmittedAsync(
                        club!.ContactEmail, clubName, memberName,
                        ForeningsintygRequestKind.Label(request.Kind), firearmLabel, clubName,
                        replyToMember);
                    // MemberId = null: mejlet gick till klubbens adress, inte till en person.
                    LogNotify(request.ClubId, request.Id, null, club.ContactEmail,
                              NotifyKind.NewRequest, "klubbens kontaktadress (ingen mottagare)", ok);
                    return;
                }

                _logger.LogWarning(
                    "Föreningsintygsförfrågan {Id}: klubb {ClubId} har varken utsedd ansvarig eller " +
                    "kontaktadress — ingen avisering kunde skickas.", request.Id, request.ClubId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte avisera klubb {ClubId} om föreningsintygsförfrågan {Id}.",
                    request.ClubId, request.Id);
            }
        }

        /// <summary>
        /// Medlemmen får klubbens svar — utfärdat eller avslaget.
        ///
        /// <para>Samma sväljande felhantering som ovan, och av samma skäl: beslutet är redan fattat
        /// och sparat när det här körs.</para>
        /// </summary>
        /// <returns><c>true</c> bara nar mejlet faktiskt gick ut. Kvittot pa skarmen laser det
        /// har vardet — inte anvandarens kryssruta — sa det aldrig kan lova ett mejl som fastnade.</returns>
        /// <param name="actingMemberId">Den som handlägger. Standardsvarsadress — se <paramref name="replyToClub"/>.</param>
        /// <param name="replyToClub">
        /// <c>true</c> = svaret går till klubbens kontaktadress, <c>false</c> = till handläggaren.
        /// <b>⚠️ Valet görs vid VARJE utskick</b>, inte i en sparad inställning: ett formellt
        /// besked hör ofta till klubbens brevlåda medan en fråga om ett vapenärende hör till den som
        /// sitter och väntar på svaret — och det beror på ärendet, inte på personen.
        /// </param>
        public async Task<bool> NotifyMemberOfDecisionAsync(
            ForeningsintygRequest request, bool issued, string? note,
            int actingMemberId = 0, bool replyToClub = false)
        {
            try
            {
                var toEmail = EmailOf(request.MemberId);
                if (string.IsNullOrWhiteSpace(toEmail))
                {
                    _logger.LogWarning(
                        "Föreningsintygsförfrågan {Id}: medlem {MemberId} saknar e-postadress.",
                        request.Id, request.MemberId);
                    return false;
                }

                var ok = await _email.SendForeningsintygRequestDecisionAsync(
                    toEmail!, ResolveMemberName(request.MemberId),
                    _clubs.GetClubById(request.ClubId)?.Name ?? "Klubben",
                    FirearmLabel(request), issued, note,
                    _replyContacts.ForClubAdminChoice(actingMemberId, request.ClubId, replyToClub));

                // ⚠️ Mottagaren ar MEDLEMMEN som begarde intyget, inte en ansvarig.
                LogNotify(request.ClubId, request.Id, request.MemberId, toEmail!,
                          NotifyKind.Decision, issued ? "Utfardad" : "Avslagen", ok);
                return ok;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte avisera medlem {MemberId} om beslut på förfrågan {Id}.",
                    request.MemberId, request.Id);
                return false;
            }
        }

        /// <summary>
        /// Ber medlemmen komplettera sin förfrågan. Returnerar om mejlet FAKTISKT gick ut.
        ///
        /// <para><b>⚠️ Samma form som beslutsaviseringen, men en EGEN `NotifyKind`.</b> Loggen ska
        /// kunna svara på skillnaden mellan "vi avslog" och "vi bad om mer" — det är två helt olika
        /// besked till medlemmen, och den som läser loggen i efterhand kan inte gissa vilket det
        /// var ur ett gemensamt "Beslut".</para>
        /// </summary>
        /// <param name="actingMemberId">Den som begär kompletteringen. Standardsvarsadress.</param>
        /// <param name="replyToClub">Se <see cref="NotifyMemberOfDecisionAsync"/>.</param>
        public async Task<bool> NotifyMemberOfCompletionRequestAsync(
            ForeningsintygRequest request, string note,
            int actingMemberId = 0, bool replyToClub = false)
        {
            try
            {
                var toEmail = EmailOf(request.MemberId);
                if (string.IsNullOrWhiteSpace(toEmail))
                {
                    _logger.LogWarning(
                        "Föreningsintygsförfrågan {Id}: medlem {MemberId} saknar e-postadress.",
                        request.Id, request.MemberId);
                    return false;
                }

                // ⚠️ SVARSLÄNKEN ÄR VAD SOM GÖR STATUSEN SANN. Utan den kan medlemmen bara svara på
                // mejlet, och då står "Väntar på medlemmen" kvar hur snabbt hen än kompletterar.
                // Går länken inte att mynta säger mejlet det i stället för att tystna.
                var replyUrl = _replyLinks.BuildUrl(
                    MailThreadKind.Foreningsintyg, request.Id, request.ClubId, request.MemberId);

                var ok = await _email.SendForeningsintygCompletionRequestAsync(
                    toEmail!, ResolveMemberName(request.MemberId),
                    _clubs.GetClubById(request.ClubId)?.Name ?? "Klubben",
                    FirearmLabel(request), note,
                    _replyContacts.ForClubAdminChoice(actingMemberId, request.ClubId, replyToClub),
                    replyUrl);

                LogNotify(request.ClubId, request.Id, request.MemberId, toEmail!,
                          NotifyKind.Completion, note, ok);
                return ok;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte be medlem {MemberId} komplettera förfrågan {Id}.",
                    request.MemberId, request.Id);
                return false;
            }
        }

        /// <summary>
        /// Medlemmen har svarat — säg det till den som väntar.
        ///
        /// <para><b>⚠️ HANDLÄGGAREN FÖRST, de utsedda som reserv.</b> Den som klickade "Begär
        /// komplettering" är den som sitter och väntar på svaret; ett mejl till hela listan hade
        /// gjort svaret till allas och ingens. Saknas handläggaren (äldre ärende, avgången ledamot)
        /// går det till de utsedda enligt samma mejlinställning som en ny förfrågan.</para>
        ///
        /// <para><b>⚠️ Svaret går till MEDLEMMEN.</b> Den som läser "NN har svarat" vill svara NN.</para>
        ///
        /// <para>Returnerar antalet FAKTISKT skickade mejl. Noll betyder att ingen fick veta att
        /// medlemmen svarat — och då ligger svaret bara i ärendet, vilket är bättre än före men
        /// inte vad som utlovades.</para>
        /// </summary>
        public async Task<int> NotifyHandlerOfMemberReplyAsync(ForeningsintygRequest request, string replyBody)
        {
            var sent = 0;
            try
            {
                var memberName = ResolveMemberName(request.MemberId);
                var clubName = _clubs.GetClubById(request.ClubId)?.Name ?? "din klubb";
                var replyToMember = _replyContacts.ForMember(request.MemberId);

                var recipients = ResolveHandlerRecipients(request);
                if (recipients.Count == 0)
                {
                    _logger.LogWarning(
                        "Föreningsintygsförfrågan {Id}: medlemmen svarade men ingen kunde aviseras.",
                        request.Id);
                    return 0;
                }

                foreach (var (name, toEmail, memberId) in recipients)
                {
                    var ok = false;
                    try
                    {
                        ok = await _email.SendForeningsintygMemberReplyAsync(
                            toEmail, name, memberName, clubName, FirearmLabel(request),
                            replyBody, replyToMember);
                        if (ok) sent++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Kunde inte avisera {Name} om medlemssvar på förfrågan {Id}.", name, request.Id);
                    }

                    LogNotify(request.ClubId, request.Id, memberId, toEmail,
                              NotifyKind.MemberReply, Cut(replyBody, 200), ok);
                }
            }
            catch (Exception ex)
            {
                // ⚠️ Sväljer, som resten av tjänsten: svaret ÄR sparat när det här körs, och ett
                // aviseringsfel får inte rapporteras som att svaret inte togs emot.
                _logger.LogError(ex,
                    "Kunde inte avisera om medlemssvar på förfrågan {Id}.", request.Id);
            }
            return sent;
        }

        /// <summary>
        /// Medlemmen försökte svara men svaret gick inte att spara — säg det till klubben ändå.
        ///
        /// <para><b>⚠️⚠️ DET HÄR ÄR HELA POÄNGEN MED FUNKTIONEN, SPEGELVÄNT.</b> Utan den är ett
        /// sparfel exakt den tystnad arbetet skulle ta bort: medlemmen har svarat och vet att hen
        /// svarat, klubben tror att hen tiger, och ingen av dem kan se att de är oense. Hände i
        /// prod 2026-09-09 (okörd migrering).</para>
        ///
        /// <para><b>⚠️ Mejlet bär medlemmens TEXT.</b> Svaret finns ingen annanstans — databasen
        /// tog inte emot det. Utelämnas texten är det enda vi räddat att någon *försökte*, och då
        /// måste klubben ringa och be hen upprepa sig. Med texten är ärendet i praktiken besvarat.</para>
        ///
        /// <para><b>⚠️ Svarsadressen är MEDLEMMEN</b>, precis som på det lyckade svaret — den som
        /// läser vill kunna svara direkt.</para>
        /// </summary>
        public async Task<int> NotifyHandlerOfFailedReplyAsync(ForeningsintygRequest request, string replyBody)
        {
            var sent = 0;
            try
            {
                var memberName = ResolveMemberName(request.MemberId);
                var clubName = _clubs.GetClubById(request.ClubId)?.Name ?? "din klubb";
                var replyToMember = _replyContacts.ForMember(request.MemberId);

                var recipients = ResolveHandlerRecipients(request);
                if (recipients.Count == 0)
                {
                    _logger.LogError(
                        "Föreningsintygsförfrågan {Id}: medlemmens svar gick inte att spara OCH ingen "
                        + "kunde aviseras. Svaret är förlorat: {Body}",
                        request.Id, Cut(replyBody, 500));
                    return 0;
                }

                foreach (var (name, toEmail, memberId) in recipients)
                {
                    var ok = false;
                    try
                    {
                        ok = await _email.SendForeningsintygFailedReplyAsync(
                            toEmail, name, memberName, clubName, FirearmLabel(request),
                            replyBody, replyToMember);
                        if (ok) sent++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Kunde inte avisera {Name} om TAPPAT medlemssvar på förfrågan {Id}.",
                            name, request.Id);
                    }

                    LogNotify(request.ClubId, request.Id, memberId, toEmail,
                              NotifyKind.MemberReplyFailed, Cut(replyBody, 200), ok);
                }

                if (sent == 0)
                {
                    // ⚠️ Sista utposten: gick inte ens mejlet ut finns svaret BARA i loggen.
                    // Skriv ut det i klartext hellre än att låta det försvinna helt.
                    _logger.LogError(
                        "Föreningsintygsförfrågan {Id}: medlemssvar tappat och ingen avisering gick "
                        + "fram. Svaret var: {Body}", request.Id, Cut(replyBody, 500));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte hantera tappat medlemssvar på förfrågan {Id}. Svaret var: {Body}",
                    request.Id, Cut(replyBody, 500));
            }
            return sent;
        }

        /// <summary>
        /// Vem som ska veta att medlemmen svarat: handläggaren, annars de utsedda, annars klubbens
        /// kontaktadress. Samma reservtrappa som en ny förfrågan, av samma skäl — tystnad är det
        /// sämsta utfallet.
        /// </summary>
        private List<(string Name, string Email, int? MemberId)> ResolveHandlerRecipients(ForeningsintygRequest request)
        {
            var list = new List<(string, string, int?)>();

            if (request.HandledByMemberId is int handlerId && handlerId > 0)
            {
                var email = EmailOf(handlerId);
                if (!string.IsNullOrWhiteSpace(email))
                {
                    list.Add((ResolveMemberName(handlerId), email!, handlerId));
                    return list;
                }
            }

            var settings = GetNotifySettings(request.ClubId);
            foreach (var v in _auth.GetViewers(request.ClubId).Where(v => !v.IsDormant))
            {
                if (settings.TryGetValue(v.MemberId, out var on) && !on) continue;
                var email = EmailOf(v.MemberId);
                if (!string.IsNullOrWhiteSpace(email)) list.Add((v.Name, email!, v.MemberId));
            }
            if (list.Count > 0) return list;

            var club = _clubs.GetClubById(request.ClubId);
            if (!string.IsNullOrWhiteSpace(club?.ContactEmail))
                list.Add((club!.Name ?? "Klubben", club.ContactEmail, null));

            return list;
        }

        /// <summary>Aliaset, eller "vapnet" när förfrågan hämtats utan sina visningsfält.</summary>
        private static string FirearmLabel(ForeningsintygRequest r) =>
            string.IsNullOrWhiteSpace(r.FirearmAlias) ? "vapnet i förfrågan" : r.FirearmAlias!;

        private string? EmailOf(int memberId) => _memberService.GetById(memberId)?.Email;

        private string ResolveMemberName(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m is null) return $"Medlem {memberId}";
            var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return string.IsNullOrWhiteSpace(name) ? (m.Name ?? $"Medlem {memberId}") : name;
        }
    }
}
