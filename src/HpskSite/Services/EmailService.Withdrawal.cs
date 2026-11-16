using System.Globalization;
using System.Net;
using System.Text;
using HpskSite.Services.Mail;

namespace HpskSite.Services
{
    /// <summary>
    /// Mejlet till arrangören när en skytt avanmält sig från en tävling där avgiften redan var betald
    /// (eller angiven som betald): <b>skytten ska få pengarna tillbaka</b>. Svarsadressen är SKYTTEN —
    /// arrangören behöver fråga efter kontonummer eller Swish-nummer, och ett svar ska nå skytten, inte
    /// sajtägaren (se "Varje mejl måste välja Reply-To"). From-adressen ändras aldrig.
    /// </summary>
    public partial class EmailService
    {
        public Task<bool> SendCompetitionWithdrawalAsync(string toEmail, string toName, string competitionName,
            string shooterName, string? clubName, string? classes, decimal paidAmount, decimal claimedAmount,
            bool ledgerModel, string url, MailReplyTo replyTo)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return Task.FromResult(false);
            static string Kr(decimal v) => v.ToString("0.##", CultureInfo.GetCultureInfo("sv-SE")) + " kr";
            string E(string? s) => WebUtility.HtmlEncode(s ?? "");

            var hello = string.IsNullOrWhiteSpace(toName) ? "Hej," : $"Hej {E(toName)},";
            var sb = new StringBuilder();
            sb.Append("<html><head><style>body { font-family: Arial, sans-serif; line-height: 1.6; color: #333; }</style></head><body>");
            sb.Append($"<h2>{hello}</h2>");
            sb.Append($"<p><strong>{E(shooterName)}</strong>{(string.IsNullOrWhiteSpace(clubName) ? "" : $" ({E(clubName)})")} har avanmält sig från <strong>{E(competitionName)}</strong>");
            if (!string.IsNullOrWhiteSpace(classes)) sb.Append($" (klass: {E(classes)})");
            sb.Append(".</p>");

            if (paidAmount > 0)
                sb.Append($"<p><strong>{Kr(paidAmount)} är redan betalt och ska återbetalas till skytten.</strong></p>");
            if (claimedAmount > 0)
                sb.Append($"<p>Skytten har dessutom angett att hen betalat {Kr(claimedAmount)} (betalningen är inte bekräftad hos er). " +
                          "Kontrollera i er Swish eller bank om pengarna kommit — i så fall ska även de återbetalas.</p>");

            sb.Append("<p>Svara på det här mejlet för att nå skytten (svaret går direkt till hen) och fråga efter Swish-nummer eller kontonummer.</p>");
            sb.Append(ledgerModel
                ? "<p style=\"font-size:14px\">När ni har återbetalat: hitta raden med skyttens namn markerad <em>(borttagen)</em> i avgiftslistan under Anmälningar och välj <strong>Ångra betalning</strong> så blir bokföringen rätt. Markera sedan återbetalningen som gjord i rutan <em>Ska få pengarna tillbaka</em>.</p>"
                : "<p style=\"font-size:14px\">Den betalda fakturan ligger kvar som underlag för bokföringen. När ni har återbetalat: markera det under Anmälningar → <em>Ska få pengarna tillbaka</em>.</p>");

            sb.Append($"<p><a href=\"{E(url)}\" style=\"display:inline-block;background:#0d6efd;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none;\">Öppna Anmälningar</a></p>");
            sb.Append("<p>Med vänliga hälsningar,<br/>Pistol.nu</p></body></html>");
            return SendEmailAsync(toEmail, $"Avanmälan med betald avgift: {shooterName} – {competitionName}", sb.ToString(), replyTo);
        }
    }
}
