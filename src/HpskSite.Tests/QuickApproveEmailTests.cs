using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using HpskSite.Services;
using Xunit;
using Xunit.Abstractions;

namespace HpskSite.Tests
{
    /// <summary>
    /// Manual integration tests for the Quick Approve email feature.
    /// These tests send real emails via SMTP and are skipped by default.
    ///
    /// To run:
    ///   1. Fill in the SMTP credentials below (use smtp.simply.com when running
    ///      locally — websmtp.simply.com only works from Simply.com's hosting servers).
    ///   2. Remove the Skip parameter from the [Fact] attribute on the test you want to run.
    ///   3. Run the specific test from Visual Studio Test Explorer or CLI:
    ///      dotnet test --filter "FullyQualifiedName~QuickApproveEmailTests" src/HpskSite.Tests
    /// </summary>
    public class QuickApproveEmailTests
    {
        // ── SMTP credentials — fill these in before running ──────────────
        // Use smtp.simply.com (port 587, TLS) when sending from a local machine.
        // websmtp.simply.com only works from Simply.com's own web servers.
        // Username must be the full email address (same as webmail login).
        private const string SmtpHost     = "smtp.simply.com";
        private const int    SmtpPort     = 587;
        private const bool   UseSsl       = true;
        private const string Username     = "";
        private const string Password     = "";
        private const string FromAddress  = "";
        // ─────────────────────────────────────────────────────────────────

        private readonly ITestOutputHelper _output;

        public QuickApproveEmailTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// Bare-bones SMTP test that throws on failure (EmailService swallows errors).
        /// Run this first to verify credentials and connectivity.
        /// </summary>
        [Fact(Skip = "Manual test — sends real email. Remove Skip to run.")]
        public async Task SmtpConnectivity_SendsSimpleEmail()
        {
            using var message = new MailMessage();
            message.From = new MailAddress(FromAddress, "Pistol.nu Test");
            message.To.Add("admin@pistol.nu");
            message.Subject = "[TEST] SMTP-anslutning fungerar";
            message.Body = "<p>Om du ser detta mail fungerar SMTP-inställningarna.</p>";
            message.IsBodyHtml = true;

            using var smtp = new SmtpClient(SmtpHost, SmtpPort);
            smtp.EnableSsl = UseSsl;
            smtp.UseDefaultCredentials = false;
            smtp.Credentials = new NetworkCredential(Username, Password);

            _output.WriteLine($"Connecting to {SmtpHost}:{SmtpPort} as {Username}...");
            await smtp.SendMailAsync(message);
            _output.WriteLine("Email sent successfully.");
        }

        private EmailService CreateEmailService()
        {
            var settings = new Dictionary<string, string?>
            {
                ["Email:SmtpHost"]     = SmtpHost,
                ["Email:SmtpPort"]     = SmtpPort.ToString(),
                ["Email:UseSsl"]       = UseSsl.ToString(),
                ["Email:Username"]     = Username,
                ["Email:Password"]     = Password,
                ["Email:FromAddress"]  = FromAddress,
                ["Email:FromName"]     = "Pistol.nu",
                ["Email:AdminEmail"]   = "admin@pistol.nu",
                ["SiteUrl"]            = "https://pistol.nu",
            };

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            var logger = new Mock<ILogger<EmailService>>();

            // Log any errors to test output so we can see them
            logger.Setup(x => x.Log(
                It.Is<LogLevel>(l => l >= LogLevel.Warning),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
                .Callback((LogLevel level, EventId id, object state, Exception? ex, Delegate formatter) =>
                {
                    _output.WriteLine($"[{level}] {state}");
                    if (ex != null) _output.WriteLine($"Exception: {ex}");
                });

            return new EmailService(configuration, logger.Object);
        }

        [Fact(Skip = "Manual test — sends real email. Remove Skip to run.")]
        public async Task SendClubAdminNotification_WithApproveButton_SendsToAdmin()
        {
            var emailService = CreateEmailService();

            await emailService.SendRegistrationNotificationToClubAdminAsync(
                adminEmail: "admin@pistol.nu",
                adminName: "Test Klubbadmin",
                memberName: "Anna Testsson",
                memberEmail: "anna.testsson@example.com",
                clubName: "Testskytteklubb",
                pendingMemberId: 99999);
        }

        [Fact(Skip = "Manual test — sends real email. Remove Skip to run.")]
        public async Task SendRegionalAdminNotification_NoClubAdmins_WithApproveButton_SendsToAdmin()
        {
            var emailService = CreateEmailService();

            await emailService.SendRegistrationNotificationToRegionalAdminAsync(
                adminEmail: "admin@pistol.nu",
                adminName: "Test Regionaladmin",
                memberName: "Anna Testsson",
                memberEmail: "anna.testsson@example.com",
                clubName: "Testskytteklubb",
                hasClubAdmins: false,
                clubAdminNames: new List<string>(),
                pendingMemberId: 99999);
        }

        [Fact(Skip = "Manual test — sends real email. Remove Skip to run.")]
        public async Task SendRegionalAdminNotification_WithClubAdmins_WithApproveButton_SendsToAdmin()
        {
            var emailService = CreateEmailService();

            await emailService.SendRegistrationNotificationToRegionalAdminAsync(
                adminEmail: "admin@pistol.nu",
                adminName: "Test Regionaladmin",
                memberName: "Anna Testsson",
                memberEmail: "anna.testsson@example.com",
                clubName: "Testskytteklubb",
                hasClubAdmins: true,
                clubAdminNames: new List<string> { "Kalle Klubbadmin", "Lisa Klubbadmin" },
                pendingMemberId: 99999);
        }
    }
}
