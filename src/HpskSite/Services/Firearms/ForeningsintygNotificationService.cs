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
        public async Task<(int Sent, string? Error)> SendPendingReminderAsync(
            int clubId, List<ForeningsintygRequest> openRequests)
        {
            if (openRequests == null || openRequests.Count == 0)
                return (0, "Det finns inga obehandlade förfrågningar att påminna om.");

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
                .Select(v => (v.Name, Email: EmailOf(v.MemberId)))
                .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                .ToList();

            if (recipients.Count == 0)
            {
                // ⚠️ Två olika orsaker, och de kräver olika åtgärd av den som klickade. Att svara
                // "inga mottagare" på båda hade lämnat hen utan nästa steg.
                var anyViewer = _auth.GetViewers(clubId).Any(v => !v.IsDormant);
                return (0, anyViewer
                    ? "Ingen av de ansvariga har mejl påslaget, eller saknar e-postadress. " +
                      "Slå på mejl för minst en i listan ovan."
                    : "Klubben har ingen utsedd föreningsintygsansvarig att påminna.");
            }

            var sent = 0;
            foreach (var (name, toEmail) in recipients)
            {
                try
                {
                    // ⚠️ RAKNA BARA LYCKADE. Ytan rapporterar det har talet till anvandaren, och
                    // dev sa "Paminnelse skickad till 1 person" medan loggen sa "Failed to send
                    // email" — exakt den logn EmailService egen dokumentation varnar for.
                    if (await _email.SendForeningsintygReminderAsync(
                            toEmail!, name, clubName, openRequests.Count, oldest, names))
                        sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Kunde inte påminna {Name} i klubb {ClubId}.", name, clubId);
                }
            }

            return sent > 0
                ? (sent, null)
                : (0, "Påminnelsen kunde inte skickas. Kontrollera e-postinställningarna.");
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
                    .Select(v => (v.Name, Email: EmailOf(v.MemberId)))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Email))
                    .ToList();

                if (recipients.Count > 0)
                {
                    foreach (var (name, toEmail) in recipients)
                    {
                        await _email.SendForeningsintygRequestSubmittedAsync(
                            toEmail!, name, memberName,
                            ForeningsintygRequestKind.Label(request.Kind), firearmLabel, clubName);
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
                    await _email.SendForeningsintygRequestSubmittedAsync(
                        club!.ContactEmail, clubName, memberName,
                        ForeningsintygRequestKind.Label(request.Kind), firearmLabel, clubName);
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

                return await _email.SendForeningsintygRequestDecisionAsync(
                    toEmail!, ResolveMemberName(request.MemberId),
                    _clubs.GetClubById(request.ClubId)?.Name ?? "Klubben",
                    FirearmLabel(request), issued, note);
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
