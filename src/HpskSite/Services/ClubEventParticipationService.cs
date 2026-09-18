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
            // Vem som får anmäla sig. ⚠️ Saknad egenskap och oläsbart värde ger BÅDA
            // EventAudience.Club — se den klassens huvud för varför riktningen är enkelriktad.
            // `AudiencePropertyExists` skiljer de två åt, så gränssnittet kan säga "egenskapen
            // saknas" i stället för att visa en väljare vars val tyst inte sparas.
            ctx.Audience = EventAudience.Normalise(node.GetValue<string>(EventAudience.Property));
            ctx.AudiencePropertyExists = node.HasProperty(EventAudience.Property);

            // ── Swish-numret ────────────────────────────────────────────────────────────────
            // ⚠️ ÄGAREN ÄR STANDARD, händelsen är en ÅSIDOSÄTTNING. Klubben och kretsen bär redan
            // `swishNumber` (klubbens redigeringsmodal skriver det), så ett obligatoriskt fält per
            // händelse hade tvingat varje arrangör att skriva om numret varje gång — och en
            // felskrivning skickar pengarna till fel konto, tyst, eftersom vi inte har någon
            // Swish-API som kan säga emot.
            var ownSwish = (node.GetValue<string>(ClubEvents.SwishProperty) ?? "").Trim();
            ctx.SwishNumber = ownSwish.Length > 0
                ? ownSwish
                : (parent?.GetValue<string>(ClubEvents.SwishProperty) ?? "").Trim();
            ctx.SwishFromOwner = ownSwish.Length == 0 && ctx.SwishNumber.Length > 0;
            ctx.SwishPropertyExists = node.HasProperty(ClubEvents.SwishProperty);
            // Prisraderna. En lista, inte ett tal - se EventPrices for varfor.
            ctx.Prices = EventPrices.Parse(node.GetValue<string>(EventPrices.Property));
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
        /// <summary>
        /// Får den här personen anmäla sig?
        ///
        /// <para>⚠️ De två breda nivåerna besvaras FÖRE klubbuppslaget, inte genom att fylla
        /// mängden med varje klubb i landet — dels är det tusentals innehållsläsningar för en fråga
        /// som redan är avgjord, dels skulle en klubb som råkar sakna nod tyst utesluta sina
        /// medlemmar ur en händelse som är öppen för alla.</para>
        ///
        /// <para>⚠️ <see cref="EventAudience.Open"/> svarar sant även för <c>null</c>: nivån
        /// betyder att ingen inloggning krävs. Anroparen måste alltså själv veta om den frågar om
        /// en inloggad person eller om en besökare — se <c>SignUpOpen</c>.</para>
        /// </summary>
        public bool IsEligible(ClubEventContext ctx, IMember? member)
        {
            if (ctx.Audience == EventAudience.Open) return true;
            if (member == null) return false;
            if (ctx.Audience == EventAudience.AllMembers) return true;
            return IsEligible(GetEligibleClubIds(ctx), member);
        }

        /// <summary>
        /// Får den här personen se DELTAGARLISTAN?
        ///
        /// <para><b>⚠️⚠️ SKILD FRÅN <see cref="IsEligible"/>, och det är hela poängen.</b> Att
        /// arrangören öppnar anmälan för hela landet är ett beslut om vem som får KOMMA — det är
        /// inte ett beslut om att publicera namnen på alla som kommer. Läts listan följa
        /// anmälningsrätten skulle en klubb som bjuder in grannklubbarna samtidigt, och utan att
        /// någonstans få veta det, göra sin deltagarlista läsbar för varje medlem i landet.</para>
        ///
        /// <para>Listan följer därför klubben, eller kretsen när arrangören valt kretsnivå — de
        /// bredare nivåerna kapas. Funktionärer ser den alltid (<c>CanManageAsync</c>), och
        /// utomstående ser ANTALET, vilket är det som säger om det finns plats.</para>
        /// </summary>
        public bool CanSeeRoster(ClubEventContext ctx, IMember? member)
        {
            if (member == null) return false;
            return IsEligible(GetRosterClubIds(ctx), member);
        }

        /// <summary>Klubbarna vars medlemmar får se listan — publiknivån kapad vid kretsen.</summary>
        public HashSet<int> GetRosterClubIds(ClubEventContext ctx)
            => GetClubIdsFor(ctx, EventAudience.IsAtLeastAsWideAs(ctx.Audience, EventAudience.Region)
                ? EventAudience.Region
                : EventAudience.Club);

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
            => GetClubIdsFor(ctx, ctx.Audience);

        /// <summary>
        /// Klubbarna vars medlemmar omfattas av en given nivå.
        ///
        /// <para>⚠️ <see cref="EventAudience.AllMembers"/> och <see cref="EventAudience.Open"/>
        /// ger en TOM mängd med flit — de går inte att uttrycka som en klubblista, och den som
        /// frågar om dem via den här metoden ställer fel fråga. <see cref="IsEligible"/> besvarar
        /// dem före uppslaget.</para>
        /// </summary>
        private HashSet<int> GetClubIdsFor(ClubEventContext ctx, string audience)
        {
            var ids = new HashSet<int>();
            var level = EventAudience.Normalise(audience);

            // En kretshändelse ÄR kretsen — där betyder Klubb och Krets samma sak.
            bool wantRegion = level == EventAudience.Region || ctx.IsRegionOwned;

            if (ctx.IsClubOwned && !wantRegion)
            {
                if (ctx.OwnerId > 0) ids.Add(ctx.OwnerId);
                return ids;
            }

            // ⚠️ Kretsnoden slås upp OLIKA beroende på vem som äger händelsen: en kretshändelse
            // hänger direkt under kretsen, medan en klubbhändelse ligger två steg ned
            // (regionalPage > clubsPage > club). Att anta det ena är hur den här kodbasen fyra
            // gånger har låst ute kretsen från sin egen tävling.
            var region = ctx.IsRegionOwned
                ? _contentService.GetById(ctx.OwnerId)
                : FindRegionForClub(ctx.OwnerId);
            if (region == null)
            {
                // Hittas ingen krets faller vi tillbaka på klubben — smalare, aldrig bredare.
                if (ctx.IsClubOwned && ctx.OwnerId > 0) ids.Add(ctx.OwnerId);
                return ids;
            }

            // The krets's clubs live under its clubsPage child; read the tree rather than scanning
            // every club in the country.
            foreach (var child in _contentService.GetPagedChildren(region.Id, 0, int.MaxValue, out _))
            {
                if (child.ContentType.Alias == ClubEvents.OwnerClubAlias) { ids.Add(child.Id); continue; }
                foreach (var grand in _contentService.GetPagedChildren(child.Id, 0, int.MaxValue, out _))
                    if (grand.ContentType.Alias == ClubEvents.OwnerClubAlias) ids.Add(grand.Id);
            }
            return ids;
        }

        /// <summary>Kretsnoden ovanför en klubb: <c>regionalPage &gt; clubsPage &gt; club</c>.
        /// Samma väg som URL-provideren använder, alltså trädet och inte <c>regionalFederation</c>
        /// — koden är en sträng som kan vara tom eller stavad annorlunda.</summary>
        private Umbraco.Cms.Core.Models.IContent? FindRegionForClub(int clubId)
        {
            if (clubId <= 0) return null;
            var club = _contentService.GetById(clubId);
            var clubsPage = club?.ParentId > 0 ? _contentService.GetById(club.ParentId) : null;
            var region = clubsPage?.ParentId > 0 ? _contentService.GetById(clubsPage.ParentId) : null;
            return region?.ContentType.Alias == ClubEvents.OwnerRegionAlias ? region : null;
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
                    Id = p.Id,
                    MemberId = p.MemberId,
                    IsGuest = p.IsGuest,
                    GuestOfMemberId = p.GuestOfMemberId,
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
                    // ⚠️ !IsGuest forst. En gast KAN inte sjalvregistrera sig - hen har inget konto
                    // att skanna QR-affischen med - sa fragan ar meningslos for gastrader. Skyddet
                    // ar uttryckligt och inte underforstatt: bar MemberId nagon gang null i stallet
                    // for 0 blir jamforelsen null == null, alltsa SANT, och varje oregistrerad gast
                    // hade visats som sjalvregistrerad pa ett underlag till ett Foreningsintyg.
                    SelfRegistered = !p.IsGuest && p.AttendanceStatus != null
                                     && p.RecordedByMemberId == p.MemberId,
                    FeeAmount = p.FeeAmount,
                    FeePriceId = p.FeePriceId,
                    FeeLabel = p.FeeLabel
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
        /// activity summary that a Föreningsintyg is generated from.
        ///
        /// <para><b>⚠️ Gästrader räknas ALDRIG som medlemmens egen aktivitet</b>, och filtret
        /// <c>MemberId = @0</c> gör det av sig självt eftersom en gäst bär 0. Det är avsiktligt och
        /// inte en slump: att Hugo tog med sin fru säger ingenting om Hugos egen skytteverksamhet,
        /// och det här underlaget går till Polismyndigheten.</para></summary>
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
        /// <summary>
        /// Vad deltagaren faktiskt valde, eller null nar evenemanget inte tar nagon avgift.
        ///
        /// <para><b>Reglerna, i den har ordningen:</b> ingen avgift -> null; EXAKT ETT pris ->
        /// det priset, oavsett vad som skickades (det valjer sig sjalvt); flera priser -> den rad
        /// id:t pekar pa.</para>
        ///
        /// <para><b>⚠️ PLOCKAR ALDRIG "FORSTA RADEN".</b> Har evenemanget flera priser och inget
        /// giltigt id kom in returneras null, och anropare MASTE da vagra anmalan. Att gissa hade
        /// satt ett belopp ingen pekat pa - och beloppet ar en overenskommelse som personen sedan
        /// debiteras efter.</para>
        ///
        /// <para>⚠️ Olasbara prisrader ger null OCH far aldrig lasas som "ingen avgift" - se
        /// <see cref="EventPriceList"/>. Anroparen skiljer de tva at via <see cref="PriceChoiceError"/>.</para>
        /// </summary>
        public static EventPrice? ResolvePriceChoice(ClubEventContext ctx, string? priceId)
        {
            var prices = ctx.Prices;
            if (prices.Unreadable || prices.Rows.Count == 0) return null;
            if (prices.Rows.Count == 1) return prices.Rows[0];
            return prices.ById(priceId);
        }

        /// <summary>
        /// Felmeddelandet nar ett prisval kravs men saknas, eller null nar anmalan far ga igenom.
        ///
        /// <para>⚠️ Skild fran <see cref="ResolvePriceChoice"/> for att "ingen avgift" och "du
        /// maste valja" bada ger null dar, men betyder motsatta saker for anroparen.</para>
        /// </summary>
        public static string? PriceChoiceError(ClubEventContext ctx, string? priceId)
        {
            if (ctx.Prices.Unreadable)
                return "Evenemangets priser gar inte att lasa. Kontakta arrangoren - ingen anmalan gjordes.";
            if (ctx.Prices.Rows.Count <= 1) return null;
            if (ResolvePriceChoice(ctx, priceId) != null) return null;

            var val = string.Join(", ", ctx.Prices.Rows.Select(r => r.Label));
            return string.IsNullOrWhiteSpace(priceId)
                ? $"Valj vilket pris som galler for dig: {val}."
                : "Priset du valde finns inte langre pa evenemanget. Ladda om sidan och valj igen.";
        }

        public async Task<(bool Ok, string? Message, bool IsReserve)> SignUpAsync(
            ClubEventContext ctx, int memberId, string? note, int actingMemberId, string? priceId = null)
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
            // SNAPSHOTTA VALET. Id, etikett OCH belopp tillsammans - se ClubEventParticipant.FeeLabel
            // for varfor alla tre. En senare prisandring far aldrig skriva om vad nagon sagt ja till.
            //
            // Ett enda pris valjer sig sjalvt; flera kraver ett val, och det ar validerat i
            // ResolvePriceChoice ovanfor. Ingen rad plockas nagonsin "forst i listan".
            var chosen = ResolvePriceChoice(ctx, priceId);
            existing.FeePriceId = chosen?.Id;
            existing.FeeLabel = chosen?.Label;
            existing.FeeAmount = chosen?.Amount;
            existing.UpdatedDate = now;

            if (existing.Id > 0) await db.UpdateAsync(existing);
            else await db.InsertAsync(existing);

            var roster = await BuildRosterAsync(ctx);
            bool reserve = roster.Rows.Any(r => !r.IsGuest && r.MemberId == memberId && r.IsReserve);
            return (true, null, reserve);
        }

        /// <summary>
        /// Anmäl en person utan konto — en anhörig eller en gäst — på en medlems ansvar.
        ///
        /// <para><b>En egen rad, inte ett antal på medlemmens rad.</b> Gästen tar en plats, väljer
        /// sitt eget pris och prickas av för sig. Ett "+2" på medlemmens rad hade gjort alla tre
        /// sakerna fel samtidigt, och tyst.</para>
        ///
        /// <para>⚠️ Prisvalet valideras med exakt samma regler som en medlems: har evenemanget flera
        /// priser och inget giltigt val kom in <b>vägras anmälan</b>. Det är hela poängen med
        /// familjepriserna — "Vuxen 180 / Barn 7-15 90 / Under 7 år 0" betyder ingenting om vi
        /// gissar vilken rad sonen hör till.</para>
        /// </summary>
        public async Task<(bool Ok, string? Message, bool IsReserve)> AddGuestAsync(
            ClubEventContext ctx, int guestOfMemberId, string? name, string? priceId, int actingMemberId)
        {
            var host = _memberService.GetById(guestOfMemberId);
            if (host == null) return (false, "Medlemmen hittades inte.", false);

            name = name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return (false, "Gästen behöver ett namn — det är det som står på uppropslistan.", false);
            if (name.Length > ClubEvents.GuestNameMaxLength)
                return (false, $"Namnet får vara högst {ClubEvents.GuestNameMaxLength} tecken.", false);

            var priceError = PriceChoiceError(ctx, priceId);
            if (priceError != null) return (false, priceError, false);

            using var db = _databaseFactory.CreateDatabase();

            // ⚠️ Gasten hanger pa medlemmens egen anmalan. Star inte medlemmen sjalv pa listan finns
            // det ingen som ansvarar for platsen eller avgiften, och avbokningen nedan skulle inte
            // ha nagot att kaskadera fran.
            var hostRow = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND MemberId = @1", ctx.EventId, guestOfMemberId);
            if (hostRow == null || hostRow.SignedUpAt == null || hostRow.CancelledAt != null)
                return (false, "Anmäl dig själv först — gästen anmäls på din anmälan.", false);

            var mine = await db.FetchAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND GuestOfMemberId = @1 AND CancelledAt IS NULL",
                ctx.EventId, guestOfMemberId);
            if (mine.Count >= ClubEvents.MaxGuestsPerMember)
                return (false, $"Du kan ta med högst {ClubEvents.MaxGuestsPerMember} gäster. Kontakta arrangören för fler.", false);

            // ⚠️ Samma namn tva ganger ar nastan alltid en dubbelklickad knapp, och tva rader betyder
            // tva platser och dubbel avgift. Namnet ar det enda vi har att kanna igen gasten pa.
            if (mine.Any(g => string.Equals(g.MemberName, name, StringComparison.OrdinalIgnoreCase)))
                return (false, $"{name} är redan anmäld som din gäst.", false);

            var now = DateTime.Now;
            var chosen = ResolvePriceChoice(ctx, priceId);

            var row = new ClubEventParticipant
            {
                EventId = ctx.EventId,
                MemberId = ClubEvents.GuestMemberId,
                MemberName = name,
                GuestOfMemberId = guestOfMemberId,
                SignedUpAt = now,
                SignedUpByMemberId = actingMemberId,
                // SNAPSHOTTA VALET, samma skal som for en medlem - se ClubEventParticipant.FeeLabel.
                FeePriceId = chosen?.Id,
                FeeLabel = chosen?.Label,
                FeeAmount = chosen?.Amount,
                CreatedDate = now,
                UpdatedDate = now
            };
            await db.InsertAsync(row);

            var roster = await BuildRosterAsync(ctx);
            bool reserve = roster.Rows.Any(r => r.Id == row.Id && r.IsReserve);
            return (true, null, reserve);
        }

        /// <summary>
        /// Withdraw. The row survives — the fee snapshot and the history hang off it.
        ///
        /// <para><b>⚠️⚠️ AVBOKAR HELA SÄLLSKAPET.</b> Avbokar Hugo sig själv följer frun och sonen
        /// med. De hänger på hans anmälan och kan inte stå kvar utan den: platserna hade varit
        /// upptagna av personer ingen ansvarar för, och avgiften hade fakturerats någon som inte
        /// kommer. Vill han avboka bara sonen finns <see cref="CancelGuestAsync"/>.</para>
        /// </summary>
        public async Task<(bool Ok, string? Message, int GuestsCancelled)> CancelAsync(
            int eventId, int memberId, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var row = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND MemberId = @1", eventId, memberId);
            if (row == null || row.SignedUpAt == null) return (false, "Ingen anmälan att avboka.", 0);
            if (row.CancelledAt != null) return (false, "Anmälan är redan avbokad.", 0);

            var now = DateTime.Now;
            row.CancelledAt = now;
            row.CancelledByMemberId = actingMemberId;
            row.UpdatedDate = now;
            await db.UpdateAsync(row);

            var guests = await db.FetchAsync<ClubEventParticipant>(
                "WHERE EventId = @0 AND GuestOfMemberId = @1 AND CancelledAt IS NULL", eventId, memberId);
            foreach (var g in guests)
            {
                g.CancelledAt = now;
                g.CancelledByMemberId = actingMemberId;
                g.UpdatedDate = now;
                await db.UpdateAsync(g);
            }

            return (true, null, guests.Count);
        }

        /// <summary>
        /// Avboka EN gäst utan att röra medlemmens egen anmälan.
        ///
        /// <para>⚠️ Adresseras på radens id, inte på ett namn: två gäster kan heta likadant hos
        /// olika medlemmar, och ett namn är inte en nyckel.</para>
        ///
        /// <para>Behörigheten avgörs här och inte i kontrollern: den ansvariga medlemmen, eller
        /// någon som får administrera evenemanget. En tredje medlem får aldrig avboka någon annans
        /// gäst.</para>
        /// </summary>
        public async Task<(bool Ok, string? Message)> CancelGuestAsync(
            ClubEventContext ctx, int participantId, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var row = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE Id = @0 AND EventId = @1", participantId, ctx.EventId);
            if (row == null) return (false, "Gästen hittades inte.");
            if (!row.IsGuest) return (false, "Raden är en medlems egen anmälan, inte en gäst.");
            if (row.CancelledAt != null) return (false, "Gästen är redan avbokad.");

            if (row.GuestOfMemberId != actingMemberId && !await CanManageAsync(ctx, actingMemberId))
                return (false, "Du kan bara avboka dina egna gäster.");

            row.CancelledAt = DateTime.Now;
            row.CancelledByMemberId = actingMemberId;
            row.UpdatedDate = row.CancelledAt.Value;
            await db.UpdateAsync(row);
            return (true, null);
        }

        /// <summary>
        /// Vad en medlem har med sig och vad det kostar — medlemmens egen rad plus gästerna.
        ///
        /// <para><b>⚠️ Summan läses ur radernas snapshots</b> (<c>FeeAmount</c>), aldrig ur
        /// evenemangets nuvarande prisrader. Höjer arrangören priset efteråt ska sällskapet stå
        /// kvar på det de sa ja till, och en omräkning mot nuläget hade tyst ändrat överenskommelsen
        /// — samma regel som verifikationsradens belopp.</para>
        /// </summary>
        /// <param name="payments">
        /// Evenemangets betalningar ur liggaren, eller null när betalning inte är påslagen.
        ///
        /// <para><b>⚠️ SKICKAS IN, hämtas inte här.</b> Metoden är ren och enhetstestad; ett
        /// databasanrop inuti hade gjort varje påstående om summan beroende av en riktig liggare,
        /// och då hade reglerna bara gått att mäta genom hela stacken.</para>
        /// </param>
        public static ClubEventParty BuildParty(
            ClubEventRoster roster, int memberId,
            IEnumerable<HpskSite.Models.Ledger.LedgerPayment>? payments = null)
        {
            var party = new ClubEventParty { MemberId = memberId };

            party.Self = roster.Rows.FirstOrDefault(r => !r.IsGuest && r.MemberId == memberId && !r.Cancelled);
            party.Guests = roster.Rows
                .Where(r => r.IsGuest && r.GuestOfMemberId == memberId && !r.Cancelled)
                .ToList();

            var all = (party.Self != null ? new[] { party.Self } : Array.Empty<ClubEventRosterRow>())
                .Concat(party.Guests)
                .ToList();

            party.People = all.Count;

            // ⚠️ Summera bara over rader som FAKTISKT bar ett belopp. Ett null ar inte noll kronor.
            party.Total = all.Where(r => r.FeeAmount.HasValue).Sum(r => r.FeeAmount!.Value);

            // ⚠️⚠️ ETT SAKNAT BELOPP BETYDER TVA HELT OLIKA SAKER, och skillnaden ar evenemangets,
            // inte radens: pa ett GRATIS evenemang ar null helt ratt och sallskapet ar fardigt, pa
            // ett evenemang MED prisrader ar null en rad som annu inte valt och summan ar darmed
            // inte hela sanningen. Utan fragan till kontexten hade varje gratis evenemang flaggats
            // som ofullstandigt, och da slutar folk att tro pa flaggan nar den val betyder nagot.
            var prices = roster.Context.Prices;
            bool eventCharges = prices.Unreadable || prices.Rows.Count > 0;
            party.MissingPrice = eventCharges && all.Any(r => !r.FeeAmount.HasValue);

            // ── Betalningarna ───────────────────────────────────────────────────────────────
            // ⚠️ Filtrerat på BETALAREN, inte på raden. Hugo swishar en gång för hela sällskapet —
            // en betalning per rad hade gett tre QR-koder för en överföring.
            //
            // ⚠️⚠️ Makulerade räknas bort FÖRST. En makulerad betalning är inte pengar, och att
            // låta den ligga kvar i summan hade gjort en anmälan giltig på en betalning arrangören
            // uttryckligen strukit.
            var mine = (payments ?? Enumerable.Empty<HpskSite.Models.Ledger.LedgerPayment>())
                .Where(p => p.PayerMemberId == memberId && p.VoidedUtc is null)
                .ToList();

            // ⚠️ Bekräftade räknas på SettledAmount (det arrangören faktiskt tog emot), påstådda på
            // det begärda beloppet — ett påstående har inget mottaget belopp att tala om.
            party.ConfirmedPaid = mine.Where(p => p.IsMoney).Sum(p => p.SettledAmount);
            party.ClaimedPaid = mine.Where(p => p.IsClaimedOnly).Sum(p => p.Amount);

            // ⚠️⚠️ ATT RADEN FINNS BETYDER ATT KODEN VISATS. `StartPayment` skapar raden och
            // returnerar QR-uppgifterna i SAMMA anrop — det finns ingen väg att få en betalningsrad
            // utan att Swish-uppgifterna lämnats ut. Därför behövs ingen egen "presenterad"-kolumn,
            // och därmed ingen migrering på en liggartabell som redan står i prod.
            //
            // ⚠️ Bekräftade räknas på det MOTTAGNA beloppet ovan, men här på det BEGÄRDA: frågan är
            // vad medlemmen ombetts betala, inte vad som kom in. Betalade hen 400 av 450 har hen
            // ändå fått medlet att betala 450.
            party.AmountPresented = mine.Sum(p => p.Amount);

            return party;
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
        /// Pricka av EN rad, adresserad på radens id.
        ///
        /// <para><b>⚠️⚠️ DEN ENDA VÄGEN ATT PRICKA AV EN GÄST.</b> <see cref="SetAttendanceAsync"/>
        /// slår upp raden på <c>MemberId</c>, och varje gäst bär
        /// <see cref="ClubEvents.GuestMemberId"/> (0) — uppslaget hade alltså antingen vägrats eller,
        /// värre, träffat en annan gästs rad på samma evenemang. Utan den här metoden kan en
        /// funktionär inte pricka av frun och sonen, och då är hela poängen med att gästerna är egna
        /// rader borta.</para>
        ///
        /// <para>Skapar aldrig en rad: en gäst måste redan vara anmäld av sin medlem. Den som dyker
        /// upp oanmäld går via <see cref="AddGuestAsync"/>.</para>
        /// </summary>
        public async Task<(bool Ok, string? Message)> SetAttendanceForRowAsync(
            int eventId, int participantId, string? status, string? note, int actingMemberId)
        {
            if (status != null && !ClubEvents.IsAttendanceStatus(status))
                return (false, "Ogiltig närvarostatus.");

            using var db = _databaseFactory.CreateDatabase();
            var row = await db.SingleOrDefaultAsync<ClubEventParticipant>(
                "WHERE Id = @0 AND EventId = @1", participantId, eventId);
            if (row == null) return (false, "Deltagaren hittades inte.");

            var now = DateTime.Now;
            row.AttendanceStatus = status;
            row.AttendanceNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            row.RecordedByMemberId = status == null ? null : actingMemberId;
            row.RecordedAt = status == null ? null : now;
            row.UpdatedDate = now;
            await db.UpdateAsync(row);
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

        /// <summary>
        /// Vem som får anmäla sig — <see cref="EventAudience"/>. <b>Alltid ett känt värde</b>, för
        /// <c>Normalise</c> körs vid inläsningen; standard och reserv är <c>Club</c>.
        /// </summary>
        public string Audience { get; set; } = EventAudience.Club;

        /// <summary>
        /// Finns doctype-egenskapen? <b>⚠️ Skild från <see cref="Audience"/>, för de svarar på
        /// olika saker:</b> en saknad egenskap och ett medvetet valt "klubbens medlemmar" läses
        /// båda som <c>Club</c>, men bara det första betyder att en sparning skulle rinna ut i
        /// sanden. Utan den här flaggan kan gränssnittet inte skilja dem åt.
        /// </summary>
        public bool AudiencePropertyExists { get; set; }

        /// <summary>
        /// Swish-numret pengarna ska till — händelsens eget, annars ägarens. Tomt = klubben har
        /// inget nummer, och då kan ingen Swish-betalning erbjudas.
        /// </summary>
        public string SwishNumber { get; set; } = "";

        /// <summary>Numret kom från klubben/kretsen och inte från händelsen. Värt att säga på
        /// arrangörens skärm: annars ser fältet tomt ut och hen tror att betalning saknas.</summary>
        public bool SwishFromOwner { get; set; }

        /// <summary>Finns doctype-egenskapen på HÄNDELSEN? Ägarens nummer fungerar ändå — det här
        /// säger bara om åsidosättningen går att spara.</summary>
        public bool SwishPropertyExists { get; set; }

        /// <summary>Kan evenemanget ta betalt via Swish? Kräver både ett nummer och en avgift.</summary>
        public bool CanTakeSwish => SwishNumber.Length > 0 && Prices.Rows.Count > 0;
        /// <summary>
        /// Evenemangets prisrader. Tom lista = ingen avgift; <c>Unreadable</c> = gar inte att lasa,
        /// vilket ALDRIG far renderas som gratis.
        /// </summary>
        public EventPriceList Prices { get; set; } = new();

        /// <summary>
        /// Det ENDA priset, nar evenemanget bara har ett. Null nar det saknas avgift OCH nar det
        /// finns flera - i det senare fallet ar det deltagaren som valjer, och att plocka det forsta
        /// hade debiterat efter en rad ingen pekat pa.
        /// </summary>
        public decimal? SinglePrice => Prices.IsSingle ? Prices.Rows[0].Amount : (decimal?)null;

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

    /// <summary>
    /// En medlem och de hen tagit med sig, med vad sällskapet kostar tillsammans.
    /// <b>En vy, ingen lagring</b> — sanningen är raderna.
    /// </summary>
    public class ClubEventParty
    {
        public int MemberId { get; set; }

        /// <summary>Medlemmens egen rad, eller null när hen inte är anmäld (och då kan hen heller
        /// inte ha gäster — se <c>AddGuestAsync</c>).</summary>
        public ClubEventRosterRow? Self { get; set; }

        public List<ClubEventRosterRow> Guests { get; set; } = new();

        /// <summary>Antal personer, medlemmen inräknad. <b>Det här är antalet PLATSER sällskapet
        /// tar</b>, vilket är hela skälet att gästerna är rader.</summary>
        public int People { get; set; }

        /// <summary>Summan av radernas snapshottade belopp. Aldrig omräknad mot evenemangets
        /// nuvarande priser.</summary>
        public decimal Total { get; set; }

        /// <summary>Någon i sällskapet saknar belopp på ett evenemang som tar avgift, alltså är
        /// <see cref="Total"/> inte hela sanningen. <b>Falskt på gratis evenemang</b>, där saknat
        /// belopp är det normala och rätta.</summary>
        public bool MissingPrice { get; set; }

        public bool HasGuests => Guests.Count > 0;

        // ── Betalningen ─────────────────────────────────────────────────────────────────────

        /// <summary>Bekräftade betalningar — pengar som FAKTISKT kommit, enligt arrangören.</summary>
        public decimal ConfirmedPaid { get; set; }

        /// <summary>
        /// Påstådda men obekräftade betalningar.
        ///
        /// <para><b>⚠️⚠️ DET HÄR ÄR INTE PENGAR.</b> Vi har ingen Swish-API och ingen callback, så
        /// "jag har betalat" är allt vi vet. Summan hålls SKILD från <see cref="ConfirmedPaid"/>
        /// just därför — slås de ihop kan arrangörens avprickningslista inte skilja den som
        /// betalat från den som sagt det, och då är listan inte längre en kontroll.</para>
        /// </summary>
        public decimal ClaimedPaid { get; set; }

        /// <summary>
        /// Belopp som medlemmen har FÅTT MEDLET ATT BETALA — en betalningsrad finns, alltså har
        /// Swish-koden visats (eller mejlats). Räknar även påstådda och bekräftade, som per
        /// definition har passerat det steget.
        ///
        /// <para><b>⚠️⚠️ DET HÄR ÄR SPÄRRENS TAL, och det är INTE pengar.</b> Stefans regel
        /// 2026-09-18: <i>"betalningen behöver inte bekräftas, men swish-koden måste ha visats
        /// eller mailats, annars kan vi inte förutsätta att det har betalats."</i> Har koden aldrig
        /// visats har medlemmen aldrig fått en chans att betala, och då är det inte rimligt att
        /// hålla hen till betalningen.</para>
        /// </summary>
        public decimal AmountPresented { get; set; }

        /// <summary>
        /// ⚠️⚠️ SPÄRREN: är anmälan giltig? Gratis sällskap alltid; annars krävs att Swish-koden
        /// visats för hela summan.
        ///
        /// <para><b>Skild från <see cref="IsSettled"/> med flit.</b> Den ena frågan är "får den här
        /// personen stå på listan", den andra är "har pengarna kommit". Drevs båda av samma tal
        /// vore antingen anmälan ogiltig tills arrangören hunnit stämma av — alltså varje
        /// kvällsanmälan ogiltig till dagen efter — eller så vore arrangörens avprickningslista
        /// blind för dem som aldrig betalade.</para>
        /// </summary>
        public bool IsPaymentPresented => Total <= 0m || AmountPresented >= Total;

        /// <summary>
        /// Vad arrangören fortfarande väntar på. <b>Bara BEKRÄFTADE pengar räknas bort</b> — ett
        /// påstående och en visad QR är inte pengar, och den som ska stämma av ett bankkonto måste
        /// se skillnaden.
        /// </summary>
        public decimal Outstanding => Math.Max(0m, Total - ConfirmedPaid);

        /// <summary>Pengarna har kommit och är bokförda.</summary>
        public bool IsSettled => Outstanding <= 0m;

        /// <summary>
        /// Vad en NY betalningsbegäran ska gälla.
        ///
        /// <para>⚠️ Drar bort både bekräftat och PÅSTÅTT: har medlemmen sagt att hen betalat 270 och
        /// sedan lägger till en gäst för 180, ska nästa QR gälla 180 — inte 450. En begäran på hela
        /// summan hade bett hen betala det hon redan sagt sig ha betalat.</para>
        /// </summary>
        public decimal RemainingToRequest => Math.Max(0m, Total - ConfirmedPaid - ClaimedPaid);

        /// <summary>Medlemmen har sagt att hen betalat, men arrangören har inte stämt av.</summary>
        public bool AwaitingConfirmation => ClaimedPaid > 0m && ConfirmedPaid < Total;
    }

    public class ClubEventRosterRow
    {
        /// <summary>Radens id. <b>Adressen till en gäst</b>, som inte har något MemberId att
        /// pekas ut med.</summary>
        public int Id { get; set; }

        public int MemberId { get; set; }

        /// <summary>Personen har inget konto — en anhörig eller gäst på <see cref="GuestOfMemberId"/>:s
        /// ansvar.</summary>
        public bool IsGuest { get; set; }

        /// <summary>Medlemmen som ansvarar för gästens plats och avgift. Null för en medlems egen rad.</summary>
        public int? GuestOfMemberId { get; set; }

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

        /// <summary>Vilken prisrad deltagaren valde. Snapshot - se ClubEventParticipant.FeeLabel.</summary>
        public string? FeePriceId { get; set; }

        public string? FeeLabel { get; set; }
    }
}
