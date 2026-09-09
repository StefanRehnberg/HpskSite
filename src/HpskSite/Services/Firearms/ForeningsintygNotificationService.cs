using Umbraco.Cms.Core.Services;

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
        private readonly ILogger<ForeningsintygNotificationService> _logger;

        public ForeningsintygNotificationService(
            EmailService email,
            FirearmAuthorizationService auth,
            IMemberService memberService,
            ClubService clubs,
            ILogger<ForeningsintygNotificationService> logger)
        {
            _email = email;
            _auth = auth;
            _memberService = memberService;
            _clubs = clubs;
            _logger = logger;
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

                var recipients = _auth.GetViewers(request.ClubId)
                    .Where(v => !v.IsDormant)
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

                // Ingen utsedd ansvarig. ⚠️ Då är ärendet OHANTERBART tills klubben utser någon, och
                // tystnad vore det sämsta utfallet — mejlet går till klubbens kontaktadress så att
                // någon över huvud taget får veta att en medlem väntar.
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
        public async Task NotifyMemberOfDecisionAsync(
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
                    return;
                }

                await _email.SendForeningsintygRequestDecisionAsync(
                    toEmail!, ResolveMemberName(request.MemberId),
                    _clubs.GetClubById(request.ClubId)?.Name ?? "Klubben",
                    FirearmLabel(request), issued, note);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte avisera medlem {MemberId} om beslut på förfrågan {Id}.",
                    request.MemberId, request.Id);
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
