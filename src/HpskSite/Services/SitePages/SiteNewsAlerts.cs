using System.Net;
using HpskSite.Models;
using HpskSite.Services.Hosting;
using HpskSite.Services.Mail;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Mejl till klubbens administratörer när en läsare ber om att en bild tas bort eller anmäler en kommentar. Ingen mejlavisering vid vanliga kommentarer (Stefan 2026-11-19).
    /// <para><b>Svarsadressen är föreningens kontaktadress (annars sajtägaren), aldrig den som anmält:</b> ett svar på mejlet får inte avslöja eller nå anmälaren (<see cref="MailReplyTo"/>).
    /// Mejlet nämner inte heller anmälarens namn. <b>Claim före utskick</b> (<see cref="RateCounterService.TryClaimOnce"/>): samma sak mejlas högst en gång per dygn, så en som
    /// trycker flera gånger inte fyller postlådan. Skickas efter att databasscopet stängts och aldrig i <c>Task.Run</c>. Ett misslyckat mejl är aldrig ett fel för den som anmäler —
    /// anmälan ligger kvar i kön på nyhetens sida.</para>
    /// </summary>
    public sealed class SiteNewsAlerts
    {
        private readonly IMemberService _memberService;
        private readonly BoardWorkService _work;
        private readonly EmailService _email;
        private readonly RateCounterService _rate;
        private readonly ILogger<SiteNewsAlerts> _logger;

        public SiteNewsAlerts(IMemberService memberService, BoardWorkService work, EmailService email, RateCounterService rate, ILogger<SiteNewsAlerts> logger)
        {
            _memberService = memberService; _work = work; _email = email; _rate = rate; _logger = logger;
        }

        /// <summary>Klubbens egna administratörer med e-postadress (en gång per adress). Kretsens och sajtens administratörer mejlas inte.</summary>
        public IReadOnlyList<(string Name, string Email)> ClubAdmins(int clubId)
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _memberService.GetMembersInRole($"ClubAdmin_{clubId}"))
                if (!string.IsNullOrWhiteSpace(m.Email) && seen.Add(m.Email.Trim())) list.Add((m.Name ?? "", m.Email.Trim()));
            return list;
        }

        public Task<int> ImageRemovalRequestedAsync(string ownerType, int ownerId, string ownerName, SitePostRow post, int fileId, string? message, int requesterMemberId, string postUrl)
        {
            var key = $"newsalert-img:{ownerType}:{ownerId}:{post.Id}:{fileId}:{requesterMemberId}";
            var p = new List<string>
            {
                $"Någon som syns på en bild i nyheten <strong>{H(post.Title)}</strong> har bett om att bilden tas bort.",
                "Öppna nyheten och titta under <em>Förfrågningar om att ta bort bilder</em>. Ta bort bilden ur galleriet (Ändra) och markera sedan förfrågan som hanterad.",
            };
            if (!string.IsNullOrWhiteSpace(message)) p.Insert(1, $"Meddelande: <em>{H(message)}</em>");
            return SendAsync(ownerType, ownerId, ownerName, key, $"Någon vill att en bild tas bort: {post.Title}", p, postUrl, "Öppna nyheten");
        }

        public Task<int> CommentReportedAsync(string ownerType, int ownerId, string ownerName, SitePostRow post, int commentId, string postUrl)
        {
            var key = $"newsalert-cmt:{ownerType}:{ownerId}:{post.Id}:{commentId}";
            var p = new List<string>
            {
                $"En kommentar på nyheten <strong>{H(post.Title)}</strong> har anmälts.",
                "Klubbens styrelse ansvarar för kommentarerna. Öppna nyheten, läs kommentaren och ta bort den eller markera anmälan som hanterad.",
            };
            return SendAsync(ownerType, ownerId, ownerName, key, $"En kommentar har anmälts: {post.Title}", p, postUrl + "#kommentarer", "Öppna kommentarerna");
        }

        private async Task<int> SendAsync(string ownerType, int ownerId, string ownerName, string claimKey, string subject, List<string> paragraphs, string url, string linkLabel)
        {
            if (ownerType != SiteTemplates.Club) return 0;
            var to = ClubAdmins(ownerId);
            if (to.Count == 0 || !_rate.TryClaimOnce(claimKey, TimeSpan.FromDays(1))) return 0;
            var (orgName, orgEmail) = _work.OwnerContact(DocumentOwnerType.Club, ownerId);
            var replyTo = orgEmail != null ? MailReplyTo.FromClub(orgName, orgEmail) : MailReplyTo.SiteAdmin;
            var sent = 0;
            foreach (var (name, email) in to)
            {
                try { if (await _email.SendBoardWorkNoticeAsync(email, name, subject, paragraphs, url, linkLabel, replyTo)) sent++; }
                catch (Exception ex) { _logger.LogWarning(ex, "Nyhetsbesked till {Email} gick inte iväg.", email); }
            }
            return sent;
        }

        private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
    }
}
