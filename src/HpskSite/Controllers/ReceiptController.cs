using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Print-friendly legal "Kvitto" (receipt) for a paid competition registration,
    /// reached from Min sida → Tävlingar at /kvitto/{invoiceId}. Distinct from the
    /// "Betalningsbekräftelse" email — the receipt is the document a shooter prints and
    /// hands to an employer for friskvårdsbidrag.
    ///
    /// Routed MVC controller (no Umbraco node) following the FaltskyttePrintController
    /// pattern: chromeless view, typed model. Access is owner-or-staff gated.
    /// </summary>
    [Route("kvitto")]
    public class ReceiptController : Controller
    {
        private readonly ReceiptModelBuilder _builder;
        private readonly IContentService _contentService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _auth;
        private readonly ConsolidatedInvoiceService _consolidated;

        public ReceiptController(
            ReceiptModelBuilder builder,
            IContentService contentService,
            IMemberManager memberManager,
            AdminAuthorizationService auth,
            ConsolidatedInvoiceService consolidated)
        {
            _builder = builder;
            _contentService = contentService;
            _memberManager = memberManager;
            _auth = auth;
            _consolidated = consolidated;
        }

        [HttpGet("{invoiceId:int}")]
        public async Task<IActionResult> Index(int invoiceId)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null)
            {
                // Bounce to login, returning here afterwards.
                return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/kvitto/{invoiceId}")}");
            }

            var model = _builder.Build(invoiceId);
            if (model == null || !model.Found)
                return NotFound();

            // Only a paid registration produces a receipt.
            if (!model.IsPaid)
                return View("~/Views/Receipt.cshtml", model); // view shows a "not paid yet" notice

            // Authorization: the buyer themselves, an admin of the club that PAID (a samlingsfaktura is
            // billed to "club-<id>", which never parses as a member, so the paying club would otherwise
            // be locked out of its own receipt), or staff for the hosting competition.
            var isOwner = int.TryParse(current.Id, out var currentId) && currentId == model.MemberId;
            if (!isOwner && !await IsPayingClubAdmin(invoiceId) && !await IsStaffForCompetition(model.CompetitionId))
                return Forbid();

            return View("~/Views/Receipt.cshtml", model);
        }

        /// <summary>
        /// The club a samlingsfaktura was issued to. That club paid, so its admins get the receipt —
        /// and they are frequently NOT staff for the competition, since a club may pay invoices on
        /// another club's or the krets's competition.
        /// </summary>
        private async Task<bool> IsPayingClubAdmin(int invoiceId)
        {
            var payerClubId = _consolidated.ReadPayerClubId(invoiceId);
            return payerClubId > 0 && await _auth.IsClubAdminForClub(payerClubId);
        }

        private async Task<bool> IsStaffForCompetition(int competitionId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (await _auth.IsCompetitionManager(competitionId)) return true;
            var comp = _contentService.GetById(competitionId);
            var clubId = comp?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0 && (await _auth.IsClubAdminForClub(clubId) || await _auth.IsSkjutledareForClub(clubId)))
                return true;

            // Region-hosted competition (clubId unset): the KRETS is the organiser, so its admins are
            // the staff. Without this a region-organised competition — which is what an SM is — has no
            // organiser who can open a receipt.
            if (clubId == 0 && comp != null)
            {
                var regionCode = comp.GetValue<string>("regionalFederation") ?? "";
                if (!string.IsNullOrWhiteSpace(regionCode)
                    && await _auth.IsRegionalAdminForRegion(regionCode.Trim()))
                    return true;
            }
            return false;
        }
    }
}
