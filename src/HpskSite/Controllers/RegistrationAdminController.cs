using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;
using Umbraco.Cms.Core.Models;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Handles all competition registration management operations for administrators.
    /// Extracted from AdminController as part of the controller refactoring.
    /// </summary>
    public class RegistrationAdminController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _authService;
        private readonly ClubService _clubService;
        private readonly PaymentService _paymentService;

        public RegistrationAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IContentService contentService,
            AdminAuthorizationService authService,
            ClubService clubService,
            PaymentService paymentService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _contentService = contentService;
            _authService = authService;
            _clubService = clubService;
            _paymentService = paymentService;
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

                var newClasses = request.ShootingClasses != null && request.ShootingClasses.Count > 0
                    ? request.ShootingClasses
                        .Where(c => !string.IsNullOrWhiteSpace(c.Class))
                        .Select(c => new HpskSite.Models.ShootingClassEntry
                        {
                            Class = c.Class.Trim(),
                            StartPreference = c.StartPreference ?? request.StartPreference ?? "Inget"
                        })
                        .ToList()
                    : existingClasses
                        .Select(c => new HpskSite.Models.ShootingClassEntry
                        {
                            Class = c.Class,
                            StartPreference = request.StartPreference ?? c.StartPreference ?? "Inget"
                        })
                        .ToList();

                if (newClasses.Count == 0)
                    return Json(new { success = false, message = "Anmälan måste ha minst en klass." });

                var newClassesJson = HpskSite.Models.CompetitionRegistrationDocument
                    .SerializeShootingClasses(newClasses);
                registration.SetValue("shootingClasses", newClassesJson);

                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                    return Json(new { success = false, message = "Failed to save registration" });

                _contentService.Publish(registration, new[] { "*" }, -1);

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
                if (classesChanged && competition != null)
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
                            if (existingPending != null)
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

                // NEW: Store shooting classes as JSON array (single-class for late registration)
                var shootingClassEntry = new[]
                {
                    new
                    {
                        @class = request.ShootingClass,
                        startPreference = request.StartPreference ?? "Inget"
                    }
                };
                var shootingClassesJson = System.Text.Json.JsonSerializer.Serialize(shootingClassEntry);
                registration.SetValue("shootingClasses", shootingClassesJson);

                registration.SetValue("registrationDate", DateTime.Now);
                registration.SetValue("registeredBy", "Admin (Late Registration)");
                registration.SetValue("isActive", true);

                // Save and publish
                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                {
                    return Json(new { success = false, message = "Failed to save registration" });
                }

                _contentService.Publish(registration, new[] { "*" }, -1);

                return Json(new
                {
                    success = true,
                    message = $"Late registration created for {member.Name}. The start list can now be regenerated without losing existing results.",
                    registrationId = registration.Id,
                    memberName = member.Name,
                    shootingClass = request.ShootingClass,
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

                if (invoicesHub == null) return "No Invoice";

                // Get all invoices under the hub - filter out cancelled and sort by most recent
                var allInvoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, int.MaxValue, out _)
                    .Where(x => x.ContentType.Alias == "registrationInvoice")
                    .Where(x => x.GetValue<string>("paymentStatus") != "Cancelled")
                    .OrderByDescending(x => x.Id)
                    .ToList();

                // Search through invoices to find one containing this registration
                foreach (var invoice in allInvoices)
                {
                    var relatedIdsJson = invoice.GetValue<string>("relatedRegistrationIds") ?? "";
                    var registrationIds = ParseRegistrationIds(relatedIdsJson);

                    if (registrationIds.Contains(registrationId))
                    {
                        // Found the invoice - return its payment status
                        var status = invoice.GetValue<string>("paymentStatus") ?? "Unknown";
                        // Clean up if it's in JSON array format like ["Paid"]
                        return CleanPaymentStatus(status);
                    }
                }

                return "No Invoice";
            }
            catch
            {
                return "Unknown";
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
            public string ShootingClass { get; set; } = "";
            public string? StartPreference { get; set; }
            public string? Notes { get; set; }
        }

        #endregion
    }
}
