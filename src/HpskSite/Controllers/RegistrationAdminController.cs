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
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.CompetitionTypes.Precision.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using Newtonsoft.Json;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Handles all competition registration management operations for administrators.
    /// Extracted from AdminController as part of the controller refactoring.
    /// </summary>
    public class RegistrationAdminController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _authService;
        private readonly ClubService _clubService;
        private readonly PaymentService _paymentService;
        private readonly InvoiceAuditService _auditService;
        private readonly DirektplaceringStartListService _dpStartListService;
        private readonly StartListHtmlRenderer _startListRenderer;
        private readonly UmbracoStartListRepository _startListRepository;
        private readonly CompetitionTeamService _teamService;

        public RegistrationAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberManager memberManager,
            IContentService contentService,
            AdminAuthorizationService authService,
            ClubService clubService,
            PaymentService paymentService,
            InvoiceAuditService auditService,
            DirektplaceringStartListService dpStartListService,
            StartListHtmlRenderer startListRenderer,
            UmbracoStartListRepository startListRepository,
            CompetitionTeamService teamService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _contentService = contentService;
            _authService = authService;
            _clubService = clubService;
            _paymentService = paymentService;
            _auditService = auditService;
            _dpStartListService = dpStartListService;
            _startListRenderer = startListRenderer;
            _startListRepository = startListRepository;
            _teamService = teamService;
        }

        #region Registration Management

        /// <summary>
        /// Get all competition registrations or registrations for a specific competition
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCompetitionRegistrations(int? competitionId = null)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get all registration documents
                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
                var allRegistrations = allContent
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .Select(reg =>
                    {
                        // Resolve club name from clubId
                        var clubId = reg.GetValue<int>("clubId");
                        var clubName = clubId > 0 ? _clubService.GetClubNameById(clubId) : null;

                        // Fallback to memberClub for legacy data
                        if (string.IsNullOrEmpty(clubName))
                        {
                            var legacyClub = reg.GetValue<string>("memberClub");
                            if (!string.IsNullOrEmpty(legacyClub) && int.TryParse(legacyClub, out var legacyId))
                            {
                                clubName = _clubService.GetClubNameById(legacyId);
                            }
                            else
                            {
                                clubName = legacyClub;
                            }
                        }

                        // Get payment status for this registration
                        var compId = reg.GetValue<int>("competitionId");
                        var paymentStatus = GetRegistrationPaymentStatus(reg.Id, compId);

                        return new
                        {
                            id = reg.Id,
                            competitionId = compId,
                            competitionName = GetCompetitionName(compId),
                            memberId = reg.GetValue<int>("memberId"),
                            memberName = reg.GetValue<string>("memberName") ?? "",
                            memberClub = clubName ?? "",
                            shootingClass = reg.GetValue<string>("shootingClass") ?? "",
                            startPreference = reg.GetValue<string>("startPreference") ?? "Inget",
                            registrationDate = reg.GetValue<DateTime>("registrationDate"),
                            registeredBy = reg.GetValue<string>("registeredBy") ?? "",
                            isActive = reg.GetValue<bool>("isActive"),
                            shooterNotes = reg.GetValue<string>("shooterNotes") ?? "",
                            paymentStatus = paymentStatus
                        };
                    })
                    .OrderByDescending(r => r.registrationDate);

                // Filter by competition if specified
                var registrations = competitionId.HasValue
                    ? allRegistrations.Where(r => r.competitionId == competitionId.Value).ToList()
                    : allRegistrations.ToList();

                // Calculate statistics
                var stats = new
                {
                    totalRegistrations = registrations.Count,
                    activeCompetitions = registrations.Select(r => r.competitionId).Distinct().Count(),
                    uniqueMembers = registrations.Select(r => r.memberId).Distinct().Count(),
                    popularClass = registrations.GroupBy(r => r.shootingClass)
                                              .OrderByDescending(g => g.Count())
                                              .FirstOrDefault()?.Key ?? "-"
                };

                return Json(new { success = true, registrations = registrations, statistics = stats });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading registrations: " + ex.Message });
            }
        }

        /// <summary>
        /// Get list of all active competitions for dropdown/filtering.
        /// Site admins see every competition; regional admins see only competitions
        /// whose club belongs to one of their managed regions.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetActiveCompetitions()
        {
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = isSiteAdmin ? new List<string>() : await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants).ToList();

                // For regional admins, build clubId -> region lookup so we can filter
                Dictionary<int, string>? clubRegions = null;
                if (isRegionalAdmin)
                {
                    clubRegions = allContent
                        .Where(c => c.ContentType.Alias == "club")
                        .GroupBy(c => c.Id)
                        .ToDictionary(g => g.Key, g => g.First().GetValue<string>("regionalFederation") ?? "");
                }

                var competitionsQuery = allContent.Where(c => c.ContentType.Alias == "competition");

                if (isRegionalAdmin && clubRegions != null)
                {
                    var managedRegionSet = new HashSet<string>(managedRegions, StringComparer.OrdinalIgnoreCase);
                    competitionsQuery = competitionsQuery.Where(comp =>
                    {
                        var clubId = comp.GetValue<int?>("clubId") ?? 0;
                        return clubId > 0
                            && clubRegions.TryGetValue(clubId, out var clubRegion)
                            && managedRegionSet.Contains(clubRegion);
                    });
                }

                var competitions = competitionsQuery
                    .Select(comp => new
                    {
                        id = comp.Id,
                        name = comp.Name
                    })
                    .OrderBy(c => c.name)
                    .ToList();

                return Json(new { success = true, competitions = competitions });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading competitions: " + ex.Message });
            }
        }

        /// <summary>
        /// Update a registration's classes and/or start preference. Recomputes the linked
        /// invoice's totalAmount when classes change AND the invoice is still Pending; for
        /// already-Paid invoices the new fee is reported back for manual reconciliation.
        ///
        /// Auth: same four-tier rule as the rest of the per-competition surface (site admin /
        /// competition manager / club admin (incl. regional) / skjutledare for the comp's club).
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> UpdateCompetitionRegistration([FromBody] UpdateRegistrationRequest request)
        {
            try
            {
                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                    return Json(new { success = false, message = "Registration not found" });

                var competitionId = registration.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;

                // Four-tier auth — load competition first to find the club
                bool authorized = await _authService.IsCurrentUserAdminAsync();
                if (!authorized && competition != null)
                {
                    authorized = await _authService.IsCompetitionManager(competitionId);
                    if (!authorized)
                    {
                        var clubId = competition.GetValue<int>("clubId");
                        if (clubId > 0)
                        {
                            authorized = await _authService.IsClubAdminForClub(clubId)
                                      || await _authService.IsSkjutledareForClub(clubId);
                        }
                    }
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                // Build the new shootingClasses list. Three input shapes from the caller:
                //   1. ShootingClasses provided  → use it (per-class start prefs preferred,
                //      shared StartPreference applied as fallback when an entry omits one)
                //   2. Only StartPreference set → load existing classes, re-stamp every entry
                //   3. Neither                  → no class change (just touches save+publish)
                var existingClasses = HpskSite.Models.CompetitionRegistrationDocument
                    .DeserializeShootingClasses(registration.GetValue<string>("shootingClasses"));

                // When the caller passes a class list, preserve per-class state (StartPreference
                // and TeamNumber) for entries that were already on the registration. The Edit
                // modal in the management UI only sends explicit values for newly-added classes,
                // so the existing rows must keep what they had.
                var existingByClass = existingClasses
                    .GroupBy(c => c.Class.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var newClasses = request.ShootingClasses != null && request.ShootingClasses.Count > 0
                    ? request.ShootingClasses
                        .Where(c => !string.IsNullOrWhiteSpace(c.Class))
                        .Select(c =>
                        {
                            var key = c.Class.Trim();
                            existingByClass.TryGetValue(key, out var existing);
                            return new HpskSite.Models.ShootingClassEntry
                            {
                                Class = key,
                                StartPreference = c.StartPreference
                                    ?? existing?.StartPreference
                                    ?? request.StartPreference
                                    ?? "Inget",
                                TeamNumber = c.TeamNumber ?? existing?.TeamNumber
                            };
                        })
                        .ToList()
                    : existingClasses
                        .Select(c => new HpskSite.Models.ShootingClassEntry
                        {
                            Class = c.Class,
                            StartPreference = request.StartPreference ?? c.StartPreference ?? "Inget",
                            TeamNumber = c.TeamNumber
                        })
                        .ToList();

                if (newClasses.Count == 0)
                    return Json(new { success = false, message = "Anmälan måste ha minst en klass." });

                // Capacity check: same rule as walk-in, but exclude this registration's existing
                // contribution so re-saving without changing slots doesn't trip the guard.
                var dpConfigEdit = competition != null
                    ? HpskSite.Models.DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"))
                    : null;
                if (dpConfigEdit != null && newClasses.Any(c => c.TeamNumber.HasValue))
                {
                    var capacityError = ValidateCapacity(competitionId, dpConfigEdit, newClasses, request.RegistrationId);
                    if (capacityError != null)
                        return Json(new { success = false, message = capacityError });
                }

                var newClassesJson = HpskSite.Models.CompetitionRegistrationDocument
                    .SerializeShootingClasses(newClasses);
                registration.SetValue("shootingClasses", newClassesJson);

                // Persist the deltävling opt-in. We need to know the previous stored value to
                // decide whether the fee changed when only this flag flipped (no class diff).
                bool previousIsSubCompetition = registration.HasProperty("isSubCompetition")
                    && registration.GetValue<bool>("isSubCompetition");
                if (registration.HasProperty("isSubCompetition"))
                    registration.SetValue("isSubCompetition", request.IsSubCompetition);

                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                    return Json(new { success = false, message = "Failed to save registration" });

                _contentService.Publish(registration, new[] { "*" }, -1);

                // Direktplacering: any add/remove or slot reshuffle changes per-team occupancy,
                // so drop the availability cache and regenerate the auto-built start list so
                // both reflect the new state without operator action.
                bool teamAssignmentsChanged = newClasses.Any(c => c.TeamNumber.HasValue)
                    || existingClasses.Any(c => c.TeamNumber.HasValue);
                if (teamAssignmentsChanged && competitionId > 0)
                {
                    AppCaches.RuntimeCache.ClearByKey($"dp_availability_{competitionId}");
                    if (dpConfigEdit != null && competition != null)
                    {
                        _dpStartListService.Regenerate(competitionId, competition, dpConfigEdit);
                    }
                }

                // Invoice fee re-compute. We consider ALL non-cancelled invoices for this
                // registration (a registration can have several after a couple of class
                // adds: e.g. original Paid 100 + Paid top-up 50 + Pending top-up 30). The
                // logic is:
                //   sumPaid          = total of every Paid invoice
                //   existingPending  = the (single) Pending invoice if one exists
                //   currentlyBilled  = sumPaid + existingPending?.totalAmount
                //   delta            = newFee - sumPaid       (what the shooter still owes)
                //
                // If delta > 0:
                //   - patch the existing Pending invoice's totalAmount to delta, OR
                //   - create a new Pending top-up invoice for delta
                // If delta == 0:
                //   - nothing to collect; cancel a leftover Pending invoice if one exists
                // If delta < 0:
                //   - refund situation; surface as a manual-handling note
                string? feeChangeNote = null;
                int? topUpInvoiceId = null;
                bool classesChanged = !ClassListEquivalent(existingClasses, newClasses);
                bool subCompetitionChanged = previousIsSubCompetition != request.IsSubCompetition;
                if ((classesChanged || subCompetitionChanged) && competition != null)
                {
                    var classCodes = newClasses.Select(c => c.Class).ToList();
                    var newFee = HpskSite.Services.RegistrationFeeCalculator.Calculate(
                        competition, classCodes, request.IsSubCompetition);

                    var allInvoices = GetAllInvoicesForRegistration(competition, request.RegistrationId);
                    if (allInvoices.Count > 0)
                    {
                        decimal sumPaid = 0m;
                        IContent? existingPending = null;
                        foreach (var inv in allInvoices)
                        {
                            var s = (inv.GetValue<string>("paymentStatus") ?? "Pending").Trim().Trim('[', ']').Trim('"');
                            var amt = inv.GetValue<decimal>("totalAmount");
                            if (s == "Paid")
                            {
                                sumPaid += amt;
                            }
                            else if (s == "Pending" && existingPending == null)
                            {
                                existingPending = inv;
                            }
                        }

                        var delta = newFee - sumPaid;

                        if (delta > 0)
                        {
                            if (existingPending != null)
                            {
                                var oldPendingAmount = existingPending.GetValue<decimal>("totalAmount");
                                if (oldPendingAmount != delta)
                                {
                                    existingPending.SetValue("totalAmount", delta);
                                    _contentService.Save(existingPending);
                                    _contentService.Publish(existingPending, new[] { "*" }, -1);
                                    feeChangeNote = sumPaid > 0
                                        ? $"Tilläggsfaktura uppdaterad: {oldPendingAmount:0} kr → {delta:0} kr (totalt: {newFee:0} kr; redan betalt: {sumPaid:0} kr)."
                                        : $"Fakturabelopp uppdaterat: {oldPendingAmount:0} kr → {delta:0} kr.";
                                }
                            }
                            else
                            {
                                // Create a new Pending top-up invoice for the difference. The
                                // existing Paid invoices stay untouched as the historical
                                // record of what was actually paid, when, and by whom.
                                var memberId = registration.GetValue<int>("memberId");
                                var memberName = registration.GetValue<string>("memberName") ?? "";
                                var topUp = await _paymentService.CreateInvoiceAsync(
                                    competitionId,
                                    memberId.ToString(),
                                    memberName,
                                    request.RegistrationId,
                                    delta,
                                    "Swish");
                                if (topUp != null)
                                {
                                    topUpInvoiceId = topUp.Id;
                                    feeChangeNote = $"Tilläggsfaktura skapad för {delta:0} kr (totalt: {newFee:0} kr; redan betalt: {sumPaid:0} kr).";
                                }
                                else
                                {
                                    feeChangeNote = $"Avgiften ökade till {newFee:0} kr men en tilläggsfaktura kunde inte skapas. Hantera manuellt.";
                                }
                            }
                        }
                        else if (delta == 0)
                        {
                            // Already fully covered. If there's a leftover Pending invoice
                            // (e.g. a previous top-up that's no longer needed because a class
                            // was removed), cancel it so the row stops nagging the operator.
                            // Not if a samlingsfaktura is still charging for it: the parent's total
                            // includes this invoice and is never recalculated, so silently cancelling it
                            // here would leave the paying club covering a fee that has been written off.
                            if (existingPending != null
                                && _paymentService.IsCoveredByOpenConsolidation(existingPending.Id, out var pendCover, out var pendPaid))
                            {
                                feeChangeNote = $"Klassändringen täcks av befintliga betalningar ({sumPaid:0} kr), "
                                    + $"men den väntande tilläggsfakturan ingår i samlingsfaktura {pendCover} och har "
                                    + (pendPaid ? "INTE makulerats — skapa en kreditfaktura." : "INTE makulerats.");
                            }
                            else if (existingPending != null)
                            {
                                existingPending.SetValue("paymentStatus", "Cancelled");
                                _contentService.Save(existingPending);
                                _contentService.Publish(existingPending, new[] { "*" }, -1);
                                feeChangeNote = $"Klassändringen täcks av befintliga betalningar ({sumPaid:0} kr). Tidigare väntande tilläggsfaktura har makulerats.";
                            }
                        }
                        else // delta < 0
                        {
                            feeChangeNote = $"Avgiften minskade till {newFee:0} kr men {sumPaid:0} kr är redan betalt. Hantera återbetalning manuellt.";
                        }
                    }
                }

                return Json(new
                {
                    success = true,
                    message = "Registration updated successfully",
                    feeChangeNote,
                    topUpInvoiceId
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error updating registration: " + ex.Message });
            }
        }

        /// <summary>
        /// True when two ShootingClassEntry lists describe the same set of class codes
        /// (order-independent). Used to skip the invoice re-compute when only e.g. start
        /// preference changed.
        /// </summary>
        private static bool ClassListEquivalent(
            List<HpskSite.Models.ShootingClassEntry> a,
            List<HpskSite.Models.ShootingClassEntry> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            var aSet = a.Select(x => x.Class?.Trim() ?? "").OrderBy(x => x).ToList();
            var bSet = b.Select(x => x.Class?.Trim() ?? "").OrderBy(x => x).ToList();
            return aSet.SequenceEqual(bSet);
        }

        /// <summary>
        /// Look up the registrationInvoice document for a registration id, scoped to the
        /// competition's invoicesHub. Matches both the new single-int registrationId field
        /// and the legacy relatedRegistrationIds JSON array. Returns null when no match.
        /// </summary>
        private IContent? FindInvoiceForRegistration(IContent competition, int registrationId)
        {
            return GetAllInvoicesForRegistration(competition, registrationId).FirstOrDefault();
        }

        /// <summary>
        /// Return EVERY non-cancelled invoice linked to a registration id, newest first.
        /// A registration can have multiple invoices after one or more class adds (the
        /// original invoice + one or more Paid top-ups + at most one Pending top-up). The
        /// fee-recompute logic in UpdateCompetitionRegistration relies on summing across
        /// all of them, not just the most recent.
        /// </summary>
        private List<IContent> GetAllInvoicesForRegistration(IContent competition, int registrationId)
        {
            var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (hub == null) return new List<IContent>();

            return _contentService.GetPagedChildren(hub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "registrationInvoice")
                .Where(c => c.GetValue<string>("paymentStatus") != "Cancelled")
                .Where(c =>
                {
                    if (c.GetValue<int>("registrationId") == registrationId) return true;
                    var related = c.GetValue<string>("relatedRegistrationIds") ?? "";
                    return !string.IsNullOrEmpty(related)
                        && ParseRegistrationIds(related).Contains(registrationId);
                })
                .OrderByDescending(c => c.Id)
                .ToList();
        }

        /// <summary>
        /// Delete a competition registration
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> DeleteCompetitionRegistration([FromBody] DeleteRegistrationRequest request)
        {
            try
            {
                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                {
                    return Json(new { success = false, message = "Registration not found" });
                }

                // Four-tier auth — same rule as the rest of the per-competition surface so the
                // cashier (club admin / competition manager / skjutledare) can delete from the
                // desk, not just site admins.
                var competitionId = registration.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
                bool authorized = await _authService.IsCurrentUserAdminAsync();
                if (!authorized && competition != null)
                {
                    authorized = await _authService.IsCompetitionManager(competitionId);
                    if (!authorized)
                    {
                        var clubId = competition.GetValue<int>("clubId");
                        if (clubId > 0)
                        {
                            authorized = await _authService.IsClubAdminForClub(clubId)
                                      || await _authService.IsSkjutledareForClub(clubId);
                        }
                    }
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                // Cancel any Pending invoices linked to this registration BEFORE deleting it.
                // Otherwise they orphan and Bokföringsunderlag keeps listing them as
                // "Utestående betalningar" — no shooter exists to chase. Paid invoices stay
                // untouched: the money was actually collected and bookkeeping needs to see it.
                if (competition != null)
                {
                    var (actorId, actorName) = await GetCurrentActorAsync();
                    var linkedInvoices = GetAllInvoicesForRegistration(competition, request.RegistrationId);
                    foreach (var inv in linkedInvoices)
                    {
                        var status = inv.GetValue<string>("paymentStatus") ?? "Pending";
                        if (status.Trim().Trim('[', ']').Trim('"', '\'').Trim() == "Pending")
                        {
                            await _paymentService.UpdatePaymentStatusAsync(
                                invoiceId: inv.Id,
                                paymentStatus: "Cancelled",
                                notes: "Anmälan borttagen",
                                actorMemberId: actorId,
                                actorMemberName: actorName,
                                sendReceiptOnPaid: false);
                        }
                    }
                }

                var result = _contentService.Delete(registration);
                if (result.Success)
                {
                    return Json(new { success = true, message = "Registration deleted successfully" });
                }
                else
                {
                    return Json(new { success = false, message = "Failed to delete registration" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error deleting registration: " + ex.Message });
            }
        }

        /// <summary>
        /// Add a late registration for a competition after results entry has started
        /// IDENTITY-BASED RESULTS: This is now safe! Results are tied to MemberId, not position.
        /// Start list can be regenerated without losing existing results.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AddLateRegistration([FromBody] LateRegistrationRequest request)
        {
            try
            {
                // Validate competition exists
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Competition not found" });
                }

                // Authorization: site admin / competition manager / club admin / skjutledare
                // for the competition's club. Same four-tier rule as the rest of the per-competition
                // surface — late/walk-in registrations are part of running the competition, not a
                // privileged operation that only site admins can do.
                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(request.CompetitionId);
                if (!authorized)
                {
                    var competitionClubId = competition.GetValue<int>("clubId");
                    if (competitionClubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(competitionClubId)
                                  || await _authService.IsSkjutledareForClub(competitionClubId);
                    }
                }
                if (!authorized)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Validate member exists
                var member = _memberService.GetById(request.MemberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Check if member is already registered
                var existingRegistration = await CheckExistingRegistration(request.CompetitionId, request.MemberId);
                if (existingRegistration != null)
                {
                    return Json(new { success = false, message = $"{member.Name} is already registered for this competition" });
                }

                // Find or create the competition registrations hub
                var registrationsHub = GetOrCreateRegistrationsHub(competition);

                // Create new registration document
                var registration = _contentService.Create(
                    $"{member.Name} - {DateTime.Now:yyyy-MM-dd}",
                    registrationsHub.Id,
                    "competitionRegistration"
                );

                // Set registration properties
                registration.SetValue("competitionId", request.CompetitionId);
                registration.SetValue("memberId", request.MemberId);
                registration.SetValue("memberName", member.Name);

                // Get member's club
                var clubId = member.GetValue<int>("primaryClubId");
                var clubName = clubId > 0 ? _clubService.GetClubNameById(clubId) : "";
                registration.SetValue("clubId", clubId);

                // Build the shooting-class list. Two input shapes:
                //   1. Multi-class:   request.Classes provided  → one entry per class with optional
                //                     per-class teamNumber (Egenbokning slot picker)
                //   2. Single-class:  legacy shape using ShootingClass / StartPreference / TeamNumber
                List<HpskSite.Models.ShootingClassEntry> classEntries;
                if (request.Classes != null && request.Classes.Count > 0)
                {
                    classEntries = request.Classes
                        .Where(c => !string.IsNullOrWhiteSpace(c.Class))
                        .Select(c => new HpskSite.Models.ShootingClassEntry
                        {
                            Class = c.Class.Trim(),
                            StartPreference = c.StartPreference ?? request.StartPreference ?? "Inget",
                            TeamNumber = c.TeamNumber
                        })
                        .ToList();
                }
                else
                {
                    classEntries = new List<HpskSite.Models.ShootingClassEntry>
                    {
                        new()
                        {
                            Class = (request.ShootingClass ?? "").Trim(),
                            StartPreference = request.StartPreference ?? "Inget",
                            TeamNumber = request.TeamNumber
                        }
                    };
                }

                if (classEntries.Count == 0 || classEntries.Any(e => string.IsNullOrEmpty(e.Class)))
                {
                    return Json(new { success = false, message = "Anmälan måste ha minst en giltig klass." });
                }

                // Capacity validation: refuse over-booking direktplacering slots. Compute
                // existing usage across all *other* registrations, then verify each picked
                // teamNumber still has remaining positions after this walk-in lands.
                var dpConfig = HpskSite.Models.DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
                if (dpConfig != null && classEntries.Any(e => e.TeamNumber.HasValue))
                {
                    var capacityError = ValidateCapacity(competition.Id, dpConfig, classEntries, excludeRegistrationId: null);
                    if (capacityError != null)
                        return Json(new { success = false, message = capacityError });
                }

                var shootingClassesJson = HpskSite.Models.CompetitionRegistrationDocument
                    .SerializeShootingClasses(classEntries);
                registration.SetValue("shootingClasses", shootingClassesJson);

                registration.SetValue("registrationDate", DateTime.Now);
                registration.SetValue("registeredBy", "Admin (Late Registration)");
                registration.SetValue("isActive", true);
                if (registration.HasProperty("isSubCompetition"))
                    registration.SetValue("isSubCompetition", request.IsSubCompetition);

                // Save and publish
                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                {
                    return Json(new { success = false, message = "Failed to save registration" });
                }

                _contentService.Publish(registration, new[] { "*" }, -1);

                // Eager invoice: create the Pending invoice now so the registration carries it
                // from the start (best-effort; a free registration just returns null).
                await _paymentService.EnsureRegistrationInvoiceAsync(request.CompetitionId, registration.Id);

                // Direktplacering: invalidate availability cache and regenerate the auto-built
                // start list so the new shooter shows up on the right team without the operator
                // having to click "Generera startlista". Mirrors what RegisterForCompetition does.
                if (dpConfig != null && classEntries.Any(e => e.TeamNumber.HasValue))
                {
                    AppCaches.RuntimeCache.ClearByKey($"dp_availability_{request.CompetitionId}");
                    _dpStartListService.Regenerate(request.CompetitionId, competition, dpConfig);
                }

                var classCodes = classEntries.Select(e => e.Class).ToList();
                return Json(new
                {
                    success = true,
                    message = $"Late registration created for {member.Name}. The start list can now be regenerated without losing existing results.",
                    registrationId = registration.Id,
                    memberName = member.Name,
                    shootingClasses = classCodes,
                    canRegenerateStartList = true,
                    note = "Thanks to identity-based results, regenerating the start list will preserve all existing scores!"
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error creating late registration: " + ex.Message });
            }
        }

        /// <summary>
        /// Refuse over-booking when a walk-in / edit picks direktplacering slots. Returns null
        /// when capacity is OK; an error message otherwise. Existing usage is computed from
        /// every other registration (or all of them when excludeRegistrationId is null), then
        /// each picked team is checked against its configured Positions.
        /// </summary>
        private string? ValidateCapacity(
            int competitionId,
            HpskSite.Models.DirektplaceringConfig dpConfig,
            List<HpskSite.Models.ShootingClassEntry> proposedEntries,
            int? excludeRegistrationId)
        {
            var usage = _dpStartListService.GetTeamUsage(competitionId, excludeRegistrationId);

            // Bucket proposed assignments per team so a multi-class registration that puts two
            // shooters on the same team is checked against capacity once with the right count.
            var proposedPerTeam = proposedEntries
                .Where(e => e.TeamNumber.HasValue)
                .GroupBy(e => e.TeamNumber!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var (teamNumber, addCount) in proposedPerTeam)
            {
                var team = dpConfig.Teams.FirstOrDefault(t => t.TeamNumber == teamNumber);
                if (team == null)
                    return $"Skjutlag {teamNumber} finns inte längre i tävlingens konfiguration.";

                var existing = usage.GetValueOrDefault(teamNumber);
                if (existing + addCount > team.Positions)
                {
                    var remaining = Math.Max(0, team.Positions - existing);
                    return remaining == 0
                        ? $"Skjutlag {teamNumber} är fullt ({team.Positions} platser)."
                        : $"Skjutlag {teamNumber} har bara {remaining} ledig(a) plats(er) kvar — du försöker boka {addCount}.";
                }
            }
            return null;
        }

        /// <summary>
        /// Walk-in helper for non-direktplacering precision competitions: returns the teams
        /// from the current precisionStartList document with capacity info, so the cashier
        /// can drop a late shooter onto a specific start team without regenerating the
        /// whole list. Empty `teams` (with hasStartList=false) means there's nothing to
        /// pick — frontend hides the picker in that case.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetWalkInStartListTeams(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(competitionId);
                if (!authorized)
                {
                    var clubId = competition.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(clubId)
                                  || await _authService.IsSkjutledareForClub(clubId);
                    }
                }
                if (!authorized) return Json(new { success = false, message = "Access denied" });

                // Pick the most recent valid start list (matches GetOfficialStartList's rule).
                var startLists = _startListRepository.GetStartListsForCompetition(competitionId)
                    .Where(sl =>
                    {
                        var content = sl.GetValue<string>("startListContent");
                        return !string.IsNullOrEmpty(content) && !content.Contains("System.Threading.Tasks.Task");
                    })
                    .OrderByDescending(sl => sl.GetValue<DateTime>("generatedDate"))
                    .ToList();

                if (startLists.Count == 0)
                    return Json(new { success = true, hasStartList = false, teams = Array.Empty<object>() });

                var current = startLists.First();
                var configData = current.GetValue<string>("configurationData") ?? "";
                StartListConfiguration? config = null;
                try
                {
                    config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                }
                catch { /* fall through to empty */ }

                if (config?.Teams == null || config.Teams.Count == 0)
                    return Json(new { success = true, hasStartList = true, teams = Array.Empty<object>(), startListId = current.Id });

                var maxPer = config.Settings?.MaxShootersPerTeam ?? 30;
                var teams = config.Teams.Select(t => new
                {
                    teamNumber = t.TeamNumber,
                    startTime = t.StartTime,
                    endTime = t.EndTime,
                    weaponClasses = t.WeaponClasses,
                    shooterCount = t.Shooters?.Count ?? 0,
                    positionsTotal = maxPer,
                    positionsRemaining = Math.Max(0, maxPer - (t.Shooters?.Count ?? 0)),
                    isFull = (t.Shooters?.Count ?? 0) >= maxPer
                }).ToList();

                return Json(new
                {
                    success = true,
                    hasStartList = true,
                    startListId = current.Id,
                    teams
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Walk-in helper for non-direktplacering precision competitions: appends the
        /// just-registered shooter to the chosen team in the precisionStartList, one row
        /// per class on the registration. The renderer rebuilds the HTML so the published
        /// page reflects the new placement immediately. Refuses when the chosen team is
        /// at MaxShootersPerTeam capacity to keep the cashier from over-booking.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AssignWalkInToStartListTeam([FromBody] AssignWalkInToStartListTeamRequest request)
        {
            try
            {
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(request.CompetitionId);
                if (!authorized)
                {
                    var clubId = competition.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(clubId)
                                  || await _authService.IsSkjutledareForClub(clubId);
                    }
                }
                if (!authorized) return Json(new { success = false, message = "Access denied" });

                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                    return Json(new { success = false, message = "Anmälan hittades inte." });

                var classesJson = registration.GetValue<string>("shootingClasses") ?? "";
                var classes = HpskSite.Models.CompetitionRegistrationDocument.DeserializeShootingClasses(classesJson);
                if (classes.Count == 0)
                    return Json(new { success = false, message = "Anmälan saknar klass." });

                var memberId = registration.GetValue<int>("memberId");
                var memberName = registration.GetValue<string>("memberName") ?? "";
                var clubIdReg = registration.GetValue<int>("clubId");
                var clubName = clubIdReg > 0 ? (_clubService.GetClubNameById(clubIdReg) ?? "Okänd klubb") : "Okänd klubb";

                // Use the most recent valid start list, same pick rule as GetOfficialStartList.
                var startLists = _startListRepository.GetStartListsForCompetition(request.CompetitionId)
                    .Where(sl =>
                    {
                        var content = sl.GetValue<string>("startListContent");
                        return !string.IsNullOrEmpty(content) && !content.Contains("System.Threading.Tasks.Task");
                    })
                    .OrderByDescending(sl => sl.GetValue<DateTime>("generatedDate"))
                    .ToList();
                if (startLists.Count == 0)
                    return Json(new { success = false, message = "Ingen startlista finns för denna tävling." });

                var startList = startLists.First();
                var configData = startList.GetValue<string>("configurationData") ?? "";
                var config = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (config?.Teams == null)
                    return Json(new { success = false, message = "Startlistan har ingen konfigurationsdata." });

                var team = config.Teams.FirstOrDefault(t => t.TeamNumber == request.TeamNumber);
                if (team == null)
                    return Json(new { success = false, message = $"Skjutlag {request.TeamNumber} finns inte i startlistan." });

                team.Shooters ??= new List<StartListShooter>();

                var maxPer = config.Settings?.MaxShootersPerTeam ?? 30;
                if (team.Shooters.Count + classes.Count > maxPer)
                {
                    var remaining = Math.Max(0, maxPer - team.Shooters.Count);
                    return Json(new
                    {
                        success = false,
                        message = remaining == 0
                            ? $"Skjutlag {request.TeamNumber} är fullt ({maxPer} platser)."
                            : $"Skjutlag {request.TeamNumber} har bara {remaining} ledig(a) plats(er) kvar — du försöker boka {classes.Count}."
                    });
                }

                // Append a row per class. Position is recomputed below in one pass so adds
                // and removes both leave a gap-free list.
                foreach (var entry in classes)
                {
                    if (string.IsNullOrEmpty(entry.Class)) continue;
                    team.Shooters.Add(new StartListShooter
                    {
                        Position = team.Shooters.Count + 1,
                        Name = memberName,
                        Club = clubName,
                        WeaponClass = entry.Class,
                        MemberId = memberId
                    });

                    if (!team.WeaponClasses.Contains(entry.Class))
                        team.WeaponClasses.Add(entry.Class);
                }
                team.WeaponClasses = team.WeaponClasses.OrderBy(c => c).ToList();

                // Renumber positions across the whole team (defensive — covers the case where
                // the existing list had stale numbering).
                for (int i = 0; i < team.Shooters.Count; i++)
                {
                    team.Shooters[i].Position = i + 1;
                }
                team.ShooterCount = team.Shooters.Count;

                var competitionName = competition.Name ?? "";
                startList.SetValue("configurationData", JsonConvert.SerializeObject(config));
                startList.SetValue("startListContent", await _startListRenderer.GenerateStartListHtml(config, competitionName));

                var saveResult = _contentService.Save(startList);
                if (!saveResult.Success)
                    return Json(new { success = false, message = "Kunde inte spara startlistan." });

                _contentService.Publish(startList, new[] { "*" }, -1);

                return Json(new
                {
                    success = true,
                    teamNumber = team.TeamNumber,
                    addedShooters = classes.Count
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Transfer a registration from one member to another. Used when shooter A can't make
        /// it and shooter B takes their spot at the desk. Updates the registration in-place
        /// (preserving its id, classes, and any results-entry data linked by registrationId)
        /// and re-points every linked invoice to the new member, so the cashier doesn't have
        /// to delete + recreate (which would orphan the original payment).
        ///
        /// Auth: same four-tier rule as the rest of the per-competition surface.
        /// Conflict: refuses if the target member already has their own registration.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> TransferRegistration([FromBody] TransferRegistrationRequest request)
        {
            try
            {
                if (request == null || request.RegistrationId <= 0 || request.ToMemberId <= 0)
                    return Json(new { success = false, message = "Ogiltiga parametrar." });

                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                    return Json(new { success = false, message = "Anmälan kunde inte hittas." });

                var competitionId = registration.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                // Four-tier auth
                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(competitionId);
                if (!authorized)
                {
                    var clubId = competition.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(clubId)
                                  || await _authService.IsSkjutledareForClub(clubId);
                    }
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                // Resolve the new member
                var toMember = _memberService.GetById(request.ToMemberId);
                if (toMember == null)
                    return Json(new { success = false, message = "Mottagande medlem kunde inte hittas." });

                var fromMemberId = registration.GetValue<int>("memberId");
                var fromMemberName = registration.GetValue<string>("memberName") ?? "";
                if (fromMemberId == request.ToMemberId)
                    return Json(new { success = false, message = "Anmälan är redan kopplad till denna medlem." });

                // Conflict guard: target member can't already be registered for this competition
                var existing = await CheckExistingRegistration(competitionId, request.ToMemberId);
                if (existing != null && existing.Id != registration.Id)
                {
                    return Json(new
                    {
                        success = false,
                        message = $"{toMember.Name} är redan anmäld till denna tävling. Slå ihop manuellt eller välj en annan mottagare."
                    });
                }

                // Resolve the new member's club for the registration's clubId field
                var toClubId = 0;
                var toClubIdStr = toMember.GetValue<string>("primaryClubId");
                if (!string.IsNullOrEmpty(toClubIdStr)) int.TryParse(toClubIdStr, out toClubId);
                var toClubName = toClubId > 0 ? _clubService.GetClubNameById(toClubId) : null;

                // Update the registration in-place. Don't touch shootingClasses or
                // registrationDate — the spot, classes, and date all transfer to the new
                // shooter as-is. Update the document's display Name so the backoffice tree
                // shows the new shooter; preserve the original date in the suffix.
                registration.SetValue("memberId", request.ToMemberId);
                registration.SetValue("memberName", toMember.Name ?? "");
                if (toClubId > 0) registration.SetValue("clubId", toClubId);
                registration.Name = $"{toMember.Name} - {DateTime.Now:yyyy-MM-dd}";

                var saveReg = _contentService.Save(registration);
                if (!saveReg.Success)
                    return Json(new { success = false, message = "Kunde inte spara den uppdaterade anmälan." });
                _contentService.Publish(registration, new[] { "*" }, -1);

                // Re-point every linked invoice. Each invoice's audit history gets a
                // Transferred row capturing both the from and to members so the trail
                // stays intact even after the invoice's own memberName/memberId are
                // overwritten.
                var (actorId, actorName) = await GetCurrentActorAsync();
                var invoices = GetAllInvoicesForRegistration(competition, request.RegistrationId);
                foreach (var inv in invoices)
                {
                    inv.SetValue("memberId", request.ToMemberId.ToString());
                    inv.SetValue("memberName", toMember.Name ?? "");
                    var saveInv = _contentService.Save(inv);
                    if (saveInv.Success)
                    {
                        _contentService.Publish(inv, new[] { "*" }, -1);
                        await _auditService.LogAsync(
                            invoiceId: inv.Id,
                            competitionId: competitionId,
                            eventType: HpskSite.Models.InvoicePaymentEventTypes.Transferred,
                            byMemberId: actorId,
                            byMemberName: actorName,
                            amount: inv.GetValue<decimal>("totalAmount"),
                            reference: inv.GetValue<string>("invoiceNumber"),
                            notes: $"Betalningsunderlag överfört från {fromMemberName} (id {fromMemberId}) till {toMember.Name} (id {request.ToMemberId})");
                    }
                }

                return Json(new
                {
                    success = true,
                    message = $"Anmälan överförd från {fromMemberName} till {toMember.Name}.",
                    registrationId = registration.Id,
                    invoicesUpdated = invoices.Count
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod vid överföring: " + ex.Message });
            }
        }

        /// <summary>
        /// Toggle the at-the-desk attendance state on a registration (item #9). The flag
        /// lives on the registration document so it persists across page reloads and is
        /// visible to anyone reading the registrations table — no SQL migration needed.
        /// Auth: same four-tier rule as the rest of the per-competition surface.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SetCheckedIn([FromBody] SetCheckedInRequest request)
        {
            try
            {
                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                    return Json(new { success = false, message = "Anmälan hittades inte." });

                var competitionId = registration.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(competitionId);
                if (!authorized)
                {
                    var competitionClubId = competition.GetValue<int>("clubId");
                    if (competitionClubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(competitionClubId)
                                  || await _authService.IsSkjutledareForClub(competitionClubId);
                    }
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                registration.SetValue("isCheckedIn", request.CheckedIn);

                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                    return Json(new { success = false, message = "Misslyckades att spara närvarostatus." });

                _contentService.Publish(registration, new[] { "*" }, -1);

                return Json(new { success = true, isCheckedIn = request.CheckedIn });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// List the teams and relays (stafett) registered for a competition, with each one's
        /// payment status, for the "Lag" section of the Anmälningar tab. Discipline-agnostic —
        /// any competition with teams/relay enabled. Team invoices use memberId "team-{teamId}".
        /// Auth: same four-tier rule as the rest of the per-competition desk surface.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCompetitionTeams(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(competitionId);
                if (!authorized)
                {
                    var clubId = competition.GetValue<int>("clubId");
                    if (clubId > 0)
                        authorized = await _authService.IsClubAdminForClub(clubId)
                                  || await _authService.IsSkjutledareForClub(clubId);
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                var teams = await _teamService.GetTeamsForCompetitionAsync(competitionId);

                // Pre-load team invoices (memberId "team-{id}", newest non-cancelled per team).
                var teamInvoices = new Dictionary<string, IContent>();
                var invoicesHub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
                if (invoicesHub != null)
                {
                    foreach (var inv in _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                        .Where(c => c.ContentType.Alias == "registrationInvoice")
                        .OrderByDescending(c => c.Id))
                    {
                        var mid = inv.GetValue<string>("memberId") ?? "";
                        if (mid.StartsWith("team-")
                            && !teamInvoices.ContainsKey(mid)
                            && CleanPaymentStatus(inv.GetValue<string>("paymentStatus") ?? "") != "Cancelled")
                        {
                            teamInvoices[mid] = inv;
                        }
                    }
                }

                decimal.TryParse(competition.GetValue<string>("teamRegistrationFee") ?? "0", out var teamFee);
                decimal.TryParse(competition.GetValue<string>("stafettRegistrationFee") ?? "0", out var stafettFee);

                var result = teams.Select(t =>
                {
                    teamInvoices.TryGetValue($"team-{t.Team.Id}", out var inv);
                    var fee = t.Team.IsRelay ? stafettFee : teamFee;
                    string status = inv != null
                        ? CleanPaymentStatus(inv.GetValue<string>("paymentStatus") ?? "Unknown")
                        : (fee > 0 ? "No Invoice" : "No Fee");

                    return new
                    {
                        id = t.Team.Id,
                        teamName = t.Team.TeamName,
                        teamClass = t.Team.TeamClass,
                        isRelay = t.Team.IsRelay,
                        clubName = t.ClubName ?? "",
                        members = t.Members.Select(m => new { memberId = m.MemberId, name = m.Name, isSpare = m.IsSpare }),
                        memberCount = t.Members.Count,
                        paymentStatus = status,
                        invoiceId = inv?.Id ?? 0,
                        invoiceNumber = inv?.GetValue<string>("invoiceNumber") ?? "",
                        amount = inv?.GetValue<decimal>("totalAmount") ?? fee
                    };
                }).ToList();

                return Json(new { success = true, teams = result });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Ensure a team/relay has its Pending fee invoice so the cashier can mark it paid
        /// (covers teams created before eager invoicing or with "Betala senare"). Idempotent.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> EnsureTeamInvoice([FromBody] EnsureTeamInvoiceRequest request)
        {
            try
            {
                if (request == null || request.TeamId <= 0 || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "competitionId och teamId krävs." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(request.CompetitionId);
                if (!authorized)
                {
                    var clubId = competition.GetValue<int>("clubId");
                    if (clubId > 0)
                        authorized = await _authService.IsClubAdminForClub(clubId)
                                  || await _authService.IsSkjutledareForClub(clubId);
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                var invoiceId = await _teamService.EnsureTeamInvoiceAsync(request.CompetitionId, request.TeamId);
                if (invoiceId == 0)
                    return Json(new { success = false, message = "Ingen lagavgift är konfigurerad för denna tävling." });

                var invoice = _contentService.GetById(invoiceId);
                return Json(new
                {
                    success = true,
                    invoiceId,
                    invoiceNumber = invoice?.GetValue<string>("invoiceNumber") ?? invoiceId.ToString(),
                    amount = invoice?.GetValue<decimal>("totalAmount") ?? 0m
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Ensure a registration has an invoice so the cashier can record a payment
        /// (cash/bankgiro/Swish-via-app/etc.) — WITHOUT requiring a Swish number on the
        /// competition. The old desk flow lazily created the invoice via
        /// Swish/GeneratePaymentQR, which bails with "Ingen Swish-nummer är konfigurerad"
        /// when Swish isn't set up, blocking the entirely Swish-independent "Registrera
        /// betalning" action. This endpoint computes the fee via RegistrationFeeCalculator
        /// (single source of truth) and creates the invoice directly.
        ///
        /// Idempotent: if a non-cancelled invoice already exists for the registration it's
        /// returned as-is rather than creating a duplicate.
        /// Auth: same four-tier rule as the rest of the per-competition surface.
        /// </summary>
        /// <summary>
        /// Find every registration on a competition that SHOULD have an invoice but doesn't, and mint
        /// the missing ones. Idempotent — it calls the same single source of truth as
        /// <see cref="EnsureInvoice"/> per registration, so re-running it is harmless.
        ///
        /// Why this needs to exist: the eager invoice for a new registration is created by a BACKGROUND
        /// job (CompetitionController enqueues it ~12 s after the registration publishes) and it is
        /// best-effort — if the app pool recycles inside that window, or content locks are contended
        /// during a registration burst, the job is lost silently. Re-registering does NOT retry it: an
        /// update only reconciles the invoice when the FEE changed, so a registration that lost its
        /// invoice stays without one permanently. Such a registration cannot be paid at all — not
        /// individually and not through a samlingsfaktura, because there is no invoice to select.
        ///
        /// Observed on dev while testing the SM flow: five Springskytte registrations reported their fee
        /// but ended up with no invoice, and no amount of re-registering fixed them.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> EnsureMissingInvoices([FromBody] EnsureMissingInvoicesRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "competitionId krävs." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null || competition.ContentType.Alias != "competition")
                    return Json(new { success = false, message = "Tävling hittades inte." });

                // Same four-tier rule as the rest of the per-competition surface, plus the region path
                // (a region-hosted competition's organiser is the krets).
                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(request.CompetitionId);
                if (!authorized)
                {
                    var clubId = competition.GetValue<int>("clubId");
                    if (clubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(clubId)
                                  || await _authService.IsSkjutledareForClub(clubId);
                    }
                    else
                    {
                        var regionCode = (competition.GetValue<string>("regionalFederation") ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(regionCode))
                            authorized = await _authService.IsRegionalAdminForRegion(regionCode);
                    }
                }
                if (!authorized) return Json(new { success = false, message = "Access denied" });

                var children = _contentService.GetPagedChildren(request.CompetitionId, 0, 200, out _).ToList();
                var regsHub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
                var invoicesHub = children.FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");

                var registrations = regsHub == null
                    ? new List<IContent>()
                    : _contentService.GetPagedChildren(regsHub.Id, 0, 2000, out _)
                        .Where(c => c.ContentType.Alias == "competitionRegistration").ToList();

                // A registration counts as covered when ANY non-cancelled invoice references it, either
                // by registrationId or via the legacy relatedRegistrationIds list.
                var covered = new HashSet<int>();
                if (invoicesHub != null)
                {
                    foreach (var inv in _contentService.GetPagedChildren(invoicesHub.Id, 0, 5000, out _)
                                 .Where(c => c.ContentType.Alias == "registrationInvoice"))
                    {
                        var status = (inv.GetValue<string>("paymentStatus") ?? "").Trim().Trim('[', ']').Trim('"');
                        if (string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase)) continue;

                        var rid = inv.GetValue<int>("registrationId");
                        if (rid > 0) covered.Add(rid);

                        var related = inv.GetValue<string>("relatedRegistrationIds") ?? "";
                        foreach (System.Text.RegularExpressions.Match m in
                                 System.Text.RegularExpressions.Regex.Matches(related, @"\d+"))
                            if (int.TryParse(m.Value, out var legacyId)) covered.Add(legacyId);
                    }
                }

                int created = 0, alreadyOk = 0, noFee = 0, failed = 0;
                var createdList = new List<object>();
                foreach (var reg in registrations)
                {
                    if (covered.Contains(reg.Id)) { alreadyOk++; continue; }
                    try
                    {
                        var invoice = await _paymentService.EnsureRegistrationInvoiceAsync(request.CompetitionId, reg.Id);
                        if (invoice == null) { noFee++; continue; }   // free registration — nothing owed
                        created++;
                        createdList.Add(new
                        {
                            registrationId = reg.Id,
                            memberName = reg.GetValue<string>("memberName") ?? "",
                            invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? "",
                            amount = invoice.GetValue<decimal>("totalAmount")
                        });
                    }
                    catch (Exception ex)
                    {
                        // No ILogger on this controller; the count is reported to the caller instead so a
                        // partial repair is never silent.
                        Console.WriteLine($"EnsureMissingInvoices: registration {reg.Id} failed: {ex.Message}");
                        failed++;
                    }
                }

                InvalidateInvoiceCachesForCompetition();

                var msg = created == 0
                    ? (failed > 0
                        ? $"Inga fakturor kunde skapas ({failed} misslyckades)."
                        : $"Alla anmälningar har redan faktura ({alreadyOk} st"
                          + (noFee > 0 ? $", {noFee} avgiftsfria" : "") + ").")
                    : $"{created} saknad(e) faktura(or) skapades."
                      + (alreadyOk > 0 ? $" {alreadyOk} hade redan." : "")
                      + (noFee > 0 ? $" {noFee} är avgiftsfria." : "")
                      + (failed > 0 ? $" {failed} MISSLYCKADES." : "");

                return Json(new
                {
                    success = true, message = msg,
                    registrations = registrations.Count,
                    created, alreadyOk, noFee, failed,
                    createdInvoices = createdList
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>Drop the admin invoice-list cache so a repaired competition shows up at once.</summary>
        private void InvalidateInvoiceCachesForCompetition()
        {
            try { AppCaches.RuntimeCache.ClearByRegex("^admin_invoices_"); } catch { }
        }

        [HttpPost]
        public async Task<IActionResult> EnsureInvoice([FromBody] EnsureInvoiceRequest request)
        {
            try
            {
                if (request == null || request.RegistrationId <= 0)
                    return Json(new { success = false, message = "registrationId krävs." });

                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null || registration.ContentType.Alias != "competitionRegistration")
                    return Json(new { success = false, message = "Anmälan hittades inte." });

                var competitionId = registration.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
                if (competition == null)
                    return Json(new { success = false, message = "Tävling hittades inte." });

                bool authorized = await _authService.IsCurrentUserAdminAsync()
                    || await _authService.IsCompetitionManager(competitionId);
                if (!authorized)
                {
                    var competitionClubId = competition.GetValue<int>("clubId");
                    if (competitionClubId > 0)
                    {
                        authorized = await _authService.IsClubAdminForClub(competitionClubId)
                                  || await _authService.IsSkjutledareForClub(competitionClubId);
                    }
                }
                if (!authorized)
                    return Json(new { success = false, message = "Access denied" });

                // Single source of truth — idempotently creates the Pending invoice (or returns
                // the existing one). Returns null only for free registrations / external comps.
                var invoice = await _paymentService.EnsureRegistrationInvoiceAsync(competitionId, request.RegistrationId);
                if (invoice == null)
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad för denna tävling." });

                return Json(new
                {
                    success = true,
                    invoiceId = invoice.Id,
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString(),
                    amount = invoice.GetValue<decimal>("totalAmount")
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Resolve the current logged-in member to (id, name) for stamping audit rows.
        /// Returns (null, null) when no member is logged in or the lookup fails.
        /// </summary>
        private async Task<(int? id, string? name)> GetCurrentActorAsync()
        {
            try
            {
                var current = await _memberManager.GetCurrentMemberAsync();
                if (current == null) return (null, null);
                var data = _memberService.GetByEmail(current.Email ?? string.Empty);
                if (data == null) return (null, current.Name);
                return (data.Id, data.Name ?? current.Name);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>
        /// Check if a member is already registered for a competition
        /// </summary>
        private async Task<IContent?> CheckExistingRegistration(int competitionId, int memberId)
        {
            // PERFORMANCE FIX: Direct traversal from competition instead of loading entire site tree
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return null;

            // Get registrations hub (only load first 20 children, hub is usually near top)
            var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null) return null;

            // Search only within registrations hub for this specific member
            var registrations = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _);
            return registrations.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistration" &&
                c.GetValue<int>("memberId") == memberId);
        }

        /// <summary>
        /// Get or create the registrations hub for a competition
        /// </summary>
        private IContent GetOrCreateRegistrationsHub(IContent competition)
        {
            // PERFORMANCE FIX: Only load first 20 children instead of int.MaxValue
            var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
            var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");

            if (hub == null)
            {
                // Create new registrations hub
                hub = _contentService.Create("Anmälningar", competition.Id, "competitionRegistrationsHub");
                _contentService.Save(hub);
                // Publish the hub
                _contentService.Publish(hub, new[] { "*" }, -1);
            }

            return hub;
        }

        /// <summary>
        /// Export all registrations for a single competition as a CSV (semicolon-separated,
        /// UTF-8 with BOM so Excel opens it correctly with Swedish characters). Includes
        /// payment columns so the treasurer can reconcile against bank/Swish statements.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportCompetitionRegistrations(int competitionId)
        {
            // Same four-tier auth as the per-competition registration view:
            // site admin OR competition manager OR club admin (incl. regional) OR skjutledare.
            if (competitionId <= 0)
                return BadRequest("competitionId is required");

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return NotFound("Competition not found");

            bool authorized = await _authService.IsCurrentUserAdminAsync()
                || await _authService.IsCompetitionManager(competitionId);
            if (!authorized)
            {
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    authorized = await _authService.IsClubAdminForClub(competitionClubId)
                              || await _authService.IsSkjutledareForClub(competitionClubId);
                }
            }
            if (!authorized) return Forbid();

            try
            {
                // Load registrations + invoices in one pass
                var competitionChildren = _contentService.GetPagedChildren(competitionId, 0, 100, out _).ToList();
                var registrationsHub = competitionChildren
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");

                var registrations = registrationsHub != null
                    ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                        .Where(c => c.ContentType.Alias == "competitionRegistration")
                        .ToList()
                    : new List<IContent>();

                // Build invoice lookup keyed by registrationId. A registration may match either
                // via the new invoice.registrationId int field or via the legacy
                // relatedRegistrationIds JSON array.
                var invoicesHub = competitionChildren
                    .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
                var invoiceByRegId = new Dictionary<int, IContent>();
                if (invoicesHub != null)
                {
                    var invoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                        .Where(x => x.ContentType.Alias == "registrationInvoice")
                        .OrderByDescending(x => x.Id) // most recent invoice wins on collisions
                        .ToList();

                    foreach (var inv in invoices)
                    {
                        var single = inv.GetValue<int>("registrationId");
                        if (single > 0 && !invoiceByRegId.ContainsKey(single))
                            invoiceByRegId[single] = inv;

                        var relatedJson = inv.GetValue<string>("relatedRegistrationIds") ?? "";
                        foreach (var regId in ParseRegistrationIds(relatedJson))
                        {
                            if (!invoiceByRegId.ContainsKey(regId))
                                invoiceByRegId[regId] = inv;
                        }
                    }
                }

                var competitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";

                // Build CSV using ; separator (Swedish Excel default) and quote every field
                // so commas and embedded line breaks in notes don't break parsing.
                var csv = new System.Text.StringBuilder();
                csv.Append('﻿'); // BOM so Excel detects UTF-8
                csv.AppendLine(string.Join(";", new[]
                {
                    "Tävling", "Skytt", "Klubb", "Klasser", "Startpreferenser",
                    "Anmälningsdatum", "Anmäld av", "Aktiv", "Skytt-anteckning",
                    "Fakturanummer", "Belopp", "Betalstatus", "Betalningsmetod",
                    "Betaldatum", "Transaktions-ID", "Faktura-anteckning"
                }.Select(QuoteCsv)));

                foreach (var reg in registrations.OrderBy(r => r.GetValue<string>("memberName") ?? ""))
                {
                    var clubId = reg.GetValue<int>("clubId");
                    var clubName = clubId > 0 ? (_clubService.GetClubNameById(clubId) ?? "") : "";
                    if (string.IsNullOrEmpty(clubName))
                    {
                        var legacy = reg.GetValue<string>("memberClub") ?? "";
                        clubName = int.TryParse(legacy, out var legacyId) ? (_clubService.GetClubNameById(legacyId) ?? "") : legacy;
                    }

                    // Decode shootingClasses JSON array. For each entry write the class id and
                    // its preference. Multi-class registrations get pipe-separated lists in one cell.
                    var shootingClassesJson = reg.GetValue<string>("shootingClasses") ?? "";
                    var classEntries = HpskSite.Models.CompetitionRegistrationDocument.DeserializeShootingClasses(shootingClassesJson);
                    var classes = classEntries.Count > 0
                        ? string.Join(" | ", classEntries.Select(c => c.Class))
                        : (reg.GetValue<string>("shootingClass") ?? "");
                    var preferences = classEntries.Count > 0
                        ? string.Join(" | ", classEntries.Select(c => c.StartPreference ?? "Inget"))
                        : (reg.GetValue<string>("startPreference") ?? "Inget");

                    invoiceByRegId.TryGetValue(reg.Id, out var invoice);
                    var invoiceNumber = invoice?.GetValue<string>("invoiceNumber") ?? "";
                    var amount = invoice?.GetValue<decimal>("totalAmount") ?? 0m;
                    var paymentStatus = CleanPaymentStatus(invoice?.GetValue<string>("paymentStatus") ?? "No Invoice");
                    var paymentMethod = invoice?.GetValue<string>("paymentMethod") ?? "";
                    var paymentDate = invoice?.GetValue<DateTime?>("paymentDate");
                    var transactionId = invoice?.GetValue<string>("transactionId") ?? "";
                    var invoiceNotes = invoice?.GetValue<string>("notes") ?? "";

                    csv.AppendLine(string.Join(";", new[]
                    {
                        QuoteCsv(competitionName),
                        QuoteCsv(reg.GetValue<string>("memberName") ?? ""),
                        QuoteCsv(clubName),
                        QuoteCsv(classes),
                        QuoteCsv(preferences),
                        QuoteCsv(reg.GetValue<DateTime>("registrationDate").ToString("yyyy-MM-dd HH:mm")),
                        QuoteCsv(reg.GetValue<string>("registeredBy") ?? ""),
                        QuoteCsv(reg.GetValue<bool>("isActive") ? "Ja" : "Nej"),
                        QuoteCsv(reg.GetValue<string>("shooterNotes") ?? ""),
                        QuoteCsv(invoiceNumber),
                        QuoteCsv(amount > 0 ? amount.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : ""),
                        QuoteCsv(paymentStatus),
                        QuoteCsv(paymentMethod),
                        QuoteCsv(paymentDate?.ToString("yyyy-MM-dd") ?? ""),
                        QuoteCsv(transactionId),
                        QuoteCsv(invoiceNotes)
                    }));
                }

                var safeName = System.Text.RegularExpressions.Regex.Replace(competitionName, @"[^\w\-]", "_");
                if (string.IsNullOrEmpty(safeName)) safeName = $"comp_{competitionId}";
                var fileName = $"Anmalningar_{safeName}_{DateTime.Now:yyyy-MM-dd}.csv";

                return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", fileName);
            }
            catch (Exception ex)
            {
                return BadRequest("Error exporting registrations: " + ex.Message);
            }
        }

        /// <summary>
        /// Wrap a CSV cell in double quotes and escape embedded quotes by doubling them.
        /// </summary>
        private static string QuoteCsv(string value)
        {
            if (value == null) return "\"\"";
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Recursively gets all descendants of a content node
        /// </summary>
        private IEnumerable<IContent> GetAllDescendants(IContent content)
        {
            yield return content;

            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out var totalRecords);
            foreach (var child in children)
            {
                foreach (var descendant in GetAllDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Gets the name of a competition by its ID
        /// </summary>
        private string GetCompetitionName(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                return competition?.Name ?? "Unknown Competition";
            }
            catch
            {
                return "Unknown Competition";
            }
        }

        /// <summary>
        /// Gets the payment status for a registration by checking invoices
        /// </summary>
        private string GetRegistrationPaymentStatus(int registrationId, int competitionId)
        {
            try
            {
                // Get the competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return "No Invoice";

                // Find the "Betalningar" (invoices) hub under the competition
                var children = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _);
                var invoicesHub = children.FirstOrDefault(x => x.ContentType.Alias == "registrationInvoicesHub");

                if (invoicesHub == null) return NoInvoiceOrNoFee(competition, registrationId);

                // Get all invoices under the hub - filter out cancelled and sort by most recent
                var allInvoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, int.MaxValue, out _)
                    .Where(x => x.ContentType.Alias == "registrationInvoice")
                    .Where(x => x.GetValue<string>("paymentStatus") != "Cancelled")
                    .OrderByDescending(x => x.Id)
                    .ToList();

                // Match the invoice by the registration's own id (the field CreateInvoiceAsync
                // writes) OR the legacy relatedRegistrationIds array. Matching only the latter
                // used to miss every invoice created via CreateInvoiceAsync.
                foreach (var invoice in allInvoices)
                {
                    var single = invoice.GetValue<int>("registrationId");
                    var relatedIds = ParseRegistrationIds(invoice.GetValue<string>("relatedRegistrationIds") ?? "");
                    if (single == registrationId || relatedIds.Contains(registrationId))
                    {
                        // Clean up if it's in JSON array format like ["Paid"]
                        return CleanPaymentStatus(invoice.GetValue<string>("paymentStatus") ?? "Unknown");
                    }
                }

                return NoInvoiceOrNoFee(competition, registrationId);
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// When a registration has no invoice, distinguish a free registration ("No Fee" →
        /// "Ingen avgift") from a fee-bearing one that's missing its invoice ("No Invoice" →
        /// "Saknar Faktura", the error/edge case now that invoices are created eagerly).
        /// </summary>
        private string NoInvoiceOrNoFee(IContent competition, int registrationId)
        {
            try
            {
                var reg = _contentService.GetById(registrationId);
                if (reg == null) return "No Invoice";
                var entries = HpskSite.Models.CompetitionRegistrationDocument
                    .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "");
                var codes = entries.Select(e => e.Class).Where(c => !string.IsNullOrEmpty(c)).ToList();
                var isSub = reg.HasProperty("isSubCompetition") && reg.GetValue<bool>("isSubCompetition");
                var classesForCalc = codes.Count > 0
                    ? (IReadOnlyCollection<string>)codes
                    : new[] { string.Empty };
                var fee = HpskSite.Services.RegistrationFeeCalculator.Calculate(competition, classesForCalc, isSub);
                return fee > 0 ? "No Invoice" : "No Fee";
            }
            catch
            {
                return "No Invoice";
            }
        }

        /// <summary>
        /// Parses a JSON array of registration IDs from string format: "[123, 124, 125]"
        /// </summary>
        private List<int> ParseRegistrationIds(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int>();

            try
            {
                // Remove brackets and whitespace
                var trimmed = json.Trim().Trim('[', ']');
                if (string.IsNullOrWhiteSpace(trimmed)) return new List<int>();

                // Split by comma and parse each ID
                return trimmed.Split(',')
                    .Select(id => id.Trim())
                    .Where(id => int.TryParse(id, out _))
                    .Select(int.Parse)
                    .ToList();
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// Cleans payment status string, removing JSON array formatting if present
        /// Converts: ["Paid"] -> Paid, or ["Pending"] -> Pending
        /// </summary>
        private string CleanPaymentStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return "Unknown";

            // Remove JSON array brackets and quotes
            var cleaned = status.Trim().Trim('[', ']').Trim('"', '\'').Trim();

            return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
        }

        #endregion

        #region Request Models

        /// <summary>
        /// Request model for updating a registration. ShootingClasses (when present) overwrites
        /// the registration's class list and triggers a re-compute of the linked invoice's fee
        /// for Pending invoices. StartPreference (when present) is applied to every class on
        /// the registration. Either field may be omitted to leave the corresponding state alone.
        /// </summary>
        public class UpdateRegistrationRequest
        {
            public int RegistrationId { get; set; }
            public string? StartPreference { get; set; }
            public List<UpdateRegistrationClass>? ShootingClasses { get; set; }
            public bool IsSubCompetition { get; set; } // mirrors the registration's existing flag
        }

        /// <summary>One class entry in an UpdateRegistrationRequest.</summary>
        public class UpdateRegistrationClass
        {
            public string Class { get; set; } = "";
            public string? StartPreference { get; set; }
            /// <summary>Direktplacering team/slot for this class. When omitted on a class
            /// the registration already had, the existing entry's team is preserved.</summary>
            public int? TeamNumber { get; set; }
        }

        /// <summary>
        /// Request model for deleting registration
        /// </summary>
        public class DeleteRegistrationRequest
        {
            public int RegistrationId { get; set; }
        }

        /// <summary>
        /// Request model for adding late registration
        /// </summary>
        public class LateRegistrationRequest
        {
            public int CompetitionId { get; set; }
            public int MemberId { get; set; }
            /// <summary>Single-class shorthand. Used when Classes is null/empty so the legacy
            /// callers (and the rolling-start-only path) keep working unchanged.</summary>
            public string ShootingClass { get; set; } = "";
            public string? StartPreference { get; set; }
            public string? Notes { get; set; }
            /// <summary>Direktplacering single-class shorthand: applies to ShootingClass when
            /// the multi-class Classes field is not provided.</summary>
            public int? TeamNumber { get; set; }
            /// <summary>Multi-class walk-in. When set, ShootingClass / StartPreference /
            /// TeamNumber are ignored. Each entry can carry its own slot in direktplacering
            /// competitions, so a shooter walking up to register A + C with different start
            /// times completes in one round trip.</summary>
            public List<UpdateRegistrationClass>? Classes { get; set; }
            /// <summary>Opt-in to the competition's deltävling (sub-competition). Persisted on
            /// the registration so subsequent fee recomputes via RegistrationFeeCalculator add
            /// the surcharge consistently. Defaults to false when the cashier doesn't tick it.</summary>
            public bool IsSubCompetition { get; set; }
        }

        /// <summary>
        /// Request model for transferring an existing registration to a different member.
        /// FromMemberId is informational — the source is the registration's current owner.
        /// </summary>
        public class TransferRegistrationRequest
        {
            public int RegistrationId { get; set; }
            public int ToMemberId { get; set; }
        }

        /// <summary>
        /// Request model for the at-the-desk check-in toggle (item #9).
        /// </summary>
        public class SetCheckedInRequest
        {
            public int RegistrationId { get; set; }
            public bool CheckedIn { get; set; }
        }

        /// <summary>
        /// Request model for ensuring a registration has an invoice (Swish-independent
        /// payment recording).
        /// </summary>
        /// <summary>Repair every registration on a competition that lost its eager invoice.</summary>
        public class EnsureMissingInvoicesRequest
        {
            public int CompetitionId { get; set; }
        }

        public class EnsureInvoiceRequest
        {
            public int RegistrationId { get; set; }
        }

        /// <summary>Request model for ensuring a team/relay has its fee invoice.</summary>
        public class EnsureTeamInvoiceRequest
        {
            public int CompetitionId { get; set; }
            public int TeamId { get; set; }
        }

        /// <summary>
        /// Walk-in helper request: drop a just-registered shooter onto a specific start
        /// team in a non-direktplacering precision competition.
        /// </summary>
        public class AssignWalkInToStartListTeamRequest
        {
            public int CompetitionId { get; set; }
            public int RegistrationId { get; set; }
            public int TeamNumber { get; set; }
        }

        #endregion
    }
}
