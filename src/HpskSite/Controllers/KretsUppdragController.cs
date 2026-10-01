using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Kretsgranskning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kretsens uppdrag (kretsgranskning fas 1) — läsvägen för vem som har vilket uppdrag.
    /// Rollerna tilldelas som vanligt via <see cref="BoardRoleController"/>; den här kontrollern
    /// svarar bara på frågor om dem.
    /// </summary>
    public class KretsUppdragController : SurfaceController
    {
        private readonly KretsUppdragService _uppdrag;
        private readonly BoardRoleService _roles;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly KretsLinkTokenService _tokens;
        private readonly EmailService _email;
        private readonly HpskSite.Services.Mail.ReplyContactResolver _replyTo;

        public KretsUppdragController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            KretsUppdragService uppdrag,
            BoardRoleService roles,
            AdminAuthorizationService auth,
            IMemberManager memberManager,
            IMemberService memberService,
            KretsLinkTokenService tokens,
            EmailService email,
            HpskSite.Services.Mail.ReplyContactResolver replyTo)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _tokens = tokens;
            _email = email;
            _replyTo = replyTo;
            _uppdrag = uppdrag;
            _roles = roles;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        /// <summary>
        /// Kretsens tre uppdrag och vem som har dem. Kretsadmin, kretsens styrelse och sajtadmin.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStatus(int regionId)
        {
            if (!await CanReadAsync(regionId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var data = _uppdrag.Status(regionId).Select(s => new
            {
                roleKey = s.RoleKey,
                label = s.Label,
                task = BoardRoleDefinitions.KretsUppdragTask(s.RoleKey),
                isMissing = s.IsMissing,
                holders = s.Holders.Select(h => new { h.MemberId, h.Name, hasEmail = !string.IsNullOrWhiteSpace(h.Email) })
            });
            return Json(new { success = true, data });
        }

        /// <summary>
        /// "Jag vill bli kretsens granskare": en inloggad medlem ber om ett av kretsens uppdrag.
        /// Mejlet går till kretsadministratörerna och kretsens ordförande — de som kan bekräfta.
        ///
        /// <para><b>Ingen tabell.</b> Förfrågan bärs i sin helhet av den signerade länken. Det
        /// betyder att en förfrågan vars mejl inte gick fram inte finns någonstans — därför säger
        /// svaret uttryckligen hur många som nåddes, och medlemmen får veta när ingen gjorde det.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestUppdrag(int regionId, string roleKey, string? message)
        {
            var me = await CurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Logga in först." });

            if (!BoardRoleDefinitions.IsKretsUppdrag(roleKey))
                return Json(new { success = false, message = "Okänt uppdrag." });

            var region = RegionNode(regionId);
            if (region == null) return Json(new { success = false, message = "Kretsen hittades inte." });

            if (_uppdrag.HasUppdrag(regionId, me.Id, roleKey))
                return Json(new { success = false, message = "Du har redan det uppdraget i kretsen." });

            var regionName = region.Value<string>("regionName") ?? region.Name ?? "";
            var label = BoardRoleDefinitions.GetLabel(roleKey);
            var token = _tokens.CreateUppdragRequest(regionId, me.Id, roleKey);
            var confirmUrl = $"{Request.Scheme}://{Request.Host}/kretsen/uppdrag?t={Uri.EscapeDataString(token)}";
            var requesterName = DisplayName(me);
            message = (message ?? "").Trim();
            if (message.Length > 1000) message = message[..1000];

            var recipients = ConfirmerRecipients(regionId, region.Value<string>("regionCode") ?? "");
            if (recipients.Count == 0)
            {
                var contact = region.Value<string>("contactEmail") ?? "";
                if (!string.IsNullOrWhiteSpace(contact)) recipients.Add((contact.Trim(), regionName));
            }

            int sent = 0;
            foreach (var (email, name) in recipients)
            {
                if (await _email.SendKretsUppdragRequestAsync(email, name, regionName, requesterName,
                        label, message, confirmUrl, _replyTo.ForMember(me.Id)))
                    sent++;
            }

            // Sajtadministratören får länken i svaret (supportvägen) — hen kan ändå tilldela
            // uppdraget direkt. Ingen annan får den: länken är det som skickas till bekräftaren.
            var isSiteAdmin = await _auth.IsCurrentUserAdminAsync();
            return Json(new
            {
                success = sent > 0 || isSiteAdmin,
                sent,
                recipients = recipients.Count,
                message = sent > 0
                    ? $"Förfrågan skickad till {sent} {(sent == 1 ? "person" : "personer")} i {regionName}. Du får uppdraget när någon av dem bekräftar."
                    : recipients.Count == 0
                        ? $"{regionName} har varken kretsadministratör, ordförande eller kontaktadress på pistol.nu, så förfrågan kunde inte skickas. Kontakta kretsen direkt."
                        : "Mejlet kunde inte skickas. Försök igen senare, eller kontakta kretsen direkt.",
                confirmUrl = isSiteAdmin ? confirmUrl : null
            });
        }

        /// <summary>
        /// Bekräfta en uppdragsförfrågan. Bekräftaren måste vara kretsadministratör eller kretsens
        /// ordförande — länken säger bara VAD som ska bekräftas, den ger ingen rätt.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmUppdrag(string t)
        {
            var p = _tokens.ReadUppdragRequest(t);
            if (p == null) return Json(new { success = false, message = "Länken gäller inte längre. Be medlemmen skicka förfrågan igen." });

            var me = await CurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Logga in först." });

            if (!await CanConfirmAsync(p.RegionId, me.Id))
                return Json(new { success = false, message = "Bara kretsens administratör eller ordförande kan bekräfta." });

            var requester = _memberService.GetById(p.MemberId);
            if (requester == null) return Json(new { success = false, message = "Medlemmen finns inte längre." });

            var label = BoardRoleDefinitions.GetLabel(p.RoleKey);
            if (_uppdrag.HasUppdrag(p.RegionId, p.MemberId, p.RoleKey))
                return Json(new { success = true, already = true, message = $"{DisplayName(requester)} har redan uppdraget {label.ToLowerInvariant()}." });

            _roles.AssignBoardRole(DocumentOwnerType.Region, p.RegionId, p.MemberId, p.RoleKey,
                null, isBoardMember: false, assignedByMemberId: me.Id, electedDate: DateTime.Today);

            return Json(new { success = true, message = $"Klart. {DisplayName(requester)} är nu {label.ToLowerInvariant()} i kretsen." });
        }

        private async Task<bool> CanConfirmAsync(int regionId, int memberId)
        {
            var region = RegionNode(regionId);
            if (region == null) return false;
            var code = region.Value<string>("regionCode") ?? "";
            if (!string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code)) return true;  // inkl. sajtadmin
            return _roles.HasActiveRole(DocumentOwnerType.Region, regionId, memberId, BoardRoleDefinitions.RoleOrdforande);
        }

        /// <summary>Kretsadministratörer + kretsens ordförande, med e-post, utan dubbletter.</summary>
        private List<(string Email, string Name)> ConfirmerRecipients(int regionId, string regionCode)
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(Umbraco.Cms.Core.Models.IMember? m)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Email) || !seen.Add(m.Email.Trim())) return;
                list.Add((m.Email.Trim(), DisplayName(m)));
            }

            if (!string.IsNullOrEmpty(regionCode))
            {
                try
                {
                    foreach (var m in _memberService.GetMembersByGroup($"RegionalAdmin_{regionCode}") ?? Enumerable.Empty<Umbraco.Cms.Core.Models.IMember>())
                        Add(m);
                }
                catch { /* en trasig grupp får inte stoppa förfrågan — ordföranden kan fortfarande nås */ }
            }
            foreach (var r in _roles.GetActiveRoleHolders(DocumentOwnerType.Region, regionId, BoardRoleDefinitions.RoleOrdforande))
                Add(_memberService.GetById(r.MemberId));
            return list;
        }

        private Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent? RegionNode(int regionId)
        {
            if (regionId <= 0) return null;
            var node = UmbracoContext.Content?.GetById(regionId);
            return node?.ContentType.Alias == "regionalPage" ? node : null;
        }

        private async Task<Umbraco.Cms.Core.Models.IMember?> CurrentMemberAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            return current?.Email == null ? null : _memberService.GetByEmail(current.Email);
        }

        private static string DisplayName(Umbraco.Cms.Core.Models.IMember m)
        {
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return n.Length > 0 ? n : (m.Name ?? "");
        }

        private async Task<bool> CanReadAsync(int regionId)
        {
            if (regionId <= 0) return false;
            if (await _auth.IsCurrentUserAdminAsync()) return true;

            var node = UmbracoContext.Content?.GetById(regionId);
            if (node == null || node.ContentType.Alias != "regionalPage") return false;

            var code = node.Value<string>("regionCode") ?? "";
            if (!string.IsNullOrEmpty(code) && await _auth.IsRegionalAdminForRegion(code)) return true;

            var current = await _memberManager.GetCurrentMemberAsync();
            var memberId = current?.Email == null ? 0 : _memberService.GetByEmail(current.Email)?.Id ?? 0;
            return memberId > 0 && _roles.IsBoardMemberOf(DocumentOwnerType.Region, regionId, memberId);
        }
    }
}
