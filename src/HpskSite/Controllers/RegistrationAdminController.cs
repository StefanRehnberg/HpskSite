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
            ClubService clubService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _contentService = contentService;
            _authService = authService;
            _clubService = clubService;
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
        /// Get list of all active competitions for dropdown/filtering
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetActiveCompetitions()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get all competition documents
                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
                var competitions = allContent
                    .Where(c => c.ContentType.Alias == "competition")
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
        /// Update a competition registration (start preference, active status)
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> UpdateCompetitionRegistration([FromBody] UpdateRegistrationRequest request)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                {
                    return Json(new { success = false, message = "Registration not found" });
                }

                // Update properties
                registration.SetValue("startPreference", request.StartPreference ?? "Inget");
                // Note: isActive property removed - use Published status instead

                var saveResult = _contentService.Save(registration);
                if (saveResult.Success)
                {
                    _contentService.Publish(registration, Array.Empty<string>());
                    return Json(new { success = true, message = "Registration updated successfully" });
                }
                else
                {
                    return Json(new { success = false, message = "Failed to save registration" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error updating registration: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a competition registration
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> DeleteCompetitionRegistration([FromBody] DeleteRegistrationRequest request)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var registration = _contentService.GetById(request.RegistrationId);
                if (registration == null)
                {
                    return Json(new { success = false, message = "Registration not found" });
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
            // Check authorization
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Validate competition exists
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Competition not found" });
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
                registration.SetValue("memberEmail", member.Email);

                // Get member's club
                var clubId = member.GetValue<int>("primaryClubId");
                var clubName = clubId > 0 ? _clubService.GetClubNameById(clubId) : "";
                registration.SetValue("clubId", clubId);
                registration.SetValue("memberClub", clubName ?? ""); // Legacy field

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
                // Note: isActive property removed - use Published status instead
                registration.SetValue("shooterNotes", request.Notes ?? "Late registration after results entry started");

                // Save and publish
                var saveResult = _contentService.Save(registration);
                if (!saveResult.Success)
                {
                    return Json(new { success = false, message = "Failed to save registration" });
                }

                _contentService.Publish(registration, Array.Empty<string>());

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
                _contentService.Publish(hub, Array.Empty<string>());
            }

            return hub;
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
        /// Request model for updating registration
        /// </summary>
        public class UpdateRegistrationRequest
        {
            public int RegistrationId { get; set; }
            public string? StartPreference { get; set; }
            public bool IsActive { get; set; }
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
