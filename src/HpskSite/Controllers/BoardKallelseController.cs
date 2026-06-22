using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;
using System.Text;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kallelse (meeting notice) email. Recipients depend on type/scope: club årsmöte → all approved
    /// members; club other → the board; region → the region board. Confirm-before-send (preview returns
    /// the count), records the send on the meeting, ticks the årshjul kallelse item, copies the admin.
    /// </summary>
    public class BoardKallelseController : SurfaceController
    {
        private const int MaxSmtp = 250;

        private readonly BoardMeetingService _meetingService;
        private readonly BoardGovernanceService _gov;
        private readonly BoardRoleService _boardRoleService;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly EmailService _emailService;
        private readonly BrevoEmailService _brevo;
        private readonly ClubService _clubService;
        private readonly ILogger<BoardKallelseController> _logger;

        public BoardKallelseController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            BoardMeetingService meetingService,
            BoardGovernanceService gov,
            BoardRoleService boardRoleService,
            AdminAuthorizationService auth,
            IMemberService memberService,
            IMemberManager memberManager,
            EmailService emailService,
            BrevoEmailService brevo,
            ClubService clubService,
            ILogger<BoardKallelseController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _meetingService = meetingService;
            _gov = gov;
            _boardRoleService = boardRoleService;
            _auth = auth;
            _memberService = memberService;
            _memberManager = memberManager;
            _emailService = emailService;
            _brevo = brevo;
            _clubService = clubService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetKallelsePreview(int meetingId)
        {
            var meeting = _meetingService.GetMeeting(meetingId);
            if (meeting == null || !meeting.IsActive) return Json(new { success = false, message = "Mötet hittades inte" });
            if (!await CanAccessBoardWork(meeting.OwnerType, meeting.OwnerId)) return Json(new { success = false, message = "Åtkomst nekad" });

            var (recipients, audience) = ResolveRecipients(meeting);
            bool hasBrevo = !string.IsNullOrEmpty(GetClubBrevoKey(meeting.OwnerType, meeting.OwnerId));
            return Json(new
            {
                success = true,
                audience,
                count = recipients.Count,
                sample = recipients.Take(8).Select(r => r.Name),
                tooMany = recipients.Count > MaxSmtp && !hasBrevo,
                alreadySent = meeting.KallelseSentDate?.ToString("yyyy-MM-dd"),
                alreadyCount = meeting.KallelseRecipientCount
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendKallelse(int meetingId, string? message)
        {
            try
            {
                var meeting = _meetingService.GetMeeting(meetingId);
                if (meeting == null || !meeting.IsActive) return Json(new { success = false, message = "Mötet hittades inte" });
                if (!await CanAccessBoardWork(meeting.OwnerType, meeting.OwnerId)) return Json(new { success = false, message = "Åtkomst nekad" });

                var (recipients, _) = ResolveRecipients(meeting);
                if (recipients.Count == 0)
                    return Json(new { success = false, message = "Inga mottagare med e-postadress hittades." });

                var (orgName, contactEmail, brevoKey) = ResolveOrg(meeting.OwnerType, meeting.OwnerId);
                var subject = $"Kallelse: {meeting.Title} – {meeting.MeetingDate:yyyy-MM-dd HH:mm}";
                var html = BuildHtml(meeting, orgName, message);

                int sent = 0, failed = 0;
                if (recipients.Count > MaxSmtp)
                {
                    if (string.IsNullOrEmpty(brevoKey))
                        return Json(new { success = false, message = $"För många mottagare ({recipients.Count}) för direktutskick (max {MaxSmtp}). Lägg in klubbens Brevo-nyckel eller använd klubbens vanliga mailutskick." });
                    var list = recipients.Select(r => (r.Email, r.Name)).ToList();
                    (sent, failed) = await _brevo.SendBulkEmailAsync(brevoKey, contactEmail, orgName, list, subject, html);
                }
                else
                {
                    foreach (var r in recipients)
                    {
                        var ok = await _emailService.SendHtmlEmailAsync(r.Email, subject, html, orgName, contactEmail, orgName);
                        if (ok) sent++; else failed++;
                    }
                }

                var meId = await GetCurrentMemberId();
                _meetingService.MarkKallelseSent(meetingId, meId, sent);

                // Record sent in the årshjul (best effort) for årsmöten.
                if (IsArsmote(meeting.MeetingType))
                    TryTickKallelseInWheel(meeting);

                await _emailService.SendMemberMailAdminCopyAsync(orgName + " (kallelse)", sent, subject, html);

                _logger.LogInformation("Kallelse for meeting {Id} sent to {Sent} (failed {Failed})", meetingId, sent, failed);
                return Json(new { success = true, sent, failed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending kallelse for meeting {Id}", meetingId);
                return Json(new { success = false, message = "Ett fel uppstod vid utskick." });
            }
        }

        // ---- Recipients -----------------------------------------------------

        private (List<(string Email, string Name)> Recipients, string Audience) ResolveRecipients(BoardMeeting m)
        {
            var list = new List<(string Email, string Name)>();
            string audience;

            if (m.OwnerType == DocumentOwnerType.Club && IsArsmote(m.MeetingType))
            {
                audience = "alla medlemmar i klubben";
                var clubIdStr = m.OwnerId.ToString();
                var members = _memberService.GetAll(0, int.MaxValue, out _)
                    .Where(x => x.ContentType.Alias != "hpskClub" && x.IsApproved && !string.IsNullOrEmpty(x.Email))
                    .Where(x => (x.GetValue<string>("primaryClubId") ?? "") == clubIdStr ||
                                (x.GetValue<string>("memberClubIds") ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(clubIdStr));
                foreach (var x in members) list.Add((x.Email!, DisplayName(x)));
            }
            else
            {
                // Board (the seeded attendees) for styrelsemöten and for region meetings.
                audience = m.OwnerType == DocumentOwnerType.Region ? "kretsens styrelse" : "styrelsen";
                foreach (var a in _meetingService.GetAttendees(m.Id))
                {
                    var mem = _memberService.GetById(a.MemberId);
                    if (mem != null && !string.IsNullOrEmpty(mem.Email))
                        list.Add((mem.Email!, a.MemberName ?? DisplayName(mem)));
                }
            }

            // de-dup by email (case-insensitive)
            var deduped = list.GroupBy(r => r.Email.ToLowerInvariant()).Select(g => g.First()).ToList();
            return (deduped, audience);
        }

        private static bool IsArsmote(string type) => type == "Arsmote" || type == "ExtraArsmote";

        private static string DisplayName(Umbraco.Cms.Core.Models.IMember m)
        {
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return string.IsNullOrEmpty(n) ? m.Name : n;
        }

        // ---- Email body -----------------------------------------------------

        private string BuildHtml(BoardMeeting m, string orgName, string? message)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var agenda = _meetingService.GetAgenda(m.Id);
            var links = _meetingService.GetLinksForMeeting(m.Id);
            var sb = new StringBuilder();
            sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;color:#222;line-height:1.5;\">");
            sb.Append($"<p style=\"color:#666;text-transform:uppercase;letter-spacing:.05em;font-size:13px;margin:0;\">Kallelse</p>");
            sb.Append($"<h2 style=\"margin:.2em 0;\">{Enc(m.Title)}</h2>");
            sb.Append($"<p style=\"font-size:15px;\"><strong>{Enc(orgName)}</strong><br>");
            sb.Append($"{m.MeetingDate:dddd d MMMM yyyy, HH:mm}");
            if (!string.IsNullOrWhiteSpace(m.Location)) sb.Append($"<br>Plats: {Enc(m.Location)}");
            sb.Append("</p>");

            if (!string.IsNullOrWhiteSpace(message))
                sb.Append($"<p>{Enc(message).Replace("\n", "<br>")}</p>");

            sb.Append("<h3>Dagordning</h3><ol>");
            foreach (var a in agenda)
            {
                sb.Append($"<li>{Enc(a.Heading)}");
                var its = links.Where(l => l.AgendaItemId == a.Id).ToList();
                if (its.Count > 0)
                {
                    sb.Append("<br>");
                    foreach (var l in its)
                        sb.Append($"<span style=\"font-size:13px;\">Bilaga: <a href=\"{LinkHref(l, m, baseUrl)}\">{Enc(l.Label)}</a></span><br>");
                }
                sb.Append("</li>");
            }
            sb.Append("</ol>");

            if (IsArsmote(m.MeetingType))
                sb.Append("<p style=\"font-size:14px;color:#444;\">Motioner och övriga frågor anmäls till styrelsen före mötet. Kallelsen har utlysts i enlighet med stadgarna.</p>");

            sb.Append($"<p><a href=\"{baseUrl}/styrelse/dagordning/{m.Id}\" style=\"display:inline-block;padding:10px 16px;background:#0d6efd;color:#fff;text-decoration:none;border-radius:6px;\">Visa/skriv ut dagordningen</a></p>");
            sb.Append($"<p style=\"color:#888;font-size:12px;\">Detta är en kallelse från {Enc(orgName)} via pistol.nu.</p>");
            sb.Append("</div>");
            return sb.ToString();
        }

        private string LinkHref(BoardMeetingAgendaLink l, BoardMeeting m, string baseUrl) => l.Kind switch
        {
            "document" => $"{baseUrl}/umbraco/surface/Document/DownloadDocument?id={l.RefId}",
            "meeting" => $"{baseUrl}/styrelse/protokoll/{l.RefId}",
            "valforslag" => $"{baseUrl}/styrelse/valforslag?type={m.OwnerType}&id={m.OwnerId}&year={l.RefId}",
            _ => l.Url ?? "#"
        };

        private static string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

        // ---- Org / config ---------------------------------------------------

        private (string Name, string ContactEmail, string? BrevoKey) ResolveOrg(int ownerType, int ownerId)
        {
            var node = UmbracoContext.Content?.GetById(ownerId);
            if (ownerType == DocumentOwnerType.Club)
            {
                var name = _clubService.GetClubNameById(ownerId) ?? node?.Name ?? "Klubben";
                return (name, node?.Value<string>("contactEmail") ?? "", node?.Value<string>("brevoApiKey"));
            }
            return (node?.Value<string>("regionName") ?? node?.Name ?? "Kretsen", node?.Value<string>("contactEmail") ?? "", null);
        }

        private string? GetClubBrevoKey(int ownerType, int ownerId)
            => ownerType == DocumentOwnerType.Club ? UmbracoContext.Content?.GetById(ownerId)?.Value<string>("brevoApiKey") : null;

        private void TryTickKallelseInWheel(BoardMeeting m)
        {
            try
            {
                var items = _gov.GetYearWheel(m.OwnerType, m.OwnerId, m.MeetingDate.Year);
                var item = items.FirstOrDefault(i => !i.Done && i.Title.Contains("Kallelse", StringComparison.OrdinalIgnoreCase));
                if (item != null) _gov.SetWheelDone(item.Id, true);
            }
            catch { /* best effort */ }
        }

        // ---- Auth -----------------------------------------------------------

        private async Task<bool> CanAccessBoardWork(int ownerType, int ownerId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (ownerType == DocumentOwnerType.Club)
            {
                if (await _auth.IsClubAdminForClub(ownerId)) return true;
            }
            else if (ownerType == DocumentOwnerType.Region)
            {
                var regionCode = UmbracoContext.Content?.GetById(ownerId)?.Value<string>("regionCode") ?? "";
                if (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode)) return true;
            }
            var meId = await GetCurrentMemberId();
            return meId > 0 && _boardRoleService.IsBoardMemberOf(ownerType, ownerId, meId);
        }

        private async Task<int> GetCurrentMemberId()
        {
            var cm = await _memberManager.GetCurrentMemberAsync();
            if (cm?.Email == null) return 0;
            return _memberService.GetByEmail(cm.Email)?.Id ?? 0;
        }
    }
}
