using System.Net;
using System.Text;
using HpskSite.Services.Mail;

namespace HpskSite.Services
{
    /// <summary>
    /// Mejlet "Ny nyhet från {förening}" (Nyheter, Mejla till medlemmarna). Svarsadressen väljs av anroparen (<c>SiteNewsMailer</c>): ett svar ska nå föreningen,
    /// aldrig sajtägaren. From-adressen ändras aldrig (SPF/DMARC) — bara visningsnamnet och Reply-To speglar föreningen, via <see cref="MailReplyTo"/>.
    /// </summary>
    public partial class EmailService
    {
        public Task<bool> SendSiteNewsAsync(string toEmail, string toName, string ownerName, string title, string? excerpt, string url, MailReplyTo replyTo)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return Task.FromResult(false);
            var hello = string.IsNullOrWhiteSpace(toName) ? "Hej," : $"Hej {WebUtility.HtmlEncode(toName)},";
            var sb = new StringBuilder();
            sb.Append("<html><head><style>body { font-family: Arial, sans-serif; line-height: 1.6; color: #333; }</style></head><body>");
            sb.Append($"<h2>{hello}</h2>");
            sb.Append($"<p><strong>{WebUtility.HtmlEncode(ownerName)}</strong> har skrivit en ny nyhet:</p>");
            sb.Append($"<h3 style=\"margin-bottom:4px\">{WebUtility.HtmlEncode(title)}</h3>");
            if (!string.IsNullOrWhiteSpace(excerpt)) sb.Append($"<p>{WebUtility.HtmlEncode(excerpt)}</p>");
            sb.Append($"<p><a href=\"{WebUtility.HtmlEncode(url)}\" style=\"display:inline-block;background:#0d6efd;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;\">Läs nyheten</a></p>");
            sb.Append($"<p style=\"color:#666;font-size:13px\">Du får det här mejlet för att du är medlem i {WebUtility.HtmlEncode(ownerName)}. Du kan svara på mejlet; svaret går till föreningen.</p>");
            sb.Append("<p>Med vänliga hälsningar,<br/>Pistol.nu</p></body></html>");
            return SendEmailAsync(toEmail, $"Nyhet från {ownerName}: {title}", sb.ToString(), replyTo);
        }
    }
}
