using System.Net;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.SitePages
{
    /// <summary>Meddelar ägarens egna administratörer att ett stödläge öppnats på deras sidor.</summary>
    public interface ISiteSupportNotifier
    {
        /// <summary>Returnerar antalet mottagare som mejlet försökte skickas till. Kastar aldrig: ett misslyckat mejl får inte hindra eller dölja stödläget (det är loggat och synligt i SiteAudit ändå).</summary>
        Task<int> NotifyStartedAsync(string ownerType, int ownerId, string ownerName, string? regionCode, string supporterName, string reason, DateTime expires);

        /// <summary>Nödbromsen: meddelar ägarens egna administratörer att en sida stoppats (eller att stoppet hävts). Kastar aldrig.</summary>
        Task<int> NotifyPageSuspendedAsync(string ownerType, int ownerId, string ownerName, string? regionCode, string pageTitle, bool suspended);
    }

    /// <summary>
    /// Mejlar klubbens (<c>ClubAdmin_{id}</c>) eller kretsens (<c>RegionalAdmin_{kod}</c>) egna administratörer när någon annan öppnar ett stödläge hos dem.
    /// Svar går till sajtens adress (<see cref="MailReplyTo.SiteAdmin"/>, ett medvetet val: sajtägaren är rätt mottagare av en fråga om stödläget).
    /// Allt som skrivs in i mejlet kodas.
    /// </summary>
    public sealed class SiteSupportNotifier : ISiteSupportNotifier
    {
        private readonly IMemberService _members;
        private readonly EmailService _email;
        private readonly ILogger<SiteSupportNotifier> _logger;

        public SiteSupportNotifier(IMemberService members, EmailService email, ILogger<SiteSupportNotifier> logger)
        {
            _members = members;
            _email = email;
            _logger = logger;
        }

        public async Task<int> NotifyPageSuspendedAsync(string ownerType, int ownerId, string ownerName, string? regionCode, string pageTitle, bool suspended)
        {
            try
            {
                var role = ownerType == SiteTemplates.Club ? $"ClubAdmin_{ownerId}" : $"RegionalAdmin_{regionCode}";
                var to = _members.GetMembersInRole(role).Select(m => m.Email).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (to.Count == 0) { _logger.LogWarning("Sida {Title} hos {OwnerType} {OwnerId} {What} men ingen egen administratör att meddela ({Role}).", pageTitle, ownerType, ownerId, suspended ? "stoppad" : "frigjord", role); return 0; }
                string H(string s) => WebUtility.HtmlEncode(s);
                var subject = suspended ? $"En sida på {ownerName}s hemsida har stoppats" : $"Stoppet på en sida hos {ownerName} har hävts";
                var body = suspended
                    ? $"<p>Sidan <strong>{H(pageTitle)}</strong> har stoppats av pistol.nu och visas inte längre. Den går inte att publicera igen förrän stoppet hävts. Ert innehåll finns kvar.</p><p>Svara på det här mejlet om ni vill veta varför.</p>"
                    : $"<p>Stoppet på sidan <strong>{H(pageTitle)}</strong> har hävts. Är sidan publicerad visas den igen.</p>";
                var sent = 0;
                foreach (var addr in to)
                {
                    try { if (await _email.SendSiteSupportNoticeAsync(addr!, subject, body)) sent++; }
                    catch (Exception ex) { _logger.LogError(ex, "Nödbromsmejlet till en administratör hos {OwnerType} {OwnerId} kunde inte skickas.", ownerType, ownerId); }
                }
                return sent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nödbromsmejlet för {OwnerType} {OwnerId} kunde inte förberedas.", ownerType, ownerId);
                return 0;
            }
        }

        public async Task<int> NotifyStartedAsync(string ownerType, int ownerId, string ownerName, string? regionCode, string supporterName, string reason, DateTime expires)
        {
            try
            {
                var role = ownerType == SiteTemplates.Club ? $"ClubAdmin_{ownerId}" : $"RegionalAdmin_{regionCode}";
                var to = _members.GetMembersInRole(role).Select(m => m.Email).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (to.Count == 0) { _logger.LogWarning("Stödläge öppnat hos {OwnerType} {OwnerId} men ingen egen administratör att meddela ({Role}).", ownerType, ownerId, role); return 0; }
                string H(string s) => WebUtility.HtmlEncode(s);
                var subject = $"Stödläge öppnat på {ownerName}s hemsida";
                var body = $"<p><strong>{H(supporterName)}</strong> har öppnat ett stödläge och kan ändra era egna sidor på pistol.nu till {H(expires.ToString("HH:mm"))}.</p>" +
                           $"<p>Anledning: <em>{H(reason)}</em></p>" +
                           "<p>Allt som ändras märks som gjort i stödläge och går att se och ångra under Hemsidan. Känner ni inte igen det här, svara på det här mejlet.</p>";
                var sent = 0;
                foreach (var addr in to)
                {
                    try { if (await _email.SendSiteSupportNoticeAsync(addr!, subject, body)) sent++; }
                    catch (Exception ex) { _logger.LogError(ex, "Stödlägesmejlet till en administratör hos {OwnerType} {OwnerId} kunde inte skickas.", ownerType, ownerId); }
                }
                return sent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stödlägesmejlet för {OwnerType} {OwnerId} kunde inte förberedas.", ownerType, ownerId);
                return 0;
            }
        }
    }
}
