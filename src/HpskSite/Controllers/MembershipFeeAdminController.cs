using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
using HpskSite.Services.Mail;
using HpskSite.Services;
using HpskSite.Services.Ledger;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Club-admin surface for the membership-fee (medlemsavgift) feature: define fee
    /// categories per year, generate per-member charges, confirm payments, and send
    /// payment requests by email with the /medlemsavgift/{token} pay link.
    /// All endpoints are gated to club admins (or site admins) of the club in question.
    /// </summary>
    public class MembershipFeeAdminController : SurfaceController
    {
        private readonly MembershipFeeService _feeService;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ClubService _clubService;
        private readonly EmailService _emailService;
        private readonly ReplyContactResolver _replyContacts;
        private readonly LedgerAccessService _ledgerAccess;
        private readonly IDataProtector _protector;
        private readonly ILogger<MembershipFeeAdminController> _logger;

        // Same purpose string as the public MembershipFeeController — tokens must round-trip.
        private const string ProtectorPurpose = "Membership.FeeCharge.v1";

        public MembershipFeeAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            MembershipFeeService feeService,
            ClubMembershipService clubMembershipService,
            AdminAuthorizationService auth,
            IMemberService memberService,
            IMemberManager memberManager,
            ClubService clubService,
            EmailService emailService,
            ReplyContactResolver replyContacts,
            LedgerAccessService ledgerAccess,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<MembershipFeeAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _feeService = feeService;
            _clubMembershipService = clubMembershipService;
            _auth = auth;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _emailService = emailService;
            _replyContacts = replyContacts;
            _ledgerAccess = ledgerAccess;
            _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            _logger = logger;
        }

        // ── Overview ──────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetOverview(int clubId, int year)
        {
            // ⚠️ LÄSRÄTT räcker (styrelsen, revisorn) — ytan bor på ekonomisidan, där styrelsen läser
            //    och kassören skriver. Samma upplösning som sidan själv (LedgerAccessService).
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead)
                return Json(new { success = false, message = "Åtkomst nekad" });

            var categories = _feeService.GetCategories(clubId, year);
            var charges = _feeService.GetChargesForClubYear(clubId, year);

            return Json(new
            {
                success = true,
                categories = categories.Select(c => new
                {
                    c.Id,
                    c.MembershipType,
                    c.Label,
                    c.Amount
                }),
                paid = charges.Where(c => c.PaymentStatus == "Paid").Select(ChargeDto),
                unpaid = charges.Where(c => c.PaymentStatus != "Paid").Select(ChargeDto)
            });
        }

        private static object ChargeDto(MembershipFeeCharge c) => new
        {
            c.Id,
            c.MemberId,
            c.MemberName,
            c.MemberEmail,
            c.Amount,
            c.PaymentStatus,
            paymentSentDate = c.PaymentSentDate?.ToString("yyyy-MM-dd HH:mm"),
            c.PaymentSentBy,
            paidDate = c.PaidDate?.ToString("yyyy-MM-dd HH:mm"),
            hasEmail = !string.IsNullOrWhiteSpace(c.MemberEmail),
            covered = c.HouseholdCoveredByChargeId.HasValue
        };

        // ── Categories ────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCategory(int id, int clubId, int year,
            string membershipType, string label, decimal amount)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (string.IsNullOrWhiteSpace(membershipType))
                return Json(new { success = false, message = "Medlemstyp måste anges" });

            var cat = new MembershipFeeCategory
            {
                Id = id,
                ClubId = clubId,
                Year = year,
                MembershipType = membershipType.Trim(),
                Label = string.IsNullOrWhiteSpace(label) ? membershipType.Trim() : label.Trim(),
                Amount = amount
            };
            _feeService.SaveCategory(cat);
            return Json(new { success = true, data = new { cat.Id } });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCategory(int id, int clubId)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            _feeService.DeleteCategory(id);
            return Json(new { success = true });
        }

        // ── Charges ───────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateCharges(int clubId, int year)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            // Bill from the per-club membership records (ClubMembership). Exclude members who
            // have left or are deceased; membership type drives which fee category applies.
            var members = _clubMembershipService.GetForClub(clubId)
                .Where(cm => cm.MembershipStatus != "Utträdd" && cm.MembershipStatus != "Avliden")
                .Select(cm => new MemberFeeInput
                {
                    MemberId = cm.MemberId,
                    MembershipType = cm.MembershipType ?? "",
                    HouseholdId = cm.HouseholdId ?? "",
                    HouseholdPrimary = cm.HouseholdPrimary
                })
                .ToList();

            var created = _feeService.GenerateChargesForClub(clubId, year, members);
            return Json(new { success = true, created });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkPaid(int chargeId)
        {
            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return Json(new { success = false, message = "Avgiften hittades inte" });
            if (!await CanManageChargeAsync(charge))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var byId = await GetCurrentMemberIdAsync();
            _feeService.MarkPaid(chargeId, byId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkUnpaid(int chargeId)
        {
            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return Json(new { success = false, message = "Avgiften hittades inte" });
            if (!await CanManageChargeAsync(charge))
                return Json(new { success = false, message = "Åtkomst nekad" });

            _feeService.MarkUnpaid(chargeId);
            return Json(new { success = true });
        }

        // ── Payment links ─────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetPaymentLink(int chargeId)
        {
            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return Json(new { success = false, message = "Avgiften hittades inte" });
            if (!await CanManageChargeAsync(charge))
                return Json(new { success = false, message = "Åtkomst nekad" });

            // ⚠️ En kretsavgift vars länk kopieras räknas som SKICKAD: kretsen skickar den själv (klubben
            //    saknar ofta e-postadress). Annars står klubben kvar som "Inte skickad" och räknas med i
            //    nästa "Skicka avgiften" fast den redan fått sin räkning.
            if (charge.IsRegionFee) _feeService.MarkRegionRequestSent(chargeId);

            return Json(new { success = true, url = BuildPayUrl(chargeId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendPaymentRequests(int clubId, int year)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var clubName = _clubService.GetClubNameById(clubId) ?? "Klubben";
            var charges = _feeService.GetChargesForClubYear(clubId, year);

            var sent = 0;
            foreach (var charge in charges)
            {
                if (charge.PaymentStatus == "Paid") continue;
                if (charge.HouseholdCoveredByChargeId.HasValue) continue; // covered by the primary's charge
                if (string.IsNullOrWhiteSpace(charge.MemberEmail)) continue;

                var payUrl = BuildPayUrl(charge.Id);
                try
                {
                    // ⚠️ Svaret hör till KLUBBEN, som är den som kräver avgiften. Ett vanligt svar
                    // på ett avgiftskrav är "jag har flyttat, vill avsluta medlemskapet" — och det
                    // är klubbens kassör som kan göra något med det.
                    await _emailService.SendMembershipFeeRequestAsync(
                        charge.MemberEmail!, charge.MemberName ?? "medlem", clubName, year, charge.Amount, payUrl,
                        _replyContacts.ForClub(clubId));
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send membership fee request for charge {ChargeId}", charge.Id);
                }
            }

            return Json(new { success = true, sent });
        }

        // ══ KRETSAVGIFTEN ═══════════════════════════════════════════════════════════════════
        //
        // ⚠️ Samma motor, samma betalsida, samma MarkPaid — det här är bara kretsens taxa, förslaget
        //    per klubb och kravens rader. Behörigheten är kretsadmin för just den kretsen
        //    (sajtadmin ingår); klubbarna ser inte varandras krav.

        /// <summary>
        /// Kretsens taxa, förslaget per klubb och de krav som redan finns.
        ///
        /// <para><b>⚠️ Medlemsantalet är ett FÖRSLAG ur registret</b> (Stefans beslut 2026-09-23) —
        /// registret kan vara ofullständigt, så kretsen får rätta talet innan kravet skapas, och det
        /// rättade talet sparas på kravet.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionOverview(int regionId, int year)
        {
            var region = await AuthorizeRegionAsync(regionId, readOnly: true);
            if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });

            var (baseAmount, perMember) = _feeService.GetRegionRate(regionId, year);
            var charges = _feeService.GetChargesForRegionYear(regionId, year)
                .ToDictionary(c => c.PayerClubId ?? 0);

            var rows = RegionClubs(region.Value.Code).Select(club =>
            {
                var count = ActiveMemberCount(club.Id);
                var proposal = RegionFeeCalculator.Lines(year, baseAmount, perMember, count, null);
                charges.TryGetValue(club.Id, out var charge);

                return new
                {
                    clubId = club.Id,
                    clubName = club.Name,
                    hasEmail = !string.IsNullOrWhiteSpace(club.ContactEmail),
                    proposedCount = count,
                    proposedAmount = RegionFeeCalculator.Total(proposal),
                    charge = charge is null ? null : RegionChargeDto(charge)
                };
            }).OrderBy(r => r.clubName, StringComparer.CurrentCultureIgnoreCase).ToList();

            var all = charges.Values.ToList();
            return Json(new
            {
                success = true,
                regionName = region.Value.Name,
                year,
                rate = new { baseAmount, perMember },
                rows,
                // ⚠️ Krav på klubbar som inte längre finns i kretsen syns ändå — en klubb som bytt
                //    krets eller lagts ner kan ha en obetald avgift, och den får inte försvinna ur
                //    listan bara för att klubben gjorde det.
                orphans = all.Where(c => rows.All(r => r.clubId != (c.PayerClubId ?? 0))).Select(RegionChargeDto),
                totals = new
                {
                    charged = all.Sum(c => c.Amount),
                    paid = all.Where(c => c.PaymentStatus == "Paid").Sum(c => c.Amount),
                    outstanding = all.Where(c => c.PaymentStatus != "Paid").Sum(c => c.Amount),
                    count = all.Count,
                    paidCount = all.Count(c => c.PaymentStatus == "Paid")
                }
            });
        }

        private static object RegionChargeDto(MembershipFeeCharge c) => new
        {
            id = c.Id,
            clubId = c.PayerClubId,
            clubName = c.PayerClubName,
            amount = c.Amount,
            memberCount = c.MemberCount,
            status = c.PaymentStatus,
            requestSentDate = c.RequestSentDate?.ToString("yyyy-MM-dd"),
            paymentSentDate = c.PaymentSentDate?.ToString("yyyy-MM-dd"),
            paymentSentBy = c.PaymentSentBy,
            paidDate = c.PaidDate?.ToString("yyyy-MM-dd"),
            lines = c.Lines.Select(l => new
            {
                id = l.Id, kind = l.Kind, description = l.Description,
                quantity = l.Quantity, unitPrice = l.UnitPrice, amount = l.Amount
            })
        };

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveRegionRate([FromBody] RegionRateRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });
            if (await AuthorizeRegionAsync(request.RegionId) is null)
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (request.BaseAmount < 0 || request.PerMember < 0)
                return Json(new { success = false, message = "Beloppen kan inte vara negativa." });

            _feeService.SaveRegionRate(request.RegionId, request.Year, request.BaseAmount, request.PerMember);
            return Json(new
            {
                success = true,
                // ⚠️ Säger uttryckligen att redan skapade krav står kvar — annars tror kassören att
                //    en höjd taxa slog igenom på de räkningar klubbarna redan fått.
                message = "Taxan är sparad. Krav som redan skapats ändras inte."
            });
        }

        /// <summary>
        /// Skapar kraven för de klubbar som skickas med. <b>Bara klubbar i kretsen godtas</b> — listan
        /// kommer från klienten, och en krets får inte ställa ut ett krav på en annan krets klubb.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateRegionCharges([FromBody] RegionGenerateRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att skapa." });
            var region = await AuthorizeRegionAsync(request.RegionId);
            if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });

            var inRegion = RegionClubs(region.Value.Code).Select(c => c.Id).ToHashSet();
            var foreign = (request.Clubs ?? new()).Count(c => !inRegion.Contains(c.ClubId));
            if (foreign > 0)
                return Json(new { success = false, message = "Minst en av klubbarna hör inte till kretsen. Ingenting skapades." });

            var clubs = (request.Clubs ?? new())
                .Where(c => c.MemberCount >= 0 && (c.ManualAmount is null or >= 0))
                .ToList();

            var byId = await GetCurrentMemberIdAsync();
            var result = _feeService.GenerateRegionCharges(request.RegionId, request.Year, clubs, byId);

            var parts = new List<string> { $"Kretsavgift skapades för {result.Created} klubbar ({result.TotalAmount:N0} kr)." };
            if (result.SkippedExisting > 0) parts.Add($"{result.SkippedExisting} klubbar hade redan fått årets avgift och rördes inte.");
            // ⚠️ Nollkraven SÄGS. Utan det ser en klubb som inte fick något krav ut att ha glömts bort.
            if (result.SkippedZero > 0) parts.Add($"{result.SkippedZero} klubbar fick ingen avgift eftersom beloppet blev 0 kr.");

            return Json(new
            {
                success = true,
                created = result.Created,
                createdChargeIds = result.CreatedChargeIds,
                message = string.Join(" ", parts)
            });
        }

        /// <summary>
        /// Klubbens avgift för året — skapas oskickad om den inte finns, med registrets antal. Varje
        /// handling på en rad börjar här, så raden beter sig likadant före och efter utskicket.
        /// <b>Bara klubbar i kretsen godtas.</b>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EnsureRegionCharge([FromBody] RegionEnsureRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Ingen klubb angiven." });
            var region = await AuthorizeRegionAsync(request.RegionId);
            if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });
            if (!RegionClubs(region.Value.Code).Any(c => c.Id == request.ClubId))
                return Json(new { success = false, message = "Klubben hör inte till kretsen." });

            var (id, error) = _feeService.EnsureRegionCharge(request.RegionId, request.Year, request.ClubId,
                ActiveMemberCount(request.ClubId), await GetCurrentMemberIdAsync());
            return Json(error is null ? new { success = true, chargeId = id, message = (string?)null }
                                      : new { success = false, chargeId = (int?)null, message = (string?)error });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRegionChargeManualAmount([FromBody] RegionChargeLineRequest request)
        {
            if (request?.ManualAmount < 0) return Json(new { success = false, message = "Beloppet kan inte vara negativt." });
            return await EditRegionCharge(request?.ChargeId ?? 0, () =>
                _feeService.SetRegionChargeManualAmount(request!.ChargeId, request.ManualAmount));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddRegionChargeLine([FromBody] RegionChargeLineRequest request)
        {
            var byId = await GetCurrentMemberIdAsync();
            return await EditRegionCharge(request?.ChargeId ?? 0, () =>
                _feeService.AddRegionChargeLine(request!.ChargeId, request.Description ?? "", request.Amount, byId));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveRegionChargeLine([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, () =>
                _feeService.RemoveRegionChargeLine(request!.ChargeId, request.LineId));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRegionChargeMemberCount([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, () =>
                _feeService.SetRegionChargeMemberCount(request!.ChargeId, request.MemberCount));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRegionCharge([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, () =>
                _feeService.DeleteRegionCharge(request!.ChargeId));

        private async Task<IActionResult> EditRegionCharge(int chargeId, Func<string?> edit)
        {
            var charge = chargeId == 0 ? null : _feeService.GetCharge(chargeId);
            if (charge is null || !charge.IsRegionFee)
                return Json(new { success = false, message = "Kravet hittades inte." });
            if (!await CanManageChargeAsync(charge))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var error = edit();
            return Json(error is null ? new { success = true, message = (string?)null } : new { success = false, message = (string?)error });
        }

        /// <summary>
        /// Mejlar betalkravet till varje klubb med ett obetalt krav.
        ///
        /// <para><b>⚠️ Svaret räknar det som FAKTISKT skickades</b> och namnger klubbarna som inte
        /// nåddes — både de utan kontaktadress och de där mejlet inte gick iväg. Ett "skickat" om ett
        /// mejl som aldrig gick är lögnen den här kodbasen redan fått rätta en gång.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendRegionPaymentRequests([FromBody] RegionSendRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att skicka." });
            var region = await AuthorizeRegionAsync(request.RegionId);
            if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });

            var reply = _replyContacts.ForRegion(request.RegionId);
            var sent = new List<string>();
            var noEmail = new List<string>();
            var failed = new List<string>();

            // ⚠️ Tre urval, och de får inte blandas ihop:
            //    ChargeIds  = bara de avgifterna (t.ex. "skicka till den här klubben"),
            //    "unsent"   = de som inte gått ut än (knappen "Skicka avgiften"),
            //    "reminder" = de som gått ut men inte betalats (knappen "Påminn").
            //    Utan något av dem mejlas alla obetalda.
            var only = request.ChargeIds is { Count: > 0 } ? request.ChargeIds.ToHashSet() : null;

            foreach (var charge in _feeService.GetChargesForRegionYear(request.RegionId, request.Year))
            {
                if (charge.PaymentStatus == "Paid") continue;
                if (only is not null && !only.Contains(charge.Id)) continue;
                if (request.Mode == "unsent" && charge.IsRequestSent) continue;
                if (request.Mode == "reminder" && !charge.IsRequestSent) continue;
                // En avgift på 0 kr är ingen räkning och går aldrig ut.
                if (charge.Amount <= 0) continue;

                var club = _clubService.GetClubById(charge.PayerClubId ?? 0);
                var name = charge.PayerClubName ?? club?.Name ?? $"Klubb {charge.PayerClubId}";

                if (string.IsNullOrWhiteSpace(club?.ContactEmail)) { noEmail.Add(name); continue; }

                bool ok;
                try
                {
                    ok = await _emailService.SendRegionFeeRequestAsync(
                        club.ContactEmail!, name, region.Value.Name, charge.Year,
                        charge.Lines.Select(l => (l.Description, l.Amount)).ToList(),
                        charge.Amount, BuildPayUrl(charge.Id), reply);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kretsavgiftens betalkrav {ChargeId} kunde inte skickas.", charge.Id);
                    ok = false;
                }

                (ok ? sent : failed).Add(name);
                // ⚠️ Bara ett mejl som FAKTISKT gick räknas som skickat. Annars står klubben som
                //    "Skickad" utan att ha fått något.
                if (ok) _feeService.MarkRegionRequestSent(charge.Id);
            }

            var parts = new List<string> { $"Betalningsuppmaningen mejlades till {sent.Count} klubbar." };
            if (noEmail.Count > 0) parts.Add($"Saknar kontaktadress: {string.Join(", ", noEmail)}.");
            if (failed.Count > 0) parts.Add($"Kunde inte skickas: {string.Join(", ", failed)}.");

            return Json(new
            {
                success = true,
                sent = sent.Count,
                noEmail,
                failed,
                message = string.Join(" ", parts)
            });
        }

        /// <summary>
        /// Visar betalkravsmejlet för ett krav precis som det skulle skickas — kuvert, ämne, kropp
        /// och svarsfot — utan att skicka något. Byggs av samma kod som utskicket.
        ///
        /// <para>⚠️ Visar sig som en egen sida (öppnas i ny flik), så mejlets egna stilar inte
        /// blandas med adminpanelens. Kuvertet står i en ram ovanför, tydligt skilt från mejlet.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PreviewRegionPaymentRequest(int chargeId)
        {
            var charge = chargeId == 0 ? null : _feeService.GetCharge(chargeId);
            if (charge is null || !charge.IsRegionFee)
                return Content("Kravet hittades inte.");
            var region = await AuthorizeRegionAsync(charge.RegionId ?? 0, readOnly: true);
            if (region is null) return Content("Åtkomst nekad.");

            var club = _clubService.GetClubById(charge.PayerClubId ?? 0);
            var name = charge.PayerClubName ?? club?.Name ?? $"Klubb {charge.PayerClubId}";
            var to = string.IsNullOrWhiteSpace(club?.ContactEmail) ? "" : club!.ContactEmail!;

            var mail = _emailService.PreviewRegionFeeRequest(
                to, name, region.Value.Name, charge.Year,
                charge.Lines.Select(l => (l.Description, l.Amount)).ToList(),
                charge.Amount, BuildPayUrl(charge.Id), _replyContacts.ForRegion(region.Value.Id));

            var enc = new Func<string?, string>(s => System.Net.WebUtility.HtmlEncode(s ?? ""));
            var envelope =
                "<div style=\"font-family:Arial,sans-serif;font-size:13px;max-width:560px;margin:16px auto;"
              + "padding:12px 16px;border:1px dashed #6b7280;border-radius:6px;background:#fff;color:#333\">"
              + "<div style=\"font-weight:bold;margin-bottom:6px\">Förhandsvisning — inget har skickats</div>"
              + $"<div><b>Från:</b> {enc(mail.FromName)} &lt;{enc(mail.FromAddress)}&gt;</div>"
              + $"<div><b>Svar till:</b> {(mail.ReplyTo is null ? "<i>ingen svarsadress</i>" : enc(mail.ReplyTo))}</div>"
              + $"<div><b>Till:</b> {(to.Length == 0 ? "<i style=\"color:#b45309\">klubben saknar kontaktadress — mejlet skickas inte</i>" : enc(to))}</div>"
              + $"<div><b>Ämne:</b> {enc(mail.Subject)}</div>"
              + (charge.PaymentStatus == "Paid"
                    ? "<div style=\"color:#b45309;margin-top:6px\">Kravet är betalt och ingår inte i ett utskick.</div>" : "")
              + "</div>";

            // Kuvertet läggs in direkt efter <body>, så mejlets eget dokument står orört under det.
            var html = mail.Html;
            var bodyAt = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            var bodyEnd = bodyAt < 0 ? -1 : html.IndexOf('>', bodyAt);
            html = bodyEnd < 0 ? envelope + html : html.Insert(bodyEnd + 1, envelope);

            return Content(html, "text/html; charset=utf-8");
        }

        /// <summary>
        /// Får den inloggade hantera kravet? Klubbadmin för en medlemsavgift, kretsadmin för en
        /// kretsavgift. <b>EN fråga för alla kravets endpoints</b>, så att de två formerna inte kan få
        /// olika regler på olika knappar.
        /// </summary>
        private async Task<bool> CanManageChargeAsync(MembershipFeeCharge charge)
            => charge.IsRegionFee
                ? await AuthorizeRegionAsync(charge.RegionId ?? 0) is not null
                : await _auth.IsClubAdminForClub(charge.ClubId);

        /// <summary>
        /// Kretsnoden, om den inloggade är kretsadmin för den (sajtadmin ingår). <b>Koden läses ur
        /// noden</b> och skickas som den står: <c>IsRegionalAdminForRegion</c> jämför gruppnamnet
        /// EXAKT, och en gemenad kod hade nekats och sett ut som ett behörighetsfel.
        /// </summary>
        private async Task<(int Id, string Code, string Name)?> AuthorizeRegionAsync(int regionId, bool readOnly = false)
        {
            if (regionId <= 0) return null;

            var node = Services.ContentService.GetById(regionId);
            if (node is null || node.ContentType.Alias != "regionalPage") return null;

            var code = node.GetValue<string>("regionCode") ?? "";
            // ⚠️ Skriva = kretsadmin (samma som ekonomisidans skrivrätt). LÄSA = ekonomisidans
            //    läsrätt: även kretsens styrelse och revisor, som ser avgifterna men inte ändrar dem.
            var allowed = readOnly
                ? (await _ledgerAccess.ResolveAsync(DocumentOwnerType.Region, regionId)).CanRead
                : await _auth.IsRegionalAdminForRegion(code);
            if (!allowed) return null;

            return (node.Id, code, node.GetValue<string>("regionName") ?? node.Name ?? "Kretsen");
        }

        private List<ClubInfo> RegionClubs(string regionCode)
            => _auth.GetClubsInRegions(new List<string> { regionCode })
                .Distinct()
                .Select(id => _clubService.GetClubById(id))
                .Where(c => c is not null)
                .Select(c => c!)
                .ToList();

        /// <summary>
        /// Registrets förslag på medlemsantal: klubbens medlemskap utom de som utträtt eller avlidit —
        /// samma regel som klubbens egen avgiftskörning, så de två inte räknar olika på samma register.
        /// </summary>
        private int ActiveMemberCount(int clubId)
            => _clubMembershipService.GetForClub(clubId)
                .Count(cm => cm.MembershipStatus != "Utträdd" && cm.MembershipStatus != "Avliden");

        // ── Helpers ───────────────────────────────────────────────────

        private string BuildPayUrl(int chargeId)
        {
            var token = _protector.Protect(chargeId.ToString());
            return $"{Request.Scheme}://{Request.Host}/medlemsavgift/{Uri.EscapeDataString(token)}";
        }

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            var data = _memberService.GetByEmail(current.Email ?? "");
            return data?.Id ?? 0;
        }

    }

    public class RegionRateRequest
    {
        public int RegionId { get; set; }
        public int Year { get; set; }
        public decimal BaseAmount { get; set; }
        public decimal PerMember { get; set; }
    }

    public class RegionEnsureRequest
    {
        public int RegionId { get; set; }
        public int Year { get; set; }
        public int ClubId { get; set; }
    }

    public class RegionSendRequest
    {
        public int RegionId { get; set; }
        public int Year { get; set; }

        /// <summary>"unsent" = inte skickade än, "reminder" = skickade men obetalda, tomt = alla obetalda.</summary>
        public string? Mode { get; set; }

        /// <summary>Tom = alla obetalda (påminnelse). Satt = bara de här avgifterna.</summary>
        public List<int>? ChargeIds { get; set; }
    }

    public class RegionGenerateRequest
    {
        public int RegionId { get; set; }
        public int Year { get; set; }
        public List<RegionFeeClubInput>? Clubs { get; set; }
    }

    public class RegionChargeLineRequest
    {
        public int ChargeId { get; set; }
        public int LineId { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public int MemberCount { get; set; }

        /// <summary>Eget belopp på en oskickad avgift. Null = tillbaka till taxan.</summary>
        public decimal? ManualAmount { get; set; }
    }
}
