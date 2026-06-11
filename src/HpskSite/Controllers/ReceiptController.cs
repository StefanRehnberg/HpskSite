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

        public ReceiptController(
            ReceiptModelBuilder builder,
            IContentService contentService,
            IMemberManager memberManager,
            AdminAuthorizationService auth)
        {
            _builder = builder;
            _contentService = contentService;
            _memberManager = memberManager;
            _auth = auth;
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

            // Authorization: the buyer themselves, or staff for the hosting competition.
            var isOwner = int.TryParse(current.Id, out var currentId) && currentId == model.MemberId;
            if (!isOwner && !await IsStaffForCompetition(model.CompetitionId))
                return Forbid();

            return View("~/Views/Receipt.cshtml", model);
        }

        private async Task<bool> IsStaffForCompetition(int competitionId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (await _auth.IsCompetitionManager(competitionId)) return true;
            var comp = _contentService.GetById(competitionId);
            var clubId = comp?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0 && (await _auth.IsClubAdminForClub(clubId) || await _auth.IsSkjutledareForClub(clubId)))
                return true;
            return false;
        }
    }
}
