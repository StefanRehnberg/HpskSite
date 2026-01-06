using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;
using HpskSite.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Umbraco.Cms.Core.Security;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for club-related operations.
    /// Provides API endpoints for club pages including statistics, events, and results.
    /// </summary>
    public class ClubController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _authorizationService;

        public ClubController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IMemberService memberService,
            IMemberManager memberManager,
            AdminAuthorizationService authorizationService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _memberService = memberService;
            _memberManager = memberManager;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Get club statistics (member count, upcoming events, active members)
        /// </summary>
        [HttpGet]
        public IActionResult GetClubStats(int clubId)
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid club ID" });
                }

                // Get all members
                var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
                    .Where(m => m.ContentType.Alias != "hpskClub")
                    .ToList();

                // Count total members for this club (primary or additional)
                var memberCount = allMembers.Count(m =>
                    m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
                    (m.GetValue("memberClubIds")?.ToString()?.Split(',')
                        .Select(s => s.Trim())
                        .Contains(clubId.ToString()) ?? false));

                // Count active members (members with competition registrations in last 30 days)
                var upcomingDate = DateTime.Now.AddDays(30);
                var activeMembers = 0;

                if (UmbracoContext.Content != null)
                {
                    // Get all competition registrations
                    var registrations = GetAllDescendants(UmbracoContext.Content.GetAtRoot().FirstOrDefault())
                        .Where(c => c.ContentType.Alias == "competitionRegistration")
                        .ToList();

                    var activeClubMemberIds = new HashSet<int>();

                    foreach (var reg in registrations)
                    {
                        // Cast to IPublishedContent to access proper extension methods
                        var registration = reg as IPublishedContent;
                        if (registration == null) continue;

                        var compId = registration.Value<int?>("competitionId");
                        var memberId = registration.Value<int?>("memberId");

                        if (compId.HasValue && memberId.HasValue)
                        {
                            // Get competition to check if it's for this club and in future
                            var competitionContent = UmbracoContext.Content.GetById(compId.Value);
                            // Cast to IPublishedContent to access extension methods
                            var competition = competitionContent as IPublishedContent;
                            if (competition != null)
                            {
                                var compDate = competition.Value<DateTime?>("competitionDate");
                                var isClubOnly = competition.Value<bool>("isClubOnly");
                                var compClubId = competition.Value<int>("clubId");

                                // Check if member is from this club
                                var member = allMembers.FirstOrDefault(m => m.Id == memberId.Value);
                                if (member != null &&
                                    (member.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
                                     member.GetValue("memberClubIds")?.ToString()?.Contains(clubId.ToString()) == true))
                                {
                                    // If not club-only competition, or if it is and matches this club
                                    if (!isClubOnly || compClubId == clubId)
                                    {
                                        activeClubMemberIds.Add(memberId.Value);
                                    }
                                }
                            }
                        }
                    }

                    activeMembers = activeClubMemberIds.Count;
                }

                // Count upcoming events (competitions for this club in next 30 days)
                var upcomingEventCount = 0;
                if (UmbracoContext.Content != null)
                {
                    var competitions = GetAllDescendants(UmbracoContext.Content.GetAtRoot().FirstOrDefault())
                        .Where(c => c.ContentType.Alias == "competition")
                        .ToList();

                    upcomingEventCount = competitions.Count(c =>
                    {
                        var compDate = c.GetValue<DateTime?>("competitionDate");
                        var isActive = c.GetValue<bool>("isActive");
                        var isClubOnly = c.GetValue<bool>("isClubOnly");
                        var compClubId = c.GetValue<int>("clubId");
                        var isApproved = c.ParentId > 0; // Published content

                        return isActive && isApproved &&
                               compDate.HasValue &&
                               compDate.Value.Date >= DateTime.Now.Date &&
                               compDate.Value.Date <= upcomingDate.Date &&
                               (!isClubOnly || compClubId == clubId);
                    });
                }

                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        memberCount = memberCount,
                        upcomingEventCount = upcomingEventCount,
                        activeMembers = activeMembers
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading club stats: " + ex.Message });
            }
        }

        /// <summary>
        /// Get upcoming events for a club (competitions and simple events from club hierarchy)
        /// </summary>
        [HttpGet]
        public IActionResult GetUpcomingEvents(int clubId, int days = 30, bool allYears = false)
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid club ID" });
                }

                var events = new List<dynamic>();
                DateTime startDate, endDate;

                if (allYears)
                {
                    // Show current year + next year
                    var now = DateTime.Now;
                    startDate = new DateTime(now.Year, 1, 1);           // Jan 1 of current year
                    endDate = new DateTime(now.Year + 1, 12, 31, 23, 59, 59);  // Dec 31 of next year
                }
                else
                {
                    // Legacy behavior: today + X days
                    startDate = DateTime.Now.Date;
                    endDate = DateTime.Now.AddDays(days).Date;
                }

                if (UmbracoContext.Content != null)
                {
                    // Get club node by ID (clubId is now the club content node ID)
                    var clubNode = UmbracoContext.Content.GetById(clubId);
                    if (clubNode == null || clubNode.ContentType.Alias != "club")
                    {
                        return Ok(new { success = false, message = "Club not found" });
                    }

                    // Get club simple events (children of club node)
                    var simpleEvents = clubNode.Children
                        .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                        .ToList();

                    foreach (var evt in simpleEvents)
                    {
                        var eventDate = evt.Value<DateTime?>("eventDate");
                        var eventName = evt.Value<string>("eventName") ?? evt.Name;
                        var eventType = evt.Value<string>("eventType") ?? "Träning";

                        if (eventDate.HasValue &&
                            eventDate.Value.Date >= startDate.Date &&
                            eventDate.Value.Date <= endDate.Date)
                        {
                            events.Add(new
                            {
                                id = evt.Id,
                                name = eventName,
                                date = eventDate.Value,
                                type = eventType,
                                description = evt.Value<string>("description") ?? "",
                                venue = evt.Value<string>("venue") ?? "",
                                contactPerson = evt.Value<string>("contactPerson") ?? ""
                            });
                        }
                    }

                    // Also get competitions for this club (if isClubOnly = true and clubId matches)
                    // These would be global competitions, but we query all competitions for this club
                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root != null)
                    {
                        var competitions = GetAllDescendants(root)
                            .Where(c => c.ContentType.Alias == "competition")
                            .ToList();

                        foreach (var comp in competitions)
                        {
                            // Cast to IPublishedContent to access Value<T> extension methods
                            var publishedComp = comp as Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent;
                            if (publishedComp == null) continue;

                            var compDate = publishedComp.Value<DateTime?>("competitionDate");
                            var isActive = publishedComp.Value<bool>("isActive");
                            var isClubOnly = publishedComp.Value<bool>("isClubOnly");
                            var compClubId = publishedComp.Value<int>("clubId");
                            var name = publishedComp.Value<string>("competitionName") ?? publishedComp.Name;

                            // Include if no club restriction or if matches this club
                            if (isActive &&
                                compDate.HasValue &&
                                compDate.Value.Date >= startDate.Date &&
                                compDate.Value.Date <= endDate.Date &&
                                (!isClubOnly || compClubId == clubId))
                            {
                                events.Add(new
                                {
                                    id = publishedComp.Id,
                                    name = name,
                                    date = compDate.Value,
                                    type = "Tävling",
                                    url = publishedComp.CompetitionUrl(),
                                    description = publishedComp.Value<string>("description") ?? "",
                                    venue = publishedComp.Value<string>("venue") ?? ""
                                });
                            }
                        }
                    }
                }

                // Sort by date
                events = events.OrderBy(e => e.date).ToList();

                return Ok(new
                {
                    success = true,
                    data = events
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading upcoming events: " + ex.Message });
            }
        }

        /// <summary>
        /// Get recent competition results for club members
        /// </summary>
        [HttpGet]
        public IActionResult GetRecentResults(int clubId, int limit = 5)
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid club ID" });
                }

                var results = new List<dynamic>();

                // Get club members
                var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
                    .Where(m => m.ContentType.Alias != "hpskClub")
                    .ToList();

                var clubMembers = allMembers.Where(m =>
                    m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
                    (m.GetValue("memberClubIds")?.ToString()?.Split(',')
                        .Select(s => s.Trim())
                        .Contains(clubId.ToString()) ?? false))
                    .ToList();

                if (clubMembers.Any() && UmbracoContext.Content != null)
                {
                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root != null)
                    {
                        // Get all competition totals (results)
                        var totals = GetAllDescendants(root)
                            .Where(c => c.ContentType.Alias == "competitionTotal")
                            .OrderByDescending(c => c.CreateDate)
                            .ToList();

                        foreach (var total in totals.Take(limit * 3)) // Get more than needed to filter
                        {
                            var memberId = total.GetValue<int?>("memberId");
                            var competitionId = total.GetValue<int?>("competitionId");

                            if (memberId.HasValue && competitionId.HasValue &&
                                clubMembers.Any(m => m.Id == memberId.Value))
                            {
                                var memberName = clubMembers.FirstOrDefault(m => m.Id == memberId.Value)?.Name ?? "Unknown";
                                var competition = _contentService.GetById(competitionId.Value);
                                var competitionName = competition?.Name ?? "Unknown Competition";

                                var score = total.GetValue<string>("totalScore");
                                var placement = total.GetValue<int?>("placement");

                                results.Add(new
                                {
                                    memberName = memberName,
                                    competitionName = competitionName,
                                    score = score ?? "N/A",
                                    placement = placement
                                });

                                if (results.Count >= limit)
                                    break;
                            }
                        }
                    }
                }

                return Ok(new
                {
                    success = true,
                    data = results
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading recent results: " + ex.Message });
            }
        }

        /// <summary>
        /// Create a new simple club event as child of club node
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateClubEvent(int clubId, string eventName, string eventType = "Träning",
            string description = "", string venue = "", string contactPerson = "",
            string contactEmail = "", string contactPhone = "", DateTime? eventDate = null)
        {
            try
            {
                if (clubId <= 0 || string.IsNullOrWhiteSpace(eventName))
                {
                    return Ok(new { success = false, message = "Club ID and event name are required" });
                }

                // Authorization check - user must be club admin for this club or site admin
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                // Get club node by ID
                var clubNode = UmbracoContext.Content.GetById(clubId);
                if (clubNode == null || clubNode.ContentType.Alias != "club")
                {
                    return Ok(new { success = false, message = "Club not found" });
                }

                // Create new event content as child of club
                var newEvent = _contentService.Create(
                    eventName,
                    clubId,  // Parent is the club node
                    "clubSimpleEvent"
                );

                // Set properties
                newEvent.SetValue("eventName", eventName);
                newEvent.SetValue("eventType", eventType);
                newEvent.SetValue("description", description);
                newEvent.SetValue("venue", venue);
                newEvent.SetValue("contactPerson", contactPerson);
                newEvent.SetValue("contactEmail", contactEmail);
                newEvent.SetValue("contactPhone", contactPhone);
                newEvent.SetValue("eventDate", eventDate ?? DateTime.Now);
                newEvent.SetValue("isActive", true);

                // Save and publish
                _contentService.Save(newEvent);
                _contentService.Publish(newEvent, Array.Empty<string>());

                return Ok(new
                {
                    success = true,
                    message = "Event created successfully",
                    data = new
                    {
                        id = newEvent.Id,
                        name = eventName,
                        date = eventDate ?? DateTime.Now,
                        type = eventType
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating event: " + ex.Message });
            }
        }

        /// <summary>
        /// Get club events (children of club node) for management/editing
        /// </summary>
        [HttpGet]
        public IActionResult GetClubEvents(int clubId)
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid club ID" });
                }

                var events = new List<dynamic>();

                if (UmbracoContext.Content != null)
                {
                    // Get club node and fetch its event children
                    var clubNode = UmbracoContext.Content.GetById(clubId);
                    if (clubNode == null || clubNode.ContentType.Alias != "club")
                    {
                        return Ok(new { success = false, message = "Club not found" });
                    }

                    // Get all clubSimpleEvent children
                    var allEvents = clubNode.Children
                        .Where(c => c.ContentType.Alias == "clubSimpleEvent")
                        .OrderBy(c => c.Value<DateTime?>("eventDate") ?? DateTime.MinValue)
                        .ToList();

                    foreach (var evt in allEvents)
                    {
                        events.Add(new
                        {
                            id = evt.Id,
                            name = evt.Value<string>("eventName") ?? evt.Name,
                            date = evt.Value<DateTime?>("eventDate"),
                            type = evt.Value<string>("eventType") ?? "Träning",
                            description = evt.Value<string>("description") ?? "",
                            venue = evt.Value<string>("venue") ?? "",
                            contactPerson = evt.Value<string>("contactPerson") ?? "",
                            contactEmail = evt.Value<string>("contactEmail") ?? "",
                            isActive = evt.Value<bool>("isActive")
                        });
                    }
                }

                return Ok(new
                {
                    success = true,
                    data = events
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading events: " + ex.Message });
            }
        }

        /// <summary>
        /// Get club members directory
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubMembers(int clubId, string search = "")
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid club ID" });
                }

                // Check if current user is club admin for this club
                var isClubAdmin = false;
                var currentMember = await _memberManager.GetCurrentMemberAsync();

                if (currentMember != null && clubId > 0)
                {
                    var member = _memberService.GetByEmail(currentMember.Email);
                    if (member != null)
                    {
                        var memberRoles = _memberService.GetAllRoles(member.Id).ToList();
                        var clubAdminGroupName = $"ClubAdmin_{clubId}";
                        isClubAdmin = memberRoles.Contains(clubAdminGroupName) || memberRoles.Contains("Administrators");
                    }
                }

                var members = new List<dynamic>();

                // Get all members and filter to club members
                var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
                    .Where(m => m.ContentType.Alias != "hpskClub")
                    .ToList();

                var clubMembers = allMembers.Where(m =>
                    (m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
                     (m.GetValue("memberClubIds")?.ToString()?.Split(',')
                         .Select(s => s.Trim())
                         .Contains(clubId.ToString()) ?? false)))
                    .ToList();

                // Filter by search if provided
                if (!string.IsNullOrWhiteSpace(search))
                {
                    clubMembers = clubMembers
                        .Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // Build member data with privacy levels
                foreach (var member in clubMembers.OrderBy(m => m.Name))
                {
                    var isPrimary = member.GetValue("primaryClubId")?.ToString() == clubId.ToString();
                    var additionalClubs = member.GetValue("memberClubIds")?.ToString() ?? "";

                    // Basic data visible to all members
                    var memberData = new
                    {
                        id = member.Id,
                        name = member.Name,
                        email = member.Email,
                        profilePictureUrl = member.GetValue<string>("profilePictureUrl") ?? "",
                        isPrimaryClub = isPrimary,
                        additionalClubs = additionalClubs,
                        isApproved = member.IsApproved,
                        // Dashboard sharing level for showing dashboard button
                        dashboardSharingLevel = string.IsNullOrEmpty(member.GetValue<string>("dashboardSharingLevel")) ? "club" : member.GetValue<string>("dashboardSharingLevel"),
                        // Extended properties only visible to club admins
                        address = isClubAdmin ? (member.GetValue<string>("address") ?? "") : null,
                        postalCode = isClubAdmin ? (member.GetValue<string>("postalCode") ?? "") : null,
                        city = isClubAdmin ? (member.GetValue<string>("City") ?? "") : null,
                        phoneNumber = isClubAdmin ? (member.GetValue<string>("phoneNumber") ?? "") : null,
                        personNumber = isClubAdmin ? (member.GetValue<string>("personNumber") ?? "") : null,
                        shooterIdNumber = isClubAdmin ? (member.GetValue<string>("shooterIdNumber") ?? "") : null,
                        memberSince = isClubAdmin ? member.GetValue<DateTime?>("memberSince") : null
                    };

                    members.Add(memberData);
                }

                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        members = members,
                        totalCount = members.Count,
                        isAdmin = isClubAdmin
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading members: " + ex.Message });
            }
        }

        /// <summary>
        /// Edit/update an existing club event
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditClubEvent(int eventId, string eventName, string eventType = "Träning",
            string description = "", string venue = "", string contactPerson = "",
            string contactEmail = "", string contactPhone = "", DateTime? eventDate = null)
        {
            try
            {
                if (eventId <= 0 || string.IsNullOrWhiteSpace(eventName))
                {
                    return Ok(new { success = false, message = "Event ID and name are required" });
                }

                var eventContent = _contentService.GetById(eventId);
                if (eventContent == null || eventContent.ContentType.Alias != "clubSimpleEvent")
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                // Authorization check - get parent club ID and verify user is club admin
                int clubId = eventContent.ParentId;
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Update properties
                eventContent.SetValue("eventName", eventName);
                eventContent.SetValue("eventType", eventType);
                eventContent.SetValue("description", description);
                eventContent.SetValue("venue", venue);
                eventContent.SetValue("contactPerson", contactPerson);
                eventContent.SetValue("contactEmail", contactEmail);
                eventContent.SetValue("contactPhone", contactPhone);
                eventContent.SetValue("eventDate", eventDate ?? DateTime.Now);

                // Save and publish
                _contentService.Save(eventContent);
                _contentService.Publish(eventContent, Array.Empty<string>());

                return Ok(new { success = true, message = "Event updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating event: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a club event
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteClubEvent(int eventId)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Event ID is required" });
                }

                var eventContent = _contentService.GetById(eventId);
                if (eventContent == null || eventContent.ContentType.Alias != "clubSimpleEvent")
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                // Authorization check - get parent club ID and verify user is club admin
                int clubId = eventContent.ParentId;
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Unpublish and delete
                _contentService.Unpublish(eventContent);
                _contentService.Delete(eventContent);

                return Ok(new { success = true, message = "Event deleted successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error deleting event: " + ex.Message });
            }
        }

        /// <summary>
        /// Create a new club news/announcement
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateClubNews(int clubId, string newsTitle, string newsContent,
            string newsAuthor = "Klubben", bool isPinned = false)
        {
            try
            {
                if (clubId <= 0 || string.IsNullOrWhiteSpace(newsTitle) || string.IsNullOrWhiteSpace(newsContent))
                {
                    return Ok(new { success = false, message = "Club ID, title, and content are required" });
                }

                // Authorization check - user must be club admin for this club or site admin
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                // Get club node by ID
                var clubNode = UmbracoContext.Content.GetById(clubId);
                if (clubNode == null || clubNode.ContentType.Alias != "club")
                {
                    return Ok(new { success = false, message = "Club not found" });
                }

                // Create new news content as child of club
                var newNews = _contentService.Create(
                    newsTitle,
                    clubId,  // Parent is the club node
                    "clubNews"
                );

                // Set properties
                newNews.SetValue("newsTitle", newsTitle);
                newNews.SetValue("newsContent", newsContent);
                newNews.SetValue("newsAuthor", newsAuthor);
                newNews.SetValue("isPinned", isPinned);

                // Save and publish
                _contentService.Save(newNews);
                _contentService.Publish(newNews, Array.Empty<string>());

                return Ok(new
                {
                    success = true,
                    message = "News created successfully",
                    data = new
                    {
                        id = newNews.Id,
                        title = newsTitle
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating news: " + ex.Message });
            }
        }

        /// <summary>
        /// Edit/update an existing club news item
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditClubNews(int newsId, string newsTitle, string newsContent,
            string newsAuthor = "Klubben", bool isPinned = false)
        {
            try
            {
                if (newsId <= 0 || string.IsNullOrWhiteSpace(newsTitle) || string.IsNullOrWhiteSpace(newsContent))
                {
                    return Ok(new { success = false, message = "News ID, title, and content are required" });
                }

                var newsContent_item = _contentService.GetById(newsId);
                if (newsContent_item == null || newsContent_item.ContentType.Alias != "clubNews")
                {
                    return Ok(new { success = false, message = "News item not found" });
                }

                // Authorization check - get parent club ID and verify user is club admin
                int clubId = newsContent_item.ParentId;
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Update properties
                newsContent_item.SetValue("newsTitle", newsTitle);
                newsContent_item.SetValue("newsContent", newsContent);
                newsContent_item.SetValue("newsAuthor", newsAuthor);
                newsContent_item.SetValue("isPinned", isPinned);

                // Save and publish
                _contentService.Save(newsContent_item);
                _contentService.Publish(newsContent_item, Array.Empty<string>());

                return Ok(new { success = true, message = "News updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating news: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a club news item
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteClubNews(int newsId)
        {
            try
            {
                if (newsId <= 0)
                {
                    return Ok(new { success = false, message = "News ID is required" });
                }

                var newsContent = _contentService.GetById(newsId);
                if (newsContent == null || newsContent.ContentType.Alias != "clubNews")
                {
                    return Ok(new { success = false, message = "News item not found" });
                }

                // Authorization check - get parent club ID and verify user is club admin
                int clubId = newsContent.ParentId;
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Unpublish and delete
                _contentService.Unpublish(newsContent);
                _contentService.Delete(newsContent);

                return Ok(new { success = true, message = "News deleted successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error deleting news: " + ex.Message });
            }
        }

        /// <summary>
        /// Get club information for settings form
        /// </summary>
        [HttpGet]
        public IActionResult GetClubInfo(int clubId)
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid club ID" });
                }

                if (UmbracoContext.Content != null)
                {
                    var clubNode = UmbracoContext.Content.GetById(clubId);
                    if (clubNode == null || clubNode.ContentType.Alias != "club")
                    {
                        return Ok(new { success = false, message = "Club not found" });
                    }

                    return Ok(new
                    {
                        success = true,
                        data = new
                        {
                            contactPerson = clubNode.Value<string>("contactPerson") ?? "",
                            contactEmail = clubNode.Value<string>("contactEmail") ?? "",
                            contactPhone = clubNode.Value<string>("contactPhone") ?? "",
                            webSite = clubNode.Value<string>("clubUrl") ?? "",  // Property is named clubUrl in Umbraco
                            address = clubNode.Value<string>("address") ?? "",
                            city = clubNode.Value<string>("city") ?? "",
                            postalCode = clubNode.Value<string>("postalCode") ?? "",
                            description = clubNode.Value<string>("description") ?? "",
                            aboutClub = clubNode.Value<string>("aboutClub") ?? ""
                        }
                    });
                }

                return Ok(new { success = false, message = "Umbraco context not available" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading club info: " + ex.Message });
            }
        }

        /// <summary>
        /// Update club contact information and settings
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateClubInfo(int clubId, string contactPerson = "",
            string contactEmail = "", string contactPhone = "", string description = "",
            string webSite = "", string address = "", string city = "",
            string postalCode = "", string aboutClub = "")
        {
            try
            {
                if (clubId <= 0)
                {
                    return Ok(new { success = false, message = "Club ID is required" });
                }

                // Authorization check - user must be club admin for this club or site admin
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var clubContent = _contentService.GetById(clubId);
                if (clubContent == null || clubContent.ContentType.Alias != "club")
                {
                    return Ok(new { success = false, message = "Club not found" });
                }

                // Update contact properties
                clubContent.SetValue("contactPerson", contactPerson);
                clubContent.SetValue("contactEmail", contactEmail);
                clubContent.SetValue("contactPhone", contactPhone);
                clubContent.SetValue("clubUrl", webSite);  // Property is named clubUrl in Umbraco

                // Update address properties
                clubContent.SetValue("address", address);
                clubContent.SetValue("city", city);
                clubContent.SetValue("postalCode", postalCode);

                // Update description properties
                clubContent.SetValue("description", description);
                clubContent.SetValue("aboutClub", aboutClub);

                // Save and publish
                _contentService.Save(clubContent);
                _contentService.Publish(clubContent, Array.Empty<string>());

                return Ok(new { success = true, message = "Club information updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating club info: " + ex.Message });
            }
        }

        /// <summary>
        /// Approve a prospect member for a club
        /// OBSOLETE: Use MemberAdminController.ApproveMember instead - includes email notifications
        /// </summary>
        [Obsolete("Use MemberAdminController.ApproveMember instead - includes email notifications and better logging")]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveMember([FromBody] ApproveMemberRequest request)
        {
            try
            {
                if (request.MemberId <= 0 || request.ClubId <= 0)
                {
                    return Ok(new { success = false, message = "Member ID and Club ID are required" });
                }

                // Check if current user is club admin or site admin
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Ok(new { success = false, message = "Not authenticated" });
                }

                var currentUmbracoMember = _memberService.GetByEmail(currentMember.Email);
                if (currentUmbracoMember == null)
                {
                    return Ok(new { success = false, message = "Member not found" });
                }

                var memberRoles = _memberService.GetAllRoles(currentUmbracoMember.Id).ToList();
                var clubAdminGroupName = $"ClubAdmin_{request.ClubId}";
                var isClubAdmin = memberRoles.Contains(clubAdminGroupName) || memberRoles.Contains("Administrators");

                if (!isClubAdmin)
                {
                    return Ok(new { success = false, message = "Unauthorized. Only club admins can approve members." });
                }

                // Get the prospect member
                var prospectMember = _memberService.GetById(request.MemberId);
                if (prospectMember == null)
                {
                    return Ok(new { success = false, message = "Member not found" });
                }

                // Approve the member
                prospectMember.IsApproved = true;
                _memberService.Save(prospectMember);

                return Ok(new { success = true, message = "Member approved successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error approving member: " + ex.Message });
            }
        }

        /// <summary>
        /// Decline a prospect member for a club
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeclineMember([FromBody] DeclineMemberRequest request)
        {
            try
            {
                if (request.MemberId <= 0 || request.ClubId <= 0)
                {
                    return Ok(new { success = false, message = "Member ID and Club ID are required" });
                }

                // Check if current user is club admin or site admin
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Ok(new { success = false, message = "Not authenticated" });
                }

                var currentUmbracoMember = _memberService.GetByEmail(currentMember.Email);
                if (currentUmbracoMember == null)
                {
                    return Ok(new { success = false, message = "Member not found" });
                }

                var memberRoles = _memberService.GetAllRoles(currentUmbracoMember.Id).ToList();
                var clubAdminGroupName = $"ClubAdmin_{request.ClubId}";
                var isClubAdmin = memberRoles.Contains(clubAdminGroupName) || memberRoles.Contains("Administrators");

                if (!isClubAdmin)
                {
                    return Ok(new { success = false, message = "Unauthorized. Only club admins can decline members." });
                }

                // Get the prospect member
                var prospectMember = _memberService.GetById(request.MemberId);
                if (prospectMember == null)
                {
                    return Ok(new { success = false, message = "Member not found" });
                }

                // Remove member's club association
                var primaryClubId = prospectMember.GetValue("primaryClubId")?.ToString();
                var memberClubIds = prospectMember.GetValue("memberClubIds")?.ToString() ?? "";

                if (primaryClubId == request.ClubId.ToString())
                {
                    // If this is the primary club, remove it
                    prospectMember.SetValue("primaryClubId", null);
                }
                else
                {
                    // Remove from additional clubs list
                    var clubIdsList = memberClubIds.Split(',')
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s) && s != request.ClubId.ToString())
                        .ToList();
                    prospectMember.SetValue("memberClubIds", string.Join(",", clubIdsList));
                }

                _memberService.Save(prospectMember);

                return Ok(new { success = true, message = "Member declined and removed from club" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error declining member: " + ex.Message });
            }
        }

        /// <summary>
        /// Helper: Get all descendants of a content item (used for querying)
        /// </summary>
        private List<dynamic> GetAllDescendants(dynamic content)
        {
            if (content == null)
                return new List<dynamic>();

            var result = new List<dynamic> { content };
            foreach (var child in content.Children)
            {
                result.AddRange(GetAllDescendants(child));
            }
            return result;
        }
    }

    // Request models for member approval endpoints
    public class ApproveMemberRequest
    {
        public int MemberId { get; set; }
        public int ClubId { get; set; }
    }

    public class DeclineMemberRequest
    {
        public int MemberId { get; set; }
        public int ClubId { get; set; }
    }
}
