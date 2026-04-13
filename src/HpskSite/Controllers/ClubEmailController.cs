using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;
using System.Net;

namespace HpskSite.Controllers
{
    public class ClubEmailController : SurfaceController
    {
        private readonly AdminAuthorizationService _authService;
        private readonly BoardRoleService _boardRoleService;
        private readonly BrevoEmailService _brevoService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly ILogger<ClubEmailController> _logger;

        public ClubEmailController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService authService,
            BoardRoleService boardRoleService,
            BrevoEmailService brevoService,
            IMemberService memberService,
            IMemberManager memberManager,
            IContentService contentService,
            ClubService clubService,
            ILogger<ClubEmailController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _authService = authService;
            _boardRoleService = boardRoleService;
            _brevoService = brevoService;
            _memberService = memberService;
            _memberManager = memberManager;
            _contentService = contentService;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>
        /// Get club members with board roles for the email modal.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubEmailRecipients(int clubId)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var clubIdStr = clubId.ToString();
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
                .Where(m => m.ContentType.Alias != "hpskClub" && m.IsApproved)
                .Where(m => m.GetValue("primaryClubId")?.ToString() == clubIdStr ||
                    (m.GetValue("memberClubIds")?.ToString()?.Split(',')
                        .Select(s => s.Trim())
                        .Contains(clubIdStr) ?? false))
                .OrderBy(m => m.Name)
                .ToList();

            var boardRoles = _boardRoleService.GetBoardRolesForClubMembers(clubId);

            var recipients = allMembers.Select(m => {
                var roles = boardRoles.ContainsKey(m.Id) ? boardRoles[m.Id] : null;
                return new
                {
                    id = m.Id,
                    name = m.Name,
                    email = m.Email ?? "",
                    boardRoles = roles?.Select(r => new { r.Title, r.IsBoardMember }) ?? Enumerable.Empty<object>()
                };
            }).Where(m => !string.IsNullOrEmpty(m.email)).ToList();

            // Check if club has Brevo key configured
            var clubContent = _contentService.GetById(clubId);
            var hasBrevoKey = !string.IsNullOrEmpty(clubContent?.GetValue<string>("brevoApiKey"));
            var clubName = _clubService.GetClubNameById(clubId) ?? "Klubb";
            var contactEmail = clubContent?.GetValue<string>("contactEmail") ?? "";

            return Json(new { success = true, data = recipients, hasBrevoKey, clubName, contactEmail });
        }

        /// <summary>
        /// Get region board members + club contacts for the email modal.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionEmailRecipients(int regionContentId)
        {
            var publishedContent = UmbracoContext.Content?.GetById(regionContentId);
            if (publishedContent == null)
                return Json(new { success = false, message = "Region hittades inte" });

            var regionCode = publishedContent.Value<string>("regionCode") ?? "";
            if (!await _authService.IsRegionalAdminForRegion(regionCode))
                return Json(new { success = false, message = "Åtkomst nekad" });

            // Board members
            var boardMembers = _boardRoleService.GetBoardMembers(DocumentOwnerType.Region, regionContentId, boardOnly: true);
            var boardList = new List<object>();
            foreach (var bm in boardMembers)
            {
                var member = _memberService.GetById(bm.MemberId);
                if (member != null && !string.IsNullOrEmpty(member.Email))
                {
                    boardList.Add(new
                    {
                        id = bm.MemberId,
                        name = bm.MemberName ?? member.Name,
                        email = member.Email,
                        role = bm.DisplayTitle
                    });
                }
            }

            // Club contacts
            var allClubs = _clubService.GetAllClubs()
                .Where(c => c.IsActive)
                .ToList();

            // Filter to clubs in this region
            var clubsPage = publishedContent.Children?.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
            var regionClubIds = new HashSet<int>();
            if (clubsPage != null)
            {
                foreach (var club in clubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                {
                    regionClubIds.Add(club.Id);
                }
            }

            var clubContacts = allClubs
                .Where(c => regionClubIds.Contains(c.Id) && !string.IsNullOrEmpty(c.ContactEmail))
                .OrderBy(c => c.Name)
                .Select(c => new
                {
                    clubId = c.Id,
                    clubName = c.Name,
                    email = c.ContactEmail
                }).ToList();

            // Check Brevo key
            var regionContent = _contentService.GetById(regionContentId);
            var hasBrevoKey = !string.IsNullOrEmpty(regionContent?.GetValue<string>("brevoApiKey"));
            var regionName = publishedContent.Value<string>("regionName") ?? "Krets";
            var contactEmail = publishedContent.Value<string>("contactEmail") ?? "";

            return Json(new { success = true, boardMembers = boardList, clubContacts, hasBrevoKey, regionName, contactEmail });
        }

        /// <summary>
        /// Send email to club members via Brevo.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendClubEmailViaBrevo(int clubId, string subject, string body, string recipientIds)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
                return Json(new { success = false, message = "Ämne och meddelande krävs" });

            var clubContent = _contentService.GetById(clubId);
            var apiKey = clubContent?.GetValue<string>("brevoApiKey") ?? "";
            if (string.IsNullOrEmpty(apiKey))
                return Json(new { success = false, message = "Ingen Brevo API-nyckel konfigurerad" });

            var fromEmail = clubContent?.GetValue<string>("contactEmail") ?? "";
            var fromName = _clubService.GetClubNameById(clubId) ?? "Klubb";
            if (string.IsNullOrEmpty(fromEmail))
                return Json(new { success = false, message = "Klubben saknar kontakt-e-post (ställ in under Inställningar)" });

            var memberIds = recipientIds.Split(',').Select(s => int.TryParse(s.Trim(), out var id) ? id : 0).Where(id => id > 0).ToList();
            var recipients = new List<(string Email, string Name)>();
            foreach (var id in memberIds)
            {
                var member = _memberService.GetById(id);
                if (member != null && !string.IsNullOrEmpty(member.Email))
                    recipients.Add((member.Email, member.Name ?? ""));
            }

            if (recipients.Count == 0)
                return Json(new { success = false, message = "Inga mottagare valda" });

            var htmlBody = FormatEmailHtml(body, fromName);
            var (sent, failed) = await _brevoService.SendBulkEmailAsync(apiKey, fromEmail, fromName, recipients, subject, htmlBody);

            _logger.LogInformation("Club {ClubId} sent email via Brevo: {Sent} sent, {Failed} failed", clubId, sent, failed);

            return Json(new { success = true, message = $"E-post skickat till {sent} mottagare" + (failed > 0 ? $" ({failed} misslyckades)" : ""), sent, failed });
        }

