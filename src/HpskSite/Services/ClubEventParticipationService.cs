using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Sign-up and attendance for club AND krets events. Both scopes use the same
    /// <c>clubSimpleEvent</c> doctype — a club event hangs under a <c>club</c> node and a krets
    /// event under a <c>regionalPage</c> node (see <c>ClubController.CreateRegionEvent</c>) — so the
    /// owner is simply the event's parent and there is exactly one code path for both.
    ///
    /// <b>Two things are DERIVED and never stored</b>, because a stored copy is a copy to keep in
    /// step and this codebase has paid for that lesson (scoringMode):
    /// <list type="bullet">
    /// <item>the OWNER, read from the event's parent node;</item>
    /// <item>the SEAT vs RESERVE split, computed from sign-up order among non-cancelled rows.
    /// Storing a reserve flag would mean rewriting every later row each time someone withdraws,
    /// and a missed rewrite is a silently wrong list.</item>
    /// </list>
    /// </summary>
    public class ClubEventParticipationService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoles;
        private readonly ILogger<ClubEventParticipationService> _logger;

        public ClubEventParticipationService(
            IUmbracoDatabaseFactory databaseFactory,
            IContentService contentService,
            IMemberService memberService,
            MemberClubService memberClubs,
            AdminAuthorizationService auth,
            BoardRoleService boardRoles,
            ILogger<ClubEventParticipationService> logger)
        {
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _memberService = memberService;
            _memberClubs = memberClubs;
            _auth = auth;
            _boardRoles = boardRoles;
            _logger = logger;
        }

        // ── Event context ─────────────────────────────────────────────

        /// <summary>
        /// Everything about an event that the sign-up rules depend on, resolved once per event
        /// rather than once per participant row.
        /// </summary>
        public ClubEventContext? GetEventContext(int eventId)
        {
            if (eventId <= 0) return null;
            var node = _contentService.GetById(eventId);
            if (node == null || node.ContentType.Alias != ClubEvents.EventAlias) return null;

            var parent = node.ParentId > 0 ? _contentService.GetById(node.ParentId) : null;
            var ownerAlias = parent?.ContentType.Alias ?? "";

            var ctx = new ClubEventContext
            {
                EventId = eventId,
                EventName = node.GetValue<string>("eventName") ?? node.Name ?? "",
                EventDate = node.GetValue<DateTime?>("eventDate"),
                EventEndDate = node.GetValue<DateTime?>("eventEndDate"),
                Venue = node.GetValue<string>("venue") ?? "",
                EventType = node.GetValue<string>("eventType") ?? "",
                RegistrationRequired = node.GetValue<bool>("registrationRequired"),
                MaxParticipants = node.GetValue<int>("maxParticipants"),
                RegistrationUrl = node.GetValue<string>("registrationUrl") ?? "",
                OwnerId = parent?.Id ?? 0,
                OwnerName = parent?.Name ?? "",
                IsClubOwned = ownerAlias == ClubEvents.OwnerClubAlias,
                IsRegionOwned = ownerAlias == ClubEvents.OwnerRegionAlias
            };

            // Both are operator-added properties. A missing property must degrade to "off" rather
            // than throw — GetValue on an unknown alias returns default, so this is safe by
            // construction, but the WRITE side is what silently no-ops (see the controller).
            ctx.IsMandatory = node.GetValue<bool>(ClubEvents.MandatoryProperty);
            ctx.Fee = node.GetValue<decimal?>(ClubEvents.FeeProperty);
            // ⚠️ RealDate, inte råvärdet: en tom Umbraco-DateTime läses som DateTime.MinValue, och
            // en deadline år 1 hade stängt anmälan på varje händelse ingen satt en deadline på.
            ctx.RegistrationDeadline = ClubEvents.RealDate(node.GetValue<DateTime?>(ClubEvents.DeadlineProperty));

            if (ctx.IsClubOwned)
                ctx.RegionCode = parent?.GetValue<string>("regionalFederation") ?? "";
            else if (ctx.IsRegionOwned)
                ctx.RegionCode = parent?.GetValue<string>("regionCode") ?? parent?.Name ?? "";

            return ctx;
        }

        /// <summary>
        /// Is the sign-up window open? Closes at the END of a day, never at a start time — a date
        /// with no clock time would otherwise be closed from midnight, i.e. for the whole day people
        /// actually sign up on. A functionary can still add someone at the door afterwards; that is
        /// a different act (see <see cref="AddWalkInAsync"/>).
        ///
        /// <para><b>Two windows, and the EARLIER one closes it.</b> The arrangör's
        /// <c>registrationDeadline</c> (inclusive) and the event's own last day. Letting the
        /// deadline simply override would leave sign-up open on an event that has already been
        /// held whenever someone typed a deadline after the event date; letting the event day
        /// override would ignore the deadline entirely. Both are gates, so both must pass.</para>
        /// </summary>
        public static bool IsSignupOpen(ClubEventContext ctx, DateTime? now = null)
        {
            if (!ctx.RegistrationRequired) return false;
            var at = now ?? DateTime.Now;

            if (ctx.RegistrationDeadline is DateTime deadline
                && at >= deadline.Date.AddDays(1)) return false;

            var last = ctx.EventEndDate ?? ctx.EventDate;
            if (last == null) return true;                      // undated event — nothing to close against
            return at < last.Value.Date.AddDays(1);
        }

        /// <summary>
        /// May the member still withdraw? <b>Deliberately a WIDER window than
        /// <see cref="IsSignupOpen"/>: it ignores the deadline</b> and runs to the end of the
        /// event's last day. A deadline exists so the arrangör knows how many are coming — locking
        /// someone in weeks ahead does the opposite, because the one who cannot come stops
        /// telling anyone and the list says they are still expected. The cancellation itself
        /// promotes the first reserve (derived, see <see cref="BuildRosterAsync"/>).
        /// </summary>
        public static bool IsCancelOpen(ClubEventContext ctx, DateTime? now = null)
        {
            if (!ctx.RegistrationRequired) return false;
            var last = ctx.EventEndDate ?? ctx.EventDate;
            if (last == null) return true;
            return (now ?? DateTime.Now) < last.Value.Date.AddDays(1);
        }

        // ── Eligibility ───────────────────────────────────────────────

        /// <summary>
        /// May this member sign themselves up? Stefan's rule (2026-08-31): a club event is for the
        /// club's own members, a krets event for members of any club in that krets. Membership is
        /// read through <see cref="MemberClubService"/>, so an additional-club membership counts —
        /// primary club alone would lock out exactly the people who joined a second club.
        /// </summary>
        public bool IsEligible(ClubEventContext ctx, IMember? member)
            => IsEligible(GetEligibleClubIds(ctx), member);

        /// <summary>
        /// Overload for loops. <b>Resolve the club set ONCE</b> with <see cref="GetEligibleClubIds"/>
        /// and pass it in — the per-member version re-reads the krets's club list for every member,
        /// which on a real member register is thousands of content lookups for one search box.
        /// </summary>
        public bool IsEligible(HashSet<int> eligibleClubIds, IMember? member)
        {
            if (member == null || eligibleClubIds.Count == 0) return false;
            return _memberClubs.GetAllClubIds(member).Any(eligibleClubIds.Contains);
        }

        /// <summary>
        /// Which clubs' members may sign up: the owning club, or every club in the owning krets.
        /// </summary>
        public HashSet<int> GetEligibleClubIds(ClubEventContext ctx)
        {
            var ids = new HashSet<int>();
            if (ctx.IsClubOwned)
            {
                if (ctx.OwnerId > 0) ids.Add(ctx.OwnerId);
                return ids;
            }
            if (!ctx.IsRegionOwned || string.IsNullOrWhiteSpace(ctx.RegionCode)) return ids;

            // The krets's clubs live under its clubsPage child; read the tree rather than scanning
            // every club in the country.
            var region = _contentService.GetById(ctx.OwnerId);
            if (region == null) return ids;

            foreach (var child in _contentService.GetPagedChildren(region.Id, 0, int.MaxValue, out _))
            {
                if (child.ContentType.Alias == ClubEvents.OwnerClubAlias) { ids.Add(child.Id); continue; }
                foreach (var grand in _contentService.GetPagedChildren(child.Id, 0, int.MaxValue, out _))
                    if (grand.ContentType.Alias == ClubEvents.OwnerClubAlias) ids.Add(grand.Id);
            }
            return ids;
        }

        /// <summary>
        /// May the current user run the roll-call and manage the list? Club admin (which folds in
        /// the region's admins), the club's board, or its skjutledare; for a krets event, the
        /// region's admins or the krets board. Deliberately the same set that signs off märken —
        /// a club should not have to learn a second permission model for a second list.
        /// </summary>
        public async Task<bool> CanManageAsync(ClubEventContext ctx, int actingMemberId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;

            if (ctx.IsClubOwned)
            {
                if (await _auth.IsClubAdminForClub(ctx.OwnerId)) return true;
                if (await _auth.IsSkjutledareForClub(ctx.OwnerId)) return true;
                return IsOnBoard(DocumentOwnerType.Club, ctx.OwnerId, actingMemberId);
            }

            if (ctx.IsRegionOwned)
            {
                if (!string.IsNullOrWhiteSpace(ctx.RegionCode)
                    && await _auth.IsRegionalAdminForRegion(ctx.RegionCode)) return true;
                return IsOnBoard(DocumentOwnerType.Region, ctx.OwnerId, actingMemberId);
            }

            return false;
        }

        private bool IsOnBoard(int ownerType, int ownerId, int memberId)
        {
            if (memberId <= 0) return false;
            try
            {
                return _boardRoles.IsBoardMemberOf(ownerType, ownerId, memberId);
            }
            catch (Exception ex)
            {
                // A failed board lookup must not hand out access, and must not take the page down.
                _logger.LogWarning(ex, "Board lookup failed for {OwnerType} {OwnerId}", ownerType, ownerId);
                return false;
            }
        }

        // ── Reads ─────────────────────────────────────────────────────

        public async Task<List<ClubEventParticipant>> GetParticipantsAsync(int eventId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<ClubEventParticipant>(
                "WHERE EventId = @0 ORDER BY CASE WHEN SignedUpAt IS NULL THEN 1 ELSE 0 END, SignedUpAt, Id", eventId);
        }

        public async Task<ClubEventParticipant?> GetParticipantAsync(int eventId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND MemberId = @1", eventId, memberId);
        }

        /// <summary>
        /// The roster with the seat/reserve split applied. <see cref="ClubEventRosterRow.IsReserve"/>
        /// is computed here and nowhere else.
        /// </summary>
        public async Task<ClubEventRoster> BuildRosterAsync(ClubEventContext ctx)
        {
            var rows = await GetParticipantsAsync(ctx.EventId);
            var roster = new ClubEventRoster { Context = ctx };

            int seatsTaken = 0;
            foreach (var p in rows)
            {
                bool active = p.SignedUpAt != null && p.CancelledAt == null;
                bool reserve = false;

                if (active && ctx.MaxParticipants > 0)
                {
                    reserve = seatsTaken >= ctx.MaxParticipants;
                    if (!reserve) seatsTaken++;
                }
                else if (active)
                {
                    seatsTaken++;
                }

                roster.Rows.Add(new ClubEventRosterRow
                {
                    MemberId = p.MemberId,
                    Name = p.MemberName,
                    SignedUpAt = p.SignedUpAt,
                    Cancelled = p.CancelledAt != null,
                    IsReserve = reserve,
                    IsWalkIn = p.SignedUpAt == null,
                    Note = p.SignedUpNote,
                    AttendanceStatus = p.AttendanceStatus,
                    AttendanceNote = p.AttendanceNote,
                    // Sjalvregistrerad = medlemmen ar sin egen registrerare (skannade QR-affischen).
                    // HARLETT, ingen extra kolumn — och viktigt att kunna se: en QR pa en vagg kan
                    // fotograferas och skickas vidare, sa det ar svagare bevis an en funktionars
                    // upprop nar narvaron sedan ska bara ett Foreningsintyg.
                    SelfRegistered = p.AttendanceStatus != null && p.RecordedByMemberId == p.MemberId,
                    FeeAmount = p.FeeAmount
                });
            }

            roster.SignedUp = roster.Rows.Count(r => !r.Cancelled && !r.IsWalkIn);
            roster.Seated = roster.Rows.Count(r => !r.Cancelled && !r.IsWalkIn && !r.IsReserve);
            roster.Reserves = roster.Rows.Count(r => !r.Cancelled && r.IsReserve);
            roster.Cancelled = roster.Rows.Count(r => r.Cancelled);
            roster.Present = roster.Rows.Count(r => r.AttendanceStatus == ClubEvents.AttendancePresent);
            roster.NotRecorded = roster.Rows.Count(r => !r.Cancelled && r.AttendanceStatus == null);
            roster.SeatsLeft = ctx.MaxParticipants > 0 ? Math.Max(0, ctx.MaxParticipants - roster.Seated) : (int?)null;
            return roster;
        }

        /// <summary>A member's own event participation, for Min sida and — later — the yearly
        /// activity summary that a Föreningsintyg is generated from.</summary>
        public async Task<List<ClubEventParticipant>> GetForMemberAsync(int memberId, int? year = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            var rows = await db.FetchAsync<ClubEventParticipant>(
                "WHERE MemberId = @0 ORDER BY Id DESC", memberId);
            if (year == null) return rows;

            // The year belongs to the EVENT, not to the row's timestamps — a roll-call taken in
            // January for a December event is December's activity.
            return rows.Where(r =>
            {
                var ctx = GetEventContext(r.EventId);
                return ctx?.EventDate?.Year == year;
            }).ToList();
        }

        // ── Writes ────────────────────────────────────────────────────

        /// <summary>
        /// Sign a member up. Re-uses an existing row when they had withdrawn earlier — the unique
        /// index on (EventId, MemberId) makes that the only possible path, which is deliberate:
        /// a second row would give one person two places in the queue.
        /// </summary>
        public async Task<(bool Ok, string? Message, bool IsReserve)> SignUpAsync(
            ClubEventContext ctx, int memberId, string? note, int actingMemberId)
        {
            var member = _memberService.GetById(memberId);
            if (member == null) return (false, "Medlemmen hittades inte.", false);

            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND MemberId = @1", ctx.EventId, memberId);

            if (existing != null && existing.SignedUpAt != null && existing.CancelledAt == null)
                return (false, "Du är redan anmäld.", false);

            var now = DateTime.Now;
            if (existing == null)
            {
                existing = new ClubEventParticipant
                {
                    EventId = ctx.EventId,
                    MemberId = memberId,
                    MemberName = member.Name ?? $"Medlem {memberId}",
                    CreatedDate = now
                };
            }

            existing.SignedUpAt = now;
            existing.SignedUpByMemberId = actingMemberId;
            existing.SignedUpNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            existing.CancelledAt = null;
            existing.CancelledByMemberId = null;
            existing.MemberName = member.Name ?? existing.MemberName;
            // Snapshot the fee as it stands now, so changing the event later cannot rewrite what
            // somebody already signed up to.
            existing.FeeAmount = ctx.Fee;
            existing.UpdatedDate = now;

            if (existing.Id > 0) await db.UpdateAsync(existing);
            else await db.InsertAsync(existing);

            var roster = await BuildRosterAsync(ctx);
            bool reserve = roster.Rows.Any(r => r.MemberId == memberId && r.IsReserve);
            return (true, null, reserve);
        }

        /// <summary>Withdraw. The row survives — the fee snapshot and the history hang off it.</summary>
        public async Task<(bool Ok, string? Message)> CancelAsync(int eventId, int memberId, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var row = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND MemberId = @1", eventId, memberId);
            if (row == null || row.SignedUpAt == null) return (false, "Ingen anmälan att avboka.");
            if (row.CancelledAt != null) return (false, "Anmälan är redan avbokad.");

            row.CancelledAt = DateTime.Now;
            row.CancelledByMemberId = actingMemberId;
            row.UpdatedDate = DateTime.Now;
            await db.UpdateAsync(row);
            return (true, null);
        }

        /// <summary>
        /// Record (or clear) attendance. <paramref name="status"/> null clears it back to
        /// "ej registrerad" — which is a real state and not the same as absent.
        /// </summary>
        public async Task<(bool Ok, string? Message)> SetAttendanceAsync(
            int eventId, int memberId, string? status, string? note, int actingMemberId)
        {
            if (status != null && !ClubEvents.IsAttendanceStatus(status))
                return (false, "Ogiltig närvarostatus.");

            var member = _memberService.GetById(memberId);
            if (member == null) return (false, "Medlemmen hittades inte.");

            using var db = _databaseFactory.CreateDatabase();
            var row = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND MemberId = @1", eventId, memberId);

            var now = DateTime.Now;
            if (row == null)
            {
                // Turned up without signing up. That is a legitimate row with no SignedUpAt.
                row = new ClubEventParticipant
                {
                    EventId = eventId,
                    MemberId = memberId,
                    MemberName = member.Name ?? $"Medlem {memberId}",
                    CreatedDate = now
                };
            }

            row.AttendanceStatus = status;
            row.AttendanceNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            row.RecordedByMemberId = status == null ? null : actingMemberId;
            row.RecordedAt = status == null ? null : now;
            row.UpdatedDate = now;

            if (row.Id > 0) await db.UpdateAsync(row);
            else await db.InsertAsync(row);
            return (true, null);
        }

        /// <summary>
        /// A functionary adds someone at the door. Separate from <see cref="SignUpAsync"/> because
        /// it bypasses the sign-up window and the capacity split on purpose — the person is
        /// standing there, and a full list is not a reason to leave them off the roll-call.
        /// </summary>
        public async Task<(bool Ok, string? Message)> AddWalkInAsync(int eventId, int memberId, int actingMemberId)
            => await SetAttendanceAsync(eventId, memberId, ClubEvents.AttendancePresent, null, actingMemberId);
    }

    /// <summary>Resolved facts about one event — read once, not per participant row.</summary>
    public class ClubEventContext
    {
        public int EventId { get; set; }
        public string EventName { get; set; } = "";
        public DateTime? EventDate { get; set; }
        public DateTime? EventEndDate { get; set; }
        public string Venue { get; set; } = "";
        public string EventType { get; set; } = "";

        public bool RegistrationRequired { get; set; }
        public int MaxParticipants { get; set; }

        /// <summary>Legacy escape hatch: an external sign-up link. When set, we link out instead of
        /// offering our own sign-up, so a club mid-migration is not signed up in two places.</summary>
        public string RegistrationUrl { get; set; } = "";

        public bool IsMandatory { get; set; }
        public decimal? Fee { get; set; }

        /// <summary>
        /// Sista anmälningsdag, <b>inklusive dagen själv</b> (operator-added doctype property; null
        /// = no deadline, sign-up runs until the event itself). Read by
        /// <see cref="ClubEventParticipationService.IsSignupOpen"/> — nowhere else decides it.
        /// </summary>
        public DateTime? RegistrationDeadline { get; set; }

        public int OwnerId { get; set; }
        public string OwnerName { get; set; } = "";
        public bool IsClubOwned { get; set; }
        public bool IsRegionOwned { get; set; }
        public string RegionCode { get; set; } = "";
    }

    public class ClubEventRoster
    {
        public ClubEventContext Context { get; set; } = new();
        public List<ClubEventRosterRow> Rows { get; set; } = new();
        public int SignedUp { get; set; }
        public int Seated { get; set; }
        public int Reserves { get; set; }
        public int Cancelled { get; set; }
        public int Present { get; set; }
        public int NotRecorded { get; set; }
        /// <summary>null when the event has no capacity cap.</summary>
        public int? SeatsLeft { get; set; }
    }

    public class ClubEventRosterRow
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public DateTime? SignedUpAt { get; set; }
        public bool Cancelled { get; set; }
        public bool IsReserve { get; set; }
        public bool IsWalkIn { get; set; }
        public string? Note { get; set; }
        public string? AttendanceStatus { get; set; }
        public string? AttendanceNote { get; set; }
        /// <summary>Narvaron registrerades av medlemmen sjalv via QR-affischen, inte av en funktionar.</summary>
        public bool SelfRegistered { get; set; }
        public decimal? FeeAmount { get; set; }
    }
}
