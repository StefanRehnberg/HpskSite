using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    public class BugReportController : SurfaceController
    {
        private readonly EmailService _emailService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<BugReportController> _logger;

        public BugReportController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            EmailService emailService,
            IMemberManager memberManager,
            ILogger<BugReportController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _emailService = emailService;
            _memberManager = memberManager;
            _logger = logger;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitReport(string name, string email, string description, string pageUrl, List<IFormFile>? images = null)
        {
            try
            {
                // Validate input
                if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
                {
                    return Json(new { success = false, message = "Namn måste anges (minst 2 tecken)" });
                }

                if (string.IsNullOrWhiteSpace(email) || !IsValidEmail(email))
                {
                    return Json(new { success = false, message = "En giltig e-postadress måste anges" });
                }

                if (string.IsNullOrWhiteSpace(description) || description.Length < 10)
                {
                    return Json(new { success = false, message = "Beskrivning måste anges (minst 10 tecken)" });
                }

                if (description.Length > 2000)
                {
                    return Json(new { success = false, message = "Beskrivning får inte överstiga 2000 tecken" });
                }

                // Validate images if provided
                if (images != null && images.Count > 0)
                {
                    if (images.Count > 3)
                    {
                        return Json(new { success = false, message = "Max 3 bilder kan bifogas" });
                    }

                    foreach (var image in images)
                    {
                        // Check file size (5MB max)
                        if (image.Length > 5 * 1024 * 1024)
                        {
                            return Json(new { success = false, message = "Bildfiler får inte överstiga 5MB vardera" });
                        }

                        // Check file type
                        var allowedTypes = new[] { "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp" };
                        if (!allowedTypes.Contains(image.ContentType.ToLower()))
                        {
                            return Json(new { success = false, message = "Endast bildfiler (JPG, PNG, GIF, WebP) är tillåtna" });
                        }
                    }
                }

                // Get current member info if logged in
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                string? memberName = currentMember?.Name;
                string? memberEmail = currentMember?.Email;

                // Send bug report email
                await _emailService.SendBugReportAsync(name, email, description, images, memberName, memberEmail, pageUrl);

                _logger.LogInformation("Bug report submitted from {Name} ({Email}), Member: {Member}", name, email, memberName ?? "Guest");

                return Json(new { success = true, message = "Tack för din felrapport! Den har skickats till administratören." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting bug report");
                return Json(new { success = false, message = "Ett fel uppstod vid skicka felrapporten. Försök igen senare." });
            }
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }
}
