using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for sending email notifications
    /// </summary>
    public class EmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;
        private readonly string _smtpHost;
        private readonly int _smtpPort;
        private readonly bool _useSsl;
        private readonly string _username;
        private readonly string _password;
        private readonly string _fromAddress;
        private readonly string _fromName;
        private readonly string _adminEmail;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            // Load SMTP settings from appsettings.json
            _smtpHost = _configuration["Email:SmtpHost"] ?? "";
            _smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
            _useSsl = bool.Parse(_configuration["Email:UseSsl"] ?? "true");
            _username = _configuration["Email:Username"] ?? "";
            _password = _configuration["Email:Password"] ?? "";
            _fromAddress = _configuration["Email:FromAddress"] ?? "noreply@pistol.nu";
            _fromName = _configuration["Email:FromName"] ?? "Pistol.nu";
            _adminEmail = _configuration["Email:AdminEmail"] ?? "";
        }

        /// <summary>
        /// Send email notification when a new user registers
        /// </summary>
        public async Task SendRegistrationNotificationToAdminAsync(string memberName, string memberEmail, string clubName)
        {
            if (string.IsNullOrEmpty(_adminEmail))
            {
                _logger.LogWarning("Admin email not configured. Cannot send registration notification.");
                return;
            }

            var subject = $"Ny medlemsregistrering: {memberName}";
            var body = $@"
<html>
<body>
    <h2>Ny medlemsregistrering</h2>
    <p>En ny medlem har registrerat sig och väntar på godkännande.</p>
    <p><strong>Namn:</strong> {memberName}<br/>
    <strong>E-post:</strong> {memberEmail}<br/>
    <strong>Klubb:</strong> {clubName ?? "Ingen klubb vald"}</p>
    <p>Logga in på administrationspanelen för att godkänna eller avslå ansökan.</p>
</body>
</html>";

            await SendEmailAsync(_adminEmail, subject, body);
        }

        /// <summary>
        /// Send email confirmation to user after registration
        /// </summary>
        public async Task SendRegistrationConfirmationToUserAsync(string memberEmail, string memberName, string clubName)
        {
            var subject = "Välkommen till Pistol.nu - Registrering mottagen";
            var body = $@"
<html>
<body>
    <h2>Välkommen till Pistol.nu, {memberName}!</h2>
    <p>Tack för din registrering. Din ansökan har tagits emot och väntar nu på godkännande.</p>
    <p><strong>Nästa steg:</strong></p>
    <ul>
        <li>En administratör för {clubName ?? "din klubb"} kommer att granska din ansökan</li>
        <li>Du får ett nytt e-postmeddelande när ditt konto har godkänts</li>
        <li>Efter godkännande kan du logga in och använda alla funktioner på sidan</li>
    </ul>
    <p>Om du har frågor, kontakta din klubbadministratör eller webbansvarig.</p>
    <p>Med vänliga hälsningar,<br/>Pistol.nu</p>
</body>
</html>";

            await SendEmailAsync(memberEmail, subject, body);
        }

        /// <summary>
        /// Send email when a member's registration is approved
        /// Includes auto-login token for one-click login
        /// </summary>
        public async Task SendApprovalNotificationAsync(string memberEmail, string memberName, string autoLoginToken)
        {
            var subject = "Ditt Pistol.nu-konto har godkänts!";

            // Build auto-login URL with token and URL-encoded email
            var siteUrl = _configuration["SiteUrl"] ?? "https://pistol.nu";
            var encodedEmail = Uri.EscapeDataString(memberEmail);
            var autoLoginUrl = $"{siteUrl}/umbraco/surface/Member/AutoLogin?token={autoLoginToken}&email={encodedEmail}";

            var body = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .button {{
            display: inline-block;
            background-color: #007bff;
            color: white !important;
            padding: 14px 30px;
            text-decoration: none;
            border-radius: 5px;
            font-weight: bold;
            font-size: 16px;
        }}
        .button:hover {{ background-color: #0056b3; }}
        .notice {{ color: #666; font-size: 13px; margin-top: 10px; }}
    </style>
</head>
<body>
    <h2>Grattis {memberName}!</h2>
    <p>Ditt medlemskap på Pistol.nu har godkänts av en klubbadministratör!</p>

    <p><strong>Klicka på knappen nedan för att logga in direkt:</strong></p>
    <p style=""text-align: center; margin: 30px 0;"">
        <a href=""{autoLoginUrl}"" class=""button"">Logga in nu</a>
    </p>
    <p class=""notice"">
        <em>Länken är giltig i 7 dagar och kan endast användas en gång.
        Du förblir inloggad i 90 dagar så du slipper logga in igen.</em>
    </p>

    <p>Nu kan du:</p>
    <ul>
        <li>Anmäla dig till tävlingar</li>
        <li>Registrera dina träningsresultat</li>
        <li>Delta i träningsprogrammet Skyttetrappan</li>
        <li>Se och delta i klubbaktiviteter</li>
    </ul>

    <p>Med vänliga hälsningar,<br/>Pistol.nu</p>
</body>
</html>";

            await SendEmailAsync(memberEmail, subject, body);
        }

        /// <summary>
        /// Send email when a member's registration is rejected
        /// </summary>
        public async Task SendRejectionNotificationAsync(string memberEmail, string memberName, string? reason = null)
        {
            var subject = "Angående din Pistol.nu-registrering";
            var body = $@"
<html>
<body>
    <h2>Hej {memberName},</h2>
    <p>Tyvärr kunde vi inte godkänna din registrering på Pistol.nu i nuläget.</p>
    {(string.IsNullOrEmpty(reason) ? "" : $"<p><strong>Anledning:</strong> {reason}</p>")}
    <p>Om du har frågor om detta, kontakta din klubbadministratör eller webbansvarig.</p>
    <p>Med vänliga hälsningar,<br/>Pistol.nu</p>
</body>
</html>";

            await SendEmailAsync(memberEmail, subject, body);
        }

        /// <summary>
        /// Send invitation email to member to set their password
        /// Includes invitation token for password setup
        /// </summary>
        public async Task SendMemberInvitationAsync(string memberEmail, string memberName, string invitationToken, string clubName = "din klubb")
        {
            var subject = "Du har blivit inbjuden till Pistol.nu!";

            // Build invitation URL with token and URL-encoded email
            var siteUrl = _configuration["SiteUrl"] ?? "https://pistol.nu";
            var encodedEmail = Uri.EscapeDataString(memberEmail);
            var invitationUrl = $"{siteUrl}/umbraco/surface/Member/AcceptInvitation?token={invitationToken}&email={encodedEmail}";

            var body = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .button {{
            display: inline-block;
            background-color: #28a745;
            color: white !important;
            padding: 14px 30px;
            text-decoration: none;
            border-radius: 5px;
            font-weight: bold;
            font-size: 16px;
        }}
        .button:hover {{ background-color: #218838; }}
        .notice {{ color: #666; font-size: 13px; margin-top: 10px; }}
    </style>
</head>
<body>
    <h2>Välkommen {memberName}!</h2>
    <p>Du har blivit inbjuden att bli medlem på sidan för {clubName} på sajten pistol.nu.</p>

    <p><strong>För att aktivera ditt konto behöver du sätta ett lösenord:</strong></p>
    <p style=""text-align: center; margin: 30px 0;"">
        <a href=""{invitationUrl}"" class=""button"">Sätt ditt lösenord</a>
    </p>
    <p class=""notice"">
        <em>Länken är giltig i 7 dagar och kan endast användas en gång.</em>
    </p>

    <p>När du har satt ditt lösenord kan du:</p>
    <ul>
        <li>Anmäla dig till tävlingar</li>
        <li>Registrera dina träningsresultat</li>
        <li>Delta i träningsprogrammet Skyttetrappan</li>
        <li>Se och delta i klubbaktiviteter</li>
    </ul>

    <p>Med vänliga hälsningar,<br/>Pistol.nu</p>
</body>
</html>";

            await SendEmailAsync(memberEmail, subject, body);
        }

        /// <summary>
        /// Send confirmation email to admin when an invitation is sent to a member
        /// Notifies both the sending admin and the general admin email
        /// </summary>
        public async Task SendInvitationConfirmationAsync(
            string recipientEmail,
            string senderName,
            string memberName,
            string memberEmail,
            string clubName,
            bool wasSuccessful,
            string? failureReason = null)
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            string subject;
            string statusColor;
            string statusIcon;
            string statusText;
            string additionalInfo;

            if (wasSuccessful)
            {
                subject = $"Inbjudan skickad till {memberName}";
                statusColor = "#28a745";
                statusIcon = "&#10003;";
                statusText = "Inbjudan skickad";
                additionalInfo = @"
                    <div style='background-color: #d4edda; border-left: 4px solid #28a745; padding: 15px; margin: 20px 0;'>
                        <p style='margin: 0; color: #155724;'><strong>Inbjudan skickad!</strong></p>
                        <p style='margin: 10px 0 0 0; color: #155724;'>
                            Medlemmen har f&aring;tt ett e-postmeddelande med en l&auml;nk f&ouml;r att s&auml;tta sitt l&ouml;senord.
                            L&auml;nken &auml;r giltig i 7 dagar.
                        </p>
                    </div>";
            }
            else
            {
                subject = $"Inbjudan till {memberName} misslyckades";
                statusColor = "#dc3545";
                statusIcon = "&#10007;";
                statusText = "Inbjudan misslyckades";
                var escapedReason = System.Net.WebUtility.HtmlEncode(failureReason ?? "Ok&auml;nt fel");
                additionalInfo = $@"
                    <div style='background-color: #f8d7da; border-left: 4px solid #dc3545; padding: 15px; margin: 20px 0;'>
                        <p style='margin: 0; color: #721c24;'><strong>Inbjudan kunde inte skickas</strong></p>
                        <p style='margin: 10px 0 0 0; color: #721c24;'>
                            <strong>Orsak:</strong> {escapedReason}
                        </p>
                        <p style='margin: 10px 0 0 0; color: #721c24;'>
                            F&ouml;rs&ouml;k igen eller kontakta webbansvarig om problemet kvarst&aring;r.
                        </p>
                    </div>";
            }

            var body = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: {statusColor}; color: white; padding: 20px; text-align: center; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #f8f9fa; padding: 30px; border: 1px solid #dee2e6; }}
        .info-box {{ background-color: white; padding: 20px; border-left: 4px solid #0d6efd; margin: 20px 0; }}
        .info-item {{ margin: 10px 0; }}
        .info-label {{ font-weight: bold; color: #495057; }}
        .info-value {{ color: #212529; }}
        .footer {{ margin-top: 20px; padding: 15px; background-color: #e9ecef; border-radius: 0 0 5px 5px; font-size: 12px; color: #6c757d; text-align: center; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h2>{statusIcon} {statusText}</h2>
        </div>
        <div class='content'>
            <p>Hej,</p>

            <p>Detta &auml;r en bekr&auml;ftelse p&aring; att en inbjudan har {(wasSuccessful ? "skickats" : "f&ouml;rs&ouml;kt skickas")} till en medlem.</p>

            <div class='info-box'>
                <h4 style='margin-top: 0; color: #0d6efd;'>Medlemsinformation</h4>
                <div class='info-item'>
                    <span class='info-label'>Namn:</span>
                    <span class='info-value'>{System.Net.WebUtility.HtmlEncode(memberName)}</span>
                </div>
                <div class='info-item'>
                    <span class='info-label'>E-post:</span>
                    <span class='info-value'>{System.Net.WebUtility.HtmlEncode(memberEmail)}</span>
                </div>
                <div class='info-item'>
                    <span class='info-label'>Klubb:</span>
                    <span class='info-value'>{System.Net.WebUtility.HtmlEncode(clubName)}</span>
                </div>
                <div class='info-item'>
                    <span class='info-label'>Skickad av:</span>
                    <span class='info-value'>{System.Net.WebUtility.HtmlEncode(senderName)}</span>
                </div>
                <div class='info-item'>
                    <span class='info-label'>Tidpunkt:</span>
                    <span class='info-value'>{timestamp}</span>
                </div>
            </div>

            {additionalInfo}

            <p>Med v&auml;nliga h&auml;lsningar,<br/>Pistol.nu</p>
        </div>
        <div class='footer'>
            <p>Detta &auml;r ett automatiskt meddelande fr&aring;n Pistol.nu.</p>
        </div>
    </div>
</body>
</html>";

            await SendEmailAsync(recipientEmail, subject, body);
        }

        /// <summary>
        /// Send email notification about missing club request
        /// </summary>
        public async Task SendMissingClubRequestAsync(string clubName, string clubLocation, string contactPerson,
            string requestorEmail, string additionalNotes)
        {
            if (string.IsNullOrEmpty(_adminEmail))
            {
                _logger.LogWarning("Admin email not configured. Cannot send missing club request.");
                return;
            }

            var subject = $"Förfrågan om att lägga till klubb: {clubName}";
            var body = $@"
<html>
<body>
    <h2>Förfrågan om saknad klubb</h2>
    <p>En användare har begärt att följande klubb ska läggas till i systemet:</p>
    <p><strong>Klubbnamn:</strong> {clubName}<br/>
    <strong>Ort/Plats:</strong> {clubLocation ?? "Ej angiven"}<br/>
    <strong>Kontaktperson:</strong> {contactPerson ?? "Ej angiven"}<br/>
    <strong>Användare som begärde:</strong> {requestorEmail}</p>
    {(string.IsNullOrEmpty(additionalNotes) ? "" : $"<p><strong>Ytterligare information:</strong><br/>{additionalNotes}</p>")}
    <p><strong>OBS:</strong> Kontakta klubbens kontaktperson enligt Svenska Pistolskytteförbundets register innan klubben läggs till.</p>
</body>
</html>";

            await SendEmailAsync(_adminEmail, subject, body);
        }

        /// <summary>
        /// Core method to send an email
        /// </summary>
        private async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
        {
            // If SMTP is not configured, log and return
            if (string.IsNullOrEmpty(_smtpHost))
            {
                _logger.LogWarning("SMTP host not configured. Email not sent to {Email} with subject: {Subject}", toEmail, subject);
                return;
            }

            try
            {
                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(_fromAddress, _fromName);
                    message.To.Add(toEmail);
                    message.Subject = subject;
                    message.Body = htmlBody;
                    message.IsBodyHtml = true;

                    using (var smtpClient = new SmtpClient(_smtpHost, _smtpPort))
                    {
                        smtpClient.EnableSsl = _useSsl;
                        smtpClient.UseDefaultCredentials = false;
                        smtpClient.Credentials = new NetworkCredential(_username, _password);

                        await smtpClient.SendMailAsync(message);
                        _logger.LogInformation("Email sent successfully to {Email} with subject: {Subject}", toEmail, subject);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email to {Email} with subject: {Subject}", toEmail, subject);
                // Don't throw - we don't want email failures to break registration/approval flows
            }
        }

        /// <summary>
        /// Send bug report email to admin
        /// </summary>
        public async Task SendBugReportAsync(
            string reporterName,
            string reporterEmail,
            string description,
            List<Microsoft.AspNetCore.Http.IFormFile>? images,
            string? memberName,
            string? memberEmail,
            string pageUrl)
        {
            if (string.IsNullOrEmpty(_adminEmail))
            {
                _logger.LogWarning("Admin email not configured. Cannot send bug report.");
                return;
            }

            var subject = $"[Felrapport] Bug Report from {reporterName}";

            var userType = !string.IsNullOrEmpty(memberName) ? $"Inloggad medlem: {memberName} ({memberEmail})" : "Gäst";
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var body = $@"
                <html>
                <head>
                    <style>
                        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
                        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
                        .header {{ background-color: #dc3545; color: white; padding: 15px; border-radius: 5px 5px 0 0; }}
                        .content {{ background-color: #f8f9fa; padding: 20px; border: 1px solid #dee2e6; }}
                        .field {{ margin-bottom: 15px; }}
                        .field-label {{ font-weight: bold; color: #495057; }}
                        .field-value {{ margin-top: 5px; padding: 10px; background-color: white; border-left: 3px solid #dc3545; }}
                        .description {{ white-space: pre-wrap; word-wrap: break-word; }}
                        .footer {{ margin-top: 20px; padding: 15px; background-color: #e9ecef; border-radius: 0 0 5px 5px; font-size: 12px; color: #6c757d; }}
                        .image-section {{ margin-top: 20px; }}
                        img {{ max-width: 100%; height: auto; margin: 10px 0; border: 1px solid #dee2e6; }}
                    </style>
                </head>
                <body>
                    <div class='container'>
                        <div class='header'>
                            <h2>🐛 Felrapport från Pistol.nu</h2>
                        </div>
                        <div class='content'>
                            <div class='field'>
                                <div class='field-label'>Rapportör:</div>
                                <div class='field-value'>{reporterName} ({reporterEmail})</div>
                            </div>
                            <div class='field'>
                                <div class='field-label'>Användare:</div>
                                <div class='field-value'>{userType}</div>
                            </div>
                            <div class='field'>
                                <div class='field-label'>Datum/Tid:</div>
                                <div class='field-value'>{timestamp}</div>
                            </div>
                            <div class='field'>
                                <div class='field-label'>Sida:</div>
                                <div class='field-value'>{pageUrl}</div>
                            </div>
                            <div class='field'>
                                <div class='field-label'>Beskrivning:</div>
                                <div class='field-value description'>{System.Net.WebUtility.HtmlEncode(description)}</div>
                            </div>";

            // Add image attachments section if images are provided
            if (images != null && images.Count > 0)
            {
                body += @"
                            <div class='image-section'>
                                <div class='field-label'>Bifogade bilder:</div>";

                foreach (var image in images)
                {
                    try
                    {
                        using (var memoryStream = new System.IO.MemoryStream())
                        {
                            await image.CopyToAsync(memoryStream);
                            var imageBytes = memoryStream.ToArray();
                            var base64Image = Convert.ToBase64String(imageBytes);
                            var mimeType = image.ContentType;

                            body += $@"
                                <div style='margin: 10px 0;'>
                                    <p><strong>{System.Net.WebUtility.HtmlEncode(image.FileName)}</strong> ({image.Length / 1024} KB)</p>
                                    <img src='data:{mimeType};base64,{base64Image}' alt='{System.Net.WebUtility.HtmlEncode(image.FileName)}' />
                                </div>";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to embed image {FileName} in bug report", image.FileName);
                        body += $"<p><em>Kunde inte bifoga bild: {System.Net.WebUtility.HtmlEncode(image.FileName)}</em></p>";
                    }
                }

                body += "</div>";
            }

            body += @"
                        </div>
                        <div class='footer'>
                            <p>Detta är en automatisk felrapport från Pistol.nu.</p>
                            <p>Svara på detta mail för att kontakta rapportören direkt.</p>
                        </div>
                    </div>
                </body>
                </html>";

            await SendEmailAsync(_adminEmail, subject, body);
        }

        /// <summary>
        /// Send password reset email with secure token link
        /// </summary>
        public async Task SendPasswordResetEmailAsync(string memberEmail, string memberName, string resetToken)
        {
            var siteUrl = _configuration["Email:SiteUrl"] ?? "https://pistol.nu";

            // URL encode the token and email
            var encodedToken = System.Web.HttpUtility.UrlEncode(resetToken);
            var encodedEmail = System.Web.HttpUtility.UrlEncode(memberEmail);

            // Build reset link
            var resetLink = $"{siteUrl}/password-reset?token={encodedToken}&email={encodedEmail}";

            var subject = "Återställ ditt lösenord - Pistol.nu";
            var body = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #0d6efd; color: white; padding: 20px; text-align: center; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #f8f9fa; padding: 30px; border: 1px solid #dee2e6; }}
        .button {{ display: inline-block; padding: 12px 30px; background-color: #0d6efd; color: white; text-decoration: none; border-radius: 5px; margin: 20px 0; }}
        .button:hover {{ background-color: #0b5ed7; }}
        .warning {{ background-color: #fff3cd; border-left: 4px solid #ffc107; padding: 15px; margin: 20px 0; }}
        .footer {{ margin-top: 20px; padding: 15px; background-color: #e9ecef; border-radius: 0 0 5px 5px; font-size: 12px; color: #6c757d; text-align: center; }}
        .code {{ background-color: #e9ecef; padding: 2px 6px; border-radius: 3px; font-family: monospace; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h2>🔐 Återställ ditt lösenord</h2>
        </div>
        <div class='content'>
            <p>Hej {memberName}!</p>

            <p>Vi har mottagit en begäran om att återställa lösenordet för ditt Pistol.nu-konto.</p>

            <p>Klicka på knappen nedan för att skapa ett nytt lösenord:</p>

            <div style='text-align: center;'>
                <a href='{resetLink}' class='button'>Återställ mitt lösenord</a>
            </div>

            <p style='font-size: 12px; color: #6c757d;'>
                Om knappen inte fungerar, kopiera och klistra in följande länk i din webbläsare:<br/>
                <span class='code'>{resetLink}</span>
            </p>

            <div class='warning'>
                <strong>⏰ Viktigt:</strong> Denna länk är giltig i endast <strong>1 timme</strong> av säkerhetsskäl.
            </div>

            <div class='warning'>
                <strong>🛡️ Säkerhetsvarning:</strong>
                <ul style='margin: 10px 0; padding-left: 20px;'>
                    <li>Om du <strong>inte</strong> begärde denna återställning, ignorera detta e-postmeddelande</li>
                    <li>Dela <strong>aldrig</strong> denna länk med någon annan</li>
                    <li>Vi kommer <strong>aldrig</strong> be dig att skicka ditt lösenord via e-post</li>
                    <li>Vid misstanke om obehörig åtkomst, kontakta administratören omedelbart</li>
                </ul>
            </div>

            <p>Om du har frågor, kontakta din klubbadministratör eller webbansvarig.</p>

            <p>Med vänliga hälsningar,<br/>Pistol.nu</p>
        </div>
        <div class='footer'>
            <p>Detta är ett automatiskt meddelande från Pistol.nu.</p>
            <p>Svara inte på detta e-postmeddelande.</p>
        </div>
    </div>
</body>
</html>";

            await SendEmailAsync(memberEmail, subject, body);
        }

        /// <summary>
        /// Generate Swish payment redirect URL (Gmail-compatible HTTPS link)
        /// </summary>
        private string GenerateSwishRedirectUrl(string swishNumber, decimal amount, string message)
        {
            // Get site URL from configuration (same as password reset)
            var siteUrl = _configuration["Email:SiteUrl"] ?? _configuration["SiteUrl"] ?? "https://pistol.nu";

            // URL encode the message parameter
            var encodedMessage = Uri.EscapeDataString(message);

            // Create HTTPS redirect URL that will be redirected server-side to swish:// URL
            // This works in Gmail and other email clients that block custom URL schemes
            return $"{siteUrl}/umbraco/surface/Swish/SwishRedirect?payee={swishNumber}&amount={amount}&message={encodedMessage}";
        }

        /// <summary>
        /// Send Swish QR code payment email with inline image attachment
        /// </summary>
        public async Task SendSwishQRCodeEmailAsync(
            string memberEmail,
            string memberName,
            string competitionName,
            byte[] qrCodeBytes,
            decimal amount,
            string shootingClasses,
            string invoiceNumber,
            string swishNumber,
            string invoiceMessage)
        {
            // If SMTP is not configured, log and return
            if (string.IsNullOrEmpty(_smtpHost))
            {
                _logger.LogWarning("SMTP host not configured. Email not sent to {Email}", memberEmail);
                return;
            }

            // Generate Swish redirect URL for mobile users (Gmail-compatible)
            var swishRedirectUrl = GenerateSwishRedirectUrl(swishNumber, amount, invoiceMessage);

            var subject = $"Swish-betalning för {competitionName} - Pistol.nu";
            var body = $@"
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background-color: #0d6efd; color: white; padding: 20px; text-align: center; border-radius: 5px 5px 0 0; }}
        .content {{ background-color: #f8f9fa; padding: 30px; border: 1px solid #dee2e6; }}
        .qr-code {{ text-align: center; margin: 30px 0; padding: 20px; background-color: white; border: 2px solid #0d6efd; border-radius: 10px; }}
        .qr-code img {{ max-width: 200px; width: 200px; height: 200px; display: block; margin: 0 auto; }}
        .info-box {{ background-color: white; padding: 20px; border-left: 4px solid #0d6efd; margin: 20px 0; }}
        .info-item {{ margin: 10px 0; }}
        .info-label {{ font-weight: bold; color: #495057; }}
        .info-value {{ color: #212529; }}
        .instructions {{ background-color: #e7f3ff; border-left: 4px solid #0d6efd; padding: 15px; margin: 20px 0; }}
        .footer {{ margin-top: 20px; padding: 15px; background-color: #e9ecef; border-radius: 0 0 5px 5px; font-size: 12px; color: #6c757d; text-align: center; }}
    </style>
</head>
<body>
    <div class='container'>
        <div class='header'>
            <h2>💳 Betala din tävlingsavgift med Swish</h2>
        </div>
        <div class='content'>
            <p>Hej {memberName}!</p>

            <p>Tack för din anmälan till <strong>{competitionName}</strong>!</p>

            <p>För att slutföra din anmälan, betala tävlingsavgiften med Swish genom att scanna QR-koden nedan:</p>

            <div class='qr-code'>
                <img src='cid:swishQRCode' alt='Swish QR Code' />
                <p style='margin-top: 15px; color: #6c757d; font-size: 14px;'>
                    <i>📱 Scanna QR-koden med Swish-appen</i>
                </p>
            </div>

            <div style='text-align: center; margin: 30px 0; padding: 20px; background-color: #e8f5e9; border-radius: 10px;'>
                <p style='margin-bottom: 15px; color: #2e7d32; font-weight: bold; font-size: 16px;'>📱 Läser du detta på din telefon?</p>
                <table width='100%' cellpadding='0' cellspacing='0' style='margin: 0 auto;'>
                    <tr>
                        <td align='center'>
                            <a href='{swishRedirectUrl}' style='display: inline-block; background-color: #00a652; color: #ffffff; padding: 16px 40px; text-decoration: none; border-radius: 8px; font-weight: bold; font-size: 18px; font-family: Arial, sans-serif;'>
                                💳 Öppna Swish och betala
                            </a>
                        </td>
                    </tr>
                </table>
                <p style='margin-top: 15px; font-size: 14px; color: #2e7d32;'>
                    <em>Klicka på knappen så öppnas Swish-appen automatiskt<br/>med alla uppgifter redan ifyllda</em>
                </p>
            </div>

            <div class='info-box'>
                <h4 style='margin-top: 0; color: #0d6efd;'>📋 Betalningsinformation</h4>
                <div class='info-item'>
                    <span class='info-label'>Belopp:</span>
                    <span class='info-value'>{amount:F2} kr</span>
                </div>
                <div class='info-item'>
                    <span class='info-label'>Klass{(shootingClasses.Contains(",") ? "er" : "")}:</span>
                    <span class='info-value'>{shootingClasses}</span>
                </div>
                <div class='info-item'>
                    <span class='info-label'>Fakturanummer:</span>
                    <span class='info-value'>{invoiceNumber}</span>
                </div>
            </div>

            <div class='instructions'>
                <h5 style='margin-top: 0;'><strong>💡 Två sätt att betala</strong></h5>

                <div style='margin: 20px 0; padding: 15px; background-color: #fff; border-left: 4px solid #00a652;'>
                    <p style='margin: 0 0 10px 0;'><strong style='color: #00a652;'>📱 Om du läser detta på din telefon:</strong></p>
                    <p style='margin: 0;'>Klicka på den gröna knappen ovan så öppnas Swish automatiskt med alla uppgifter ifyllda.</p>
                </div>

                <div style='margin: 20px 0; padding: 15px; background-color: #fff; border-left: 4px solid #0d6efd;'>
                    <p style='margin: 0 0 10px 0;'><strong style='color: #0d6efd;'>💻 Om du läser detta på en dator:</strong></p>
                    <ol style='margin: 5px 0; padding-left: 20px;'>
                        <li>Öppna Swish-appen på din mobiltelefon</li>
                        <li>Välj <strong>Scanna QR-kod</strong></li>
                        <li>Scanna QR-koden ovan</li>
                        <li>Bekräfta betalningen</li>
                    </ol>
                </div>

                <p style='margin-top: 15px; padding: 10px; background-color: #fff3cd; border-radius: 5px; color: #856404;'>
                    <strong>✓</strong> Alla betalningsuppgifter är redan ifyllda – du behöver bara bekräfta!
                </p>
            </div>

            <p>När betalningen är genomförd kommer klubbadministratören att bekräfta den och din anmälan är klar.</p>

            <p>Om du har frågor, kontakta din klubbadministratör.</p>

            <p>Med vänliga hälsningar,<br/>Pistol.nu</p>
        </div>
        <div class='footer'>
            <p>Detta är ett automatiskt meddelande från Pistol.nu.</p>
            <p>Spara detta e-postmeddelande tills betalningen är bekräftad.</p>
        </div>
    </div>
</body>
</html>";

            try
            {
                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(_fromAddress, _fromName);
                    message.To.Add(memberEmail);
                    message.Subject = subject;
                    message.Body = body;
                    message.IsBodyHtml = true;

                    // Create inline attachment for QR code
                    using (var qrStream = new System.IO.MemoryStream(qrCodeBytes))
                    {
                        var qrAttachment = new System.Net.Mail.Attachment(qrStream, "swish-qr-code.png", "image/png");
                        qrAttachment.ContentId = "swishQRCode";
                        qrAttachment.ContentDisposition.Inline = true;
                        qrAttachment.ContentDisposition.DispositionType = System.Net.Mime.DispositionTypeNames.Inline;

                        message.Attachments.Add(qrAttachment);

                        using (var smtpClient = new SmtpClient(_smtpHost, _smtpPort))
                        {
                            smtpClient.EnableSsl = _useSsl;
                            smtpClient.UseDefaultCredentials = false;
                            smtpClient.Credentials = new NetworkCredential(_username, _password);

                            await smtpClient.SendMailAsync(message);
                            _logger.LogInformation("Swish QR code email sent successfully to {Email}", memberEmail);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Swish QR code email to {Email}", memberEmail);
                // Don't throw - we don't want email failures to break the payment flow
            }
        }

        /// <summary>
        /// Send email notification when a user requests Android app test access
        /// </summary>
        public async Task SendAndroidTestAccessRequestAsync(string memberName, string memberEmail)
        {
            if (string.IsNullOrEmpty(_adminEmail))
            {
                _logger.LogWarning("Admin email not configured. Cannot send Android test access request.");
                return;
            }

            var subject = $"Android App Test - Ny förfrågan: {memberName}";
            var body = $@"
<html>
<body style='font-family: Arial, sans-serif;'>
    <h2>Ny förfrågan om Android-app testaccess</h2>
    <p>En medlem har begärt att bli tillagd som testare för Pistol.nu Träningsmatch Android-appen.</p>
    <table style='border-collapse: collapse; margin: 20px 0;'>
        <tr>
            <td style='padding: 8px; font-weight: bold;'>Namn:</td>
            <td style='padding: 8px;'>{memberName}</td>
        </tr>
        <tr>
            <td style='padding: 8px; font-weight: bold;'>E-post:</td>
            <td style='padding: 8px;'><a href='mailto:{memberEmail}'>{memberEmail}</a></td>
        </tr>
    </table>
    <h3>Åtgärd:</h3>
    <p>För att lägga till användaren som testare:</p>
    <ol>
        <li>Gå till <a href='https://play.google.com/console'>Google Play Console</a></li>
        <li>Välj Pistol.nu Träningsmatch-appen</li>
        <li>Gå till Release → Testing → Internal testing (eller Closed testing)</li>
        <li>Lägg till e-postadressen <strong>{memberEmail}</strong> i testarlistan</li>
        <li>Skicka ett bekräftelsemail till användaren med länk till testversionen</li>
    </ol>
    <hr style='margin: 20px 0;'>
    <p style='color: #666; font-size: 12px;'>Detta meddelande skickades automatiskt från Pistol.nu.</p>
</body>
</html>";

            try
            {
                using (var message = new MailMessage())
                {
                    message.From = new MailAddress(_fromAddress, _fromName);
                    message.To.Add(_adminEmail);
                    message.Subject = subject;
                    message.Body = body;
                    message.IsBodyHtml = true;

                    using (var smtpClient = new SmtpClient(_smtpHost, _smtpPort))
                    {
                        smtpClient.EnableSsl = _useSsl;
                        smtpClient.UseDefaultCredentials = false;
                        smtpClient.Credentials = new NetworkCredential(_username, _password);

                        await smtpClient.SendMailAsync(message);
                        _logger.LogInformation("Android test access request email sent for {MemberEmail}", memberEmail);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send Android test access request email for {MemberEmail}", memberEmail);
                throw; // Re-throw so the controller can handle it
            }
        }
    }
}
