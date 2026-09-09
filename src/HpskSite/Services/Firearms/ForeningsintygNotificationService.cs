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
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<ForeningsintygNotificationService> _logger;

        public ForeningsintygNotificationService(
            EmailService email,
            FirearmAuthorizationService auth,
            IMemberService memberService,
            ClubService clubs,
            IScopeProvider scopeProvider,
            ILogger<ForeningsintygNotificationService> logger)
        {
            _email = email;
            _auth = auth;
            _memberService = memberService;
            _clubs = clubs;
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

                if (recipients.Count > 0)
                {
                    foreach (var (name, toEmail, viewerId) in recipients)
                    {
                        var ok = await _email.SendForeningsintygRequestSubmittedAsync(
                            toEmail!, name, memberName,
                            ForeningsintygRequestKind.Label(request.Kind), firearmLabel, clubName);
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
                        ForeningsintygRequestKind.Label(request.Kind), firearmLabel, clubName);
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
        public async Task<bool> NotifyMemberOfDecisionAsync(
            ForeningsintygRequest request, bool issued, string? note)
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
                    FirearmLabel(request), issued, note);

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
