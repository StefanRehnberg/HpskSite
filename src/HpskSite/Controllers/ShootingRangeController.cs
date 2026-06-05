using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// API surface for the Shooting Range Database (Skjutbanedatabas), Phase 0.
    /// Members-only throughout. Reads of the directory tier are open to any logged-in member; writes
    /// to a range's private data require stewardship (or site admin). Claiming requires club-admin.
    /// See Documentation/SHOOTING_RANGE_DATABASE.md.
    /// </summary>
    public class ShootingRangeController : SurfaceController
    {
        private readonly ShootingRangeService _rangeService;
        private readonly AdminAuthorizationService _authService;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly IContentService _contentService;
        private readonly RangeDocumentStorage _docStorage;
        private readonly ILogger<ShootingRangeController> _logger;

        public ShootingRangeController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            ShootingRangeService rangeService,
            AdminAuthorizationService authService,
            IMemberService memberService,
            ClubService clubService,
            IContentService contentService,
            RangeDocumentStorage docStorage,
            ILogger<ShootingRangeController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _rangeService = rangeService;
            _authService = authService;
            _memberService = memberService;
            _clubService = clubService;
            _contentService = contentService;
            _docStorage = docStorage;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────────────

        /// <summary>Members-only list of all ranges (summary), flagging which the caller can manage.</summary>
        [HttpGet]
        public async Task<IActionResult> GetRanges()
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                var stewarded = await _rangeService.GetStewardedRangeIdsAsync(memberId.Value);
                // Club admins (incl. regional admins, via GetManagedClubIds) and site admins may
                // create ranges and edit unclaimed ones — gates the "Ny skjutbana" / placement buttons.
                bool canCreate = isSiteAdmin || (await _authService.GetManagedClubIds()).Count > 0;
                var ranges = await _rangeService.ListAsync();

                var items = ranges.Select(r =>
                {
                    bool canManage = isSiteAdmin || stewarded.Contains(r.Id);
                    // Restricted ranges hide coords from non-managers (military/discreet).
                    bool showCoords = r.LocationSensitivity != RangeConstants.SensRestricted || canManage;
                    return new
                    {
                        id = r.Id,
                        name = r.Name,
                        city = r.City,
                        municipality = r.Municipality,
                        county = r.County,
                        status = r.Status,
                        source = r.Source,
                        huvudmanName = HuvudmanDisplay(r),
                        latitude = showCoords ? r.Latitude : null,
                        longitude = showCoords ? r.Longitude : null,
                        hasCoords = showCoords && r.Latitude.HasValue && r.Longitude.HasValue,
                        canManage
                    };
                }).ToList();

                return Json(new { success = true, isSiteAdmin, canCreate, ranges = items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing shooting ranges");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Full detail for one range: core, sections, linked clubs (+ their allocations), stewards.</summary>
        [HttpGet]
        public async Task<IActionResult> GetRange(int id)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var range = await _rangeService.GetByIdAsync(id);
                if (range == null) return Json(new { success = false, message = "Skjutbanan hittades inte." });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                bool canManage = isSiteAdmin || await _rangeService.CanManageRangeAsync(id, memberId);
                // An unclaimed range can have its name + data edited by any club/regional admin even
                // before it's claimed (claiming assigns a steward; editing basic data doesn't need one).
                bool isClubOrRegionalAdmin = isSiteAdmin || (await _authService.GetManagedClubIds()).Count > 0;
                bool canEdit = canManage || (range.Status == RangeConstants.StatusUnclaimedSeed && isClubOrRegionalAdmin);

                var sections = await _rangeService.GetSectionsAsync(id);
                var links = await _rangeService.GetLinksAsync(id);
                var allocations = await _rangeService.GetAllocationsForRangeAsync(id);
                var stewards = await _rangeService.GetStewardsAsync(id);

                // Advisory: the permitted shooting window (union across permits), so the UI can flag
                // club allocations that fall outside it. Manager-only (private tier).
                var allowedWindows = new List<object>();
                if (canManage)
                {
                    foreach (var p in await _rangeService.GetPermitsAsync(id))
                        allowedWindows.AddRange(ParseWindows(p.AllowedWindows));
                }

                var clubs = links.Select(l => new
                {
                    linkId = l.Id,
                    clubId = l.ClubId,
                    clubName = _clubService.GetClubNameById(l.ClubId) ?? $"Klubb {l.ClubId}",
                    relationType = l.RelationType,
                    allocations = allocations
                        .Where(a => a.ClubRangeLinkId == l.Id)
                        .Select(a => new
                        {
                            id = a.Id,
                            rangeSectionId = a.RangeSectionId,
                            dayOfWeek = a.DayOfWeek,
                            startTime = a.StartTime.ToString(@"hh\:mm"),
                            endTime = a.EndTime.ToString(@"hh\:mm"),
                            note = a.Note
                        }).ToList()
                }).ToList();

                bool sensitiveHidden = range.LocationSensitivity == RangeConstants.SensRestricted && !canManage;

                return Json(new
                {
                    success = true,
                    canManage,
                    canEdit,
                    canDelete = isSiteAdmin, // only site admins may delete a range
                    range = new
                    {
                        id = range.Id,
                        name = range.Name,
                        latitude = sensitiveHidden ? null : range.Latitude,
                        longitude = sensitiveHidden ? null : range.Longitude,
                        locationSensitivity = range.LocationSensitivity,
                        address = range.Address,
                        postcode = range.Postcode,
                        city = range.City,
                        municipality = range.Municipality,
                        county = range.County,
                        huvudmanType = range.HuvudmanType,
                        huvudmanClubId = range.HuvudmanClubId,
                        huvudmanName = range.HuvudmanName,
                        skjutbanechefName = range.SkjutbanechefName,
                        skjutbanechefContact = range.SkjutbanechefContact,
                        description = range.Description,
                        status = range.Status,
                        source = range.Source,
                        osmRef = range.OsmRef
                    },
                    sections = sections.Select(s => new
                    {
                        id = s.Id,
                        label = s.Label,
                        banaType = s.BanaType,
                        distanceMeters = s.DistanceMeters,
                        directionDegrees = s.DirectionDegrees,
                        firingPoints = s.FiringPoints,
                        kulfangSpec = s.KulfangSpec,
                        allowedWeaponsCalibers = s.AllowedWeaponsCalibers,
                        notes = s.Notes
                    }).ToList(),
                    clubs,
                    stewards = stewards.Select(s => new
                    {
                        memberId = s.MemberId,
                        memberName = ResolveMemberName(s.MemberId)
                    }).ToList(),
                    allowedWindows
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading shooting range {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Coordinates for a set of range ids (comma-separated) — for the /competitions map
        /// view. Members-only; Restricted ranges' coords hidden from non-managers. Batched (one query).</summary>
        [HttpGet]
        public async Task<IActionResult> GetRangeLocations(string? ids)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var idList = (ids ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0)
                    .Where(n => n > 0).Distinct().ToList();
                if (idList.Count == 0) return Json(new { success = true, ranges = new List<object>() });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                var stewarded = await _rangeService.GetStewardedRangeIdsAsync(memberId.Value);
                var ranges = await _rangeService.GetByIdsAsync(idList);

                var items = ranges
                    .Where(r => r.Latitude.HasValue && r.Longitude.HasValue)
                    .Where(r => r.LocationSensitivity != RangeConstants.SensRestricted || isSiteAdmin || stewarded.Contains(r.Id))
                    .Select(r => new { id = r.Id, name = r.Name, latitude = r.Latitude, longitude = r.Longitude })
                    .ToList();
                return Json(new { success = true, ranges = items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading range locations");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Clubs the current user may administer — for the claim / link-club pickers.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyAdminClubs()
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var clubIds = await _authService.GetManagedClubIds();
                var clubs = clubIds
                    .Select(cid => new { clubId = cid, clubName = _clubService.GetClubNameById(cid) ?? $"Klubb {cid}" })
                    .OrderBy(c => c.clubName)
                    .ToList();
                return Json(new { success = true, clubs });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing managed clubs");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Member search (any logged-in user) for the steward picker. Up to 20 matches.</summary>
        [HttpGet]
        public async Task<IActionResult> SearchMembers(string query)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                    return Json(new { success = true, members = new List<object>() });

                var all = _memberService.GetAll(0, int.MaxValue, out _);
                var members = all
                    .Where(m => m.IsApproved
                        && ((m.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                            || (m.Email ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Take(20)
                    .Select(m => new { memberId = m.Id, memberName = ResolveMemberName(m.Id) })
                    .ToList();
                return Json(new { success = true, members });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching members");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Range create / update / delete ──────────────────────────────────

        /// <summary>Create (id == 0) or update a range. Create: any club admin or site admin (and the
        /// creator becomes a steward). Update: steward or site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveRange([FromBody] SaveRangeRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Json(new { success = false, message = "Namn krävs." });

                if (req.Id > 0)
                {
                    var range = await _rangeService.GetByIdAsync(req.Id);
                    if (range == null) return Json(new { success = false, message = "Skjutbanan hittades inte." });
                    // Stewards + site admins may always edit. Unclaimed ranges may additionally be edited
                    // by any club/regional admin so name + data can be filled in before the range is claimed.
                    bool isUnclaimed = range.Status == RangeConstants.StatusUnclaimedSeed;
                    bool isClubOrRegionalAdmin = (await _authService.GetManagedClubIds()).Count > 0;
                    bool canEdit = isSiteAdmin
                        || await _rangeService.IsStewardAsync(req.Id, memberId.Value)
                        || (isUnclaimed && isClubOrRegionalAdmin);
                    if (!canEdit)
                        return Json(new { success = false, message = "Endast förvaltare eller administratör kan ändra skjutbanan." });

                    ApplyFields(range, req);
                    await _rangeService.UpdateAsync(range);
                    return Json(new { success = true, id = range.Id });
                }
                else
                {
                    // Create — must be a club admin somewhere (or site admin). The creator becomes a steward.
                    var managed = await _authService.GetManagedClubIds();
                    if (!isSiteAdmin && managed.Count == 0)
                        return Json(new { success = false, message = "Endast klubb- eller webbadministratörer kan skapa skjutbanor." });

                    var range = new ShootingRange
                    {
                        Status = RangeConstants.StatusActive,
                        Source = RangeConstants.SourceManual,
                        CreatedByMemberId = memberId
                    };
                    ApplyFields(range, req);
                    var newId = await _rangeService.CreateAsync(range);
                    await _rangeService.AddStewardAsync(newId, memberId.Value, memberId);
                    return Json(new { success = true, id = newId });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving shooting range");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRange(int id)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.IsSiteAdminAsync())
                    return Json(new { success = false, message = "Endast administratör kan ta bort skjutbanan." });

                await _rangeService.DeleteAsync(id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting shooting range {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Claim an unclaimed range for a club the caller administers.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClaimRange([FromBody] ClaimRangeRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                if (!isSiteAdmin && !await _authService.IsClubAdminForClub(req.ClubId))
                    return Json(new { success = false, message = "Du är inte administratör för den valda klubben." });

                await _rangeService.ClaimAsync(req.RangeId, req.ClubId, memberId.Value);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error claiming shooting range");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Sections ──────────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSection([FromBody] SaveSectionRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });
                if (string.IsNullOrWhiteSpace(req.Label))
                    return Json(new { success = false, message = "Etikett krävs." });

                if (req.Id > 0)
                {
                    var sections = await _rangeService.GetSectionsAsync(req.RangeId);
                    var section = sections.FirstOrDefault(s => s.Id == req.Id);
                    if (section == null) return Json(new { success = false, message = "Sektionen hittades inte." });
                    ApplySectionFields(section, req);
                    await _rangeService.UpdateSectionAsync(section);
                    return Json(new { success = true, id = section.Id });
                }
                else
                {
                    var section = new RangeSection { RangeId = req.RangeId };
                    ApplySectionFields(section, req);
                    var newId = await _rangeService.AddSectionAsync(section);
                    return Json(new { success = true, id = newId });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving range section");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSection([FromBody] IdRangeRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                await _rangeService.DeleteSectionAsync(req.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting range section");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Club links ──────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddClubLink([FromBody] AddClubLinkRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                var relation = string.IsNullOrWhiteSpace(req.RelationType) ? RangeConstants.RelationUser : req.RelationType!;
                var linkId = await _rangeService.AddLinkAsync(req.RangeId, req.ClubId, relation, memberId);
                return Json(new { success = true, linkId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding club link");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveClubLink([FromBody] LinkIdRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var link = await _rangeService.GetLinkAsync(req.LinkId);
                if (link == null) return Json(new { success = false, message = "Kopplingen hittades inte." });
                if (!await _rangeService.CanManageRangeAsync(link.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                await _rangeService.RemoveLinkAsync(req.LinkId);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing club link");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Allocations ───────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddAllocation([FromBody] AddAllocationRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var link = await _rangeService.GetLinkAsync(req.ClubRangeLinkId);
                if (link == null) return Json(new { success = false, message = "Klubbkopplingen hittades inte." });
                if (!await _rangeService.CanManageRangeAsync(link.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                if (req.DayOfWeek < 1 || req.DayOfWeek > 7)
                    return Json(new { success = false, message = "Ogiltig veckodag." });
                if (!TimeSpan.TryParse(req.StartTime, out var start) || !TimeSpan.TryParse(req.EndTime, out var end))
                    return Json(new { success = false, message = "Ogiltig tid (använd HH:mm)." });
                if (end <= start)
                    return Json(new { success = false, message = "Sluttiden måste vara efter starttiden." });

                // A section-scoped slot must reference a section that belongs to this link's range.
                if (req.RangeSectionId.HasValue)
                {
                    var sections = await _rangeService.GetSectionsAsync(link.RangeId);
                    if (sections.All(s => s.Id != req.RangeSectionId.Value))
                        return Json(new { success = false, message = "Vald bana/vall tillhör inte skjutbanan." });
                }

                var id = await _rangeService.AddAllocationAsync(new ClubRangeAllocation
                {
                    ClubRangeLinkId = req.ClubRangeLinkId,
                    RangeSectionId = req.RangeSectionId,
                    DayOfWeek = (byte)req.DayOfWeek,
                    StartTime = start,
                    EndTime = end,
                    Note = req.Note,
                    CreatedByMemberId = memberId
                });
                return Json(new { success = true, id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding allocation");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteAllocation([FromBody] IdOnlyRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var rangeId = await _rangeService.GetRangeIdForAllocationAsync(req.Id);
                if (rangeId == null) return Json(new { success = false, message = "Tiden hittades inte." });
                if (!await _rangeService.CanManageRangeAsync(rangeId.Value, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                await _rangeService.DeleteAllocationAsync(req.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting allocation");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Stewards ──────────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddSteward([FromBody] StewardRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                await _rangeService.AddStewardAsync(req.RangeId, req.MemberId, memberId);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding steward");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveSteward([FromBody] StewardRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                // Don't allow removing the last steward (would orphan the range from non-admins).
                if (await _rangeService.CountStewardsAsync(req.RangeId) <= 1)
                    return Json(new { success = false, message = "Kan inte ta bort den sista förvaltaren. Lägg till en annan först." });

                await _rangeService.RemoveStewardAsync(req.RangeId, req.MemberId);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing steward");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── OSM seed import (site admin) ───────────────────────────────────────

        /// <summary>Imports an overpass-turbo GeoJSON FeatureCollection as UnclaimedSeed ranges. Site
        /// admin only. Accepts a multipart file upload (field "file") or a raw JSON body field "geoJson".</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportOsm([FromForm] IFormFile? file, [FromForm] string? geoJson)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.IsSiteAdminAsync())
                    return Json(new { success = false, message = "Endast webbadministratörer kan importera OSM-data." });

                string? json = geoJson;
                if (file != null && file.Length > 0)
                {
                    using var reader = new StreamReader(file.OpenReadStream());
                    json = await reader.ReadToEndAsync();
                }
                if (string.IsNullOrWhiteSpace(json))
                    return Json(new { success = false, message = "Ingen GeoJSON mottagen." });

                var (imported, skipped) = await _rangeService.ImportOsmAsync(json, memberId);
                return Json(new { success = true, imported, skipped });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing OSM ranges");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Competition ↔ range linking (Phase 1) ─────────────────────────────

        /// <summary>Typeahead search for the competition range-picker (any logged-in member). Prefers
        /// claimed/active ranges. Up to 20 matches.</summary>
        [HttpGet]
        public async Task<IActionResult> GetRangePicker(string? query)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var ranges = await _rangeService.ListAsync();
                var q = (query ?? "").Trim();
                if (q.Length >= 2)
                    ranges = ranges.Where(r =>
                        (r.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (r.City ?? "").Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (r.Municipality ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

                var items = ranges
                    .OrderByDescending(r => r.Status == RangeConstants.StatusActive)
                    .ThenBy(r => r.Name)
                    .Take(20)
                    .Select(r => new
                    {
                        id = r.Id,
                        name = r.Name,
                        city = r.City,
                        municipality = r.Municipality,
                        status = r.Status
                    }).ToList();
                return Json(new { success = true, ranges = items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in range picker search");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>The range linked to a competition (for the admin card + the public "var det hålls"
        /// block). Members-only. Returns hasRange=false when none is set.</summary>
        [HttpGet]
        public async Task<IActionResult> GetRangeForCompetition(int competitionId)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var comp = _contentService.GetById(competitionId);
                if (comp == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

                int rangeId = comp.HasProperty("rangeId") ? comp.GetValue<int>("rangeId") : 0;
                if (rangeId <= 0) return Json(new { success = true, hasRange = false });

                var range = await _rangeService.GetByIdAsync(rangeId);
                if (range == null) return Json(new { success = true, hasRange = false });

                bool canManage = await _rangeService.CanManageRangeAsync(rangeId, memberId);
                bool showCoords = range.LocationSensitivity != RangeConstants.SensRestricted || canManage;

                return Json(new
                {
                    success = true,
                    hasRange = true,
                    range = new
                    {
                        id = range.Id,
                        name = range.Name,
                        address = range.Address,
                        postcode = range.Postcode,
                        city = range.City,
                        municipality = range.Municipality,
                        latitude = showCoords ? range.Latitude : null,
                        longitude = showCoords ? range.Longitude : null
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading competition range {CompetitionId}", competitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Set (rangeId &gt; 0) or clear (rangeId == 0) a competition's linked range. Auth:
        /// site admin / competition manager / club admin for the hosting club.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetCompetitionRange([FromBody] SetCompetitionRangeRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var comp = _contentService.GetById(req.CompetitionId);
                if (comp == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                bool isCompManager = await _authService.IsCompetitionManager(req.CompetitionId);
                bool isClubAdmin = false;
                int clubId = comp.HasProperty("clubId") ? comp.GetValue<int>("clubId") : 0;
                if (clubId > 0) isClubAdmin = await _authService.IsClubAdminForClub(clubId);
                if (!isSiteAdmin && !isCompManager && !isClubAdmin)
                    return Json(new { success = false, message = "Behörighet saknas." });

                if (!comp.HasProperty("rangeId"))
                    return Json(new { success = false, message = "Egenskapen 'rangeId' saknas på tävlingsdoctypen. Lägg till den i backoffice." });

                comp.SetValue("rangeId", req.RangeId > 0 ? req.RangeId : 0);
                _contentService.Save(comp);
                if (comp.Published) _contentService.Publish(comp, new[] { "*" }, -1);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting competition range");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>A club's linked ranges + that club's time slots at each — for the club page
        /// "Våra skjutbanor" section. Members-only.</summary>
        [HttpGet]
        public async Task<IActionResult> GetClubRanges(int clubId)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });

                bool isSiteAdmin = await _rangeService.IsSiteAdminAsync();
                var stewarded = await _rangeService.GetStewardedRangeIdsAsync(memberId.Value);
                var ranges = await _rangeService.GetRangesForClubAsync(clubId);

                // Batch: one query for the club's links + one for all their allocations (avoids an
                // allocation query per range).
                var links = await _rangeService.GetClubLinksAsync(clubId);
                var linkIdByRange = links.GroupBy(l => l.RangeId).ToDictionary(g => g.Key, g => g.First().Id);
                var allocsByLink = (await _rangeService.GetAllocationsByLinkIdsAsync(links.Select(l => l.Id)))
                    .GroupBy(a => a.ClubRangeLinkId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var items = ranges.Select(r =>
                {
                    bool canManage = isSiteAdmin || stewarded.Contains(r.Id);
                    bool showCoords = r.LocationSensitivity != RangeConstants.SensRestricted || canManage;
                    var allocs = linkIdByRange.TryGetValue(r.Id, out var lid) && allocsByLink.TryGetValue(lid, out var la)
                        ? la : new List<ClubRangeAllocation>();
                    return new
                    {
                        id = r.Id,
                        name = r.Name,
                        city = r.City,
                        municipality = r.Municipality,
                        latitude = showCoords ? r.Latitude : null,
                        longitude = showCoords ? r.Longitude : null,
                        allocations = allocs.Select(a => new
                        {
                            dayOfWeek = a.DayOfWeek,
                            startTime = a.StartTime.ToString(@"hh\:mm"),
                            endTime = a.EndTime.ToString(@"hh\:mm"),
                            note = a.Note
                        }).ToList()
                    };
                }).ToList();
                return Json(new { success = true, ranges = items });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading club ranges for {ClubId}", clubId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Compliance dossier: permits + documents (Phase 2) ─────────────────

        /// <summary>Permits + documents for a range. Private tier — steward/site-admin only.</summary>
        [HttpGet]
        public async Task<IActionResult> GetDossier(int rangeId)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(rangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                var permits = await _rangeService.GetPermitsAsync(rangeId);
                var docs = await _rangeService.GetDocumentsAsync(rangeId);
                return Json(new
                {
                    success = true,
                    permits = permits.Select(p => new
                    {
                        id = p.Id,
                        permitType = p.PermitType,
                        issuingAuthority = p.IssuingAuthority,
                        referenceNumber = p.ReferenceNumber,
                        issuedDate = p.IssuedDate?.ToString("yyyy-MM-dd"),
                        expiryDate = p.ExpiryDate?.ToString("yyyy-MM-dd"),
                        maxShotsPerYear = p.MaxShotsPerYear,
                        allowedWindows = ParseWindows(p.AllowedWindows).ToList(),
                        conditions = p.Conditions,
                        status = p.Status
                    }).ToList(),
                    documents = docs.Select(d => new
                    {
                        id = d.Id,
                        docType = d.DocType,
                        title = d.Title,
                        issuedDate = d.IssuedDate?.ToString("yyyy-MM-dd"),
                        validUntil = d.ValidUntil?.ToString("yyyy-MM-dd"),
                        uploadedAt = d.UploadedAt.ToString("yyyy-MM-dd")
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading dossier for range {RangeId}", rangeId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePermit([FromBody] SavePermitRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                string? windowsJson = (req.AllowedWindows != null && req.AllowedWindows.Count > 0)
                    ? System.Text.Json.JsonSerializer.Serialize(req.AllowedWindows)
                    : null;

                if (req.Id > 0)
                {
                    var p = await _rangeService.GetPermitAsync(req.Id);
                    if (p == null || p.RangeId != req.RangeId) return Json(new { success = false, message = "Tillståndet hittades inte." });
                    ApplyPermit(p, req, windowsJson);
                    await _rangeService.UpdatePermitAsync(p);
                    return Json(new { success = true, id = p.Id });
                }
                else
                {
                    var p = new RangePermit { RangeId = req.RangeId };
                    ApplyPermit(p, req, windowsJson);
                    var id = await _rangeService.AddPermitAsync(p);
                    return Json(new { success = true, id });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving permit");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeletePermit([FromBody] IdOnlyRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var p = await _rangeService.GetPermitAsync(req.Id);
                if (p == null) return Json(new { success = false, message = "Tillståndet hittades inte." });
                if (!await _rangeService.CanManageRangeAsync(p.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });
                await _rangeService.DeletePermitAsync(req.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting permit");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadDocument([FromForm] IFormFile? file, [FromForm] int rangeId,
            [FromForm] string? docType, [FromForm] string? title, [FromForm] string? issuedDate, [FromForm] string? validUntil)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(rangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });
                if (file == null || file.Length == 0) return Json(new { success = false, message = "Ingen fil mottagen." });

                var (ok, err) = _docStorage.Validate(file.FileName, file.Length);
                if (!ok) return Json(new { success = false, message = err });

                string stored;
                using (var s = file.OpenReadStream()) stored = await _docStorage.SaveAsync(s, file.FileName);

                var docId = await _rangeService.AddDocumentAsync(new RangeDocument
                {
                    RangeId = rangeId,
                    DocType = string.IsNullOrWhiteSpace(docType) ? RangeConstants.DocOther : docType!,
                    Title = string.IsNullOrWhiteSpace(title) ? file.FileName : title!.Trim(),
                    FileRef = stored,
                    IssuedDate = ParseDate(issuedDate),
                    ValidUntil = ParseDate(validUntil),
                    UploadedByMemberId = memberId
                });
                return Json(new { success = true, id = docId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading range document");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Streams a stored document inline. Steward/site-admin only.</summary>
        [HttpGet]
        public async Task<IActionResult> GetDocument(int id)
        {
            var memberId = await _rangeService.GetCurrentMemberIdAsync();
            if (memberId == null) return Forbid();
            var doc = await _rangeService.GetDocumentAsync(id);
            if (doc == null) return NotFound();
            if (!await _rangeService.CanManageRangeAsync(doc.RangeId, memberId)) return Forbid();
            var path = _docStorage.GetFilePath(doc.FileRef);
            if (path == null) return NotFound();
            var bytes = await System.IO.File.ReadAllBytesAsync(path);
            return File(bytes, RangeDocumentStorage.ContentTypeFor(doc.FileRef));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteDocument([FromBody] IdOnlyRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var doc = await _rangeService.GetDocumentAsync(req.Id);
                if (doc == null) return Json(new { success = false, message = "Dokumentet hittades inte." });
                if (!await _rangeService.CanManageRangeAsync(doc.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });
                _docStorage.Delete(doc.FileRef);
                await _rangeService.DeleteDocumentAsync(req.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting range document");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Activity report + manual entry (Phase 3) — manager-only ───────────

        [HttpGet]
        public async Task<IActionResult> GetActivityReport(int rangeId, int? year)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(rangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                int yr = year ?? DateTime.Now.Year;
                var years = await _rangeService.GetActivityYearsAsync(rangeId);
                if (!years.Contains(DateTime.Now.Year)) years.Insert(0, DateTime.Now.Year);
                if (!years.Contains(yr)) years.Add(yr);

                var sessions = await _rangeService.GetSessionsForYearAsync(rangeId, yr);
                int total = sessions.Sum(s => s.ShotCount);
                var bySource = sessions
                    .GroupBy(s => s.ShotCountSource)
                    .ToDictionary(g => g.Key, g => g.Sum(s => s.ShotCount));
                int days = sessions.Select(s => s.Date.Date).Distinct().Count();
                var byHour = new int[24];
                foreach (var s in sessions.Where(s => s.StartTime.HasValue))
                    byHour[s.StartTime!.Value.Hours] += s.ShotCount;

                int? cap = await _rangeService.GetMaxShotsPerYearAsync(rangeId);

                return Json(new
                {
                    success = true,
                    year = yr,
                    years = years.OrderByDescending(y => y).ToList(),
                    cap,
                    total,
                    bySource,
                    days,
                    byHour,
                    sessions = sessions.Select(s => new
                    {
                        id = s.Id,
                        date = s.Date.ToString("yyyy-MM-dd"),
                        startTime = s.StartTime?.ToString(@"hh\:mm"),
                        endTime = s.EndTime?.ToString(@"hh\:mm"),
                        shotCount = s.ShotCount,
                        shooterCount = s.ShooterCount,
                        source = s.ShotCountSource,
                        note = s.Note
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building activity report for range {RangeId}", rangeId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddManualActivity([FromBody] AddActivityRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                if (!await _rangeService.CanManageRangeAsync(req.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });

                var date = ParseDate(req.Date);
                if (date == null) return Json(new { success = false, message = "Ogiltigt datum." });
                if (req.ShotCount < 0) return Json(new { success = false, message = "Antal skott kan inte vara negativt." });

                var id = await _rangeService.AddSessionAsync(new RangeActivitySession
                {
                    RangeId = req.RangeId,
                    Date = date.Value,
                    StartTime = ParseTime(req.StartTime),
                    EndTime = ParseTime(req.EndTime),
                    ShotCount = req.ShotCount,
                    ShooterCount = req.ShooterCount <= 0 ? 1 : req.ShooterCount,
                    ShotCountSource = RangeConstants.ShotSourceManual,
                    ClubId = req.ClubId,
                    Note = Trim(req.Note),
                    EnteredByMemberId = memberId
                });
                return Json(new { success = true, id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding manual activity");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteActivity([FromBody] IdOnlyRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var s = await _rangeService.GetSessionAsync(req.Id);
                if (s == null) return Json(new { success = false, message = "Posten hittades inte." });
                if (!await _rangeService.CanManageRangeAsync(s.RangeId, memberId))
                    return Json(new { success = false, message = "Behörighet saknas." });
                await _rangeService.DeleteSessionAsync(req.Id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting activity session");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>CSV of a year's activity (semicolon + UTF-8 BOM for Swedish Excel). Manager-only.</summary>
        [HttpGet]
        public async Task<IActionResult> ExportActivity(int rangeId, int year)
        {
            var memberId = await _rangeService.GetCurrentMemberIdAsync();
            if (memberId == null) return Forbid();
            if (!await _rangeService.CanManageRangeAsync(rangeId, memberId)) return Forbid();

            var sessions = await _rangeService.GetSessionsForYearAsync(rangeId, year);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Datum;Starttid;Sluttid;Antal skott;Antal skyttar;Källa;Notering");
            foreach (var s in sessions.OrderBy(s => s.Date))
            {
                string note = (s.Note ?? "").Replace(";", ",").Replace("\r", " ").Replace("\n", " ");
                sb.AppendLine($"{s.Date:yyyy-MM-dd};{s.StartTime?.ToString(@"hh\:mm")};{s.EndTime?.ToString(@"hh\:mm")};{s.ShotCount};{s.ShooterCount};{s.ShotCountSource};{note}");
            }
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"skjutbana-aktivitet-{rangeId}-{year}.csv");
        }

        // ── QR check-in / check-out (Phase 3) — any logged-in member ───────────

        [HttpGet]
        public async Task<IActionResult> CheckInStatus(int rangeId)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, needLogin = true, message = "Inloggning krävs." });
                var range = await _rangeService.GetByIdAsync(rangeId);
                if (range == null) return Json(new { success = false, message = "Skjutbanan hittades inte." });
                var open = await _rangeService.GetOpenSessionAsync(rangeId, memberId.Value);
                return Json(new
                {
                    success = true,
                    rangeName = range.Name,
                    checkedIn = open != null,
                    sessionId = open?.Id,
                    since = open?.StartTime?.ToString(@"hh\:mm")
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in check-in status");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckIn([FromBody] IdOnlyRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var range = await _rangeService.GetByIdAsync(req.Id);
                if (range == null) return Json(new { success = false, message = "Skjutbanan hittades inte." });

                var open = await _rangeService.GetOpenSessionAsync(req.Id, memberId.Value);
                if (open != null) return Json(new { success = true, sessionId = open.Id, already = true });

                var now = DateTime.Now;
                var id = await _rangeService.AddSessionAsync(new RangeActivitySession
                {
                    RangeId = req.Id,
                    MemberId = memberId,
                    ClubId = ResolvePrimaryClubId(memberId.Value),
                    Date = now.Date,
                    StartTime = new TimeSpan(now.Hour, now.Minute, 0),
                    ShotCountSource = RangeConstants.ShotSourceQr,
                    ShooterCount = 1,
                    EnteredByMemberId = memberId
                });
                return Json(new { success = true, sessionId = id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking in");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckOut([FromBody] CheckOutRequest req)
        {
            try
            {
                var memberId = await _rangeService.GetCurrentMemberIdAsync();
                if (memberId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var s = await _rangeService.GetSessionAsync(req.SessionId);
                if (s == null) return Json(new { success = false, message = "Passet hittades inte." });
                if (s.MemberId != memberId) return Json(new { success = false, message = "Det här passet tillhör någon annan." });

                var now = DateTime.Now;
                s.EndTime = new TimeSpan(now.Hour, now.Minute, 0);
                s.ShotCount = req.ShotCount < 0 ? 0 : req.ShotCount;
                await _rangeService.UpdateSessionAsync(s);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking out");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static readonly System.Text.Json.JsonSerializerOptions _jsonCI = new() { PropertyNameCaseInsensitive = true };

        private static TimeSpan? ParseTime(string? s) =>
            TimeSpan.TryParse(s, out var t) ? t : (TimeSpan?)null;

        private int? ResolvePrimaryClubId(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m == null || !m.HasProperty("primaryClubId")) return null;
            var v = m.GetValue<string>("primaryClubId");
            return int.TryParse(v, out var cid) && cid > 0 ? cid : (int?)null;
        }

        private static IEnumerable<object> ParseWindows(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) yield break;
            List<AllowedWindowDto>? list = null;
            try { list = System.Text.Json.JsonSerializer.Deserialize<List<AllowedWindowDto>>(json, _jsonCI); } catch { }
            if (list == null) yield break;
            foreach (var w in list)
                yield return new { day = w.Day, start = w.Start, end = w.End };
        }

        private static DateTime? ParseDate(string? s) =>
            DateTime.TryParse(s, out var d) ? d.Date : (DateTime?)null;

        private static void ApplyPermit(RangePermit p, SavePermitRequest req, string? windowsJson)
        {
            p.PermitType = string.IsNullOrWhiteSpace(req.PermitType) ? RangeConstants.PermitPolice : req.PermitType!;
            p.IssuingAuthority = Trim(req.IssuingAuthority);
            p.ReferenceNumber = Trim(req.ReferenceNumber);
            p.IssuedDate = ParseDate(req.IssuedDate);
            p.ExpiryDate = ParseDate(req.ExpiryDate);
            p.MaxShotsPerYear = req.MaxShotsPerYear;
            p.AllowedWindows = windowsJson;
            p.Conditions = Trim(req.Conditions);
            if (!string.IsNullOrWhiteSpace(req.Status)) p.Status = req.Status!;
        }

        private static void ApplyFields(ShootingRange range, SaveRangeRequest req)
        {
            range.Name = req.Name!.Trim();
            range.Latitude = req.Latitude;
            range.Longitude = req.Longitude;
            range.Address = Trim(req.Address);
            range.Postcode = Trim(req.Postcode);
            range.City = Trim(req.City);
            range.Municipality = Trim(req.Municipality);
            range.County = Trim(req.County);
            range.HuvudmanType = Trim(req.HuvudmanType);
            range.HuvudmanClubId = req.HuvudmanClubId;
            range.HuvudmanName = Trim(req.HuvudmanName);
            range.SkjutbanechefName = Trim(req.SkjutbanechefName);
            range.SkjutbanechefContact = Trim(req.SkjutbanechefContact);
            range.Description = Trim(req.Description);
            if (!string.IsNullOrWhiteSpace(req.LocationSensitivity))
                range.LocationSensitivity = req.LocationSensitivity!;
            if (!string.IsNullOrWhiteSpace(req.Status))
                range.Status = req.Status!;
        }

        private static void ApplySectionFields(RangeSection s, SaveSectionRequest req)
        {
            s.Label = req.Label!.Trim();
            s.BanaType = Trim(req.BanaType);
            s.DistanceMeters = req.DistanceMeters;
            s.DirectionDegrees = req.DirectionDegrees;
            s.FiringPoints = req.FiringPoints;
            s.KulfangSpec = Trim(req.KulfangSpec);
            s.AllowedWeaponsCalibers = Trim(req.AllowedWeaponsCalibers);
            s.Notes = Trim(req.Notes);
        }

        private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string HuvudmanDisplay(ShootingRange r) =>
            !string.IsNullOrWhiteSpace(r.HuvudmanName) ? r.HuvudmanName! : "";

        private string ResolveMemberName(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m == null) return $"Medlem {memberId}";
            var first = m.GetValue<string>("firstName");
            var last = m.GetValue<string>("lastName");
            var full = $"{first} {last}".Trim();
            return string.IsNullOrWhiteSpace(full) ? (m.Name ?? $"Medlem {memberId}") : full;
        }

        // ── Request DTOs ──────────────────────────────────────────────────────

        public class SaveRangeRequest
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? Address { get; set; }
            public string? Postcode { get; set; }
            public string? City { get; set; }
            public string? Municipality { get; set; }
            public string? County { get; set; }
            public string? LocationSensitivity { get; set; }
            public string? HuvudmanType { get; set; }
            public int? HuvudmanClubId { get; set; }
            public string? HuvudmanName { get; set; }
            public string? SkjutbanechefName { get; set; }
            public string? SkjutbanechefContact { get; set; }
            public string? Description { get; set; }
            public string? Status { get; set; }
        }

        public class SaveSectionRequest
        {
            public int Id { get; set; }
            public int RangeId { get; set; }
            public string? Label { get; set; }
            public string? BanaType { get; set; }
            public int? DistanceMeters { get; set; }
            public int? DirectionDegrees { get; set; }
            public int? FiringPoints { get; set; }
            public string? KulfangSpec { get; set; }
            public string? AllowedWeaponsCalibers { get; set; }
            public string? Notes { get; set; }
        }

        public class ClaimRangeRequest { public int RangeId { get; set; } public int ClubId { get; set; } }
        public class AddClubLinkRequest { public int RangeId { get; set; } public int ClubId { get; set; } public string? RelationType { get; set; } }
        public class LinkIdRequest { public int LinkId { get; set; } }
        public class IdRangeRequest { public int Id { get; set; } public int RangeId { get; set; } }
        public class IdOnlyRequest { public int Id { get; set; } }
        public class StewardRequest { public int RangeId { get; set; } public int MemberId { get; set; } }
        public class SetCompetitionRangeRequest { public int CompetitionId { get; set; } public int RangeId { get; set; } }

        public class AllowedWindowDto
        {
            public byte Day { get; set; }       // 1=Mon … 7=Sun
            public string Start { get; set; } = "";
            public string End { get; set; } = "";
        }

        public class SavePermitRequest
        {
            public int Id { get; set; }
            public int RangeId { get; set; }
            public string? PermitType { get; set; }
            public string? IssuingAuthority { get; set; }
            public string? ReferenceNumber { get; set; }
            public string? IssuedDate { get; set; }
            public string? ExpiryDate { get; set; }
            public int? MaxShotsPerYear { get; set; }
            public List<AllowedWindowDto>? AllowedWindows { get; set; }
            public string? Conditions { get; set; }
            public string? Status { get; set; }
        }

        public class AddActivityRequest
        {
            public int RangeId { get; set; }
            public string? Date { get; set; }
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            public int ShotCount { get; set; }
            public int ShooterCount { get; set; }
            public int? ClubId { get; set; }
            public string? Note { get; set; }
        }

        public class CheckOutRequest { public int SessionId { get; set; } public int ShotCount { get; set; } }

        public class AddAllocationRequest
        {
            public int ClubRangeLinkId { get; set; }
            public int? RangeSectionId { get; set; }
            public int DayOfWeek { get; set; }
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            public string? Note { get; set; }
        }
    }
}
