using HpskSite.Services.Mail;

namespace HpskSite.Services
{
    /// <summary>
    /// Mejl för klubbsidornas stödläge (klubbsidorna 2b). <b>Svarsadressen är sajtens egen</b> (<see cref="MailReplyTo.SiteAdmin"/>, ett medvetet val):
    /// ett svar på "någon har öppnat ett stödläge hos er" hör hemma hos den som driver sajten. Texten byggs och kodas av anroparen
    /// (<c>SiteSupportNotifier</c>); den här metoden öppnar bara den privata sändvägen och väljer Reply-To.
    /// </summary>
    public partial class EmailService
    {
        public Task<bool> SendSiteSupportNoticeAsync(string toEmail, string subject, string htmlBody) =>
            SendEmailAsync(toEmail, subject, htmlBody, MailReplyTo.SiteAdmin);
    }
}
