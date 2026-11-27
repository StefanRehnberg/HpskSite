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
        private readonly RegionFeeCompetitionService _regionCompetitions;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ClubService _clubService;
        private readonly EmailService _emailService;
        private readonly ReplyContactResolver _replyContacts;
        private readonly LedgerAccessService _ledgerAccess;
        private readonly MembershipFeeAccess _feeAccess;
        private readonly MembershipAccess _memberAccess;
        private readonly ClubFeeInputService _feeInput;
        private readonly ClubFeeSettingsService _feeSettings;
        private readonly MembershipAgeLimits _ageLimits;
        private readonly MembershipAgeProposalService _ageProposals;
        private readonly ClubFeeChangeLogService _feeLog;
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
            RegionFeeCompetitionService regionCompetitions,
            ClubMembershipService clubMembershipService,
            AdminAuthorizationService auth,
            IMemberService memberService,
            IMemberManager memberManager,
            ClubService clubService,
            EmailService emailService,
            ReplyContactResolver replyContacts,
            LedgerAccessService ledgerAccess,
            MembershipFeeAccess feeAccess,
            MembershipAccess memberAccess,
            ClubFeeInputService feeInput,
            ClubFeeSettingsService feeSettings,
            MembershipAgeLimits ageLimits,
            MembershipAgeProposalService ageProposals,
            ClubFeeChangeLogService feeLog,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<MembershipFeeAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _feeService = feeService;
            _regionCompetitions = regionCompetitions;
            _clubMembershipService = clubMembershipService;
            _auth = auth;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _emailService = emailService;
            _replyContacts = replyContacts;
            _ledgerAccess = ledgerAccess;
            _feeAccess = feeAccess;
            _memberAccess = memberAccess;
            _feeInput = feeInput;
            _feeSettings = feeSettings;
            _ageLimits = ageLimits;
            _ageProposals = ageProposals;
            _feeLog = feeLog;
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

            // ⚠️⚠️ EN RAD PER MEDLEM, med förslaget från SAMMA regel som skapandet (ClubFeeProposal).
            //    Listan ska se likadan ut före och efter utskicket — samma form som kretsavgiften.
            var members = ActiveFeeMembers(clubId);
            var proposal = ClubFeeProposal.Build(categories, members);
            // ⚠️ Samma beräkning som skapandet (ClubFeeCalculator.ForProposal): listan visar summan inklusive tillägg/avdrag.
            var (feeParts, feePartTypes) = _feeService.GetParts(clubId, year);
            var calcs = ClubFeeCalculator.ForProposal(proposal, categories, members, feeParts, feePartTypes);
            var chargeByMember = charges.GroupBy(c => c.MemberId).ToDictionary(g => g.Key, g => g.First());
            var info = MemberInfo(members.Select(m => m.MemberId).Concat(charges.Select(c => c.MemberId)).Distinct());
            var feeEmails = _feeService.GetPayerEmails(MembershipFeeIssuer.Club, clubId);

            var rows = members.Select(m =>
            {
                proposal.TryGetValue(m.MemberId, out var p);
                chargeByMember.TryGetValue(m.MemberId, out var c);
                info.TryGetValue(m.MemberId, out var mi);
                string? primaryName = null;
                if (p?.PrimaryMemberId is int pm && info.TryGetValue(pm, out var pmi)) primaryName = pmi.Name;
                return new
                {
                    memberId = m.MemberId,
                    name = mi.Name ?? c?.MemberName ?? $"Medlem {m.MemberId}",
                    // Den adress avgiften mejlas till: klubbens egen för avgifter, annars medlemmens.
                    email = feeEmails.GetValueOrDefault(m.MemberId) ?? mi.Email ?? "",
                    emailIsOwn = feeEmails.ContainsKey(m.MemberId),
                    defaultEmail = mi.Email ?? "",
                    hasEmail = !string.IsNullOrWhiteSpace(feeEmails.GetValueOrDefault(m.MemberId) ?? mi.Email),
                    membershipType = m.MembershipType ?? "",
                    kind = (p?.Kind ?? ClubFeeKind.NoType).ToString(),
                    proposedAmount = p is null ? 0m : calcs.GetValueOrDefault(p.MemberId, FeeCalculation.None).Total,
                    // Uppdelningen bakom summan (grund, tillägg, avdrag, justering) — samma beräkning som summan.
                    lines = (p is null ? FeeCalculation.None : calcs.GetValueOrDefault(p.MemberId, FeeCalculation.None))
                        .Lines.Select(l => new { kind = l.Kind, text = l.Description, amount = l.Amount }),
                    homeClub = m.HomeClub.ToString(),   // Ours / Other / Unknown (huvudklubb saknas)
                    optionIds = m.OptionIds.ToList(),
                    coveredBy = primaryName,
                    charge = c is null ? null : ClubChargeDto(c)
                };
            })
            .OrderBy(x => x.name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

            return Json(new
            {
                success = true,
                rows,
                // Avgifter på medlemmar som inte längre finns i registret (utträdda) syns ändå —
                // en obetald avgift får inte försvinna bara för att medlemmen gick ur.
                orphans = charges.Where(c => members.All(m => m.MemberId != c.MemberId)).Select(c => new
                {
                    memberId = c.MemberId, name = c.MemberName ?? $"Medlem {c.MemberId}",
                    hasEmail = !string.IsNullOrWhiteSpace(c.MemberEmail), charge = ClubChargeDto(c)
                }),
                categories = categories.Select(c => new
                {
                    c.Id,
                    c.MembershipType,
                    c.Label,
                    c.Amount,
                    c.MemberSelectable
                }),
                paid = charges.Where(c => c.PaymentStatus == "Paid").Select(ChargeDto),
                unpaid = charges.Where(c => c.PaymentStatus != "Paid").Select(ChargeDto)
            });
        }

        private static object ClubChargeDto(MembershipFeeCharge c) => new
        {
            id = c.Id,
            amount = c.Amount,
            status = c.PaymentStatus,
            covered = c.HouseholdCoveredByChargeId.HasValue,
            requestSentDate = SwedishTime.FromUtc(c.RequestSentDate)?.ToString("yyyy-MM-dd"),
            paymentSentDate = SwedishTime.FromUtc(c.PaymentSentDate)?.ToString("yyyy-MM-dd"),
            paidDate = SwedishTime.FromUtc(c.PaidDate)?.ToString("yyyy-MM-dd"),
            // Medlemmen väljer medlemstyp på betalsidan (0 kr, ingen kategori ännu).
            needsTypeChoice = c.NeedsTypeChoice,
            // ⚠️ Kassören ska SE att medlemmen valt själv — ett förtroendesystem, inte ett osynligt.
            memberChosenType = c.MemberChosenType,
            memberChosenAt = SwedishTime.FromUtc(c.MemberChosenAt)?.ToString("yyyy-MM-dd")
        };

        // Indata till avgiften (aktiva medlemmar, namn, e-post) kommer från ClubFeeInputService — EN källa.
        private List<MemberFeeInput> ActiveFeeMembers(int clubId) => _feeInput.ActiveFeeMembers(clubId);
        private Dictionary<int, (string? Name, string? Email)> MemberInfo(IEnumerable<int> ids) => _feeInput.MemberInfo(ids);

        private static object ChargeDto(MembershipFeeCharge c) => new
        {
            c.Id,
            c.MemberId,
            c.MemberName,
            c.MemberEmail,
            c.Amount,
            c.PaymentStatus,
            paymentSentDate = SwedishTime.FromUtc(c.PaymentSentDate)?.ToString("yyyy-MM-dd HH:mm"),
            c.PaymentSentBy,
            paidDate = SwedishTime.FromUtc(c.PaidDate)?.ToString("yyyy-MM-dd HH:mm"),
            hasEmail = !string.IsNullOrWhiteSpace(c.MemberEmail),
            covered = c.HouseholdCoveredByChargeId.HasValue
        };

        // ── Categories ────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben): kassör, delegat, sajtadmin; styrelse och revisor läser bara")]
        public async Task<IActionResult> SaveCategory(int id, int clubId, int year,
            string membershipType, string label, decimal amount, string? memberSelectable = null)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (string.IsNullOrWhiteSpace(membershipType))
                return Json(new { success = false, message = "Medlemstyp måste anges" });

            // ⚠️ "Medlemmen kan välja" som STRÄNG: "1"/"0" binder inte till bool i ASP.NET Core och
            //    faller tyst tillbaka på default. Utelämnat = behåll (ändring) eller förvalet (ny typ).
            bool selectable;
            if (memberSelectable is null)
            {
                var existing = id > 0 ? _feeService.GetCategories(clubId, year).FirstOrDefault(c => c.Id == id) : null;
                selectable = existing?.MemberSelectable ?? MembershipFeeCategory.DefaultMemberSelectable(membershipType);
            }
            else selectable = memberSelectable is "1" or "true" or "on";

            // ⚠️ ÅRET HÖR TILL RADEN, inte till anropet (K10): en befintlig rad ändras i sitt eget år och rensningen gäller det året.
            //    Raden ska dessutom tillhöra klubben i anropet — annars sa tjänsten tyst "success" för en annan klubbs kategori.
            if (id > 0)
            {
                var rowYear = _feeService.GetCategoryYear(id);
                if (rowYear is not int ry || !_feeService.GetCategories(clubId, ry).Any(c => c.Id == id))
                    return Json(new { success = false, message = "Avgiften hittades inte." });
                year = ry;
            }
            // ⚠️ En avgift per typ och år (K7). Det unika indexet tas i prod efter en dubblettkontroll; servern säger det först.
            var typeTrim = membershipType.Trim();
            var typeName = _feeSettings.GetTypesWithCounts(clubId).Select(t => t.Type).FirstOrDefault(t => MembershipTypeKeys.Same(t.Key, typeTrim))?.Name ?? typeTrim;
            var yearCats = _feeService.GetCategories(clubId, year);
            if (yearCats.Any(c => c.Id != id && string.Equals((c.MembershipType ?? "").Trim(), typeTrim, StringComparison.OrdinalIgnoreCase)))
                return Json(new { success = false, message = $"{typeName} har redan en avgift {year}. Ändra den i stället." });
            var beforeAmount = id > 0 ? yearCats.FirstOrDefault(c => c.Id == id)?.Amount : null;

            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });

            var cat = new MembershipFeeCategory
            {
                Id = id,
                ClubId = clubId,
                Year = year,
                MembershipType = typeTrim,
                Label = string.IsNullOrWhiteSpace(label) ? typeTrim : label.Trim(),
                Amount = amount,
                MemberSelectable = selectable
            };
            _feeService.SaveCategory(cat);
            // ⚠️ Oskickade avgifter följer inställningen — ingen medlem har fått dem än.
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, year, ClubFeeChangeEntity.Category, typeTrim, ClubFeeChangeAction.Saved,
                beforeAmount is null ? $"Ny grundavgift {typeName} {year}: {amount:0.##} kr"
                    : $"Grundavgift {typeName} {year}: {beforeAmount:0.##} → {amount:0.##} kr");
            return Json(new { success = true, data = new { cat.Id }, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        // Ändringsloggen (K4): vem, när, vad, och på vilken grund (kassör, delegat, admin utan kassör, sajtadmin, klubbadmin).
        private async Task LogAsync(int clubId, int? year, string entity, string? key, string action, string detail)
        {
            var access = await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId);
            var basis = access.Basis != LedgerAccessBasis.None ? access.Basis.ToString() : "ClubAdmin";
            _feeLog.Record(clubId, year, entity, key, action, detail, await GetCurrentMemberIdAsync(), basis);
        }

        // Följderna av en inställningsändring, ur samma kalkyl som listan (ClubFeeConsequences). Belopp bara för den som får skriva avgifter.
        private const string SettingsBusy = "Avgifterna skickas eller skapas just nu. Försök igen om en stund.";

        private async Task<object> ConsequencesAsync(int clubId, int year, int cleared)
        {
            try
            {
                var categories = _feeService.GetCategories(clubId, year);
                var members = ActiveFeeMembers(clubId);
                var proposal = ClubFeeProposal.Build(categories, members);
                var (parts, partTypes) = _feeService.GetParts(clubId, year);
                var calcs = ClubFeeCalculator.ForProposal(proposal, categories, members, parts, partTypes);
                var c = ClubFeeConsequences.Compute(cleared, _feeService.GetChargesForClubYear(clubId, year), proposal, calcs);
                var canSeeAmounts = await _feeAccess.CanWriteAsync(clubId);
                return new { year, unsentCleared = c.UnsentCleared, sentDiverging = (int?)c.SentDiverging, paidDiverging = (int?)c.PaidDiverging,
                             divergingSum = canSeeAmounts ? (decimal?)c.DivergingSum : null };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Följderna av en inställningsändring kunde inte räknas för klubb {ClubId} {Year}", clubId, year);
                return new { year, unsentCleared = cleared, sentDiverging = (int?)null, paidDiverging = (int?)null, divergingSum = (decimal?)null };
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben) + kategorin tillhör klubben")]
        public async Task<IActionResult> DeleteCategory(int id, int clubId)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var year = _feeService.GetCategoryYear(id);
            // ⚠️ Kategorin måste höra till klubben i anropet — annars raderar skrivrätten i klubb A klubb B:s taxa.
            if (year is not int y || !_feeService.GetCategories(clubId, y).Any(c => c.Id == id))
                return Json(new { success = false, message = "Kategorin hittades inte." });
            using var lease = _feeService.TryLockSettings(clubId, y);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var gone = _feeService.GetCategories(clubId, y).FirstOrDefault(c => c.Id == id);
            _feeService.DeleteCategory(id);
            var cleared = _feeService.ClearUnsentClubCharges(clubId, y);
            await LogAsync(clubId, y, ClubFeeChangeEntity.Category, gone?.MembershipType, ClubFeeChangeAction.Deleted,
                $"Grundavgift {gone?.MembershipType} {y} borttagen ({gone?.Amount:0.##} kr)");
            return Json(new { success = true, consequences = await ConsequencesAsync(clubId, y, cleared) });
        }

        // ── Kopiera från ett tidigare år ──────────────────────────────

        /// <summary>Tidigare år (nyast först, högst sex bakåt) där klubben har grundavgifter eller tillägg att kopiera.</summary>
        private List<int> YearsWithRules(int clubId, int year)
        {
            var years = new List<int>();
            for (var y = year - 1; y >= year - 6 && y >= 2000; y--)
                if (_feeService.GetCategories(clubId, y).Count > 0 || _feeService.GetParts(clubId, y).Parts.Count > 0) years.Add(y);
            return years;
        }

        /// <summary>
        /// Vad som kan kopieras från ett tidigare år till <paramref name="year"/>: grundavgifter och tillägg/avdrag, var och en märkt
        /// med om målåret redan har den (då hoppas den över). Skriver ingenting. Läsrätt räcker.
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("LedgerAccess.CanRead(klubben); skriver inget, läser klubbens egna tidigare år")]
        public async Task<IActionResult> PreviewCopyYear(int clubId, int year, int? fromYear = null)
        {
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead)
                return Json(new { success = false, message = "Åtkomst nekad" });
            var years = YearsWithRules(clubId, year);
            if (years.Count == 0) return Json(new { success = true, fromYear = (int?)null, years });
            var from = fromYear ?? years[0];
            if (!years.Contains(from)) return Json(new { success = false, message = $"Det finns inga avgiftsregler för {from} att kopiera." });

            var types = _feeSettings.GetTypesWithCounts(clubId).Select(t => t.Type).ToList();
            var targetCats = _feeService.GetCategories(clubId, year);
            var (srcParts, srcTypes) = _feeService.GetParts(clubId, from);
            var (dstParts, dstTypes) = _feeService.GetParts(clubId, year);
            return Json(new
            {
                success = true,
                fromYear = (int?)from,
                years,
                categories = _feeService.GetCategories(clubId, from).Select(c =>
                {
                    var t = types.FirstOrDefault(x => MembershipTypeKeys.Same(x.Key, c.MembershipType));
                    return new
                    {
                        type = c.MembershipType, typeName = t?.Name ?? c.MembershipType, c.Amount, c.MemberSelectable,
                        typeGone = t is null || !t.IsActive,
                        exists = targetCats.Any(x => MembershipTypeKeys.Same(x.MembershipType, c.MembershipType))
                    };
                }),
                parts = srcParts.Select(p => new
                {
                    p.Id, p.Label, p.Amount, p.Condition, p.OptionId, optionName = p.OptionName,
                    typeKeys = srcTypes[p.Id].ToList(),
                    exists = dstParts.Any(d => ClubFeeSettingsRules.IsSamePart(p, srcTypes[p.Id], d, dstTypes[d.Id]))
                })
            });
        }

        /// <summary>
        /// Kopierar valda grundavgifter och tillägg/avdrag från ett tidigare år. Beloppen kopieras som de är (kassören ändrar dem sedan);
        /// det som målåret redan har hoppas över, aldrig skrivs över. Varje rad går genom samma skrivväg och prövning som en vanlig sparning.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben); raderna läses ur klubbens eget tidigare år och skrivs bara om målåret saknar dem")]
        public async Task<IActionResult> CopyFromYear(int clubId, int year, int fromYear, string[]? categoryTypes, int[]? partIds)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (fromYear < 2000 || fromYear >= year)
                return Json(new { success = false, message = "Välj ett tidigare år att kopiera från." });

            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });

            var types = _feeSettings.GetTypesWithCounts(clubId).Select(t => t.Type).ToList();
            var skipped = new List<string>();
            int copiedCats = 0, copiedParts = 0;

            var wantedTypes = categoryTypes ?? Array.Empty<string>();
            foreach (var src in _feeService.GetCategories(clubId, fromYear).Where(c => wantedTypes.Any(w => MembershipTypeKeys.Same(w, c.MembershipType))))
            {
                var t = types.FirstOrDefault(x => MembershipTypeKeys.Same(x.Key, src.MembershipType));
                if (t is null || !t.IsActive) { skipped.Add($"Grundavgift {src.MembershipType}: typen finns inte längre"); continue; }
                if (_feeService.GetCategories(clubId, year).Any(x => MembershipTypeKeys.Same(x.MembershipType, src.MembershipType)))
                { skipped.Add($"Grundavgift {t.Name}: finns redan {year}"); continue; }
                _feeService.SaveCategory(new MembershipFeeCategory
                {
                    Id = 0, ClubId = clubId, Year = year, MembershipType = src.MembershipType, Label = src.Label,
                    Amount = src.Amount, MemberSelectable = src.MemberSelectable
                });
                copiedCats++;
                await LogAsync(clubId, year, ClubFeeChangeEntity.Category, src.MembershipType, ClubFeeChangeAction.Saved,
                    $"Kopierad från {fromYear}: grundavgift {t.Name} {year}: {src.Amount:0.##} kr");
            }

            var wantedParts = new HashSet<int>(partIds ?? Array.Empty<int>());
            var (srcParts, srcTypes) = _feeService.GetParts(clubId, fromYear);
            foreach (var p in srcParts.Where(p => wantedParts.Contains(p.Id)))
            {
                var (dstParts, dstTypes) = _feeService.GetParts(clubId, year);
                if (dstParts.Any(d => ClubFeeSettingsRules.IsSamePart(p, srcTypes[p.Id], d, dstTypes[d.Id])))
                { skipped.Add($"{p.Label}: finns redan {year}"); continue; }
                var (newId, error) = _feeSettings.SavePart(clubId, year, 0, p.Label, p.Amount, p.Condition, p.OptionId, srcTypes[p.Id].ToList());
                if (error is not null) { skipped.Add($"{p.Label}: {error}"); continue; }
                copiedParts++;
                await LogAsync(clubId, year, ClubFeeChangeEntity.Part, newId.ToString(), ClubFeeChangeAction.Saved,
                    $"Kopierat från {fromYear}: tillägg/avdrag {p.Label} {year}: {p.Amount:+0.##;−0.##} kr ({p.Condition})");
            }

            var cleared = (copiedCats + copiedParts) > 0 ? _feeService.ClearUnsentClubCharges(clubId, year) : 0;
            return Json(new { success = true, copiedCategories = copiedCats, copiedParts, skipped, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        // ── Tillägg/avdrag och tillval (steg 3) ───────────────────────

        /// <summary>Klubbens tillägg/avdrag och tillval för året. Läsrätt räcker (styrelsen, revisorn).</summary>
        [HttpGet]
        public async Task<IActionResult> GetFeeSettings(int clubId, int year)
        {
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead)
                return Json(new { success = false, message = "Åtkomst nekad" });

            var (parts, partTypes) = _feeService.GetParts(clubId, year);
            // "Träffar N av M": SAMMA regel som avgiften (ClubFeeCalculator.Applies) över klubbens aktiva medlemmar.
            // Ett fel här får inte fälla inställningarna: utan tal visar sidan bara ingen träffrad.
            List<MemberFeeInput>? people = null;
            try { people = ActiveFeeMembers(clubId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Träffarna kunde inte räknas för klubb {ClubId}", clubId); }
            return Json(new
            {
                success = true,
                memberTotal = people?.Count,
                parts = parts.Select(p => new
                {
                    p.Id, p.Label, p.Amount, p.Condition, p.OptionId, p.SortOrder,
                    typeKeys = partTypes[p.Id].ToList(),   // tom lista = alla medlemstyper
                    appliesTo = people?.Count(m => ClubFeeCalculator.Applies(p, new HashSet<string>(partTypes[p.Id]), m))
                }),
                options = _feeSettings.GetOptions(clubId, includeInactive: true).Select(o => new
                {
                    o.Id, o.Name, o.SystemKey, o.IsActive,
                    members = people?.Count(m => m.OptionIds.Contains(o.Id))
                }),
                // Förslag när klubben lägger till ett förbund som tillval (officiella namnet = SystemKey).
                federations = HpskSite.Models.ForeningsintygDocument.AllaForbund
                    .Where(f => f != HpskSite.Models.ForeningsintygDocument.ForbundAnnat)
            });
        }

        /// <summary>
        /// Exempelräkningen: vad skulle en person (eller en familj) betala enligt årets regler? Skriver ingenting.
        /// Personerna kommer som parallella listor: <c>type</c>, <c>home</c> (ours|other|tom), <c>options</c> ("7,8").
        /// Samma väg som listan och skapandet (<see cref="ClubFeePreview"/>). Läsrätt räcker; belopp ser bara den som får läsa.
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("LedgerAccess.CanRead(klubben); skriver inget, räknar på syntetiska personer")]
        public async Task<IActionResult> PreviewFee(int clubId, int year, string[]? type, string[]? home, string[]? options, int? memberId = null)
        {
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead)
                return Json(new { success = false, message = "Åtkomst nekad" });

            var types = type ?? Array.Empty<string>();
            var people = new List<MemberFeeInput>();
            string? personName = null;
            if (memberId is > 0)
            {
                // En riktig medlem i DEN HÄR klubben (aldrig ett id rakt av): typ, huvudklubb och förbund som i listan.
                var real = ActiveFeeMembers(clubId).FirstOrDefault(m => m.MemberId == memberId.Value);
                if (real is null) return Json(new { success = false, message = "Medlemmen hittades inte i klubben." });
                people.Add(real);
                personName = MemberInfo(new[] { real.MemberId }).TryGetValue(real.MemberId, out var info) ? info.Name : null;
                types = new[] { real.MembershipType ?? "" };
            }
            else if (types.Length == 0 || types.Length > 12)
                return Json(new { success = false, message = "Ange mellan 1 och 12 personer." });

            for (var i = 0; i < types.Length && people.Count < types.Length && memberId is not > 0; i++)
            {
                var ids = new List<int>();
                var raw = options is not null && i < options.Length ? options[i] : null;
                foreach (var s in (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(s, out var id) && id > 0) ids.Add(id);
                var h = home is not null && i < home.Length ? home[i] : null;
                people.Add(new MemberFeeInput
                {
                    MembershipType = types[i],
                    HomeClub = h switch
                    {
                        "ours" => PrimaryClubState.Ours,
                        "other" => PrimaryClubState.Other,
                        _ => PrimaryClubState.Unknown
                    },
                    OptionIds = ids
                });
            }

            var categories = _feeService.GetCategories(clubId, year);
            var (parts, partTypes) = _feeService.GetParts(clubId, year);
            var result = ClubFeePreview.Compute(categories.ToList(), parts.ToList(), partTypes, people);
            // "Gäller inte den här medlemmen": de tillägg som inte träffar (första personen = huvudmedlemmen) och varför.
            var optionNames = _feeSettings.GetOptions(clubId, includeInactive: true).ToDictionary(o => o.Id, o => o.Name);
            var skipped = parts.Select(p => new
            {
                p.Label,
                reason = ClubFeePreview.NotApplyReason(p, new HashSet<string>(partTypes[p.Id]), people[0],
                    p.OptionId is int oid && optionNames.TryGetValue(oid, out var on) ? on : null)
            }).Where(x => x.reason is not null).ToList();
            return Json(new
            {
                success = true,
                kind = result.Kind.ToString(),
                total = result.Calculation.Total,
                person = memberId is > 0 ? new { name = personName, type = people[0].MembershipType } : null,
                skipped,
                lines = result.Calculation.Lines.Select(l => new { l.Kind, l.Description, l.Amount, l.PartId })
            });
        }

        /// <summary>
        /// "Så här blir det" i tilläggsformuläret, live medan kassören fyller i: hur många av klubbens medlemmar träffas, ett
        /// exempel med avgiften före och efter, och en medlem som inte träffas (med skälet). Skriver ingenting.
        /// Samma regel som avgiften (<see cref="ClubFeeCalculator.Applies"/> + <see cref="ClubFeePreview"/>). <c>partId</c> &gt; 0 =
        /// ett befintligt tillägg som ändras (ersätts i räkningen, annars räknas det dubbelt).
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("LedgerAccess.CanRead(klubben); skriver inget, räknar på klubbens medlemmar")]
        public async Task<IActionResult> PreviewPart(int clubId, int year, int partId, decimal amount, string? condition, int? optionId, string[]? typeKeys)
        {
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead)
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (condition is not (MembershipFeePartCondition.Always or MembershipFeePartCondition.PrimaryClub
                or MembershipFeePartCondition.NotPrimaryClub or MembershipFeePartCondition.Option))
                return Json(new { success = false, message = "Okänt villkor." });

            List<MemberFeeInput> people;
            try { people = ActiveFeeMembers(clubId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Förhandsvisningen kunde inte räkna för klubb {ClubId}", clubId);
                return Json(new { success = false, message = "Träffarna kunde inte räknas just nu." });
            }

            const int draftId = -1;
            var draft = new MembershipFeePart { Id = draftId, ClubId = clubId, Year = year, Label = "(nytt)", Amount = amount, Condition = condition,
                                                OptionId = condition == MembershipFeePartCondition.Option ? optionId : null };
            var draftKeys = (typeKeys ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToHashSet();
            var applies = people.Where(m => ClubFeeCalculator.Applies(draft, draftKeys, m)).ToList();

            var categories = _feeService.GetCategories(clubId, year).ToList();
            var (parts, partTypes) = _feeService.GetParts(clubId, year);
            var baseParts = parts.Where(p => p.Id != partId).ToList();
            var withDraft = baseParts.Append(draft).ToList();
            var lookup = baseParts.SelectMany(p => partTypes[p.Id].Select(k => (p.Id, k)))
                .Concat(draftKeys.Select(k => (draftId, k))).ToLookup(x => x.Item1, x => x.k);

            // Exemplet: första träffade medlem vars avgift går att räkna (har typ och grundavgift). Före = utan tillägget, efter = med.
            (int memberId, string? type, decimal before, decimal after)? example = null;
            foreach (var m in applies)
            {
                var before = ClubFeePreview.Compute(categories, baseParts, lookup, new[] { m });
                if (before.Kind is not (ClubFeeKind.Individual or ClubFeeKind.HouseholdPrimary)) continue;
                var after = ClubFeePreview.Compute(categories, withDraft, lookup, new[] { m });
                example = (m.MemberId, m.MembershipType, before.Calculation.Total, after.Calculation.Total);
                break;
            }
            var miss = people.FirstOrDefault(m => !applies.Contains(m));
            var optionName = draft.OptionId is int oid2 ? _feeSettings.GetOptions(clubId, includeInactive: true).FirstOrDefault(o => o.Id == oid2)?.Name : null;
            var names = MemberInfo(new[] { example?.memberId ?? 0, miss?.MemberId ?? 0 }.Where(i => i > 0));
            string? NameOf(int id) => names.TryGetValue(id, out var n) ? n.Name : null;
            return Json(new
            {
                success = true,
                total = people.Count,
                applies = applies.Count,
                example = example is null ? null : new { name = NameOf(example.Value.memberId), type = example.Value.type, before = example.Value.before, after = example.Value.after },
                notApplying = miss is null ? null : new { name = NameOf(miss.MemberId), reason = ClubFeePreview.NotApplyReason(draft, draftKeys, miss, optionName) }
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben); tillägget prövas mot klubben och året i anropet")]
        public async Task<IActionResult> SaveFeePart(int clubId, int year, int id, string? label, decimal amount,
            string? condition, int? optionId, string[]? typeKeys)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var (partsBefore, _) = _feeService.GetParts(clubId, year);
            var partBefore = id > 0 ? partsBefore.FirstOrDefault(p => p.Id == id) : null;
            var (partId, error) = _feeSettings.SavePart(clubId, year, id, label, amount, condition, optionId, typeKeys);
            if (error is not null) return Json(new { success = false, message = error });
            // ⚠️ Oskickade avgifter följer inställningen (som för grundavgiften); skickade och betalda rörs aldrig.
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, year, ClubFeeChangeEntity.Part, partId.ToString(), ClubFeeChangeAction.Saved,
                partBefore is null ? $"Nytt tillägg/avdrag {label?.Trim()} {year}: {amount:+0.##;−0.##} kr ({condition})"
                    : $"Tillägg/avdrag {partBefore.Label} {year}: {partBefore.Amount:+0.##;−0.##} → {amount:+0.##;−0.##} kr ({condition})");
            return Json(new { success = true, data = new { id = partId }, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben); tillägget prövas mot klubben och året i anropet")]
        public async Task<IActionResult> DeleteFeePart(int clubId, int year, int id)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var goneParts = _feeService.GetParts(clubId, year).Parts;
            var gonePart = goneParts.FirstOrDefault(p => p.Id == id);
            if (!_feeSettings.DeletePart(clubId, year, id))
                return Json(new { success = false, message = "Tillägget hittades inte." });
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, year, ClubFeeChangeEntity.Part, id.ToString(), ClubFeeChangeAction.Deleted,
                $"Tillägg/avdrag {gonePart?.Label} {year} borttaget ({gonePart?.Amount:+0.##;−0.##} kr)");
            return Json(new { success = true, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben); tillvalet prövas mot klubben i anropet")]
        public async Task<IActionResult> SaveFeeOption(int clubId, int year, int id, string? name, string? systemKey, string? isActive = "1")
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var active = isActive is "1" or "true" or "on";
            var (optId, error) = _feeSettings.SaveOption(clubId, id, name, systemKey, active);
            if (error is not null) return Json(new { success = false, message = error });
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, null, ClubFeeChangeEntity.Option, optId.ToString(), ClubFeeChangeAction.Saved,
                $"{(id > 0 ? "Förbund" : "Nytt förbund")} {name?.Trim()}{(active ? "" : " (dolt)")}");
            return Json(new { success = true, data = new { id = optId }, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben); tillvalet prövas mot klubben i anropet")]
        public async Task<IActionResult> DeleteFeeOption(int clubId, int year, int id)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var optName = _feeSettings.GetOptions(clubId, includeInactive: true).FirstOrDefault(o => o.Id == id)?.Name;
            var (deleted, error) = _feeSettings.DeleteOption(clubId, id);
            if (!deleted) return Json(new { success = false, message = error });
            await LogAsync(clubId, null, ClubFeeChangeEntity.Option, id.ToString(), ClubFeeChangeAction.Deleted, $"Förbund {optName} borttaget");
            return Json(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetMemberOptions(int clubId, int memberId)
        {
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead && !await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            return Json(new { success = true, optionIds = _feeSettings.GetMemberOptionIds(clubId, memberId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; medlemmen och tillvalen prövas mot klubben")]
        public async Task<IActionResult> SetMemberOptions(int clubId, int year, int memberId, int[]? optionIds)
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var error = _feeSettings.SetMemberOptions(clubId, memberId, optionIds ?? Array.Empty<int>());
            if (error is not null) return Json(new { success = false, message = error });
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, null, ClubFeeChangeEntity.MemberOption, memberId.ToString(), ClubFeeChangeAction.Saved,
                $"Förbund för medlem {memberId}: {(optionIds is { Length: > 0 } ? string.Join(", ", optionIds) : "inga")}");
            return Json(new { success = true, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        // ── Medlemstyper (steg 3) ─────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetMemberTypes(int clubId)
        {
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead && !await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            // Åldersgränserna (steg 3b). Ett läsfel sägs — "kunde inte läsa" är aldrig samma sak som "inga gränser".
            List<AgeStep>? steps = null;
            string? ageError = null;
            try { steps = _ageLimits.Steps(clubId); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Åldersgränserna kunde inte läsas för klubb {ClubId}", clubId);
                ageError = "Åldersgränserna kunde inte läsas.";
            }
            return Json(new
            {
                success = true,
                untyped = _feeSettings.CountUntyped(clubId),   // aktiva medlemmar utan typ: får ingen avgift
                ageRules = new { enabled = steps is { Count: > 0 }, error = ageError },
                types = _feeSettings.GetTypesWithCounts(clubId).Select(x => new
                {
                    key = x.Type.Key, name = x.Type.Name, isActive = x.Type.IsActive, isCustom = x.Type.IsCustom, members = x.Members,
                    // Undre åldersgräns ("Senior från 21"), null = ingen gräns. Gruppen som text: "21–59 år".
                    fromAge = steps?.FirstOrDefault(s => MembershipTypeKeys.Same(s.Key, x.Type.Key))?.FromAge,
                    ageGroup = steps is null ? null : AgeTypeRules.DescribeGroup(x.Type.Key, steps)
                })
            });
        }

        // ── Åldersgränser per medlemstyp (steg 3b) ────────────────────

        /// <summary>
        /// Sparar HELA stegen: parallella listor typeKeys[i] ↔ fromAges[i]. Inga listor = regeln av. Typerna prövas mot klubbens egna.
        /// Ändrar inga avgifter (bara förslagslistan och medlemmens val påverkas), så inga avgifter rensas här.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; typerna prövas mot klubbens egna typer")]
        public async Task<IActionResult> SaveAgeLimits(int clubId, string[]? typeKeys, int[]? fromAges)
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            typeKeys ??= Array.Empty<string>();
            fromAges ??= Array.Empty<int>();
            if (typeKeys.Length != fromAges.Length)
                return Json(new { success = false, message = "Varje åldersgräns måste höra till en medlemstyp." });

            var steps = typeKeys.Select((k, i) => new AgeStep((k ?? "").Trim(), fromAges[i])).ToList();
            try
            {
                var error = _ageLimits.SaveSteps(clubId, steps);
                return error is null ? Json(new { success = true }) : Json(new { success = false, message = error });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Åldersgränserna kunde inte sparas för klubb {ClubId}", clubId);
                return Json(new { success = false, message = "Åldersgränserna kunde inte sparas. Försök igen." });
            }
        }

        /// <summary>
        /// Förslagslistan: vilka medlemmar som bör byta (eller få) typ efter ålder, med avgiften före och efter. Sparas aldrig — räknas
        /// om vid varje öppning. Läsrätt räcker (styrelsen och revisorn ser åldern i år, aldrig personnummer).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAgeProposals(int clubId, int year)
        {
            var canReadFees = (await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, clubId)).CanRead;
            if (!canReadFees && !await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            try
            {
                var r = _ageProposals.GetProposals(clubId, year);
                return Json(new
                {
                    success = true, year = r.Year, enabled = r.Enabled, canApply = r.CanApply,
                    rows = r.Rows.Select(v => new
                    {
                        memberId = v.MemberId, name = v.Name, kind = v.Kind.ToString(),
                        fromKey = v.FromKey ?? "", fromName = v.FromName, toKey = v.ToKey, toName = v.ToName,
                        age = v.Age, birthStatus = v.BirthStatus.ToString(), reason = v.Reason,
                        feeBefore = canReadFees ? v.FeeBefore : null, feeAfter = canReadFees ? v.FeeAfter : null, noCategory = v.NoCategory
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Förslagslistan kunde inte räknas för klubb {ClubId} {Year}", clubId, year);
                return Json(new { success = false, message = "Förslagslistan kunde inte räknas fram. Försök igen." });
            }
        }

        /// <summary>
        /// Godkänner valda typbyten. items[] = "medlemsId:typen hen hade i listan" (tom efter kolon = ingen typ) — klienten skickar
        /// ALDRIG målet; servern räknar om och behåller bara de par som fortfarande stämmer.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; varje medlem och typ prövas mot klubben, målet räknas på servern")]
        public async Task<IActionResult> ApplyAgeProposals(int clubId, int year, string[]? items)
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var parsed = AgeApprovalItem.Parse(items);
            if (parsed.Count > 2000) return Json(new { success = false, message = "För många medlemmar på en gång." });
            try
            {
                var r = _ageProposals.Apply(clubId, year, parsed, await GetCurrentMemberIdAsync());
                if (r.Error is not null) return Json(new { success = false, message = r.Error });
                return Json(new
                {
                    success = true, applied = r.Applied, chargesCleared = r.ChargesCleared,
                    skipped = r.Skipped.Select(s => new { memberId = s.MemberId, reason = s.Reason })
                });
            }
            catch (InvalidOperationException ex) // låset: någon annan skapar avgifterna just nu
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>Ångrar godkända byten för valda medlemmar och året (bara om typen fortfarande är den bytet gav).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; loggraderna prövas mot klubben och året")]
        public async Task<IActionResult> UndoAgeProposals(int clubId, int year, int[]? memberIds)
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            try
            {
                var r = _ageProposals.Undo(clubId, year, (memberIds ?? Array.Empty<int>()).Where(i => i > 0).Distinct().Take(2000).ToList());
                return Json(new
                {
                    success = true, undone = r.Applied, chargesCleared = r.ChargesCleared,
                    skipped = r.Skipped.Select(s => new { memberId = s.MemberId, reason = s.Reason })
                });
            }
            catch (InvalidOperationException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>"Behåll typen" (keep = "1") eller "Lås upp" (keep = "0"): undantag från åldersreglerna för en medlem.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; medlemmen prövas mot klubben")]
        public async Task<IActionResult> SetAgeRuleOptOut(int clubId, int year, int memberId, string? keep = "1")
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var error = _ageProposals.SetOptOut(clubId, year, memberId, keep is "1" or "true" or "on", await GetCurrentMemberIdAsync());
            return error is null ? Json(new { success = true }) : Json(new { success = false, message = error });
        }

        /// <summary>
        /// Antal väntande åldersförslag för skicka-modalens gula varning (spärrar inte). Null när det inte gick att räkna — ett fel
        /// får aldrig stoppa genereringen eller utskicket.
        /// </summary>
        private int? PendingAgeProposals(int clubId, int year)
        {
            if (year < MembershipAgeLimits.CurrentYear) return 0;
            try { return _ageProposals.CountPending(clubId, year); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Väntande åldersförslag kunde inte räknas för klubb {ClubId} {Year}", clubId, year);
                return null;
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; typen prövas mot klubbens egna typer")]
        public async Task<IActionResult> SaveMemberType(int clubId, int year, string? key, string? name, string? isActive = "1")
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var typeActive = isActive is "1" or "true" or "on";
            var (k, error) = _feeSettings.SaveType(clubId, key, name, typeActive);
            if (error is not null) return Json(new { success = false, message = error });
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, null, ClubFeeChangeEntity.Type, k, ClubFeeChangeAction.Saved,
                $"Medlemstyp {name?.Trim()}{(typeActive ? "" : " (dold)")}");
            return Json(new { success = true, data = new { key = k }, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; typen prövas mot klubbens egna typer")]
        public async Task<IActionResult> DeleteMemberType(int clubId, string? key)
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            var (deleted, error) = _feeSettings.DeleteType(clubId, key ?? "");
            return deleted ? Json(new { success = true }) : Json(new { success = false, message = error });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipAccess.CanManageMemberAsync(klubben): klubbadmin eller kassör, för typ, ålder och förbund; typen och medlemmarna prövas mot klubben")]
        public async Task<IActionResult> SetMembersType(int clubId, int year, string? toKey, string? fromKey, int[]? memberIds, string? fromUntyped = null)
        {
            if (!await _memberAccess.CanManageMemberAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            using var lease = _feeService.TryLockSettings(clubId, year);
            if (lease is null) return Json(new { success = false, message = SettingsBusy });
            var (changed, error) = _feeSettings.SetMembersType(clubId, toKey, memberIds, fromKey, fromUntyped is "1" or "true" or "on");
            if (error is not null) return Json(new { success = false, message = error });
            var cleared = _feeService.ClearUnsentClubCharges(clubId, year);
            await LogAsync(clubId, null, ClubFeeChangeEntity.MembersType, toKey, ClubFeeChangeAction.Saved,
                $"{changed} medlemmar fick typen {toKey}{(string.IsNullOrWhiteSpace(fromKey) ? "" : " (från " + fromKey + ")")}");
            return Json(new { success = true, data = new { changed }, consequences = await ConsequencesAsync(clubId, year, cleared) });
        }

        // ── Charges ───────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben)")]
        public async Task<IActionResult> GenerateCharges(int clubId, int year)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            // Bill from the per-club membership records (ClubMembership). Exclude members who
            // have left or are deceased; membership type drives which fee category applies.
            try
            {
                var created = _feeService.GenerateChargesForClub(clubId, year, ActiveFeeMembers(clubId));
                return Json(new { success = true, created, pendingAgeProposals = PendingAgeProposals(clubId, year) });
            }
            catch (InvalidOperationException ex) // låset: någon annan skapar avgifterna just nu
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Medlemmens avgift för året — skapas (oskickad) om den inte finns. Varje handling på en rad
        /// (Betald, kopiera länk, visa/skicka mejlet) börjar här, så raden beter sig likadant före och
        /// efter utskicket. Ett hushåll skapas i sin helhet, så "ingår"-raderna pekar rätt.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben)")]
        public async Task<IActionResult> EnsureClubCharge(int clubId, int year, int memberId)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var members = ActiveFeeMembers(clubId);
            var me = members.FirstOrDefault(m => m.MemberId == memberId);
            if (me is null) return Json(new { success = false, message = "Medlemmen finns inte i klubbens register." });

            var proposal = ClubFeeProposal.Build(_feeService.GetCategories(clubId, year), members);
            proposal.TryGetValue(memberId, out var p);
            // ChooseType går igenom: avgiften skapas (0 kr) så att medlemmen kan välja på betalsidan.
            if (p is null || p.Kind is ClubFeeKind.NoCategory or ClubFeeKind.NoType)
                return Json(new { success = false, message = string.IsNullOrWhiteSpace(me.MembershipType)
                    ? "Medlemmen har ingen medlemstyp. Ange medlemstypen under Medlemmar först."
                    : $"Klubben har ingen avgift för medlemstypen {me.MembershipType} i år. Lägg till den under Ekonomi → Avgiftsregler." });
            if (p.Kind == ClubFeeKind.Covered)
                return Json(new { success = false, message = "Medlemmen ingår i familjens avgift, som huvudmedlemmen betalar." });

            var household = string.IsNullOrWhiteSpace(me.HouseholdId)
                ? new List<MemberFeeInput> { me }
                : members.Where(m => string.Equals((m.HouseholdId ?? "").Trim(), me.HouseholdId!.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            try { _feeService.GenerateChargesForClub(clubId, year, household); }
            catch (InvalidOperationException ex) { return Json(new { success = false, message = ex.Message }); }

            var charge = _feeService.GetChargeForMemberYear(memberId, clubId, year);
            return Json(charge is null ? new { success = false, chargeId = (int?)null, message = (string?)"Avgiften kunde inte skapas.", pendingAgeProposals = (int?)null }
                                       : new { success = true, chargeId = (int?)charge.Id, message = (string?)null, pendingAgeProposals = PendingAgeProposals(clubId, year) });
        }

        /// <summary>Medlemsavgiftsmejlet som det skulle skickas — samma byggväg som utskicket.</summary>
        [HttpGet]
        public async Task<IActionResult> PreviewClubPaymentRequest(int chargeId)
        {
            var charge = chargeId == 0 ? null : _feeService.GetCharge(chargeId);
            if (charge is null || charge.IsRegionFee) return Content("Avgiften hittades inte.");
            if (!(await _ledgerAccess.ResolveAsync(DocumentOwnerType.Club, charge.ClubId)).CanRead) return Content("Åtkomst nekad.");

            var clubName = _clubService.GetClubNameById(charge.ClubId) ?? "Klubben";
            var to = _feeService.GetPayerEmails(MembershipFeeIssuer.Club, charge.ClubId).GetValueOrDefault(charge.MemberId) ?? charge.MemberEmail ?? "";
            var choice = _feeService.GetTypeChoice(charge);
            var mail = _emailService.PreviewMembershipFeeRequest(to, charge.MemberName ?? "medlem",
                clubName, charge.Year, charge.Amount, BuildPayUrl(charge.Id), _replyContacts.ForClub(charge.ClubId),
                IssuerBankgiro(charge.ClubId), charge.PaymentReference, IssuerHasSwish(charge.ClubId),
                choice.CurrentLabel, charge.NeedsTypeChoice, choice.CanChoose,
                _feeService.DescribeLines(charge));
            return Content(PreviewPage(mail, to, charge.PaymentStatus == "Paid"), "text/html; charset=utf-8");
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

            // ⚠️ Bokföringen tas ut samtidigt — går det inte står avgiften kvar som betald, och skälet sägs.
            var error = _feeService.MarkUnpaid(chargeId, await GetCurrentMemberIdAsync());
            return Json(error is null
                ? new { success = true, message = (string?)null }
                : new { success = false, message = (string?)error });
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
            if (charge.Amount > 0 || charge.NeedsTypeChoice) _feeService.MarkRequestSent(chargeId);

            return Json(new { success = true, url = BuildPayUrl(chargeId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("MembershipFeeAccess.CanWriteAsync(klubben)")]
        public async Task<IActionResult> SendPaymentRequests(int clubId, int year, string? mode = null, string? chargeIds = null)
        {
            if (!await _feeAccess.CanWriteAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var clubName = _clubService.GetClubNameById(clubId) ?? "Klubben";
            // ⚠️ Låset hålls under hela utskicket: en inställningsändring mitt i loopen rensade avgifter som mejlen redan pekade på
            //    (döda betallänkar, gamla belopp). Ändringen får vänta eller säga att utskick pågår (expertgruppen 2026-11-26, K2).
            using var sendLease = _feeService.TryLockSettings(clubId, year);
            if (sendLease is null)
                return Json(new { success = false, message = "Avgifterna skickas eller skapas just nu av någon annan. Försök igen om en stund." });
            // ⚠️ Samma tre urval som kretsavgiften: chargeIds = bara de, "unsent" = de som inte gått
            //    ut, "reminder" = de som gått ut men inte betalats. Inget = alla obetalda.
            var only = (chargeIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).ToHashSet();

            var sent = new List<string>();
            var noEmail = new List<string>();
            var failed = new List<string>();
            var feeEmails = _feeService.GetPayerEmails(MembershipFeeIssuer.Club, clubId);
            var bg = IssuerBankgiro(clubId);
            var hasSwish = IssuerHasSwish(clubId);
            foreach (var charge in _feeService.GetChargesForClubYear(clubId, year))
            {
                if (charge.IsRegionFee || charge.PaymentStatus == "Paid") continue;
                if (charge.HouseholdCoveredByChargeId.HasValue) continue; // ingår i huvudmedlemmens avgift
                // 0 kr skickas inte — UTOM när medlemmen ska välja medlemstyp; då är det valet mejlet gäller.
                if (charge.Amount <= 0 && !charge.NeedsTypeChoice) continue;
                if (only.Count > 0 && !only.Contains(charge.Id)) continue;
                if (mode == "unsent" && charge.IsRequestSent) continue;
                if (mode == "reminder" && !charge.IsRequestSent) continue;

                var name = charge.MemberName ?? $"Medlem {charge.MemberId}";
                var to = feeEmails.GetValueOrDefault(charge.MemberId) ?? charge.MemberEmail;
                if (string.IsNullOrWhiteSpace(to)) { noEmail.Add(name); continue; }

                bool ok;
                try
                {
                    // ⚠️ Svaret hör till KLUBBEN, som är den som kräver avgiften.
                    var choice = _feeService.GetTypeChoice(charge);
                    ok = await _emailService.SendMembershipFeeRequestAsync(
                        to!, name, clubName, year, charge.Amount, BuildPayUrl(charge.Id),
                        _replyContacts.ForClub(clubId), bg, charge.PaymentReference, hasSwish,
                        choice.CurrentLabel, charge.NeedsTypeChoice, choice.CanChoose,
                        _feeService.DescribeLines(charge));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send membership fee request for charge {ChargeId}", charge.Id);
                    ok = false;
                }
                (ok ? sent : failed).Add(name);
                // ⚠️ Bara ett mejl som FAKTISKT gick räknas som skickat.
                if (ok) _feeService.MarkRequestSent(charge.Id);
            }

            return Json(new { success = true, sent = sent.Count, noEmail, failed, pendingAgeProposals = PendingAgeProposals(clubId, year) });
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

            var rate = _feeService.GetRegionRate(regionId, year);
            var charges = _feeService.GetChargesForRegionYear(regionId, year)
                .ToDictionary(c => c.PayerClubId ?? 0);
            var feeEmails = _feeService.GetPayerEmails(MembershipFeeIssuer.Region, regionId);
            var (counts, selection) = RegionStarts(regionId, year, region.Value.Code);

            var rows = RegionClubs(region.Value.Code).Select(club =>
            {
                var count = ActiveMemberCount(club.Id);
                var starts = counts.GetValueOrDefault(club.Id);
                var proposal = RegionFeeCalculator.Lines(year, rate, count, starts?.Starts ?? 0, starts?.Competitions ?? 0, null);
                charges.TryGetValue(club.Id, out var charge);

                return new
                {
                    clubId = club.Id,
                    clubName = club.Name,
                    // Kretsens egen adress för avgifter till klubben, annars klubbens kontaktadress.
                    email = feeEmails.GetValueOrDefault(club.Id) ?? club.ContactEmail ?? "",
                    emailIsOwn = feeEmails.ContainsKey(club.Id),
                    defaultEmail = club.ContactEmail ?? "",
                    hasEmail = !string.IsNullOrWhiteSpace(feeEmails.GetValueOrDefault(club.Id) ?? club.ContactEmail),
                    proposedCount = count,
                    // pistol.nu:s räkning i de valda tävlingarna, med underlaget per tävling.
                    proposedStarts = starts?.Starts ?? 0,
                    proposedCompetitions = starts?.Competitions ?? 0,
                    startsByCompetition = (starts?.ByCompetition ?? new Dictionary<int, int>())
                        .Select(kv => new { competitionId = kv.Key, starts = kv.Value }),
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
                rate = new
                {
                    baseAmount = rate.BaseAmount, perMember = rate.PerMember,
                    perStart = rate.PerStart, perCompetition = rate.PerCompetition,
                    startLabel = rate.StartLabel, competitionLabel = rate.CompetitionLabel
                },
                // Tävlingarna som ingår, med namn — underlaget per klubb läses mot dem.
                competitions = selection.Select(c => new { id = c.Id, name = c.Name, date = c.Date.ToString("yyyy-MM-dd") }),
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
            // Antal kretsen skrivit för hand; null = följer pistol.nu:s räkning.
            startCount = c.StartCount,
            competitionCount = c.CompetitionCount,
            status = c.PaymentStatus,
            requestSentDate = SwedishTime.FromUtc(c.RequestSentDate)?.ToString("yyyy-MM-dd"),
            paymentSentDate = SwedishTime.FromUtc(c.PaymentSentDate)?.ToString("yyyy-MM-dd"),
            paymentSentBy = c.PaymentSentBy,
            paidDate = SwedishTime.FromUtc(c.PaidDate)?.ToString("yyyy-MM-dd"),
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

            if (request.BaseAmount < 0 || request.PerMember < 0 || request.PerStart < 0 || request.PerCompetition < 0)
                return Json(new { success = false, message = "Beloppen kan inte vara negativa." });

            var rateRegion = await AuthorizeRegionAsync(request.RegionId);
            var (counts, _) = RegionStarts(request.RegionId, request.Year, rateRegion!.Value.Code);
            _feeService.SaveRegionRate(request.RegionId, request.Year,
                new RegionFeeRate(request.BaseAmount, request.PerMember, request.PerStart, request.PerCompetition,
                    request.StartLabel ?? RegionFeeRate.DefaultStartLabel,
                    request.CompetitionLabel ?? RegionFeeRate.DefaultCompetitionLabel),
                counts);
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

            // ⚠️ Starterna räknas av SERVERN. Klientens lista bär bara klubb och medlemsantal — ett
            //    antal starter ur en webbläsare vore ett belopp på en räkning som ingen räknat.
            var (counts, _) = RegionStarts(request.RegionId, request.Year, region.Value.Code, useCache: false);
            foreach (var c in clubs)
            {
                var s = counts.GetValueOrDefault(c.ClubId);
                c.Starts = s?.Starts ?? 0;
                c.Competitions = s?.Competitions ?? 0;
            }

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

            var (counts, _) = RegionStarts(request.RegionId, request.Year, region.Value.Code);
            var (id, error) = _feeService.EnsureRegionCharge(request.RegionId, request.Year, request.ClubId,
                ActiveMemberCount(request.ClubId), counts.GetValueOrDefault(request.ClubId), await GetCurrentMemberIdAsync());
            return Json(error is null ? new { success = true, chargeId = id, message = (string?)null }
                                      : new { success = false, chargeId = (int?)null, message = (string?)error });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRegionChargeManualAmount([FromBody] RegionChargeLineRequest request)
        {
            if (request?.ManualAmount < 0) return Json(new { success = false, message = "Beloppet kan inte vara negativt." });
            return await EditRegionCharge(request?.ChargeId ?? 0, charge =>
                _feeService.SetRegionChargeManualAmount(request!.ChargeId, request.ManualAmount, StartsForCharge(charge)));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddRegionChargeLine([FromBody] RegionChargeLineRequest request)
        {
            var byId = await GetCurrentMemberIdAsync();
            return await EditRegionCharge(request?.ChargeId ?? 0, _ =>
                _feeService.AddRegionChargeLine(request!.ChargeId, request.Description ?? "", request.Amount, byId));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveRegionChargeLine([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, _ =>
                _feeService.RemoveRegionChargeLine(request!.ChargeId, request.LineId));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRegionChargeMemberCount([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, charge =>
                _feeService.SetRegionChargeMemberCount(request!.ChargeId, request.MemberCount, StartsForCharge(charge)));

        /// <summary>
        /// Rättar antalet starter och tävlingar för en klubb. Tomt fält = följ pistol.nu:s räkning igen.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRegionChargeCompetitionCounts([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, charge =>
                _feeService.SetRegionChargeCompetitionCounts(request!.ChargeId, request.StartCount, request.CompetitionCount,
                    StartsForCharge(charge)));

        /// <summary>
        /// Kretsens tävlingar för avgiftsåret att välja bland, och vilka som är valda.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionFeeCompetitions(int regionId, int year)
        {
            var region = await AuthorizeRegionAsync(regionId, readOnly: true);
            if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });

            var clubIds = RegionClubs(region.Value.Code).Select(c => c.Id).ToHashSet();
            var selected = _regionCompetitions.GetSelection(regionId, year).ToHashSet();
            var candidates = _regionCompetitions.GetCandidates(region.Value.Code, clubIds, year);
            return Json(new
            {
                success = true,
                year,
                competitions = candidates.Select(c => new
                {
                    id = c.Id, name = c.Name, date = c.Date.ToString("yyyy-MM-dd"), series = c.SeriesName,
                    type = c.TypeLabel, host = c.HostName, takesFee = c.TakesFee, selected = selected.Contains(c.Id)
                }),
                // ⚠️ Ett val som inte längre finns bland kandidaterna (tävlingen raderad eller flyttad)
                //    SÄGS — annars räknas starterna tyst bort.
                missing = selected.Count(id => candidates.All(c => c.Id != id))
            });
        }

        /// <summary>
        /// Sparar vilka tävlingar som ingår och räknar om de oskickade avgifterna. <b>Bara kretsens
        /// egna kandidater godtas</b> — listan kommer från klienten.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveRegionFeeCompetitions([FromBody] RegionFeeCompetitionsRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });
            var region = await AuthorizeRegionAsync(request.RegionId);
            if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });

            var clubIds = RegionClubs(region.Value.Code).Select(c => c.Id).ToHashSet();
            var allowed = _regionCompetitions.GetCandidates(region.Value.Code, clubIds, request.Year).Select(c => c.Id).ToHashSet();
            var ids = (request.CompetitionIds ?? new()).Distinct().ToList();
            if (ids.Any(id => !allowed.Contains(id)))
                return Json(new { success = false, message = "Minst en av tävlingarna hör inte till kretsen det året. Ingenting sparades." });

            _regionCompetitions.SaveSelection(request.RegionId, request.Year, ids, await GetCurrentMemberIdAsync());
            var (counts, _) = RegionStarts(request.RegionId, request.Year, region.Value.Code, useCache: false);
            var refreshed = _feeService.RefreshUnsentRegionCharges(request.RegionId, request.Year, counts);
            return Json(new
            {
                success = true,
                message = $"{ids.Count} tävlingar ingår. "
                        + (refreshed > 0 ? $"{refreshed} avgifter som inte skickats än räknades om. " : "")
                        + "Avgifter som redan skickats ändras inte."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRegionCharge([FromBody] RegionChargeLineRequest request)
            => await EditRegionCharge(request?.ChargeId ?? 0, _ =>
                _feeService.DeleteRegionCharge(request!.ChargeId));

        private async Task<IActionResult> EditRegionCharge(int chargeId, Func<MembershipFeeCharge, string?> edit)
        {
            var charge = chargeId == 0 ? null : _feeService.GetCharge(chargeId);
            if (charge is null || !charge.IsRegionFee)
                return Json(new { success = false, message = "Kravet hittades inte." });
            if (!await CanManageChargeAsync(charge))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var error = edit(charge);
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

            // ⚠️ Oskickade avgifter räknas om PRECIS FÖRE utskicket, med dagens resultat. Det som går
            //    ut är det klubben ska betala, och räkningen kan ha ändrats sedan listan öppnades.
            if (_feeService.GetRegionRate(request.RegionId, request.Year).UsesCompetitions)
            {
                var (fresh, _) = RegionStarts(request.RegionId, request.Year, region.Value.Code, useCache: false);
                _feeService.RefreshUnsentRegionCharges(request.RegionId, request.Year, fresh);
            }
            var feeEmails = _feeService.GetPayerEmails(MembershipFeeIssuer.Region, request.RegionId);
            var bg = IssuerBankgiro(request.RegionId);
            var hasSwish = IssuerHasSwish(request.RegionId);

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

                var to = feeEmails.GetValueOrDefault(charge.PayerClubId ?? 0) ?? club?.ContactEmail;
                if (string.IsNullOrWhiteSpace(to)) { noEmail.Add(name); continue; }

                bool ok;
                try
                {
                    ok = await _emailService.SendRegionFeeRequestAsync(
                        to!, name, region.Value.Name, charge.Year,
                        charge.Lines.Select(l => (l.Description, l.Amount)).ToList(),
                        charge.Amount, BuildPayUrl(charge.Id), reply, bg, charge.PaymentReference, hasSwish);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kretsavgiftens betalkrav {ChargeId} kunde inte skickas.", charge.Id);
                    ok = false;
                }

                (ok ? sent : failed).Add(name);
                // ⚠️ Bara ett mejl som FAKTISKT gick räknas som skickat. Annars står klubben som
                //    "Skickad" utan att ha fått något.
                if (ok) _feeService.MarkRequestSent(charge.Id);
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
            var to = _feeService.GetPayerEmails(MembershipFeeIssuer.Region, region.Value.Id).GetValueOrDefault(charge.PayerClubId ?? 0)
                     ?? club?.ContactEmail ?? "";

            var mail = _emailService.PreviewRegionFeeRequest(
                to, name, region.Value.Name, charge.Year,
                charge.Lines.Select(l => (l.Description, l.Amount)).ToList(),
                charge.Amount, BuildPayUrl(charge.Id), _replyContacts.ForRegion(region.Value.Id),
                IssuerBankgiro(region.Value.Id), charge.PaymentReference, IssuerHasSwish(region.Value.Id));

            return Content(PreviewPage(mail, to, charge.PaymentStatus == "Paid"), "text/html; charset=utf-8");
        }

        /// <summary>Förhandsvisningens sida: mejlets eget dokument, med kuvertet i en ram ovanför.</summary>
        private static string PreviewPage(RegionFeeMailPreview mail, string? to, bool paid)
        {
            to ??= "";
            var enc = new Func<string?, string>(s => System.Net.WebUtility.HtmlEncode(s ?? ""));
            var envelope =
                "<div style=\"font-family:Arial,sans-serif;font-size:13px;max-width:560px;margin:16px auto;"
              + "padding:12px 16px;border:1px dashed #6b7280;border-radius:6px;background:#fff;color:#333\">"
              + "<div style=\"font-weight:bold;margin-bottom:6px\">Förhandsvisning — inget har skickats</div>"
              + $"<div><b>Från:</b> {enc(mail.FromName)} &lt;{enc(mail.FromAddress)}&gt;</div>"
              + $"<div><b>Svar till:</b> {(mail.ReplyTo is null ? "<i>ingen svarsadress</i>" : enc(mail.ReplyTo))}</div>"
              + $"<div><b>Till:</b> {(to.Length == 0 ? "<i style=\"color:#b45309\">ingen e-postadress — mejlet skickas inte</i>" : enc(to))}</div>"
              + $"<div><b>Ämne:</b> {enc(mail.Subject)}</div>"
              + (paid ? "<div style=\"color:#b45309;margin-top:6px\">Avgiften är betald och ingår inte i ett utskick.</div>" : "")
              + "</div>";

            // Kuvertet läggs in direkt efter <body>, så mejlets eget dokument står orört under det.
            var html = mail.Html;
            var bodyAt = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            var bodyEnd = bodyAt < 0 ? -1 : html.IndexOf('>', bodyAt);
            html = bodyEnd < 0 ? envelope + html : html.Insert(bodyEnd + 1, envelope);
            return html;
        }

        /// <summary>
        /// Får den inloggade hantera kravet? Klubbadmin för en medlemsavgift, kretsadmin för en
        /// kretsavgift. <b>EN fråga för alla kravets endpoints</b>, så att de två formerna inte kan få
        /// olika regler på olika knappar.
        /// </summary>
        private async Task<bool> CanManageChargeAsync(MembershipFeeCharge charge)
            => charge.IsRegionFee
                ? await AuthorizeRegionAsync(charge.RegionId ?? 0) is not null
                : await _feeAccess.CanWriteAsync(charge.ClubId);

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
        /// pistol.nu:s räkning av starter per klubb i kretsens valda tävlingar för året, och de valda
        /// tävlingarna (för namnen). Tomt när inga tävlingar är valda.
        /// </summary>
        private (Dictionary<int, RegionClubStarts> Counts, List<RegionFeeCandidate> Selection) RegionStarts(
            int regionId, int year, string regionCode, bool useCache = true)
        {
            var ids = _regionCompetitions.GetSelection(regionId, year);
            if (ids.Count == 0) return (new Dictionary<int, RegionClubStarts>(), new List<RegionFeeCandidate>());
            var clubIds = RegionClubs(regionCode).Select(c => c.Id).ToHashSet();
            var chosen = ids.ToHashSet();
            var selection = _regionCompetitions.GetCandidates(regionCode, clubIds, year).Where(c => chosen.Contains(c.Id)).ToList();
            return (_regionCompetitions.Count(ids, clubIds, useCache), selection);
        }

        /// <summary>Räkningen för klubben en kretsavgift gäller.</summary>
        private RegionClubStarts? StartsForCharge(MembershipFeeCharge charge)
        {
            var node = charge.RegionId is int rid ? Services.ContentService.GetById(rid) : null;
            var code = node?.GetValue<string>("regionCode") ?? "";
            if (node is null || code.Length == 0) return null;
            var (counts, _) = RegionStarts(node.Id, charge.Year, code);
            return counts.GetValueOrDefault(charge.PayerClubId ?? 0);
        }

        /// <summary>
        /// Registrets förslag på medlemsantal: klubbens medlemskap utom de som utträtt eller avlidit —
        /// samma regel som klubbens egen avgiftskörning, så de två inte räknar olika på samma register.
        /// </summary>
        private int ActiveMemberCount(int clubId)
            => _clubMembershipService.GetForClubApprovedMembers(clubId)
                .Count(cm => cm.MembershipStatus != "Utträdd" && cm.MembershipStatus != "Avliden");

        /// <summary>
        /// Sätter vart en avgift ska mejlas (tom = den vanliga adressen igen). Kretsen sätter den för
        /// sina klubbar, klubben för sina medlemmar. <b>Ändrar aldrig betalarens vanliga adress.</b>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Klubb: MembershipFeeAccess.CanWriteAsync(klubben); krets: AuthorizeRegionAsync (kretsadmin)")]
        public async Task<IActionResult> SetFeeEmail(int issuerType, int issuerId, int payerId, string? email)
        {
            email = (email ?? "").Trim();
            if (email.Length > 0)
            {
                try { if (new System.Net.Mail.MailAddress(email).Address != email) throw new FormatException(); }
                catch { return Json(new { success = false, message = "Det ser inte ut som en e-postadress." }); }
            }

            if (issuerType == MembershipFeeIssuer.Region)
            {
                var region = await AuthorizeRegionAsync(issuerId);
                if (region is null) return Json(new { success = false, message = "Åtkomst nekad" });
                if (!RegionClubs(region.Value.Code).Any(c => c.Id == payerId))
                    return Json(new { success = false, message = "Klubben hör inte till kretsen." });
            }
            else
            {
                if (!await _feeAccess.CanWriteAsync(issuerId)) return Json(new { success = false, message = "Åtkomst nekad" });
                if (ActiveFeeMembers(issuerId).All(m => m.MemberId != payerId))
                    return Json(new { success = false, message = "Medlemmen finns inte i klubbens register." });
            }

            _feeService.SetPayerEmail(issuerType, issuerId, payerId, email, await GetCurrentMemberIdAsync());
            return Json(new { success = true });
        }

        /// <summary>Utställarens bankgiro (formaterat), eller null om det saknas eller är ogiltigt.</summary>
        private string? IssuerBankgiro(int nodeId)
        {
            var node = Services.ContentService.GetById(nodeId);
            var bg = node is not null && node.HasProperty("bgNumber") ? node.GetValue<string>("bgNumber") : null;
            return BankgiroQrCodeGenerator.IsValidBankgiro(bg) ? BankgiroQrCodeGenerator.FormatAccount(bg) : null;
        }

        /// <summary>
        /// Har utställaren ett giltigt Swishnummer? Samma prövning som betalsidan gör innan den visar
        /// Swish-QR:en — annars erbjuder mejlet en Swish-knapp som landar på en sida utan Swish.
        /// </summary>
        private bool IssuerHasSwish(int nodeId)
        {
            var node = Services.ContentService.GetById(nodeId);
            var sw = node is not null && node.HasProperty("swishNumber") ? node.GetValue<string>("swishNumber") : null;
            var normalized = (sw ?? "").Trim().Replace(" ", "").Replace("-", "");
            return SwishQrCodeGenerator.IsValidSwishNumber(normalized);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private string BuildPayUrl(int chargeId)
        {
            var token = _protector.Protect(chargeId.ToString());
            return $"{Request.PublicBase()}/medlemsavgift/{Uri.EscapeDataString(token)}";
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
        public decimal PerStart { get; set; }
        public decimal PerCompetition { get; set; }
        /// <summary>Radernas namn på räkningen, t.ex. "Startavgifter" och "Lagavgift".</summary>
        public string? StartLabel { get; set; }
        public string? CompetitionLabel { get; set; }
    }

    public class RegionFeeCompetitionsRequest
    {
        public int RegionId { get; set; }
        public int Year { get; set; }
        public List<int>? CompetitionIds { get; set; }
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

        /// <summary>Antal starter/tävlingar skrivna för hand. Null = följ pistol.nu:s räkning.</summary>
        public int? StartCount { get; set; }
        public int? CompetitionCount { get; set; }
    }
}
