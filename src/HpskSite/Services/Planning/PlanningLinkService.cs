using System;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.Planning
{
    /// <summary>Vad en planeringslänk pekar på: en deltagare i en planering.</summary>
    public sealed record PlanningLinkTarget(int ParticipantId, int PlanningId);

    /// <summary>
    /// Signerade länkar i planeringens mejl. Stefan 2026-10-21: man ska kunna <b>svara och rösta
    /// direkt ur mejlet utan att logga in</b>, och en gäst (någon som inte är skytt) har inget konto
    /// alls. Länken ÄR alltså deltagarens nyckel — samma förtroendemodell som "Jag har betalat" och
    /// <c>/svara/{token}</c>: den går bara till deltagarens egen adress.
    ///
    /// <para><b>⚠️ Nyckeln är DELTAGAREN, inte medlemmen.</b> Tas deltagaren bort ur planeringen
    /// slutar länken att fungera, eftersom varje anrop prövar att deltagarraden fortfarande är aktiv.</para>
    ///
    /// <para><b>⚠️ Två sorters länkar.</b> Mejllänken lever 60 dagar (varje nytt mejl myntar en ny).
    /// Kalenderns prenumerationslänk är evig — en kalender som slutar uppdateras efter två månader
    /// vore värre än ingen — men prövar deltagandet vid varje hämtning.</para>
    ///
    /// <para><b>⚠️ Byts en purpose-sträng blir varje utskickad länk oläsbar, tyst.</b> Höj versionen
    /// i stället för att ändra.</para>
    /// </summary>
    public class PlanningLinkService
    {
        private const string MailPurpose = "Planning.Mail.v1";
        private const string IcsPurpose = "Planning.Ics.v1";
        /// <summary>Svar på en e-postinbjudan. Ger INGEN tillgång — bara rätt att svara ja eller nej (Stefan 2026-10-23).</summary>
        private const string InvitePurpose = "Planning.Invite.v1";
        public static readonly TimeSpan MailLifetime = TimeSpan.FromDays(60);

        private readonly ITimeLimitedDataProtector _mail;
        private readonly IDataProtector _ics;
        private readonly ITimeLimitedDataProtector _invite;
        private readonly IConfiguration _config;
        private readonly ILogger<PlanningLinkService> _logger;

        public PlanningLinkService(IDataProtectionProvider dp, IConfiguration config, ILogger<PlanningLinkService> logger)
        {
            _mail = dp.CreateProtector(MailPurpose).ToTimeLimitedDataProtector();
            _ics = dp.CreateProtector(IcsPurpose);
            _invite = dp.CreateProtector(InvitePurpose).ToTimeLimitedDataProtector();
            _config = config;
            _logger = logger;
        }

        /// <summary>Sajtens adress för länkar i mejl (bakgrundstrådar har ingen request).</summary>
        public string SiteUrl => (_config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');

        public string MailToken(int participantId, int planningId) =>
            _mail.Protect($"p|{participantId}|{planningId}", MailLifetime);

        public string IcsToken(int participantId, int planningId) =>
            _ics.Protect($"ics|{participantId}|{planningId}");

        /// <summary>Länk till svarssidan. <paramref name="action"/>: svara, rosta, oppna.</summary>
        public string MailUrl(int participantId, int planningId, string action, int? refId = null) =>
            $"{SiteUrl}/tavlingsplanering/svar?t={Uri.EscapeDataString(MailToken(participantId, planningId))}&a={action}"
            + (refId > 0 ? $"&m={refId}" : "");

        /// <summary>Kalenderlänken. <paramref name="baseUrl"/> = sidans egen adress (Request.PublicBase()); utan den sajtens (mejl).</summary>
        public string IcsUrl(int participantId, int planningId, string? baseUrl = null) =>
            $"{(string.IsNullOrWhiteSpace(baseUrl) ? SiteUrl : baseUrl.TrimEnd('/'))}/tavlingsplanering/kalender.ics?t={Uri.EscapeDataString(IcsToken(participantId, planningId))}";

        /// <summary>Länken i en e-postinbjudan: sidan där den inbjudna svarar ja eller nej.</summary>
        public string InviteUrl(int requestId, int planningId, string? baseUrl = null) =>
            $"{(string.IsNullOrWhiteSpace(baseUrl) ? SiteUrl : baseUrl.TrimEnd('/'))}/tavlingsplanering/inbjudan?t="
            + Uri.EscapeDataString(_invite.Protect($"inv|{requestId}|{planningId}", MailLifetime));

        /// <summary>Tolkar inbjudningslänken. <see cref="PlanningLinkTarget.ParticipantId"/> bär FÖRFRÅGANS id.</summary>
        public PlanningLinkTarget? ParseInvite(string? token) => Parse(token, "inv", t => _invite.Unprotect(t));

        public PlanningLinkTarget? ParseMail(string? token) => Parse(token, "p", t => _mail.Unprotect(t));
        public PlanningLinkTarget? ParseIcs(string? token) => Parse(token, "ics", t => _ics.Unprotect(t));

        private PlanningLinkTarget? Parse(string? token, string kind, Func<string, string> unprotect)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            try
            {
                var parts = unprotect(token).Split('|');
                if (parts.Length != 3 || parts[0] != kind
                    || !int.TryParse(parts[1], out var pid) || pid <= 0
                    || !int.TryParse(parts[2], out var plid) || plid <= 0)
                    return null;
                return new PlanningLinkTarget(pid, plid);
            }
            catch (Exception ex)
            {
                // Själva token loggas aldrig — den är en nyckel.
                _logger.LogInformation("Planeringslänk avvisad ({Reason}).", ex.GetType().Name);
                return null;
            }
        }
    }
}