        /// <summary>
        /// Send email to region board members + club contacts via Brevo.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendRegionEmailViaBrevo(int regionContentId, string subject, string body, string recipientEmails)
        {
            var publishedContent = UmbracoContext.Content?.GetById(regionContentId);
            if (publishedContent == null)
                return Json(new { success = false, message = "Region hittades inte" });

            var regionCode = publishedContent.Value<string>("regionCode") ?? "";
            if (!await _authService.IsRegionalAdminForRegion(regionCode))
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
                return Json(new { success = false, message = "Ämne och meddelande krävs" });

            var regionContent = _contentService.GetById(regionContentId);
            var apiKey = regionContent?.GetValue<string>("brevoApiKey") ?? "";
            if (string.IsNullOrEmpty(apiKey))
                return Json(new { success = false, message = "Ingen Brevo API-nyckel konfigurerad" });

            var fromEmail = publishedContent.Value<string>("contactEmail") ?? "";
            var fromName = publishedContent.Value<string>("regionName") ?? "Krets";
            if (string.IsNullOrEmpty(fromEmail))
                return Json(new { success = false, message = "Kretsen saknar kontakt-e-post (ställ in under Inställningar)" });

            // Parse "email:name" pairs
            var recipients = recipientEmails.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => {
                    var parts = s.Split('|');
                    return (Email: parts[0], Name: parts.Length > 1 ? parts[1] : "");
                })
                .ToList();

            if (recipients.Count == 0)
                return Json(new { success = false, message = "Inga mottagare valda" });

            var htmlBody = FormatEmailHtml(body, fromName);
            var (sent, failed) = await _brevoService.SendBulkEmailAsync(apiKey, fromEmail, fromName, recipients, subject, htmlBody);

            _logger.LogInformation("Region {RegionId} sent email via Brevo: {Sent} sent, {Failed} failed", regionContentId, sent, failed);

            return Json(new { success = true, message = $"E-post skickat till {sent} mottagare" + (failed > 0 ? $" ({failed} misslyckades)" : ""), sent, failed });
        }

        /// <summary>
        /// Test a Brevo API key.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TestBrevoApiKey(string apiKey)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad" });

            if (string.IsNullOrWhiteSpace(apiKey))
                return Json(new { success = false, message = "Ange en API-nyckel" });

            var (isValid, accountName) = await _brevoService.ValidateApiKeyAsync(apiKey);

            if (isValid)
                return Json(new { success = true, message = $"API-nyckel giltig! Konto: {accountName}" });
            else
                return Json(new { success = false, message = "Ogiltig API-nyckel" });
        }

        private static string FormatEmailHtml(string htmlContent, string senderName)
        {
            // Body is already HTML from CKEditor — wrap it in an email template
            return $@"
<html>
<head><style>body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }} img {{ max-width: 100%; height: auto; }}</style></head>
<body>
    <div style='padding: 15px; margin: 20px 0;'>
        {htmlContent}
    </div>
    <p style='color: #999; font-size: 12px;'>
        Detta meddelande skickades via Pistol.nu av {WebUtility.HtmlEncode(senderName)}.
    </p>
</body>
</html>";
        }
    }
}
