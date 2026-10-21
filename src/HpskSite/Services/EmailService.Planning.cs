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
    }
}
