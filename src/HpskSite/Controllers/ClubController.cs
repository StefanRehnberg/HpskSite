using Microsoft.AspNetCore.Antiforgery;
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
using System.IO;
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
        private readonly IMediaService _mediaService;
        private readonly MemberDataPresenceService _presenceService;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly IAntiforgery _antiforgery;
        private readonly HpskSite.Services.Training.ClubTrainingService _trainings;
        private readonly HpskSite.Services.Events.EventOwnerResolver _eventOwners;
        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;
        // Sajtstrukturen fas 2a: varje läsning och skrivning av klubbens och kretsens händelser går
        // genom lagringen — aldrig direkt mot clubSimpleEvent-noderna.
        private readonly HpskSite.Services.Events.IClubEventStore _events;
        private readonly RangeCalendarService _rangeCalendar;
        private readonly ILogger<ClubController> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;

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
            AdminAuthorizationService authorizationService,
            IMediaService mediaService,
            MemberDataPresenceService presenceService,
            ClubMembershipService clubMembershipService,
            IAntiforgery antiforgery,
            HpskSite.Services.Training.ClubTrainingService trainings,
            HpskSite.Services.Events.EventOwnerResolver eventOwners,
            RangeCalendarService rangeCalendar,
            ILogger<ClubController> logger,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations,
            HpskSite.Services.Events.IClubEventStore events,
            HpskSite.Services.Competitions.ICompetitionStore competitions)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _competitions = competitions;
            _registrations = registrations;
            _events = events;
            _antiforgery = antiforgery;
            _trainings = trainings;
            _eventOwners = eventOwners;
            _rangeCalendar = rangeCalendar;
            _logger = logger;
            _contentService = contentService;
            _memberService = memberService;
            _memberManager = memberManager;
            _authorizationService = authorizationService;
            _mediaService = mediaService;
            _presenceService = presenceService;
            _clubMembershipService = clubMembershipService;
        }

        /// <summary>
        /// Issues a fresh anti-forgery token (and re-sets the matching cookie).
        ///
        /// The token baked into a page at render time goes stale if the member logs out and
        /// in again in another tab, or if the anti-forgery session cookie is dropped while the
        /// 30-day auth cookie survives. Posting a stale token gets rejected with an empty
        /// HTTP 400 — which is what a club admin saw as the bare "Fel vid sparande av
        /// händelse". Clients call this and retry once instead of losing the form.
        /// </summary>
        [HttpGet]
        public IActionResult GetFormToken()
        {
            var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
            return Ok(new { success = true, token = tokens.RequestToken });
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

                // Klubbens medlemmar ur klubbregistret (skalplanen B3) — förut hela medlemsregistret.
                var roster = HttpContext.RequestServices.GetRequiredService<ClubRosterService>();
                var clubMemberIds = roster.ActiveMemberIds(clubId);
                var memberCount = clubMemberIds.Count;

                // Count active members (members with competition registrations in last 30 days)
                var upcomingDate = DateTime.Now.AddDays(30);
                var activeMembers = 0;

                if (UmbracoContext.Content != null)
                {
                    // Klubbens medlemmar med en anmälan, via anmälningslagringen (sajtstrukturen fas 1a)
                    // i EN fråga — förut en vandring genom hela den publicerade trädstrukturen, som
                    // dessutom bara såg de anmälningar som råkat publiceras. Samma regel som förut:
                    // en klubbintern tävling räknas bara när den är den här klubbens.
                    var activeClubMemberIds = new HashSet<int>();
                    var compCache = new Dictionary<int, HpskSite.Models.Competitions.CompetitionInfo?>();
                    foreach (var (memberId, compId) in _registrations.CompetitionsForMembers(clubMemberIds))
                    {
                        if (activeClubMemberIds.Contains(memberId)) continue;
                        if (!compCache.TryGetValue(compId, out var competition))
                            compCache[compId] = competition = _competitions.Get(compId);
                        if (competition == null) continue;

                        var isClubOnly = competition.IsClubOnly;
                        var compClubId = competition.ClubId;
                        if (!isClubOnly || compClubId == clubId)
                            activeClubMemberIds.Add(memberId);
                    }

                    activeMembers = activeClubMemberIds.Count;
                }

                // Count upcoming events (competitions for this club in next 30 days)
                var upcomingEventCount = 0;
                {
                    // Tävlingarna ur tävlingslagringen (C3-planen S3).
                    // ⚠️ Förut GetAllDescendants (dynamic) över HELA sajten, och GetValue på ett dynamic
                    // kastade alltid — klubbsidans statistik svarade "Error loading club stats" för
                    // varje klubb (rättat 2026-10-12, skalplanen). Bara tävlingsträdet, typat.
                    var competitions = _competitions.List();

                    upcomingEventCount = competitions.Count(c =>
                    {
                        var compDate = c.CompetitionDate;
                        var isActive = c.IsVisible;
                        var isClubOnly = c.IsClubOnly;
                        var compClubId = c.ClubId;

                        return isActive &&
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

        // Kalenderns fyra filter (fas C, beslut 2026-10-03). Klienten speglar strängarna i ClubCalendar.
        private const string CalendarScopeTraining = "training";
        private const string CalendarScopeCompetition = "competition"; // competitionstore-ok: kalenderfiltrets namn
        private const string CalendarScopeClub = "club";
        private const string CalendarScopeOutside = "outside";

        /// <summary>
        /// Get upcoming events for a club (competitions and simple events from club hierarchy)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUpcomingEvents(int clubId, int days = 30, bool allYears = false)
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

                    // Region the club belongs to — used to scope which competitions appear
                    // in the calendar (see competition filter below).
                    var clubRegion = clubNode.Value<string>("regionalFederation") ?? "";

                    // Klubbens händelser — genom lagringen (fas 2a), bara publicerade.
                    var simpleEvents = _events.ListForOwner(clubId);

                    foreach (var evt in simpleEvents)
                    {
                        var eventDate = evt.StartsAt;
                        var eventName = evt.DisplayName;
                        var eventType = string.IsNullOrEmpty(evt.EventType) ? "Träning" : evt.EventType;
                        // Kalenderns filter (fas C): träning är en egen kategori, allt annat klubben
                        // ordnar (städdag, möte, socialt) är "övrigt i klubben".
                        var scope = eventType.Contains("Träning", StringComparison.OrdinalIgnoreCase)
                            ? CalendarScopeTraining : CalendarScopeClub;

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
                                scope,
                                description = evt.Description,
                                venue = evt.Venue,
                                contactPerson = evt.ContactPerson,
                                url = evt.Url,
                                // Kalendern badgar obligatoriska händelser — deltagandet påverkar
                                // medlemmens Föreningsintyg, så det ska synas där man planerar,
                                // inte först när man öppnat händelsens egen sida.
                                isMandatory = evt.IsMandatory,
                                // ⚠️ Samma skäl för sista anmälningsdag: en deadline som bara syns
                                // inne på händelsens sida hinner passera för den som planerar i
                                // kalendern. Badgen bor i _EventBadges, delad av alla tre vyerna.
                                registrationRequired = evt.RegistrationRequired,
                                registrationDeadline = evt.RegistrationDeadline?.ToString("yyyy-MM-dd") ?? ""
                            });
                        }
                    }

                    // ── Fas B5: klubbens träningar (ClubTraining) ──────────────────────────────
                    // En träning har ingen egen sida — den öppnas i träningspanelen. url bär därför
                    // "#training-{id}", som kalendern fångar med en delegerad lyssnare i stället för
                    // att följa. Så slipper alla tre renderarna (lista, dag, månad) en egen gren.
                    // ⚠️ En inställd träning står kvar (märkt) — en medlem som anmält sig ska kunna
                    // se att den är inställd, inte att den försvunnit.
                    try
                    {
                        var trainingRows = _trainings.List(clubId, startDate, endDate);
                        // "Din kurs" (UX-omgången 2026-10-06): träningar som hör till en kurs den
                        // inloggade går eller leder märks, annars ser de ut som klubbens vanliga.
                        var courseMarks = new Dictionary<int, HpskSite.Services.Training.TrainingCourseService.CalendarMark>();
                        var viewer = await _memberManager.GetCurrentMemberAsync();
                        var viewerId = viewer?.Email == null ? 0 : _memberService.GetByEmail(viewer.Email)?.Id ?? 0;
                        if (viewerId > 0 && trainingRows.Count > 0)
                            courseMarks = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Training.TrainingCourseService>()
                                .CalendarMarksFor(viewerId, trainingRows.Select(r => r.Id));
                        foreach (var t in trainingRows)
                        {
                            courseMarks.TryGetValue(t.Id, out var mark);
                            var day = DateTime.ParseExact(t.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                            var when = string.IsNullOrEmpty(t.StartTime) ? day
                                : day.Add(TimeSpan.ParseExact(t.StartTime, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture));
                            events.Add(new
                            {
                                id = t.Id,
                                name = t.IsCancelled ? $"{t.Name} (inställd)" : t.Name,
                                date = when,
                                type = "Träning",
                                scope = CalendarScopeTraining,
                                description = t.Description ?? "",
                                venue = t.Venue ?? "",
                                contactPerson = t.SkjutledareName,
                                url = $"#training-{t.Id}",
                                isMandatory = false, // en träning är aldrig obligatorisk för alla
                                registrationRequired = t.RegistrationRequired,
                                registrationDeadline = "",
                                kind = "training",
                                isCancelled = t.IsCancelled,
                                myCourse = mark?.CourseName ?? "",
                                myCourseMandatory = mark?.Mandatory ?? false
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        // Tabellen kan saknas i en miljö där migreringen inte körts — kalendern ska ändå visas.
                        _logger.LogWarning(ex, "Klubbens träningar kunde inte läsas för kalendern (klubb {Club})", clubId);
                    }

                    // Also get competitions relevant to this club, scoped to the club's region:
                    //  - the club's own competitions (club-only or open) are always shown
                    //  - other clubs' private (isClubOnly) competitions are never shown
                    //  - everything else must belong to the club's region — either region-hosted
                    //    (no clubId, regionalFederation matches) or hosted by another club in the
                    //    same region.
                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root != null)
                    {
                        var allNodes = GetAllDescendants(root);

                        // Map each club node to its region so club-hosted competitions without an
                        // explicit regionalFederation inherit their host club's region.
                        var clubRegionLookup = new Dictionary<int, string>();
                        foreach (var nodeDyn in allNodes)
                        {
                            var node = nodeDyn as IPublishedContent;
                            if (node == null || node.ContentType.Alias != "club") continue;
                            var nodeRegion = node.Value<string>("regionalFederation") ?? "";
                            if (!string.IsNullOrEmpty(nodeRegion))
                                clubRegionLookup[node.Id] = nodeRegion;
                        }

                        // Tävlingarna ur tävlingslagringen (C3-planen S3); noden bara för adressen.
                        foreach (var publishedComp in _competitions.List())
                        {
                            var compDate = publishedComp.CompetitionDate;
                            var isActive = publishedComp.IsVisible;
                            var isClubOnly = publishedComp.IsClubOnly;
                            var compClubId = publishedComp.ClubId;
                            var name = publishedComp.CompetitionName ?? publishedComp.Name;

                            if (!isActive ||
                                !compDate.HasValue ||
                                compDate.Value.Date < startDate.Date ||
                                compDate.Value.Date > endDate.Date)
                            {
                                continue;
                            }

                            bool include;
                            if (compClubId == clubId)
                            {
                                // This club's own competition — always relevant.
                                include = true;
                            }
                            else if (isClubOnly)
                            {
                                // Another club's private competition — never shown.
                                include = false;
                            }
                            else if (string.IsNullOrEmpty(clubRegion))
                            {
                                // Club has no region configured — can't scope, so show only its own.
                                include = false;
                            }
                            else
                            {
                                var compRegion = publishedComp.RegionalFederation ?? "";
                                if (string.IsNullOrEmpty(compRegion) && compClubId > 0)
                                    clubRegionLookup.TryGetValue(compClubId, out compRegion);

                                include = string.Equals(compRegion ?? "", clubRegion, StringComparison.OrdinalIgnoreCase);
                            }

                            if (include)
                            {
                                events.Add(new
                                {
                                    id = publishedComp.Id,
                                    name = name,
                                    date = compDate.Value,
                                    type = "Tävling",
                                    // Klubbens egna tävlingar är "Tävlingar"; kretsens och andra
                                    // klubbars öppna tävlingar ligger "utanför klubben".
                                    scope = compClubId == clubId ? CalendarScopeCompetition : CalendarScopeOutside,
                                    url = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Competitions.CompetitionLinks>().Url(publishedComp.Id),
                                    description = publishedComp.Description ?? "",
                                    venue = publishedComp.Venue ?? ""
                                });
                            }
                        }
                    }
                }

                // Kretsens egna händelser (fas C, "utanför klubben"). Kretsen slås upp i TRÄDET —
                // regionalPage > clubsPage > club — inte ur regionalFederation, som kan vara tom eller
                // stavad annorlunda. Kretsar har aldrig träning, men eventType filtreras inte här:
                // allt kretsen ordnar är "utanför klubben" ur klubbens synvinkel.
                if (UmbracoContext.Content?.GetById(clubId) is IPublishedContent clubForRegion
                    && clubForRegion.Parent?.Parent is IPublishedContent regionNode
                    && regionNode.ContentType.Alias == "regionalPage")
                {
                    foreach (var evt in _events.ListForOwner(regionNode.Id))
                    {
                        var eventDate = evt.StartsAt;
                        if (!eventDate.HasValue || eventDate.Value.Date < startDate.Date || eventDate.Value.Date > endDate.Date)
                            continue;
                        events.Add(new
                        {
                            id = evt.Id,
                            name = evt.DisplayName,
                            date = eventDate.Value,
                            type = string.IsNullOrEmpty(evt.EventType) ? "Annat" : evt.EventType,
                            scope = CalendarScopeOutside,
                            owner = regionNode.Name,
                            description = evt.Description,
                            venue = evt.Venue,
                            contactPerson = evt.ContactPerson,
                            url = evt.Url,
                            isMandatory = evt.IsMandatory,
                            registrationRequired = evt.RegistrationRequired,
                            registrationDeadline = evt.RegistrationDeadline?.ToString("yyyy-MM-dd") ?? ""
                        });
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
                                var competitionName = _competitions.GetSaved(competitionId.Value)?.Name ?? "Unknown Competition"; // C3-planen §5d

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
            string contactEmail = "", string contactPhone = "", string eventDate = "",
            string pageName = "",
            bool registrationRequired = false, int maxParticipants = 0, bool isMandatory = false, string registrationUrl = "", bool lanevapenOffered = false,
            string registrationDeadline = "", string eventPrices = "", string eventAudience = "",
            string eventSwishNumber = "", int? rangeId = null, int? rangeSectionId = null)
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

                // Banan prövas FÖRE noden skapas — ett fel efteråt hade lämnat en händelse utan sin bana.
                var rangeError = rangeId is > 0 ? _rangeCalendar.Validate(rangeId.Value, rangeSectionId) : null;
                if (rangeError != null) return Ok(new { success = false, message = rangeError });

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

                // Ny händelse under klubben, genom lagringen (fas 2a). pageName blir sidnamnet
                // (adressens sista del) om det angetts, annars händelsens namn.
                var newEvent = new HpskSite.Models.Events.ClubEventRecord
                {
                    OwnerType = HpskSite.Models.Events.ClubEventRecord.OwnerClub,
                    OwnerId = clubId,
                    PageName = !string.IsNullOrWhiteSpace(pageName) ? pageName : eventName,
                    Name = eventName,
                    EventType = eventType ?? "",
                    Description = description ?? "",
                    Venue = venue ?? "",
                    ContactPerson = contactPerson ?? "",
                    ContactEmail = contactEmail ?? "",
                    ContactPhone = contactPhone ?? "",
                    IsActive = true,
                    IsPublished = true
                };
                var regFieldError = HpskSite.Models.Events.ClubEventInput.ApplyRegistrationFields(newEvent, registrationRequired,
                    maxParticipants, isMandatory, registrationUrl, lanevapenOffered, registrationDeadline, eventPrices, eventAudience, eventSwishNumber);
                if (regFieldError != null)
                {
                    // Avbryts FÖRE sparningen, så händelsen aldrig blir till. Ett halvsparat
                    // evenemang utan sin deadline är värre än inget: det är publikt och öppet.
                    return Ok(new { success = false, message = regFieldError });
                }
                if (!string.IsNullOrEmpty(eventDate) && DateTime.TryParse(eventDate, out var parsedEventDate))
                {
                    newEvent.StartsAt = parsedEventDate;
                }

                // Lagringen sparar och publicerar, och lämnar inget opublicerat föräldralöst utkast
                // efter sig om publiceringen misslyckas (ett nytt försök hade annars skapat dubbletter).
                var (newId, saveError) = await _events.SaveAsync(newEvent);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                _rangeCalendar.SetEventRange(newId, rangeId, rangeSectionId, null);
                return Ok(new
                {
                    success = true,
                    message = "Event created successfully",
                    data = new
                    {
                        id = newId,
                        name = eventName,
                        date = eventDate,
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
        /// Kopierar en händelse till ett nytt datum.
        ///
        /// ⚠️ <b>KOPIERINGEN GÖRS PÅ SERVERN, och det är hela poängen.</b> Tidigare byggde klienten
        /// en `FormData` med de fält den kom ihåg och postade till <c>CreateClubEvent</c> — sex
        /// grundfält. Kopian tappade därför TYST anmälan, kapaciteten, obligatorisk-flaggan,
        /// lånevapnen och sista anmälningsdag: en klubb som lägger upp en träning med lånevapen och
        /// kopierar den till nästa vecka fick en kopia utan anmälan alls. Det är tredje gången
        /// samma fel dyker upp på den här ytan (först fälten som bara låg i en dialog, sedan
        /// <c>EditEventDetails</c> som skrev över <c>lanevapenOffered</c>), och en lista i JavaScript
        /// kan alltid glömma nästa fält.
        ///
        /// Här klonas källans post (<c>ClubEventRecord.Clone</c>) i stället, och lagringen skriver alla
        /// kända fält (sajtstrukturen fas 2a). Ett fält som läggs till på posten följer då med utan att
        /// någon behöver komma ihåg den här metoden.
        ///
        /// Deltagarrader följer aldrig med: <c>ClubEventParticipant</c> är nycklad på EventId, och
        /// kopian är en ny nod — den nya händelsen börjar alltså med tom anmälningslista, vilket är
        /// vad man vill av "samma träning nästa vecka".
        ///
        /// ⚠️ De ÖVRIGA datumen (slutdatum, sista anmälningsdag) flyttas lika många dagar som
        /// startdatumet. Rakt kopierade hade de pekat på källans vecka, och kopian hade fötts med
        /// stängd anmälan.
        /// </summary>
        /// <summary>
        /// Får den inloggade administrera händelser under den här föräldernoden?
        ///
        /// ⚠️ FÖRÄLDERNODEN ÄR ÄGAREN. En händelse bär ingen klubb- eller kretskolumn — ägaren
        /// härleds ur trädet, precis som `ClubEventParticipationService` gör det. Det är också
        /// varför den här kontrollen måste fråga vad för sorts nod föräldern är, i stället för att
        /// anta klubb.
        /// </summary>
        private async Task<bool> CanManageEventParentAsync(int parentId)
        {
            if (parentId <= 0) return false;
            // Regeln bor i EventOwnerResolver (sajtstrukturen fas 0). En förälder som varken är
            // klubb eller krets behandlas som förut: klubbkontrollen, som släpper igenom sajtadmin.
            var owner = _eventOwners.ForOwnerId(parentId);
            return owner is not null
                ? await _eventOwners.CanManageAsync(owner)
                : await _authorizationService.IsClubAdminForClub(parentId);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyClubEvent(int sourceEventId, string eventName = "",
            string eventDate = "", string pageName = "")
        {
            try
            {
                if (sourceEventId <= 0)
                {
                    return Ok(new { success = false, message = "Källhändelsens id saknas." });
                }

                var source = _events.Get(sourceEventId);
                if (source == null)
                {
                    return Ok(new { success = false, message = "Källhändelsen hittades inte." });
                }

                // ⚠⚠ BÅDA VÄRDFORMERNA. En händelse är ett `clubSimpleEvent` antingen under en
                // KLUBB eller under en KRETS — kretsen arrangerar egna händelser och använder samma
                // doctype. En kontroll som bara frågar `IsClubAdminForClub` låser därför ute
                // kretsadministratören från hennes EGEN händelse. Att det inte syntes här beror på
                // att en sajtadmin släpps igenom av klubbkontrollen ändå — samma förväxling som
                // redan kostat fyra rundor på tävlingssidan.
                var sourceOwner = _eventOwners.ForEventRecord(source);
                if (!await _eventOwners.CanManageAsync(sourceOwner))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                if (!DateTime.TryParse(eventDate, out var parsedDate))
                {
                    return Ok(new { success = false, message = "Ange ett datum för kopian." });
                }

                var newName = !string.IsNullOrWhiteSpace(eventName)
                    ? eventName.Trim()
                    : (!string.IsNullOrWhiteSpace(source.DisplayName) ? source.DisplayName : "Kopia");
                var nodeName = !string.IsNullOrWhiteSpace(pageName) ? pageName.Trim() : newName;

                // Varje fält källan bär, utan en fältlista att glömma: posten klonas, och lagringen
                // skriver alla kända fält. Kopian hamnar hos samma ägare (posten bär ägaren).
                // Namnet och datumet sätts efteråt — de är de två sakerna som per definition ska skilja sig.
                var copy = source.Clone();
                copy.Id = 0;
                copy.IsPublished = true;
                copy.PageName = nodeName;
                copy.Name = newName;
                copy.StartsAt = parsedDate;
                copy.IsActive = true;
                copy.Url = "";
                copy.ImageUrl = "";

                // ⚠️ DE ANDRA DATUMEN MÅSTE FLYTTAS MED, inte kopieras rakt. Ett slutdatum eller en
                // sista anmälningsdag från källan ligger i förhållande till KÄLLANS datum: kopierade
                // de rakt in hade kopian burit en deadline som redan passerat, och `IsSignupOpen`
                // hade stängt anmälan direkt på en händelse som ser komplett ut. Samma antal dagar
                // före/efter start bevaras därför, och saknas källans startdatum nollas de i stället
                // — ett datum vi inte kan räkna om är sämre än inget.
                var sourceStart = source.StartsAt;
                TimeSpan? shift = sourceStart.HasValue ? parsedDate.Date - sourceStart.Value.Date : null;

                DateTime? Shifted(DateTime? v)
                {
                    if (v == null || shift == null) return null;
                    return v.Value.Add(shift.Value);
                }

                copy.EndsAt = Shifted(source.EndsAt);
                copy.RegistrationDeadline = Shifted(source.RegistrationDeadline);

                // Lagringen sparar och publicerar, och städar själv bort en sparad men opublicerad
                // kopia — den är osynlig på klubbsidan, så ett nytt försök hade skapat dubbletter i tysthet.
                var (copyId, saveError) = await _events.SaveAsync(copy);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                // Skjutbanan bor i ClubEventRange, inte på posten — klonen tar den inte med.
                _rangeCalendar.CopyEventRange(sourceEventId, copyId);
                return Ok(new { success = true, message = "Händelse kopierad", data = new { id = copyId } });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error copying event: " + ex.Message });
            }
        }

        /// <summary>
        /// Händelseformulärets grundfält på posten (klubbens och kretsens redigeringsdialog).
        /// Anmälningsfälten går genom <see cref="HpskSite.Models.Events.ClubEventInput.ApplyRegistrationFields"/>.
        /// </summary>
        private static void ApplyEventFormFields(HpskSite.Models.Events.ClubEventRecord e,
            string eventName, string? eventType, string? description, string? venue,
            string? contactPerson, string? contactEmail, string? contactPhone, string? pageName)
        {
            // Sidnamnet (adressens sista del) = pageName om det angetts, annars händelsens namn.
            e.PageName = !string.IsNullOrWhiteSpace(pageName) ? pageName : eventName;
            e.Name = eventName;
            e.EventType = eventType ?? "";
            e.Description = description ?? "";
            e.Venue = venue ?? "";
            e.ContactPerson = contactPerson ?? "";
            e.ContactEmail = contactEmail ?? "";
            e.ContactPhone = contactPhone ?? "";
        }

        /// <summary>
        /// En händelse som rad i klubbens eller kretsens administrationslista (GetClubEvents /
        /// GetRegionEvents). EN projektion för båda — de låg i två kopior som var tvungna att hållas
        /// lika fält för fält. Egenskapsnamn och värdeformer är oförändrade mot nodtiden.
        /// </summary>
        private object EventEditListRow(HpskSite.Models.Events.ClubEventRecord evt,
            IReadOnlyDictionary<int, HpskSite.Models.ClubEventRange> eventRanges)
        {
            var support = _events.Support;
            var displayName = evt.DisplayName;
            var prices = HpskSite.Models.EventPrices.Parse(evt.PricesJson);
            eventRanges.TryGetValue(evt.Id, out var evRange);
            return new
            {
                id = evt.Id,
                name = displayName,
                pageName = evt.PageName != displayName ? evt.PageName : "",
                date = evt.StartsAt?.ToString("yyyy-MM-ddTHH:mm:ss"),
                type = string.IsNullOrEmpty(evt.EventType) ? "Träning" : evt.EventType,
                description = evt.Description,
                venue = evt.Venue,
                // Skjutbanan (banans kalender). ⚠️ Måste med — annars förfylls väljaren
                // med klubbens förval och nästa sparning flyttar händelsen till den banan.
                rangeId = evRange?.RangeId ?? 0,
                rangeSectionId = evRange?.RangeSectionId,
                contactPerson = evt.ContactPerson,
                contactEmail = evt.ContactEmail,
                // ⚠️⚠️ SAKNADES, och det RADERADE numret. Redigeringsdialogen fyller
                // `eventContactPhone` ur den här projektionen; utan raden fylldes den
                // med tomt, och sparningen skriver kontakttelefonen villkorslöst.
                // Alltså försvann kontakttelefonen varje gång någon redigerade en händelse
                // från klubb- eller kretspanelen — tyst. Samma fältlista-som-glömmer som
                // Luleå PK rapporterade om lånevapen, fjärde instansen på den här ytan.
                contactPhone = evt.ContactPhone,
                isActive = evt.IsActive,
                url = evt.Url,
                // Anmälningsinställningarna, så redigera-dialogen kan förfyllas utan ett
                // extra anrop per rad. `*PropertyExists` följer med för att klienten ska kunna
                // stänga av kontrollen när lagringen inte kan bära fältet (_events.Support).
                registrationRequired = evt.RegistrationRequired,
                registrationUrl = evt.RegistrationUrl,
                maxParticipants = evt.MaxParticipants,
                isMandatory = evt.IsMandatory,
                lanevapenOffered = support.LanevapenOffered && evt.LanevapenOffered,
                lanevapenPropertyExists = support.LanevapenOffered,
                // ⚠️ Vem som får anmäla sig. Utan raden kan dialogen inte förfylla
                // väljaren, och nästa sparning skickar tillbaka standard — alltså
                // STÄNGER tyst en händelse arrangören öppnat. Exakt samma fälla som
                // lanevapenOffered gick i, rapporterad av Luleå PK.
                eventAudience = HpskSite.Models.EventAudience.Normalise(evt.Audience),
                audiencePropertyExists = support.Audience,
                // Åsidosättningen. Tomt = ägarens nummer används — det avgörs vid
                // läsning i ClubEventParticipationService, inte här.
                eventSwishNumber = support.Swish ? evt.SwishNumber : "",
                swishPropertyExists = support.Swish,
                mandatoryPropertyExists = support.Mandatory,
                // Sista anmälningsdag. Bara datumdelen: fältet ÄR en dag, och en
                // klockslagsdel i strängen hade flatpickr skrivit tillbaka som text.
                registrationDeadline = support.Deadline
                    ? evt.RegistrationDeadline?.ToString("yyyy-MM-dd") ?? ""
                    : "",
                deadlinePropertyExists = support.Deadline,
                // Avgiften. null = ingen avgift angiven, 0 = arrangoren har sagt gratis.
                // De far INTE slas ihop - se hpskEventRegFill i _EventRegistrationFields.
                eventPrices = prices.Rows,
                eventPricesUnreadable = prices.Unreadable,
                eventPricesPropertyExists = support.Prices
            };
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

                    // Klubbens händelser genom lagringen (fas 2a) — bara publicerade, som förut.
                    var allEvents = _events.ListForOwner(clubId)
                        .OrderBy(e => e.StartsAt ?? DateTime.MinValue)
                        .ToList();

                    var eventRanges = _rangeCalendar.GetEventRanges(allEvents.Select(e => e.Id));
                    foreach (var evt in allEvents)
                    {
                        events.Add(EventEditListRow(evt, eventRanges));
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

                // Bara klubbens medlemmar, ur klubbregistret (skalplanen B3) — förut hela registret (3,6 s vid 20 000).
                var clubMembers = HttpContext.RequestServices.GetRequiredService<ClubRosterService>()
                    .LoadActiveMembers(_memberService, clubId)
                    .Where(m => m.ContentType.Alias != "hpskClub")
                    .ToList();

                // Filter by search if provided
                if (!string.IsNullOrWhiteSpace(search))
                {
                    clubMembers = clubMembers
                        .Where(m => m.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }

                // One batched query for "which (member, discipline) pairs have any data" —
                // powers the green dot on each row's dashboard button.
                var presenceMap = await _presenceService.GetClubPresenceAsync();

                // ⚠️ "Medlem sedan" är KLUBBENS uppgift, för DEN HÄR klubben. Listan läste förut
                // medlemsegenskapen `memberSince`, som är delad över alla klubbar en medlem
                // tillhör — alltså kunde den bara vara sann för en av dem, och på klubbens egen
                // sida visade den då någon annan klubbs datum. En rad per medlem hämtas i EN fråga
                // (`GetForClub`), inte en uppslagning per medlem i loopen nedan.
                var membershipSince = _clubMembershipService.GetForClub(clubId)
                    .Where(cm => cm.MemberSince != null)
                    .GroupBy(cm => cm.MemberId)
                    .ToDictionary(g => g.Key, g => g.First().MemberSince);

                // Build member data with privacy levels
                foreach (var member in clubMembers.OrderBy(m => m.Name))
                {
                    var isPrimary = member.GetValue("primaryClubId")?.ToString() == clubId.ToString();
                    var additionalClubs = member.GetValue("memberClubIds")?.ToString() ?? "";
                    var hasData = presenceMap.TryGetValue(member.Id, out var memberDisciplines) && memberDisciplines.Count > 0;

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
                        // Any-discipline data presence — drives the green dot on the dashboard button
                        hasData = hasData,
                        // Extended properties only visible to club admins
                        address = isClubAdmin ? (member.GetValue<string>("address") ?? "") : null,
                        postalCode = isClubAdmin ? (member.GetValue<string>("postalCode") ?? "") : null,
                        city = isClubAdmin ? (member.GetValue<string>("City") ?? "") : null,
                        phoneNumber = isClubAdmin ? (member.GetValue<string>("phoneNumber") ?? "") : null,
                        personNumber = isClubAdmin ? (member.GetValue<string>("personNumber") ?? "") : null,
                        shooterIdNumber = isClubAdmin ? (member.GetValue<string>("shooterIdNumber") ?? "") : null,
                        // ⚠️ KLUBBENS datum, inte medlemsegenskapen. Se kommentaren vid
                        // `membershipSince` ovan. Tomt är ett ärligt svar — klubben har inte fyllt
                        // i det, och medlemsdialogen under Klubbmedlemskap är där det görs.
                        memberSince = isClubAdmin && membershipSince.TryGetValue(member.Id, out var since)
                            ? since
                            : null
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
            string contactEmail = "", string contactPhone = "", string eventDate = "",
            string pageName = "",
            bool registrationRequired = false, int maxParticipants = 0, bool isMandatory = false, string registrationUrl = "", bool lanevapenOffered = false,
            string registrationDeadline = "", string eventPrices = "", string eventAudience = "",
            string eventSwishNumber = "", int? rangeId = null, int? rangeSectionId = null)
        {
            try
            {
                if (eventId <= 0 || string.IsNullOrWhiteSpace(eventName))
                {
                    return Ok(new { success = false, message = "Event ID and name are required" });
                }

                var e = _events.Get(eventId);
                if (e == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                // Behörigheten går via ägaren (EventOwnerResolver). En kretshändelse som når den här
                // klubbendpointen får kretsens regel i stället för ett klubbuppslag på kretsnoden.
                if (!await _eventOwners.CanManageAsync(_eventOwners.ForEventRecord(e)))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var rangeError = rangeId is > 0 ? _rangeCalendar.Validate(rangeId.Value, rangeSectionId) : null;
                if (rangeError != null) return Ok(new { success = false, message = rangeError });

                ApplyEventFormFields(e, eventName, eventType, description, venue, contactPerson,
                    contactEmail, contactPhone, pageName);
                var regFieldError = HpskSite.Models.Events.ClubEventInput.ApplyRegistrationFields(e, registrationRequired,
                    maxParticipants, isMandatory, registrationUrl, lanevapenOffered, registrationDeadline, eventPrices, eventAudience, eventSwishNumber);
                if (regFieldError != null)
                {
                    // Avbryts FÖRE sparningen — det som ligger publicerat är orört.
                    return Ok(new { success = false, message = regFieldError });
                }
                // Ett tomt eller oläsbart datum tar bort datumet, som förut.
                e.StartsAt = !string.IsNullOrEmpty(eventDate) && DateTime.TryParse(eventDate, out var parsedEventDate)
                    ? parsedEventDate
                    : null;

                // Redigeringen publicerar alltid, som förut.
                e.IsPublished = true;
                var (_, saveError) = await _events.SaveAsync(e);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                _rangeCalendar.SetEventRange(eventId, rangeId, rangeSectionId, null);
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

                var e = _events.Get(eventId);
                if (e == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                // Behörigheten går via ägaren (EventOwnerResolver).
                if (!await _eventOwners.CanManageAsync(_eventOwners.ForEventRecord(e)))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Lagringen avpublicerar och tar bort.
                _events.Delete(e.Id);
                _rangeCalendar.DeleteEventRange(e.Id);

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
                _contentService.Publish(newNews, new[] { "*" }, -1);

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
                _contentService.Publish(newsContent_item, new[] { "*" }, -1);

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

                    var logo = clubNode.Value<IPublishedContent>("logo");
                    var bannerImage = clubNode.Value<IPublishedContent>("bannerImage");

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
                            orgNumber = clubNode.Value<string>("orgNumber") ?? "",
                            receiptEmail = clubNode.Value<string>("receiptEmail") ?? "",
                            swishNumber = clubNode.Value<string>("swishNumber") ?? "",
                            bgNumber = clubNode.Value<string>("bgNumber") ?? "",
                            description = clubNode.Value<string>("description") ?? "",
                            aboutClub = clubNode.Value<string>("aboutClub") ?? "",
                            logoUrl = logo?.Url() ?? "",
                            bannerImageUrl = bannerImage?.Url() ?? "",
                            brevoApiKey = clubNode.Value<string>("brevoApiKey") ?? ""
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
            string postalCode = "", string orgNumber = "", string receiptEmail = "", string aboutClub = "", string brevoApiKey = "",
            string swishNumber = "", string bgNumber = "")
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
                clubContent.SetValue("orgNumber", orgNumber ?? "");
                clubContent.SetValue("receiptEmail", receiptEmail ?? "");

                // Payment details shown on the club's invoices. Guarded because SetValue on a missing
                // alias is a silent no-op on some installs — a payment number that "saved" but didn't
                // is worse than a visible error.
                if (clubContent.HasProperty("swishNumber"))
                    clubContent.SetValue("swishNumber", (swishNumber ?? "").Trim());
                if (clubContent.HasProperty("bgNumber"))
                    clubContent.SetValue("bgNumber", (bgNumber ?? "").Trim());

                // Update description properties
                clubContent.SetValue("description", description);
                clubContent.SetValue("aboutClub", aboutClub);

                // Update Brevo API key (if property exists)
                if (clubContent.HasProperty("brevoApiKey"))
                    clubContent.SetValue("brevoApiKey", brevoApiKey ?? "");

                // Save and publish
                _contentService.Save(clubContent);
                _contentService.Publish(clubContent, new[] { "*" }, -1);

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
                // Synken satte nyss raden till "Utträdd" — men en avslagen sökande har aldrig varit medlem.
                // Raden tas bort om den aldrig blev ett medlemskap (genomgången 2026-10-13, A5).
                _clubMembershipService.DeleteIfNeverMember(prospectMember.Id, request.ClubId);

                return Ok(new { success = true, message = "Member declined and removed from club" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error declining member: " + ex.Message });
            }
        }

        /// <summary>
        /// Get region information for settings form
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionInfo(int contentId)
        {
            try
            {
                if (contentId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid content ID" });
                }

                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                var regionNode = UmbracoContext.Content.GetById(contentId);
                if (regionNode == null || regionNode.ContentType.Alias != "regionalPage")
                {
                    return Ok(new { success = false, message = "Region not found" });
                }

                // Auth check
                var regionCode = regionNode.Value<string>("regionCode") ?? "";
                if (string.IsNullOrEmpty(regionCode) || !await _authorizationService.IsRegionalAdminForRegion(regionCode))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var logo = regionNode.Value<IPublishedContent>("logo");
                var bannerImage = regionNode.Value<IPublishedContent>("bannerImage");

                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        welcomeTitle = regionNode.Value<string>("welcomeTitle") ?? "",
                        welcomeText = regionNode.Value<string>("welcomeText") ?? "",
                        aboutRegion = regionNode.Value<string>("aboutRegion") ?? "",
                        contactPerson = regionNode.Value<string>("contactPerson") ?? "",
                        contactEmail = regionNode.Value<string>("contactEmail") ?? "",
                        contactPhone = regionNode.Value<string>("contactPhone") ?? "",
                        orgNumber = regionNode.Value<string>("orgNumber") ?? "",
                        address = regionNode.Value<string>("address") ?? "",
                        city = regionNode.Value<string>("city") ?? "",
                        postalCode = regionNode.Value<string>("postalCode") ?? "",
                        receiptEmail = regionNode.Value<string>("receiptEmail") ?? "",
                        swishNumber = regionNode.Value<string>("swishNumber") ?? "",
                        bgNumber = regionNode.Value<string>("bgNumber") ?? "",
                        logoUrl = logo?.Url() ?? "",
                        bannerImageUrl = bannerImage?.Url() ?? "",
                        brevoApiKey = regionNode.Value<string>("brevoApiKey") ?? ""
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading region info: " + ex.Message });
            }
        }

        /// <summary>
        /// Update region text and contact information
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateRegionInfo(int contentId, string welcomeTitle = "",
            string welcomeText = "", string aboutRegion = "", string contactPerson = "",
            string contactEmail = "", string contactPhone = "", string orgNumber = "",
            string address = "", string city = "", string postalCode = "", string receiptEmail = "", string brevoApiKey = "",
            string swishNumber = "", string bgNumber = "")
        {
            try
            {
                if (contentId <= 0)
                {
                    return Ok(new { success = false, message = "Content ID is required" });
                }

                // Look up regionCode from published content for auth
                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                var publishedNode = UmbracoContext.Content.GetById(contentId);
                if (publishedNode == null || publishedNode.ContentType.Alias != "regionalPage")
                {
                    return Ok(new { success = false, message = "Region not found" });
                }

                var regionCode = publishedNode.Value<string>("regionCode") ?? "";
                if (string.IsNullOrEmpty(regionCode) || !await _authorizationService.IsRegionalAdminForRegion(regionCode))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var regionContent = _contentService.GetById(contentId);
                if (regionContent == null)
                {
                    return Ok(new { success = false, message = "Region content not found" });
                }

                regionContent.SetValue("welcomeTitle", welcomeTitle);
                regionContent.SetValue("welcomeText", welcomeText);
                regionContent.SetValue("aboutRegion", aboutRegion);
                regionContent.SetValue("contactPerson", contactPerson);
                regionContent.SetValue("contactEmail", contactEmail);
                regionContent.SetValue("contactPhone", contactPhone);
                regionContent.SetValue("orgNumber", orgNumber ?? "");
                regionContent.SetValue("address", address ?? "");
                regionContent.SetValue("city", city ?? "");
                regionContent.SetValue("postalCode", postalCode ?? "");
                regionContent.SetValue("receiptEmail", receiptEmail ?? "");

                // Payment details shown on the krets's invoices. Guarded — SetValue on a missing alias is
                // a silent no-op, and a payment number that looked saved but wasn't sends money nowhere.
                if (regionContent.HasProperty("swishNumber"))
                    regionContent.SetValue("swishNumber", (swishNumber ?? "").Trim());
                if (regionContent.HasProperty("bgNumber"))
                    regionContent.SetValue("bgNumber", (bgNumber ?? "").Trim());

                // Update Brevo API key (if property exists)
                if (regionContent.HasProperty("brevoApiKey"))
                    regionContent.SetValue("brevoApiKey", brevoApiKey ?? "");

                _contentService.Save(regionContent);
                _contentService.Publish(regionContent, new[] { "*" }, -1);

                return Ok(new { success = true, message = "Region information updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating region info: " + ex.Message });
            }
        }

        /// <summary>
        /// Upload a logo or banner image for a region
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadRegionImage(int contentId, string imageType, IFormFile imageFile)
        {
            try
            {
                if (contentId <= 0 || string.IsNullOrWhiteSpace(imageType))
                {
                    return Ok(new { success = false, message = "Content ID and image type are required" });
                }

                if (imageType != "logo" && imageType != "bannerImage")
                {
                    return Ok(new { success = false, message = "Invalid image type" });
                }

                // Auth check
                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                var publishedNode = UmbracoContext.Content.GetById(contentId);
                if (publishedNode == null || publishedNode.ContentType.Alias != "regionalPage")
                {
                    return Ok(new { success = false, message = "Region not found" });
                }

                var regionCode = publishedNode.Value<string>("regionCode") ?? "";
                if (string.IsNullOrEmpty(regionCode) || !await _authorizationService.IsRegionalAdminForRegion(regionCode))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Validate file
                if (imageFile == null || imageFile.Length == 0)
                {
                    return Ok(new { success = false, message = "No file uploaded" });
                }

                if (imageFile.Length > 5 * 1024 * 1024)
                {
                    return Ok(new { success = false, message = "File too large. Maximum 5 MB allowed." });
                }

                var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                if (!allowedExtensions.Contains(extension))
                {
                    return Ok(new { success = false, message = "Invalid file type. Only JPG, PNG, GIF and WebP allowed." });
                }

                // Find or create "Region Images" folder in Media Library
                var regionImagesFolder = _mediaService.GetRootMedia()
                    .FirstOrDefault(m => m.Name == "Region Images");

                if (regionImagesFolder == null)
                {
                    regionImagesFolder = _mediaService.CreateMedia("Region Images", -1, "Folder");
                    _mediaService.Save(regionImagesFolder);
                }

                // Create media item
                string fileName = Path.GetFileName(imageFile.FileName);
                string fileExtension = Path.GetExtension(fileName);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid()}{fileExtension}";

                var mediaItem = _mediaService.CreateMedia(fileName, regionImagesFolder.Id, "Image");

                // Save file to physical location
                var mediaFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media", "region-images");
                if (!Directory.Exists(mediaFolderPath))
                {
                    Directory.CreateDirectory(mediaFolderPath);
                }

                var physicalFilePath = Path.Combine(mediaFolderPath, uniqueFileName);
                using (var fileStream = new FileStream(physicalFilePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(fileStream);
                }

                // Set media properties
                var relativePath = $"/media/region-images/{uniqueFileName}";
                mediaItem.SetValue("umbracoFile", relativePath);
                mediaItem.SetValue("umbracoExtension", fileExtension.TrimStart('.'));
                mediaItem.SetValue("umbracoBytes", imageFile.Length.ToString());

                var mediaSaveResult = _mediaService.Save(mediaItem);
                if (!mediaSaveResult.Success)
                {
                    if (System.IO.File.Exists(physicalFilePath))
                    {
                        System.IO.File.Delete(physicalFilePath);
                    }
                    return Ok(new { success = false, message = "Failed to save image to media library" });
                }

                // Link media to region content property
                var regionContent = _contentService.GetById(contentId);
                if (regionContent == null)
                {
                    return Ok(new { success = false, message = "Region content not found" });
                }

                regionContent.SetValue(imageType, mediaItem.GetUdi().ToString());
                _contentService.Save(regionContent);
                _contentService.Publish(regionContent, new[] { "*" }, -1);

                return Ok(new
                {
                    success = true,
                    message = "Image uploaded successfully",
                    imageUrl = relativePath
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error uploading image: " + ex.Message });
            }
        }

        /// <summary>
        /// Remove a logo or banner image from a region
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveRegionImage(int contentId, string imageType)
        {
            try
            {
                if (contentId <= 0 || string.IsNullOrWhiteSpace(imageType))
                {
                    return Ok(new { success = false, message = "Content ID and image type are required" });
                }

                if (imageType != "logo" && imageType != "bannerImage")
                {
                    return Ok(new { success = false, message = "Invalid image type" });
                }

                // Auth check
                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                var publishedNode = UmbracoContext.Content.GetById(contentId);
                if (publishedNode == null || publishedNode.ContentType.Alias != "regionalPage")
                {
                    return Ok(new { success = false, message = "Region not found" });
                }

                var regionCode = publishedNode.Value<string>("regionCode") ?? "";
                if (string.IsNullOrEmpty(regionCode) || !await _authorizationService.IsRegionalAdminForRegion(regionCode))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var regionContent = _contentService.GetById(contentId);
                if (regionContent == null)
                {
                    return Ok(new { success = false, message = "Region content not found" });
                }

                regionContent.SetValue(imageType, null);
                _contentService.Save(regionContent);
                _contentService.Publish(regionContent, new[] { "*" }, -1);

                return Ok(new { success = true, message = "Image removed successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error removing image: " + ex.Message });
            }
        }

        /// <summary>
        /// Upload a logo or banner image for a club
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadClubImage(int clubId, string imageType, IFormFile imageFile)
        {
            try
            {
                if (clubId <= 0 || string.IsNullOrWhiteSpace(imageType))
                {
                    return Ok(new { success = false, message = "Club ID and image type are required" });
                }

                if (imageType != "logo" && imageType != "bannerImage")
                {
                    return Ok(new { success = false, message = "Invalid image type" });
                }

                // Auth check
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                var clubNode = UmbracoContext.Content.GetById(clubId);
                if (clubNode == null || clubNode.ContentType.Alias != "club")
                {
                    return Ok(new { success = false, message = "Club not found" });
                }

                // En regel för bilduppladdningar (C3-planen S7): klubbens logga och banner, seriens logga.
                var images = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Media.ImageMediaService>();
                var (udi, relativePath, uploadError) = await images.SaveAsync(imageFile, "Club Images", "club-images");
                if (uploadError != null)
                {
                    return Ok(new { success = false, message = uploadError });
                }

                // Link media to club content property
                var clubContent = _contentService.GetById(clubId);
                if (clubContent == null)
                {
                    return Ok(new { success = false, message = "Club content not found" });
                }

                clubContent.SetValue(imageType, udi);
                _contentService.Save(clubContent);
                _contentService.Publish(clubContent, new[] { "*" }, -1);

                return Ok(new
                {
                    success = true,
                    message = "Image uploaded successfully",
                    imageUrl = relativePath
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error uploading image: " + ex.Message });
            }
        }

        /// <summary>
        /// Remove a logo or banner image from a club
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveClubImage(int clubId, string imageType)
        {
            try
            {
                if (clubId <= 0 || string.IsNullOrWhiteSpace(imageType))
                {
                    return Ok(new { success = false, message = "Club ID and image type are required" });
                }

                if (imageType != "logo" && imageType != "bannerImage")
                {
                    return Ok(new { success = false, message = "Invalid image type" });
                }

                // Auth check
                if (!await _authorizationService.IsClubAdminForClub(clubId))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var clubContent = _contentService.GetById(clubId);
                if (clubContent == null || clubContent.ContentType.Alias != "club")
                {
                    return Ok(new { success = false, message = "Club not found" });
                }

                clubContent.SetValue(imageType, null);
                _contentService.Save(clubContent);
                _contentService.Publish(clubContent, new[] { "*" }, -1);

                return Ok(new { success = true, message = "Image removed successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error removing image: " + ex.Message });
            }
        }

        /// <summary>
        /// Get region events (the region's events, read through IClubEventStore)
        /// </summary>
        [HttpGet]
        public IActionResult GetRegionEvents(int regionId)
        {
            try
            {
                if (regionId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid region ID" });
                }

                var events = new List<dynamic>();

                if (UmbracoContext.Content != null)
                {
                    var regionNode = UmbracoContext.Content.GetById(regionId);
                    if (regionNode == null || regionNode.ContentType.Alias != "regionalPage")
                    {
                        return Ok(new { success = false, message = "Region not found" });
                    }

                    // Kretsens händelser genom lagringen (fas 2a) — bara publicerade, som förut.
                    var allEvents = _events.ListForOwner(regionId)
                        .OrderBy(e => e.StartsAt ?? DateTime.MinValue)
                        .ToList();

                    var eventRanges = _rangeCalendar.GetEventRanges(allEvents.Select(e => e.Id));
                    foreach (var evt in allEvents)
                    {
                        events.Add(EventEditListRow(evt, eventRanges));
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
                return Ok(new { success = false, message = "Error loading region events: " + ex.Message });
            }
        }

        /// <summary>
        /// Create a new event as child of a region node
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateRegionEvent(int regionId, string eventName, string eventType = "Träning",
            string description = "", string venue = "", string contactPerson = "",
            string contactEmail = "", string contactPhone = "", string eventDate = "",
            string pageName = "",
            bool registrationRequired = false, int maxParticipants = 0, bool isMandatory = false, string registrationUrl = "", bool lanevapenOffered = false,
            string registrationDeadline = "", string eventPrices = "", string eventAudience = "",
            string eventSwishNumber = "", int? rangeId = null, int? rangeSectionId = null)
        {
            try
            {
                if (regionId <= 0 || string.IsNullOrWhiteSpace(eventName))
                {
                    return Ok(new { success = false, message = "Region ID and event name are required" });
                }

                var rangeError = rangeId is > 0 ? _rangeCalendar.Validate(rangeId.Value, rangeSectionId) : null;
                if (rangeError != null) return Ok(new { success = false, message = rangeError });

                if (UmbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Umbraco context not available" });
                }

                var regionNode = UmbracoContext.Content.GetById(regionId);
                if (regionNode == null || regionNode.ContentType.Alias != "regionalPage")
                {
                    return Ok(new { success = false, message = "Region not found" });
                }

                var regionCode = regionNode.Value<string>("regionCode") ?? "";
                if (string.IsNullOrEmpty(regionCode) || !await _authorizationService.IsRegionalAdminForRegion(regionCode))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Ny händelse under kretsen, genom lagringen (fas 2a).
                var newEvent = new HpskSite.Models.Events.ClubEventRecord
                {
                    OwnerType = HpskSite.Models.Events.ClubEventRecord.OwnerRegion,
                    OwnerId = regionId,
                    IsActive = true,
                    IsPublished = true
                };
                ApplyEventFormFields(newEvent, eventName, eventType, description, venue, contactPerson,
                    contactEmail, contactPhone, pageName);
                var regFieldError = HpskSite.Models.Events.ClubEventInput.ApplyRegistrationFields(newEvent, registrationRequired,
                    maxParticipants, isMandatory, registrationUrl, lanevapenOffered, registrationDeadline, eventPrices, eventAudience, eventSwishNumber);
                if (regFieldError != null)
                {
                    // Avbryts FÖRE sparningen, så händelsen aldrig blir till. Ett halvsparat
                    // evenemang utan sin deadline är värre än inget: det är publikt och öppet.
                    return Ok(new { success = false, message = regFieldError });
                }
                if (!string.IsNullOrEmpty(eventDate) && DateTime.TryParse(eventDate, out var parsedEventDate))
                {
                    newEvent.StartsAt = parsedEventDate;
                }

                // Lagringen sparar och publicerar, och lämnar inget opublicerat utkast efter sig.
                var (newId, saveError) = await _events.SaveAsync(newEvent);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                _rangeCalendar.SetEventRange(newId, rangeId, rangeSectionId, null);
                return Ok(new
                {
                    success = true,
                    message = "Event created successfully",
                    data = new
                    {
                        id = newId,
                        name = eventName,
                        date = eventDate,
                        type = eventType
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating region event: " + ex.Message });
            }
        }

        /// <summary>
        /// Edit an existing region event
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditRegionEvent(int eventId, string eventName, string eventType = "Träning",
            string description = "", string venue = "", string contactPerson = "",
            string contactEmail = "", string contactPhone = "", string eventDate = "",
            string pageName = "",
            bool registrationRequired = false, int maxParticipants = 0, bool isMandatory = false, string registrationUrl = "", bool lanevapenOffered = false,
            string registrationDeadline = "", string eventPrices = "", string eventAudience = "",
            string eventSwishNumber = "", int? rangeId = null, int? rangeSectionId = null)
        {
            try
            {
                if (eventId <= 0 || string.IsNullOrWhiteSpace(eventName))
                {
                    return Ok(new { success = false, message = "Event ID and name are required" });
                }

                var rangeError = rangeId is > 0 ? _rangeCalendar.Validate(rangeId.Value, rangeSectionId) : null;
                if (rangeError != null) return Ok(new { success = false, message = rangeError });

                var e = _events.Get(eventId);
                if (e == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                // Behörigheten går via ägaren (EventOwnerResolver). Den här endpointen gäller
                // kretsens händelser, så en klubbhändelse vägras här som förut.
                var regionOwner = _eventOwners.ForEventRecord(e);
                if (regionOwner is null || !regionOwner.IsRegion)
                {
                    return Ok(new { success = false, message = "Parent region not found" });
                }
                if (!await _eventOwners.CanManageAsync(regionOwner))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                ApplyEventFormFields(e, eventName, eventType, description, venue, contactPerson,
                    contactEmail, contactPhone, pageName);
                var regFieldError = HpskSite.Models.Events.ClubEventInput.ApplyRegistrationFields(e, registrationRequired,
                    maxParticipants, isMandatory, registrationUrl, lanevapenOffered, registrationDeadline, eventPrices, eventAudience, eventSwishNumber);
                if (regFieldError != null)
                {
                    // Avbryts FÖRE sparningen — det som ligger publicerat är orört.
                    return Ok(new { success = false, message = regFieldError });
                }
                // Ett tomt eller oläsbart datum tar bort datumet, som förut.
                e.StartsAt = !string.IsNullOrEmpty(eventDate) && DateTime.TryParse(eventDate, out var parsedEventDate)
                    ? parsedEventDate
                    : null;

                // Redigeringen publicerar alltid, som förut.
                e.IsPublished = true;
                var (_, saveError) = await _events.SaveAsync(e);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                _rangeCalendar.SetEventRange(eventId, rangeId, rangeSectionId, null);
                return Ok(new { success = true, message = "Event updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating region event: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a region event
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRegionEvent(int eventId)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Event ID is required" });
                }

                var e = _events.Get(eventId);
                if (e == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                // Behörigheten går via ägaren (EventOwnerResolver). Den här endpointen gäller
                // kretsens händelser, så en klubbhändelse vägras här som förut.
                var regionOwner = _eventOwners.ForEventRecord(e);
                if (regionOwner is null || !regionOwner.IsRegion)
                {
                    return Ok(new { success = false, message = "Parent region not found" });
                }
                if (!await _eventOwners.CanManageAsync(regionOwner))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Lagringen avpublicerar och tar bort.
                _events.Delete(e.Id);
                _rangeCalendar.DeleteEventRange(e.Id);

                return Ok(new { success = true, message = "Event deleted successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error deleting region event: " + ex.Message });
            }
        }

        // ==========================================
        // Event Landing Page Editing Endpoints
        // ==========================================

        /// <summary>
        /// Unified auth check for event page endpoints. Works for both club and region events.
        /// Returns true if authorized, false otherwise.
        /// </summary>
        private Task<bool> IsEventAdminAsync(HpskSite.Models.Events.ClubEventRecord e)
            => _eventOwners.CanManageAsync(_eventOwners.ForEventRecord(e));

        /// <summary>
        /// Get current editable data for an event page (detail fields, image URL, content blocks, quick links)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetEventEditData(int eventId)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid event ID" });
                }

                var e = _events.Get(eventId);
                if (e == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                if (!await IsEventAdminAsync(e))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                var support = _events.Support;
                var prices = HpskSite.Models.EventPrices.Parse(e.PricesJson);

                var evRange = _rangeCalendar.GetEventRange(eventId);
                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        // Basic fields
                        eventName = e.Name,
                        pageName = e.PageName,
                        eventType = e.EventType,
                        eventDate = e.StartsAt?.ToString("yyyy-MM-dd HH:mm") ?? "",
                        venue = e.Venue,
                        // Skjutbanan — se GetClubEvents. Ägaren behövs för väljarens förval av klubbens banor.
                        rangeId = evRange?.RangeId ?? 0,
                        rangeSectionId = evRange?.RangeSectionId,
                        ownerClubId = e.OwnerType == HpskSite.Models.Events.ClubEventRecord.OwnerClub ? e.OwnerId : 0,
                        description = e.Description,
                        contactPerson = e.ContactPerson,
                        contactEmail = e.ContactEmail,
                        contactPhone = e.ContactPhone,
                        // Extended fields
                        // feeAmount returneras INTE langre - fritextavgiften ar ersatt av eventPrices.
                        equipmentRequired = e.EquipmentRequired,
                        targetAudience = e.TargetAudience,
                        registrationRequired = e.RegistrationRequired,
                        registrationUrl = e.RegistrationUrl,
                        eventEndDate = e.EndsAt?.ToString("yyyy-MM-dd") ?? "",
                        // Bildens adress är upplöst av lagringen (ur den publicerade händelsen).
                        eventImageUrl = e.ImageUrl,
                        contentBlocks = string.IsNullOrEmpty(e.ContentBlocksJson) ? "[]" : e.ContentBlocksJson,
                        quickLinks = string.IsNullOrEmpty(e.QuickLinksJson) ? "[]" : e.QuickLinksJson,
                        // Operator-added; the client needs to know whether it may offer the switch
                        // at all, since saving to a missing property is a silent no-op.
                        isMandatory = e.IsMandatory,
                        mandatoryPropertyExists = support.Mandatory,
                        maxParticipants = e.MaxParticipants,
                        registrationDeadline = support.Deadline
                            ? e.RegistrationDeadline?.ToString("yyyy-MM-dd") ?? ""
                            : "",
                        deadlinePropertyExists = support.Deadline,
                        // ⚠️ Måste med, annars kan dialogen inte förfylla kryssrutan — och en
                        // urkryssad ruta som postas tillbaka SLÄCKER klubbens val.
                        lanevapenOffered = support.LanevapenOffered && e.LanevapenOffered,
                        lanevapenPropertyExists = support.LanevapenOffered,
                        // ⚠️ Se kommentaren i EventEditListRow — samma fälla, samma skäl.
                        eventAudience = HpskSite.Models.EventAudience.Normalise(e.Audience),
                        audiencePropertyExists = support.Audience,
                        eventSwishNumber = support.Swish ? e.SwishNumber : "",
                        swishPropertyExists = support.Swish,
                        // Avgiften. ⚠️ null och 0 är olika svar — se hpskEventRegFill.
                        eventPrices = prices.Rows,
                        eventPricesUnreadable = prices.Unreadable,
                        eventPricesPropertyExists = support.Prices
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading event data: " + ex.Message });
            }
        }


        /// <summary>
        /// Edit event detail fields (fee, equipment, audience, registration required)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditEventDetails(int eventId,
            string eventName = "", string pageName = "", string eventType = "",
            string eventDate = "", string venue = "", string description = "",
            string contactPerson = "", string contactEmail = "", string contactPhone = "",
            string equipmentRequired = "", string targetAudience = "",
            bool registrationRequired = false, string registrationUrl = "", string eventEndDate = "",
            bool isMandatory = false, int maxParticipants = 0,
            bool lanevapenOffered = false, string registrationDeadline = "", string eventPrices = "", string eventAudience = "",
            string eventSwishNumber = "", int? rangeId = null, int? rangeSectionId = null)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid event ID" });
                }

                var rangeError = rangeId is > 0 ? _rangeCalendar.Validate(rangeId.Value, rangeSectionId) : null;
                if (rangeError != null) return Ok(new { success = false, message = rangeError });

                var e = _events.Get(eventId);
                if (e == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                if (!await IsEventAdminAsync(e))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                // Save basic fields
                if (!string.IsNullOrWhiteSpace(eventName))
                {
                    e.Name = eventName;
                }
                // Only update URL slug (page name) when pageName is explicitly provided
                if (!string.IsNullOrWhiteSpace(pageName))
                {
                    e.PageName = pageName;
                }
                e.EventType = eventType ?? "";
                e.Venue = venue ?? "";
                e.Description = description ?? "";
                e.ContactPerson = contactPerson ?? "";
                e.ContactEmail = contactEmail ?? "";
                e.ContactPhone = contactPhone ?? "";

                e.StartsAt = !string.IsNullOrEmpty(eventDate) && DateTime.TryParse(eventDate, out var parsedEventDate)
                    ? parsedEventDate
                    : null;

                // Save extended fields
                // feeAmount skrivs INTE langre - fritextavgiften ar ersatt av prisraderna
                // (eventPrices), som ClubEventInput.ApplyRegistrationFields ager. Lagg aldrig tillbaka
                // en avgiftsskrivning har: da finns tva falt som bada pastar vad det kostar.
                e.EquipmentRequired = equipmentRequired ?? "";
                e.TargetAudience = targetAudience ?? "";
                // ⚠️ `lanevapenOffered` MÅSTE skickas med. Utan den skrev den här dialogen false
                // över klubbens val varje gång någon sparade från händelsens egen sida — precis
                // den tysta glidningen ClubEventInput.ApplyRegistrationFields finns för att förhindra.
                var regFieldError = HpskSite.Models.Events.ClubEventInput.ApplyRegistrationFields(
                    e, registrationRequired, maxParticipants, isMandatory, registrationUrl,
                    lanevapenOffered, registrationDeadline, eventPrices, eventAudience, eventSwishNumber);

                if (regFieldError != null)
                {
                    return Ok(new { success = false, message = regFieldError });
                }

                e.EndsAt = !string.IsNullOrEmpty(eventEndDate) && DateTime.TryParse(eventEndDate, out var parsedEndDate)
                    ? parsedEndDate
                    : null;

                e.IsPublished = true;
                var (_, saveError) = await _events.SaveAsync(e);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }
                _rangeCalendar.SetEventRange(eventId, rangeId, rangeSectionId, null);

                return Ok(new { success = true, message = "Event details updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating event details: " + ex.Message });
            }
        }

        /// <summary>
        /// Upload a hero image for an event page. Saves to "Event Images" media folder and links via UDI.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadEventImage(int eventId, IFormFile imageFile)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid event ID" });
                }

                var eventRecord = _events.Get(eventId);
                if (eventRecord == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                if (!await IsEventAdminAsync(eventRecord))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                if (imageFile == null || imageFile.Length == 0)
                {
                    return Ok(new { success = false, message = "No file uploaded" });
                }

                if (imageFile.Length > 5 * 1024 * 1024)
                {
                    return Ok(new { success = false, message = "File too large. Maximum 5 MB allowed." });
                }

                var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                if (!allowedExtensions.Contains(extension))
                {
                    return Ok(new { success = false, message = "Invalid file type. Only JPG, PNG, GIF and WebP allowed." });
                }

                // Find or create "Event Images" folder in Media Library
                var eventImagesFolder = _mediaService.GetRootMedia()
                    .FirstOrDefault(m => m.Name == "Event Images");

                if (eventImagesFolder == null)
                {
                    eventImagesFolder = _mediaService.CreateMedia("Event Images", -1, "Folder");
                    _mediaService.Save(eventImagesFolder);
                }

                string fileName = Path.GetFileName(imageFile.FileName);
                string fileExtension = Path.GetExtension(fileName);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid()}{fileExtension}";

                var mediaItem = _mediaService.CreateMedia(fileName, eventImagesFolder.Id, "Image");

                var mediaFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media", "event-images");
                if (!Directory.Exists(mediaFolderPath))
                {
                    Directory.CreateDirectory(mediaFolderPath);
                }

                var physicalFilePath = Path.Combine(mediaFolderPath, uniqueFileName);
                using (var fileStream = new FileStream(physicalFilePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(fileStream);
                }

                var relativePath = $"/media/event-images/{uniqueFileName}";
                mediaItem.SetValue("umbracoFile", relativePath);
                mediaItem.SetValue("umbracoExtension", fileExtension.TrimStart('.'));
                mediaItem.SetValue("umbracoBytes", imageFile.Length.ToString());

                var mediaSaveResult = _mediaService.Save(mediaItem);
                if (!mediaSaveResult.Success)
                {
                    if (System.IO.File.Exists(physicalFilePath))
                    {
                        System.IO.File.Delete(physicalFilePath);
                    }
                    return Ok(new { success = false, message = "Failed to save image to media library" });
                }

                // Link media to the event via UDI — genom lagringen (fas 2a).
                eventRecord.ImageRef = mediaItem.GetUdi().ToString();
                eventRecord.IsPublished = true;
                var (_, saveError) = await _events.SaveAsync(eventRecord);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                return Ok(new
                {
                    success = true,
                    message = "Event image uploaded successfully",
                    imageUrl = relativePath
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error uploading event image: " + ex.Message });
            }
        }

        /// <summary>
        /// Save content blocks JSON to event page
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEventContentBlocks(int eventId, string contentBlocksJson)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid event ID" });
                }

                var eventRecord = _events.Get(eventId);
                if (eventRecord == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                if (!await IsEventAdminAsync(eventRecord))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                eventRecord.ContentBlocksJson = contentBlocksJson ?? "[]";
                eventRecord.IsPublished = true;
                var (_, saveError) = await _events.SaveAsync(eventRecord);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                return Ok(new { success = true, message = "Content blocks saved successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error saving content blocks: " + ex.Message });
            }
        }

        /// <summary>
        /// Save quick links JSON to event page
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEventQuickLinks(int eventId, string quickLinksJson)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid event ID" });
                }

                var eventRecord = _events.Get(eventId);
                if (eventRecord == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                if (!await IsEventAdminAsync(eventRecord))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                eventRecord.QuickLinksJson = quickLinksJson ?? "[]";
                eventRecord.IsPublished = true;
                var (_, saveError) = await _events.SaveAsync(eventRecord);
                if (saveError != null)
                {
                    return Ok(new { success = false, message = saveError });
                }

                return Ok(new { success = true, message = "Quick links saved successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error saving quick links: " + ex.Message });
            }
        }

        /// <summary>
        /// Upload an image for a content block. Returns URL only (not linked to any Umbraco property).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadEventBlockImage(int eventId, IFormFile imageFile)
        {
            try
            {
                if (eventId <= 0)
                {
                    return Ok(new { success = false, message = "Invalid event ID" });
                }

                var eventRecord = _events.Get(eventId);
                if (eventRecord == null)
                {
                    return Ok(new { success = false, message = "Event not found" });
                }

                if (!await IsEventAdminAsync(eventRecord))
                {
                    return Ok(new { success = false, message = "Access denied - insufficient permissions" });
                }

                if (imageFile == null || imageFile.Length == 0)
                {
                    return Ok(new { success = false, message = "No file uploaded" });
                }

                if (imageFile.Length > 5 * 1024 * 1024)
                {
                    return Ok(new { success = false, message = "File too large. Maximum 5 MB allowed." });
                }

                var extension = Path.GetExtension(imageFile.FileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                if (!allowedExtensions.Contains(extension))
                {
                    return Ok(new { success = false, message = "Invalid file type. Only JPG, PNG, GIF and WebP allowed." });
                }

                // Save to event-images folder (no Umbraco media link needed, just file storage)
                var mediaFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media", "event-images");
                if (!Directory.Exists(mediaFolderPath))
                {
                    Directory.CreateDirectory(mediaFolderPath);
                }

                string fileName = Path.GetFileName(imageFile.FileName);
                string fileExtension = Path.GetExtension(fileName);
                string uniqueFileName = $"block_{Guid.NewGuid()}{fileExtension}";

                var physicalFilePath = Path.Combine(mediaFolderPath, uniqueFileName);
                using (var fileStream = new FileStream(physicalFilePath, FileMode.Create))
                {
                    await imageFile.CopyToAsync(fileStream);
                }

                var relativePath = $"/media/event-images/{uniqueFileName}";

                return Ok(new
                {
                    success = true,
                    message = "Block image uploaded successfully",
                    imageUrl = relativePath
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error uploading block image: " + ex.Message });
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
