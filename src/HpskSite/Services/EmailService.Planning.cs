using System.Threading.Tasks;
using HpskSite.Services.Mail;

namespace HpskSite.Services
{
    public partial class EmailService
    {
        /// <summary>
        /// Tävlingsplaneringens mejl. Kroppen byggs av <c>PlanningNotifier</c> (allt användarinnehåll
        /// är HTML-kodat där). Går genom samma kuvert och fotnot som allt annat, och returnerar om
        /// mejlet FAKTISKT gick iväg — notisloggens <c>Succeeded</c> bygger på det.
        /// </summary>
        public Task<bool> SendPlanningMailAsync(string toEmail, string subject, string htmlBody, MailReplyTo replyTo)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return Task.FromResult(false);
            return SendEmailAsync(toEmail, subject, htmlBody, replyTo);
        }

        /// <summary>
        /// Planeringens mejl till förbundet med bifogade dokument (P3). ⚠️ Går genom samma kuvert som allt annat
        /// (<see cref="ApplyEnvelope"/>, <see cref="AppendReplyFooter"/>) — svarsadressen är den som skickar.
        /// Returnerar om mejlet faktiskt gick iväg.
        /// </summary>
        public async Task<bool> SendPlanningMailWithAttachmentsAsync(string toEmail, string subject, string htmlBody, MailReplyTo replyTo,
            IReadOnlyList<(string FileName, System.IO.Stream Content, string ContentType)> attachments)
        {
            if (string.IsNullOrWhiteSpace(toEmail)) return false;
            if (string.IsNullOrEmpty(_smtpHost))
            {
                _logger.LogWarning("SMTP host not configured. Email not sent to {Email} with subject: {Subject}", toEmail, subject);
                return false;
            }
            try
            {
                using var message = new System.Net.Mail.MailMessage();
                ApplyEnvelope(message, replyTo);
                message.To.Add(toEmail);
                message.Subject = subject;
                message.Body = AppendReplyFooter(htmlBody, replyTo);
                message.IsBodyHtml = true;
                foreach (var a in attachments)
                    message.Attachments.Add(new System.Net.Mail.Attachment(a.Content, a.FileName, a.ContentType));
                using var smtpClient = new System.Net.Mail.SmtpClient(_smtpHost, _smtpPort)
                {
                    EnableSsl = _useSsl,
                    UseDefaultCredentials = false,
                    Credentials = new System.Net.NetworkCredential(_username, _password),
                };
                await smtpClient.SendMailAsync(message);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send planning mail with attachments to {Email}", toEmail);
                return false;
            }
        }
    }
}
