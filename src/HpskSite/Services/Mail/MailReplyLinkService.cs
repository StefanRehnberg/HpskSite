using System;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.Mail
{
    /// <summary>Vad en svarslänk pekar på, uppackat ur token.</summary>
    public sealed record MailReplyTarget(string ThreadKind, int ThreadRefId, int ClubId, int MemberId);

    /// <summary>
    /// Myntar och läser svarslänken som mejlet bär.
    ///
    /// <para><b>⚠️ TIDSBEGRÄNSAD (60 dagar).</b> En evig länk till ett ärende är en evig
    /// inloggningsfri väg in. 60 dagar är valt för att en medlem kan behöva veckor på sig att skaffa
    /// en uppgift klubben bett om — en kortare livslängd hade gjort länken till en återvändsgränd
    /// precis i det fall den finns för.</para>
    ///
    /// <para><b>⚠️ SVARSSIDAN ÄR INLOGGNINGSFRI, med flit.</b> Samma förtroendemodell som
    /// <c>/medlemsavgift/{token}</c> och av samma skäl: en äldre medlem ska kunna svara ur mejlet
    /// utan att först hitta sitt lösenord. Den som har länken kan alltså svara i medlemmens namn.
    /// Länken går bara till medlemmens egen registrerade adress. <b>Skriv aldrig en vapenuppgift
    /// eller någon annan känslig uppgift på den sidan</b> — den är en INMATNINGSyta, inte en
    /// läsyta.</para>
    ///
    /// <para><b>⚠️ Nyttolasten bär klubben.</b> Ärendets id räcker för att hitta raden, men
    /// <c>MailReply.ClubId</c> måste vara satt för att klubbens inkorg ska kunna läsa alla sina svar
    /// i EN fråga — och en sidas uppslagning får inte behöva lita på att ärendetabellen är läsbar.</para>
    /// </summary>
    public class MailReplyLinkService
    {
        /// <summary>
        /// ⚠️ Byts strängen blir varje redan utskickad länk oläsbar, utan felmeddelande.
        /// Behövs en ny form: höj versionssiffran och behåll den gamla läsvägen.
        /// </summary>
        private const string ProtectorPurpose = "MailReply.Thread.v1";

        public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

        private readonly ITimeLimitedDataProtector _protector;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MailReplyLinkService> _logger;

        public MailReplyLinkService(
            IDataProtectionProvider dataProtectionProvider,
            IConfiguration configuration,
            ILogger<MailReplyLinkService> logger)
        {
            _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose).ToTimeLimitedDataProtector();
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Den absoluta svarslänken, eller null när ärendet inte går att adressera.
        ///
        /// <para><c>siteUrl</c> läses ur konfigurationen och inte ur <c>Request</c>, eftersom mejl
        /// skickas från bakgrundstrådar som inte har någon request.</para>
        /// </summary>
        public string? BuildUrl(string threadKind, int threadRefId, int clubId, int memberId)
        {
            if (!MailThreadKind.IsValid(threadKind) || threadRefId <= 0 || memberId <= 0)
            {
                _logger.LogWarning(
                    "Svarslänk kunde inte byggas: kind={Kind} refId={RefId} memberId={MemberId}.",
                    threadKind, threadRefId, memberId);
                return null;
            }

            try
            {
                var payload = $"{threadKind}|{threadRefId}|{clubId}|{memberId}";
                var token = _protector.Protect(payload, Lifetime);
                var siteUrl = (_configuration["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');
                return $"{siteUrl}/svara/{Uri.EscapeDataString(token)}";
            }
            catch (Exception ex)
            {
                // ⚠️ Aldrig kasta vidare. Ett mejl UTAN svarsknapp är sämre än dagens läge men
                // fortfarande ett besked; ett uteblivet mejl är medlemmen som väntar i tysthet.
                _logger.LogError(ex, "Kunde inte mynta svarslänk för {Kind} {RefId}.",
                    threadKind, threadRefId);
                return null;
            }
        }

        /// <summary>
        /// Packar upp en token. Returnerar null för en ogiltig, manipulerad eller utgången länk —
        /// <b>de tre går medvetet inte att skilja åt utåt</b>, eftersom skillnaden bara är
        /// användbar för den som gissar.
        /// </summary>
        public MailReplyTarget? Parse(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            try
            {
                var parts = _protector.Unprotect(token).Split('|');
                if (parts.Length != 4)
                {
                    _logger.LogInformation("Svarslänk avvisad: nyttolasten hade {Count} delar.", parts.Length);
                    return null;
                }

                if (!MailThreadKind.IsValid(parts[0])
                    || !int.TryParse(parts[1], out var refId) || refId <= 0
                    || !int.TryParse(parts[2], out var clubId)
                    || !int.TryParse(parts[3], out var memberId) || memberId <= 0)
                {
                    _logger.LogInformation("Svarslänk avvisad: nyttolasten kunde inte tolkas.");
                    return null;
                }

                return new MailReplyTarget(parts[0], refId, clubId, memberId);
            }
            catch (Exception ex)
            {
                // ⚠️ SJÄLVA TOKEN LOGGAS ALDRIG — den är en nyckel, och en nyckel i en logg är en
                // läcka. Men UTAN någon rad alls står den som felsöker "min länk fungerar inte"
                // helt utan spår, och kan inte skilja en utgången länk från en trasig nyckelring.
                // Undantagstypen räcker för den skillnaden.
                _logger.LogInformation(
                    "Svarslänk avvisad ({Reason}). Utgången, manipulerad, eller myntad med en annan nyckelring.",
                    ex.GetType().Name);
                return null;
            }
        }
    }
}
