using System.Text.Json;
using HpskSite.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Data + authorization layer for the Shooting Range Database (Skjutbanedatabas), Phase 0.
    /// See Documentation/SHOOTING_RANGE_DATABASE.md.
    ///
    /// Access model:
    ///   - Directory tier (name, location, sections, linked clubs): any logged-in member may read.
    ///   - Private tier (edit range core/sections/links/allocations/stewards): site admins + range
    ///     STEWARDS only. Stewardship is decoupled from club-admin (a range may be shared by several
    ///     clubs or owned by an off-platform 3rd party).
    ///   - Claiming an unclaimed range: any club admin → becomes the first steward.
    /// </summary>
    public class ShootingRangeService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<ShootingRangeService> _logger;
        private readonly AdminAuthorizationService _authService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly RangeDocumentStorage _docStorage;

        public ShootingRangeService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<ShootingRangeService> logger,
            AdminAuthorizationService authService,
            IMemberManager memberManager,
            IMemberService memberService,
            RangeDocumentStorage docStorage)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
            _authService = authService;
            _memberManager = memberManager;
            _memberService = memberService;
            _docStorage = docStorage;
        }

        // ── Current-member helper ────────────────────────────────────────────

        /// <summary>Resolves the current member's id, or null when not logged in.</summary>
        public async Task<int?> GetCurrentMemberIdAsync()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return null;
            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            return member?.Id;
        }

        // ── Authorization ────────────────────────────────────────────────────

        public Task<bool> IsSiteAdminAsync() => _authService.IsCurrentUserAdminAsync();

        /// <summary>True if the member is a steward of the range.</summary>
        public async Task<bool> IsStewardAsync(int rangeId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM RangeSteward WHERE RangeId = @0 AND MemberId = @1", rangeId, memberId);
            return count > 0;
        }

        /// <summary>Site admin OR a steward of this range may edit its private data.</summary>
        public async Task<bool> CanManageRangeAsync(int rangeId, int? memberId)
        {
            if (await IsSiteAdminAsync()) return true;
            if (memberId == null) return false;
            return await IsStewardAsync(rangeId, memberId.Value);
        }

        /// <summary>The set of range ids the member stewards — used to flag canManage in list views
        /// without an N+1 query.</summary>
        public async Task<HashSet<int>> GetStewardedRangeIdsAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ids = await db.FetchAsync<int>("SELECT RangeId FROM RangeSteward WHERE MemberId = @0", memberId);
            return ids.ToHashSet();
        }

        // ── ShootingRange CRUD ────────────────────────────────────────────────

        public async Task<ShootingRange?> GetByIdAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<ShootingRange>("WHERE Id = @0", id);
        }

        /// <summary>Batch fetch ranges by id (one query) — for the competitions map.</summary>
        public async Task<List<ShootingRange>> GetByIdsAsync(IEnumerable<int> ids)
        {
            var list = ids.ToList();
            if (list.Count == 0) return new List<ShootingRange>();
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ShootingRange>("WHERE Id IN (@0)", list);
        }

        /// <summary>All ranges, newest activity first. Phase 0 has no public/SEO directory; this is the
        /// members-only management list.</summary>
        public async Task<List<ShootingRange>> ListAsync()
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ShootingRange>("ORDER BY Name ASC");
        }

        public async Task<int> CreateAsync(ShootingRange range)
        {
            using var db = _databaseFactory.CreateDatabase();
            range.CreatedAt = DateTime.Now;
            range.UpdatedAt = DateTime.Now;
            await db.InsertAsync(range);
            return range.Id;
        }

        public async Task UpdateAsync(ShootingRange range)
        {
            using var db = _databaseFactory.CreateDatabase();
            range.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(range);
        }

        public async Task DeleteAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            // Delete stored document files first (App_Data) so they don't leak, then cascade every
            // child row (no FK constraints — keep this list in sync with the schema).
            var docs = await db.FetchAsync<RangeDocument>("WHERE RangeId = @0", id);
            foreach (var d in docs) await _docStorage.DeleteAsync(d.FileRef);

            await db.ExecuteAsync(
                "DELETE FROM ClubRangeAllocation WHERE ClubRangeLinkId IN (SELECT Id FROM ClubRangeLink WHERE RangeId = @0)", id);
            await db.ExecuteAsync("DELETE FROM ClubRangeLink WHERE RangeId = @0", id);
            await db.ExecuteAsync("DELETE FROM RangeSection WHERE RangeId = @0", id);
            await db.ExecuteAsync("DELETE FROM RangeSteward WHERE RangeId = @0", id);
            await db.ExecuteAsync("DELETE FROM RangePermit WHERE RangeId = @0", id);
            await db.ExecuteAsync("DELETE FROM RangeDocument WHERE RangeId = @0", id);
            await db.ExecuteAsync(
                "IF OBJECT_ID('RangeInspection', 'U') IS NOT NULL DELETE FROM RangeInspection WHERE RangeId = @0", id);
            // RangeActivitySession is created by a later migration (create-range-activity-table.sql) —
            // guard the delete so a range can still be removed on databases where it hasn't been run yet.
            await db.ExecuteAsync(
                "IF OBJECT_ID('RangeActivitySession', 'U') IS NOT NULL DELETE FROM RangeActivitySession WHERE RangeId = @0", id);
            // Banans kalender (create-range-calendar-tables.sql): förvaltarens externa bokningar och
            // händelsernas koppling följer med banan. Träningarna RADERAS inte — de är klubbens — men
            // pekar inte längre på en bana som inte finns.
            await db.ExecuteAsync(
                "IF OBJECT_ID('RangeExternalBooking', 'U') IS NOT NULL DELETE FROM RangeExternalBooking WHERE RangeId = @0", id);
            await db.ExecuteAsync(
                "IF OBJECT_ID('ClubEventRange', 'U') IS NOT NULL DELETE FROM ClubEventRange WHERE RangeId = @0", id);
            await db.ExecuteAsync(
                "IF COL_LENGTH('dbo.ClubTraining', 'RangeSectionId') IS NOT NULL UPDATE dbo.ClubTraining SET RangeId = NULL, RangeSectionId = NULL WHERE RangeId = @0", id);
            await db.ExecuteAsync("DELETE FROM ShootingRange WHERE Id = @0", id);
        }

        // ── Sections ──────────────────────────────────────────────────────────

        public async Task<List<RangeSection>> GetSectionsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<RangeSection>("WHERE RangeId = @0 ORDER BY SortOrder, Id", rangeId);
        }

        public async Task<int> AddSectionAsync(RangeSection section)
        {
            using var db = _databaseFactory.CreateDatabase();
            section.CreatedAt = DateTime.Now;
            section.UpdatedAt = DateTime.Now;
            await db.InsertAsync(section);
            return section.Id;
        }

        public async Task UpdateSectionAsync(RangeSection section)
        {
            using var db = _databaseFactory.CreateDatabase();
            section.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(section);
        }

        public async Task DeleteSectionAsync(int sectionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            // Null out any allocations scoped to this section (don't delete the slot, just un-scope it).
            await db.ExecuteAsync("UPDATE ClubRangeAllocation SET RangeSectionId = NULL WHERE RangeSectionId = @0", sectionId);
            // Samma sak för banans kalender: en träning, händelse eller extern bokning på en vall som
            // tas bort ligger kvar på banan, men gäller nu hela banan.
            await db.ExecuteAsync(
                "IF COL_LENGTH('dbo.ClubTraining', 'RangeSectionId') IS NOT NULL UPDATE dbo.ClubTraining SET RangeSectionId = NULL WHERE RangeSectionId = @0", sectionId);
            await db.ExecuteAsync(
                "IF OBJECT_ID('ClubEventRange', 'U') IS NOT NULL UPDATE ClubEventRange SET RangeSectionId = NULL WHERE RangeSectionId = @0", sectionId);
            await db.ExecuteAsync(
                "IF OBJECT_ID('RangeExternalBooking', 'U') IS NOT NULL UPDATE RangeExternalBooking SET RangeSectionId = NULL WHERE RangeSectionId = @0", sectionId);
            await db.ExecuteAsync("DELETE FROM RangeSection WHERE Id = @0", sectionId);
        }

        // ── Club links ──────────────────────────────────────────────────────

        public async Task<List<ClubRangeLink>> GetLinksAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubRangeLink>("WHERE RangeId = @0 ORDER BY Id", rangeId);
        }

        /// <summary>Alla kopplingar mellan klubbar och banor — för katalogen, som annars hade frågat per bana.</summary>
        public async Task<List<ClubRangeLink>> GetAllLinksAsync()
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubRangeLink>("SELECT * FROM ClubRangeLink");
        }

        public async Task<ClubRangeLink?> GetLinkAsync(int linkId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<ClubRangeLink>("WHERE Id = @0", linkId);
        }

        /// <summary>Adds a club↔range link (idempotent on (RangeId, ClubId)). Returns the link id.</summary>
        public async Task<int> AddLinkAsync(int rangeId, int clubId, string relationType, int? byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<ClubRangeLink>(
                "WHERE RangeId = @0 AND ClubId = @1", rangeId, clubId);
            if (existing != null)
            {
                existing.RelationType = relationType;
                await db.UpdateAsync(existing);
                return existing.Id;
            }
            var link = new ClubRangeLink
            {
                RangeId = rangeId,
                ClubId = clubId,
                RelationType = relationType,
                AddedByMemberId = byMemberId,
                AddedAt = DateTime.Now
            };
            await db.InsertAsync(link);
            return link.Id;
        }

        public async Task RemoveLinkAsync(int linkId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM ClubRangeAllocation WHERE ClubRangeLinkId = @0", linkId);
            await db.ExecuteAsync("DELETE FROM ClubRangeLink WHERE Id = @0", linkId);
        }

        // ── Club-scoped reads (club page "Våra skjutbanor") ───────────────────

        /// <summary>Every range a club is linked to (uses or owns).</summary>
        public async Task<List<ShootingRange>> GetRangesForClubAsync(int clubId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ShootingRange>(
                "WHERE Id IN (SELECT RangeId FROM ClubRangeLink WHERE ClubId = @0) ORDER BY Name", clubId);
        }

        /// <summary>One club's allocation slots at one range.</summary>
        public async Task<List<ClubRangeAllocation>> GetClubAllocationsAsync(int rangeId, int clubId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubRangeAllocation>(
                @"WHERE ClubRangeLinkId IN (SELECT Id FROM ClubRangeLink WHERE RangeId = @0 AND ClubId = @1)
                  ORDER BY DayOfWeek, StartTime", rangeId, clubId);
        }

        /// <summary>All of a club's range links (one per range it's linked to).</summary>
        public async Task<List<ClubRangeLink>> GetClubLinksAsync(int clubId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubRangeLink>("WHERE ClubId = @0", clubId);
        }

        /// <summary>Allocations for many links in one query (batches the club-page N+1).</summary>
        public async Task<List<ClubRangeAllocation>> GetAllocationsByLinkIdsAsync(IEnumerable<int> linkIds)
        {
            var ids = linkIds.ToList();
            if (ids.Count == 0) return new List<ClubRangeAllocation>();
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubRangeAllocation>(
                "WHERE ClubRangeLinkId IN (@0) ORDER BY DayOfWeek, StartTime", ids);
        }

        // ── Allocations ───────────────────────────────────────────────────────

        /// <summary>All allocation slots for every club linked to the range.</summary>
        public async Task<List<ClubRangeAllocation>> GetAllocationsForRangeAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubRangeAllocation>(
                @"WHERE ClubRangeLinkId IN (SELECT Id FROM ClubRangeLink WHERE RangeId = @0)
                  ORDER BY ClubRangeLinkId, DayOfWeek, StartTime", rangeId);
        }

        public async Task<int> AddAllocationAsync(ClubRangeAllocation slot)
        {
            using var db = _databaseFactory.CreateDatabase();
            slot.CreatedAt = DateTime.Now;
            await db.InsertAsync(slot);
            return slot.Id;
        }

        public async Task<ClubRangeAllocation?> GetAllocationAsync(int allocationId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<ClubRangeAllocation>("WHERE Id = @0", allocationId);
        }

        /// <summary>Ändrar en fast tid på plats (banans sida: "Ändra tiden…"). Länken flyttas aldrig —
        /// en tid byter inte klubb.</summary>
        public async Task UpdateAllocationAsync(ClubRangeAllocation slot)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.UpdateAsync(slot);
        }

        public async Task DeleteAllocationAsync(int allocationId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM ClubRangeAllocation WHERE Id = @0", allocationId);
        }

        /// <summary>Resolves the RangeId that owns an allocation (for auth before delete).</summary>
        public async Task<int?> GetRangeIdForAllocationAsync(int allocationId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteScalarAsync<int?>(
                @"SELECT l.RangeId FROM ClubRangeAllocation a
                  JOIN ClubRangeLink l ON l.Id = a.ClubRangeLinkId
                  WHERE a.Id = @0", allocationId);
        }

        // ── Stewards ──────────────────────────────────────────────────────────

        public async Task<List<RangeSteward>> GetStewardsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<RangeSteward>("WHERE RangeId = @0 ORDER BY GrantedAt", rangeId);
        }

        /// <summary>Adds a steward (idempotent on (RangeId, MemberId)).</summary>
        public async Task AddStewardAsync(int rangeId, int memberId, int? byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM RangeSteward WHERE RangeId = @0 AND MemberId = @1", rangeId, memberId);
            if (count > 0) return;
            await db.InsertAsync(new RangeSteward
            {
                RangeId = rangeId,
                MemberId = memberId,
                GrantedByMemberId = byMemberId,
                GrantedAt = DateTime.Now
            });
        }

        public async Task RemoveStewardAsync(int rangeId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM RangeSteward WHERE RangeId = @0 AND MemberId = @1", rangeId, memberId);
        }

        public async Task<int> CountStewardsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM RangeSteward WHERE RangeId = @0", rangeId);
        }

        // ── Claim ───────────────────────────────────────────────────────────

        /// <summary>
        /// Claim an unclaimed range on behalf of a club: makes the caller the first steward, links the
        /// club (PrimaryUser), and flips the range Active. Caller must already be verified as a club
        /// admin for <paramref name="clubId"/> (or site admin) by the controller.
        /// </summary>
        public async Task ClaimAsync(int rangeId, int clubId, int memberId)
        {
            var range = await GetByIdAsync(rangeId);
            if (range == null) throw new InvalidOperationException("Skjutbanan hittades inte.");

            await AddStewardAsync(rangeId, memberId, memberId);
            await AddLinkAsync(rangeId, clubId, RangeConstants.RelationPrimaryUser, memberId);

            if (range.Status == RangeConstants.StatusUnclaimedSeed)
            {
                range.Status = RangeConstants.StatusActive;
                if (range.Source == RangeConstants.SourceOsm) range.Source = RangeConstants.SourceClaimed;
            }
            if (range.HuvudmanType == null)
            {
                range.HuvudmanType = RangeConstants.HuvudmanClub;
                range.HuvudmanClubId = clubId;
            }
            await UpdateAsync(range);
        }

        // ── Permits (Phase 2) ──────────────────────────────────────────────────

        public async Task<List<RangePermit>> GetPermitsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<RangePermit>("WHERE RangeId = @0 ORDER BY PermitType", rangeId);
        }

        public async Task<RangePermit?> GetPermitAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<RangePermit>("WHERE Id = @0", id);
        }

        public async Task<int> AddPermitAsync(RangePermit permit)
        {
            using var db = _databaseFactory.CreateDatabase();
            permit.CreatedAt = DateTime.Now;
            permit.UpdatedAt = DateTime.Now;
            await db.InsertAsync(permit);
            return permit.Id;
        }

        public async Task UpdatePermitAsync(RangePermit permit)
        {
            using var db = _databaseFactory.CreateDatabase();
            permit.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(permit);
        }

        public async Task DeletePermitAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM RangePermit WHERE Id = @0", id);
        }

        // ── Documents (Phase 2) ─────────────────────────────────────────────────

        public async Task<List<RangeDocument>> GetDocumentsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<RangeDocument>("WHERE RangeId = @0 ORDER BY UploadedAt DESC", rangeId);
        }

        public async Task<RangeDocument?> GetDocumentAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<RangeDocument>("WHERE Id = @0", id);
        }

        public async Task<int> AddDocumentAsync(RangeDocument doc)
        {
            using var db = _databaseFactory.CreateDatabase();
            doc.UploadedAt = DateTime.Now;
            await db.InsertAsync(doc);
            return doc.Id;
        }

        public async Task DeleteDocumentAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            // En besiktning som hänvisade till protokollet står kvar — utan protokoll.
            await db.ExecuteAsync(
                "IF OBJECT_ID('RangeInspection', 'U') IS NOT NULL UPDATE RangeInspection SET DocumentId = NULL WHERE DocumentId = @0", id);
            await db.ExecuteAsync("DELETE FROM RangeDocument WHERE Id = @0", id);
        }

        // ── Besiktning (2026-10-21) ─────────────────────────────────────────────

        /// <summary>Banans aktiva besiktningar, senaste först.</summary>
        public async Task<List<RangeInspection>> GetInspectionsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<RangeInspection>(
                "WHERE RangeId = @0 AND IsActive = 1 ORDER BY InspectedOn DESC, Id DESC", rangeId);
        }

        /// <summary>Aktiva besiktningar för flera banor på en gång (klubbarnas årshjul, påminnelserna).</summary>
        public async Task<List<RangeInspection>> GetInspectionsForRangesAsync(IReadOnlyCollection<int> rangeIds)
        {
            if (rangeIds.Count == 0) return new List<RangeInspection>();
            using var db = _databaseFactory.CreateDatabase();
            var result = new List<RangeInspection>();
            foreach (var chunk in rangeIds.Distinct().Chunk(1000))
                result.AddRange(await db.FetchAsync<RangeInspection>(
                    "WHERE RangeId IN (@0) AND IsActive = 1", chunk));
            return result;
        }

        public async Task<RangeInspection?> GetInspectionAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<RangeInspection>("WHERE Id = @0", id);
        }

        public async Task<int> AddInspectionAsync(RangeInspection inspection)
        {
            using var db = _databaseFactory.CreateDatabase();
            inspection.CreatedDate = DateTime.Now;
            inspection.IsActive = true;
            await db.InsertAsync(inspection);
            return inspection.Id;
        }

        /// <summary>En felregistrerad besiktning göms; historiken raderas aldrig.</summary>
        public async Task HideInspectionAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("UPDATE RangeInspection SET IsActive = 0 WHERE Id = @0", id);
        }

        /// <summary>Riktad skrivning av intervallet — rör inga andra fält på banan.</summary>
        public async Task SetInspectionIntervalAsync(int rangeId, int? intervalYears)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("UPDATE ShootingRange SET InspectionIntervalYears = @1, UpdatedAt = @2 WHERE Id = @0",
                rangeId, intervalYears, DateTime.Now);
        }

        /// <summary>
        /// Banornas krav för en klubbs årshjul (B2): för banor där klubben äger, hyr eller är huvudanvändare —
        /// nästa besiktning (den senaste aktiva registreringen), tillstånd med slutdatum och dokument med
        /// "gäller t.o.m." (utom protokoll som hör till en registrerad besiktning). Synkron med egen anslutning:
        /// årshjulets läsare är synkrona. ⚠️ Anropas UTANFÖR årshjulets scope — inte inne i en annans transaktion.
        /// </summary>
        public List<RangeRequirement> GetClubRangeRequirements(int clubId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ranges = db.Fetch<RangeNameRow>(
                @"SELECT DISTINCT r.Id, r.Name FROM ClubRangeLink l JOIN ShootingRange r ON r.Id = l.RangeId
                  WHERE l.ClubId = @0 AND l.RelationType IN (@1)", clubId, RangeRequirement.Relations);
            return Requirements(db, ranges);
        }

        /// <summary>
        /// Alla banors krav (påminnelserna, del C): varje bana med en förvaltare eller en klubb som äger, hyr eller
        /// är huvudanvändare — de som har någon att påminna. Mottagarna bygger anroparen.
        /// </summary>
        public List<RangeRequirement> GetAllRangeRequirements()
        {
            using var db = _databaseFactory.CreateDatabase();
            var ranges = db.Fetch<RangeNameRow>(
                @"SELECT r.Id, r.Name FROM ShootingRange r
                  WHERE EXISTS (SELECT 1 FROM RangeSteward s WHERE s.RangeId = r.Id)
                     OR EXISTS (SELECT 1 FROM ClubRangeLink l WHERE l.RangeId = r.Id AND l.RelationType IN (@0))",
                RangeRequirement.Relations);
            return Requirements(db, ranges);
        }

        /// <summary>Banans förvaltare och de klubbar som ska påminnas om banans krav (Owner/PrimaryUser/Tenant).</summary>
        public (List<int> StewardMemberIds, List<int> ClubIds) GetRequirementRecipients(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var stewards = db.Fetch<int>("SELECT MemberId FROM RangeSteward WHERE RangeId = @0", rangeId);
            var clubs = db.Fetch<int>("SELECT DISTINCT ClubId FROM ClubRangeLink WHERE RangeId = @0 AND RelationType IN (@1)",
                rangeId, RangeRequirement.Relations);
            return (stewards, clubs);
        }

        private static List<RangeRequirement> Requirements(IUmbracoDatabase db, List<RangeNameRow> ranges)
        {
            var result = new List<RangeRequirement>();
            if (ranges.Count == 0) return result;
            var ids = ranges.Select(r => r.Id).ToList();
            var name = ranges.ToDictionary(r => r.Id, r => r.Name);

            var linkedDocs = new HashSet<int>();
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.RangeInspection', 'U') IS NULL THEN 0 ELSE 1 END") == 1)
            {
                var insp = db.Fetch<RangeInspection>("WHERE RangeId IN (@0) AND IsActive = 1", ids);
                foreach (var i in insp) if (i.DocumentId.HasValue) linkedDocs.Add(i.DocumentId.Value);
                foreach (var g in insp.GroupBy(i => i.RangeId))
                    if (RangeInspectionRules.Latest(g) is { NextDueOn: DateTime due } latest)
                        result.Add(new RangeRequirement(RangeRequirement.Inspection, latest.Id, g.Key, name[g.Key], "Besiktning", due));
            }
            foreach (var p in db.Fetch<RangePermit>("WHERE RangeId IN (@0) AND ExpiryDate IS NOT NULL", ids))
                result.Add(new RangeRequirement(RangeRequirement.Permit, p.Id, p.RangeId, name[p.RangeId], RangeLabels.Permit(p.PermitType), p.ExpiryDate!.Value));
            foreach (var d in db.Fetch<RangeDocument>("WHERE RangeId IN (@0) AND ValidUntil IS NOT NULL", ids))
                if (!linkedDocs.Contains(d.Id))
                    result.Add(new RangeRequirement(RangeRequirement.Document, d.Id, d.RangeId, name[d.RangeId],
                        string.IsNullOrWhiteSpace(d.Title) ? "Dokumentet" : d.Title, d.ValidUntil!.Value));
            return result;
        }

        private class RangeNameRow { public int Id { get; set; } public string Name { get; set; } = ""; }

        /// <summary>Saknade tabeller och kolumner för besiktningen (startkontrollen).</summary>
        public List<string> MissingInspectionSchema()
        {
            using var db = _databaseFactory.CreateDatabase();
            var missing = new List<string>();
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.RangeInspection', 'U') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("RangeInspection");
            if (db.ExecuteScalar<int>("SELECT CASE WHEN COL_LENGTH('dbo.ShootingRange', 'InspectionIntervalYears') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("ShootingRange.InspectionIntervalYears");
            return missing;
        }

        // ── Activity ledger (Phase 3) ──────────────────────────────────────────

        public async Task<int> AddSessionAsync(RangeActivitySession s)
        {
            using var db = _databaseFactory.CreateDatabase();
            s.CreatedAt = DateTime.Now;
            await db.InsertAsync(s);
            return s.Id;
        }

        public async Task<RangeActivitySession?> GetSessionAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<RangeActivitySession>("WHERE Id = @0", id);
        }

        public async Task UpdateSessionAsync(RangeActivitySession s)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.UpdateAsync(s);
        }

        public async Task DeleteSessionAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM RangeActivitySession WHERE Id = @0", id);
        }

        /// <summary>The member's currently-open (checked-in, not yet out) session at a range, if any.</summary>
        public async Task<RangeActivitySession?> GetOpenSessionAsync(int rangeId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<RangeActivitySession>(
                "WHERE RangeId = @0 AND MemberId = @1 AND EndTime IS NULL ORDER BY Id DESC", rangeId, memberId);
        }

        /// <summary>
        /// The member's currently-open check-in across ANY range, scoped to today only.
        /// Today-only is the "currently here" rule: a forgotten session from a previous day
        /// is never treated as current (and is closed by <see cref="AutoCloseStaleCheckInsAsync"/>).
        /// </summary>
        public async Task<RangeActivitySession?> GetOpenSessionForMemberAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FirstOrDefaultAsync<RangeActivitySession>(
                "WHERE MemberId = @0 AND EndTime IS NULL AND [Date] = CAST(GETDATE() AS DATE) ORDER BY Id DESC", memberId);
        }

        /// <summary>The club ids linked to a range (a range can serve several clubs).</summary>
        public async Task<List<int>> GetClubIdsForRangeAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<int>("SELECT ClubId FROM ClubRangeLink WHERE RangeId = @0", rangeId);
        }

        /// <summary>
        /// End-of-day auto-checkout: closes every check-in still open from a previous day,
        /// stamping EndTime 23:59 and the range's configured DefaultShotCount (0 when unset).
        /// Set-based; safe to call opportunistically on page load. Returns rows closed.
        /// </summary>
        public async Task<int> AutoCloseStaleCheckInsAsync()
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteAsync(@"
                UPDATE s
                SET s.EndTime = '23:59:00',
                    s.ShotCount = COALESCE(r.DefaultShotCount, 0),
                    s.ShotCountSource = 'AutoClosed'
                FROM RangeActivitySession s
                JOIN ShootingRange r ON r.Id = s.RangeId
                WHERE s.EndTime IS NULL AND s.[Date] < CAST(GETDATE() AS DATE)");
        }

        public async Task<List<RangeActivitySession>> GetSessionsForYearAsync(int rangeId, int year)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<RangeActivitySession>(
                "WHERE RangeId = @0 AND YEAR([Date]) = @1 ORDER BY [Date] DESC, Id DESC", rangeId, year);
        }

        /// <summary>Distinct years that have activity rows (for the report's year picker).</summary>
        public async Task<List<int>> GetActivityYearsAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<int>(
                "SELECT DISTINCT YEAR([Date]) FROM RangeActivitySession WHERE RangeId = @0 ORDER BY 1 DESC", rangeId);
        }

        /// <summary>The facility shot cap = the first non-null MaxShotsPerYear across the range's permits.</summary>
        public async Task<int?> GetMaxShotsPerYearAsync(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteScalarAsync<int?>(
                "SELECT MIN(MaxShotsPerYear) FROM RangePermit WHERE RangeId = @0 AND MaxShotsPerYear IS NOT NULL", rangeId);
        }

        // ── OSM seed import ───────────────────────────────────────────────────

        /// <summary>
        /// Imports an overpass-turbo FeatureCollection (the ranges.geojson export) as UnclaimedSeed
        /// ranges. Deduped by OsmRef (the "@id" property). Returns (imported, skipped).
        /// </summary>
        public async Task<(int imported, int skipped)> ImportOsmAsync(string geoJson, int? byMemberId)
        {
            using var doc = JsonDocument.Parse(geoJson);
            if (!doc.RootElement.TryGetProperty("features", out var features) ||
                features.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Ogiltig GeoJSON — saknar 'features'.");

            using var db = _databaseFactory.CreateDatabase();
            int imported = 0, skipped = 0;

            // Preload existing OSM refs once (a ranges.geojson has 1000+ features — a COUNT per
            // feature would be 1000+ round trips). Also dedups repeats within the same file.
            var seenRefs = (await db.FetchAsync<string>("SELECT OsmRef FROM ShootingRange WHERE OsmRef IS NOT NULL"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var f in features.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var props)) { skipped++; continue; }

                string? osmRef = GetStr(props, "@id") ?? (f.TryGetProperty("id", out var idEl) ? idEl.GetString() : null);
                if (string.IsNullOrWhiteSpace(osmRef)) { skipped++; continue; }

                if (!seenRefs.Add(osmRef)) { skipped++; continue; }

                double? lat = null, lon = null;
                if (f.TryGetProperty("geometry", out var geom) &&
                    geom.TryGetProperty("coordinates", out var coords) &&
                    coords.ValueKind == JsonValueKind.Array && coords.GetArrayLength() >= 2)
                {
                    lon = coords[0].GetDouble();
                    lat = coords[1].GetDouble();
                }

                string? name = GetStr(props, "name");
                string? op = GetStr(props, "operator");
                string displayName = !string.IsNullOrWhiteSpace(name) ? name!
                    : !string.IsNullOrWhiteSpace(op) ? op!
                    : "Namnlös skjutbana (OSM)";

                await db.InsertAsync(new ShootingRange
                {
                    Name = displayName.Length > 200 ? displayName[..200] : displayName,
                    Latitude = lat,
                    Longitude = lon,
                    City = GetStr(props, "addr:city"),
                    Postcode = GetStr(props, "addr:postcode"),
                    HuvudmanName = op,
                    Status = RangeConstants.StatusUnclaimedSeed,
                    Source = RangeConstants.SourceOsm,
                    OsmRef = osmRef,
                    CreatedByMemberId = byMemberId,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
                imported++;
            }
            _logger.LogInformation("OSM range import: {Imported} imported, {Skipped} skipped", imported, skipped);
            return (imported, skipped);
        }

        private static string? GetStr(JsonElement obj, string prop)
            => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
    }
}
