using HpskSite.Models;
using HpskSite.Models.Events;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// Händelserna i tabellen <c>ClubEvent</c> (sajtstrukturen fas 2c), med adresshistorik i
    /// <c>PageAddress</c> (C3-planen S6, delad med tävlingarna och serierna; förut <c>ClubEventUrl</c>) och sidans block i <c>LandingPage</c>. Väljs av växeln <c>Events:Store</c>.
    ///
    /// <para><b>Adressen är <c>/{ägarens adress}/{år}/{slug}/</c>.</b> Året och sluggen sätts när
    /// händelsen skapas; byter den namn eller år får den en NY aktuell adress och den gamla står kvar
    /// i historiken, så att <see cref="HpskSite.Routing.PageAddressContentFinder"/> kan 301:a den.
    /// En krock inom samma ägare och år ger <c>-2</c>, <c>-3</c> …</para>
    ///
    /// <para><b>Inget Umbraco-lås.</b> Att skapa en händelse är en tabellrad, inte en nodsparning som
    /// köar i det globala innehållslåset.</para>
    /// </summary>
    public class SqlClubEventStore : IClubEventStore
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IShortStringHelper _shortStrings;
        private readonly ILogger<SqlClubEventStore> _logger;
        private readonly HpskSite.Services.Addresses.PageAddressService _addresses;

        public SqlClubEventStore(IScopeProvider scopeProvider, IUmbracoContextAccessor umbracoContextAccessor,
            IShortStringHelper shortStrings, ILogger<SqlClubEventStore> logger,
            HpskSite.Services.Addresses.PageAddressService addresses)
        {
            _addresses = addresses;
            _scopeProvider = scopeProvider;
            _umbracoContextAccessor = umbracoContextAccessor;
            _shortStrings = shortStrings;
            _logger = logger;
        }

        public ClubEventFieldSupport Support => ClubEventFieldSupport.All;

        private const string Columns = @"e.Id, e.OwnerType, e.OwnerId, e.Name, e.PageName, e.Slug, e.UrlYear, e.StartsAt, e.EndsAt,
e.EventType, e.Venue, e.Description, e.ImageRef, e.ContactPerson, e.ContactEmail, e.ContactPhone, e.EquipmentRequired,
e.TargetAudience, e.IsActive, e.IsPublished, e.RegistrationRequired, e.RegistrationUrl, e.MaxParticipants,
e.RegistrationDeadline, e.IsMandatory, e.LanevapenOffered, e.PricesJson, e.Audience, e.SwishNumber, e.LandingPageId,
e.CreatedDate, e.UpdatedDate, lp.ContentBlocksJson, lp.QuickLinksJson";

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public ClubEventRecord? Get(int id)
            => id <= 0 ? null : Load("WHERE e.Id = @0", id).FirstOrDefault();

        public IReadOnlyList<ClubEventRecord> GetMany(IReadOnlyCollection<int> ids)
        {
            var result = new List<ClubEventRecord>();
            // I portioner: IN (@0) blir en parameter per id, och SQL Server tar slut vid ~2100.
            foreach (var chunk in ids.Where(i => i > 0).Distinct().Chunk(1000))
                result.AddRange(Load("WHERE e.Id IN (@0)", chunk.ToList()));
            return result;
        }

        public IReadOnlyList<ClubEventRecord> ListForOwner(int ownerId, bool publishedOnly = true)
            => ownerId <= 0
                ? Array.Empty<ClubEventRecord>()
                : Load(publishedOnly ? "WHERE e.OwnerId = @0 AND e.IsPublished = 1" : "WHERE e.OwnerId = @0", ownerId);

        public IReadOnlyList<ClubEventRecord> ListPublishedBetween(DateTime from, DateTime to)
            => Load("WHERE e.IsPublished = 1 AND e.StartsAt >= @0 AND e.StartsAt < @1", from, to);

        public IReadOnlyList<ClubEventRecord> ListAllForOwnerType(string ownerType) => Load("WHERE e.OwnerType = @0", ownerType);

        /// <summary>
        /// Händelsen bakom en adress, och om adressen är den aktuella. Årtal null = den gamla
        /// trädadressen från tiden som nod.
        ///
        /// <para><b>Ägartypen följer med, och årtalet är två frågor</b> (genomgången 2026-10-13, B). Indexet
        /// <c>UX_ClubEventUrl_Address</c> börjar på (OwnerType, OwnerId, UrlYear, Slug); utan OwnerType och med
        /// <c>(@1 IS NULL AND … ) OR …</c> kunde det inte sökas, och varje förfrågan under en klubb läste
        /// hela adresstabellen.</para>
        /// </summary>
        public (int EventId, bool IsCurrent)? FindByAddress(string ownerType, int ownerId, int? urlYear, string slug)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = urlYear == null
                ? scope.Database.FirstOrDefault<AddressRow>(@"
SELECT TOP 1 u.TargetId AS EventId, u.IsCurrent FROM dbo.PageAddress u
WHERE u.OwnerType = @0 AND u.OwnerId = @1 AND u.UrlYear IS NULL AND u.Slug = @2 AND u.TargetKind = 'event'", ownerType, ownerId, slug)
                : scope.Database.FirstOrDefault<AddressRow>(@"
SELECT TOP 1 u.TargetId AS EventId, u.IsCurrent FROM dbo.PageAddress u
WHERE u.OwnerType = @0 AND u.OwnerId = @1 AND u.UrlYear = @2 AND u.Slug = @3 AND u.TargetKind = 'event'", ownerType, ownerId, urlYear.Value, slug);
            return row == null ? null : (row.EventId, row.IsCurrent);
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public Task<(int Id, string? Error)> SaveAsync(ClubEventRecord r)
        {
            ArgumentNullException.ThrowIfNull(r);
            if (r.OwnerId <= 0 || (r.OwnerType != ClubEventRecord.OwnerClub && r.OwnerType != ClubEventRecord.OwnerRegion))
                return Task.FromResult((0, (string?)"Händelsen saknar ägare."));

            var now = DateTime.Now;
            var addressChanged = false;
            (int, string?) result;
            // ⚠️ Scopet stängs (= transaktionen skrivs) FÖRE adresscachen töms — Complete() skriver ingenting själv.
            using (var scope = _scopeProvider.CreateScope())
            {
                result = SaveInScope(scope, r, now, ref addressChanged);
            }
            if (addressChanged) _addresses.Invalidate();
            return Task.FromResult(result);
        }

        private (int, string?) SaveInScope(IScope scope, ClubEventRecord r, DateTime now, ref bool addressChanged)
        {
            var db = scope.Database;
            int id;
            int? landingPageId = null;

            var existing = r.Id > 0 ? db.FirstOrDefault<RowData>($"SELECT {Columns} FROM dbo.ClubEvent e LEFT JOIN dbo.LandingPage lp ON lp.Id = e.LandingPageId WHERE e.Id = @0", r.Id) : null;
            if (r.Id > 0 && existing == null) return (0, "Händelsen hittades inte.");

            var year = UrlYearFor(r, existing?.CreatedDate ?? now);
            var baseSlug = SlugFor(r);

            // Sidan: skapas vid första blocket eller länken, och finns sedan kvar.
            var hasPage = !string.IsNullOrWhiteSpace(r.ContentBlocksJson) && r.ContentBlocksJson.Trim() != "[]"
                          || !string.IsNullOrWhiteSpace(r.QuickLinksJson) && r.QuickLinksJson.Trim() != "[]";
            landingPageId = existing?.LandingPageId;
            if (landingPageId.HasValue)
            {
                db.Execute("UPDATE dbo.LandingPage SET ContentBlocksJson = @1, QuickLinksJson = @2, UpdatedUtc = SYSUTCDATETIME() WHERE Id = @0",
                    landingPageId.Value, Blank(r.ContentBlocksJson), Blank(r.QuickLinksJson));
            }
            else if (hasPage)
            {
                landingPageId = db.ExecuteScalar<int>(@"
INSERT INTO dbo.LandingPage (OwnerType, OwnerId, ContentBlocksJson, QuickLinksJson) VALUES (@0, @1, @2, @3);
SELECT CAST(SCOPE_IDENTITY() AS INT);", r.OwnerType, r.OwnerId, Blank(r.ContentBlocksJson), Blank(r.QuickLinksJson));
            }

            if (existing == null)
            {
                var slug = UniqueSlug(db, r.OwnerType, r.OwnerId, year, baseSlug, exceptId: 0);
                id = db.ExecuteScalar<int>(@"
INSERT INTO dbo.ClubEvent
    (OwnerType, OwnerId, Name, PageName, Slug, UrlYear, StartsAt, EndsAt, EventType, Venue, Description, ImageRef,
     ContactPerson, ContactEmail, ContactPhone, EquipmentRequired, TargetAudience, IsActive, IsPublished,
     RegistrationRequired, RegistrationUrl, MaxParticipants, RegistrationDeadline, IsMandatory, LanevapenOffered,
     PricesJson, Audience, SwishNumber, LandingPageId, CreatedDate, UpdatedDate)
VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18,@19,@20,@21,@22,@23,@24,@25,@26,@27,@28,@29,@29);
SELECT CAST(SCOPE_IDENTITY() AS INT);",
                    r.OwnerType, r.OwnerId, r.Name ?? "", PageNameOf(r), slug, year, r.StartsAt, r.EndsAt, r.EventType ?? "",
                    Blank(r.Venue), Blank(r.Description), Blank(r.ImageRef), Blank(r.ContactPerson), Blank(r.ContactEmail),
                    Blank(r.ContactPhone), Blank(r.EquipmentRequired), Blank(r.TargetAudience), r.IsActive, r.IsPublished,
                    r.RegistrationRequired, Blank(r.RegistrationUrl), Math.Max(0, r.MaxParticipants), r.RegistrationDeadline?.Date,
                    r.IsMandatory, r.LanevapenOffered, Blank(r.PricesJson), EventAudience.Normalise(r.Audience),
                    Blank(r.SwishNumber?.Trim()), landingPageId, r.CreatedDate == default ? now : r.CreatedDate);
                db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES (@0, @1, @2, @3, 'event', @4, 1)",
                    r.OwnerType, r.OwnerId, year, slug, id);
                addressChanged = true;
            }
            else
            {
                id = r.Id;
                // Ny adress bara när året eller sluggen faktiskt ändrats — en rättad beskrivning ska inte
                // ge händelsen en ny länk.
                var slug = existing.Slug;
                var urlYear = existing.UrlYear;
                if (year != existing.UrlYear || !string.Equals(baseSlug, BaseOf(existing.Slug), StringComparison.Ordinal))
                {
                    // Adresstabellen delas med tävlingarna och serierna (C3-planen S6): sluggen väljs där.
                    slug = HpskSite.Services.Addresses.PageAddressService.SetCurrent(db, HpskSite.Services.Addresses.PageAddressKinds.Event, id,
                        new HpskSite.Services.Addresses.PageAddressOwner(r.OwnerType, r.OwnerId), year, baseSlug).Slug;
                    urlYear = year;
                    addressChanged = true;
                }
                db.Execute(@"
UPDATE dbo.ClubEvent SET
    Name = @1, PageName = @2, Slug = @3, UrlYear = @4, StartsAt = @5, EndsAt = @6, EventType = @7, Venue = @8,
    Description = @9, ImageRef = @10, ContactPerson = @11, ContactEmail = @12, ContactPhone = @13,
    EquipmentRequired = @14, TargetAudience = @15, IsActive = @16, IsPublished = @17, RegistrationRequired = @18,
    RegistrationUrl = @19, MaxParticipants = @20, RegistrationDeadline = @21, IsMandatory = @22, LanevapenOffered = @23,
    PricesJson = @24, Audience = @25, SwishNumber = @26, LandingPageId = @27, UpdatedDate = @28
WHERE Id = @0",
                    id, r.Name ?? "", PageNameOf(r), slug, urlYear, r.StartsAt, r.EndsAt, r.EventType ?? "", Blank(r.Venue),
                    Blank(r.Description), Blank(r.ImageRef), Blank(r.ContactPerson), Blank(r.ContactEmail), Blank(r.ContactPhone),
                    Blank(r.EquipmentRequired), Blank(r.TargetAudience), r.IsActive, r.IsPublished, r.RegistrationRequired,
                    Blank(r.RegistrationUrl), Math.Max(0, r.MaxParticipants), r.RegistrationDeadline?.Date, r.IsMandatory,
                    r.LanevapenOffered, Blank(r.PricesJson), EventAudience.Normalise(r.Audience), Blank(r.SwishNumber?.Trim()),
                    landingPageId, now);
            }

            scope.Complete();
            r.Id = id;
            return (id, null);
        }

        public bool Delete(int id)
        {
            if (id <= 0) return false;
            using var scope = _scopeProvider.CreateScope();
            var page = scope.Database.ExecuteScalar<int?>("SELECT LandingPageId FROM dbo.ClubEvent WHERE Id = @0", id);
            // Adresshistoriken: PageAddress har ingen främmande nyckel (den bär flera sorter) och tas bort här.
            var n = scope.Database.Execute("DELETE FROM dbo.ClubEvent WHERE Id = @0", id);
            if (n > 0) HpskSite.Services.Addresses.PageAddressService.DeleteFor(scope.Database, HpskSite.Services.Addresses.PageAddressKinds.Event, id);
            if (n > 0 && page.HasValue)
                scope.Database.Execute(@"DELETE FROM dbo.LandingPage WHERE Id = @0
                    AND NOT EXISTS (SELECT 1 FROM dbo.ClubEvent WHERE LandingPageId = @0)", page.Value);
            scope.Complete();
            return n > 0;
        }

        public bool SetPublished(int id, bool published)
        {
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute("UPDATE dbo.ClubEvent SET IsPublished = @1, UpdatedDate = @2 WHERE Id = @0", id, published, DateTime.Now);
            scope.Complete();
            return n > 0;
        }

        // ── Adressen ───────────────────────────────────────────────────────────────────────

        /// <summary>Startdatumets år, annars skapandeåret.</summary>
        public static int UrlYearFor(ClubEventRecord r, DateTime created)
            => r.StartsAt?.Year ?? (r.CreatedDate == default ? created : r.CreatedDate).Year;

        private string SlugFor(ClubEventRecord r)
        {
            var slug = _shortStrings.CleanStringForUrlSegment(PageNameOf(r));
            return string.IsNullOrWhiteSpace(slug) ? "handelse" : slug;
        }

        private static string PageNameOf(ClubEventRecord r) => string.IsNullOrWhiteSpace(r.PageName) ? (r.Name ?? "") : r.PageName;

        /// <summary>"gasaskjutning-2" → "gasaskjutning".</summary>
        private static string BaseOf(string slug)
        {
            var i = slug.LastIndexOf('-');
            return i > 0 && int.TryParse(slug[(i + 1)..], out var n) && n >= 2 ? slug[..i] : slug;
        }

        private static string UniqueSlug(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
            string ownerType, int ownerId, int year, string baseSlug, int exceptId)
        {
            var taken = db.Fetch<string>(@"
SELECT Slug FROM dbo.PageAddress WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2 AND NOT (TargetKind = 'event' AND TargetId = @3)
UNION SELECT Slug FROM dbo.ClubEvent WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear = @2 AND Id <> @3",
                ownerType, ownerId, year, exceptId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(baseSlug)) return baseSlug;
            for (var n = 2; ; n++)
                if (!taken.Contains($"{baseSlug}-{n}")) return $"{baseSlug}-{n}";
        }

        private string OwnerUrl(int ownerId)
        {
            try
            {
                return _umbracoContextAccessor.TryGetUmbracoContext(out var ctx)
                    ? ctx.Content?.GetById(ownerId)?.Url() ?? ""
                    : "";
            }
            catch { return ""; }
        }

        private string ImageUrl(string? imageRef)
        {
            if (string.IsNullOrWhiteSpace(imageRef)) return "";
            try
            {
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Media == null) return "";
                if (UdiParser.TryParse(imageRef.Trim(), out GuidUdi? udi) && udi != null)
                    return ctx.Media.GetById(udi.Guid)?.Url() ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Händelsebilden {Ref} kunde inte lösas upp", imageRef);
            }
            return "";
        }

        // ── Mappning ───────────────────────────────────────────────────────────────────────

        private List<ClubEventRecord> Load(string where, params object[] args)
        {
            List<RowData> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                rows = scope.Database.Fetch<RowData>(
                    $"SELECT {Columns} FROM dbo.ClubEvent e LEFT JOIN dbo.LandingPage lp ON lp.Id = e.LandingPageId {where} ORDER BY e.StartsAt, e.Id", args);
            }
            var ownerUrls = new Dictionary<int, string>();
            return rows.Select(x =>
            {
                if (!ownerUrls.TryGetValue(x.OwnerId, out var ownerUrl)) ownerUrls[x.OwnerId] = ownerUrl = OwnerUrl(x.OwnerId);
                return new ClubEventRecord
                {
                    Id = x.Id,
                    OwnerType = x.OwnerType,
                    OwnerId = x.OwnerId,
                    Name = x.Name ?? "",
                    PageName = x.PageName ?? "",
                    StartsAt = x.StartsAt,
                    EndsAt = x.EndsAt,
                    EventType = x.EventType ?? "",
                    Venue = x.Venue ?? "",
                    Description = x.Description ?? "",
                    ImageRef = x.ImageRef ?? "",
                    ImageUrl = ImageUrl(x.ImageRef),
                    ContactPerson = x.ContactPerson ?? "",
                    ContactEmail = x.ContactEmail ?? "",
                    ContactPhone = x.ContactPhone ?? "",
                    EquipmentRequired = x.EquipmentRequired ?? "",
                    TargetAudience = x.TargetAudience ?? "",
                    IsActive = x.IsActive,
                    IsPublished = x.IsPublished,
                    RegistrationRequired = x.RegistrationRequired,
                    RegistrationUrl = x.RegistrationUrl ?? "",
                    MaxParticipants = x.MaxParticipants,
                    RegistrationDeadline = x.RegistrationDeadline,
                    IsMandatory = x.IsMandatory,
                    LanevapenOffered = x.LanevapenOffered,
                    PricesJson = x.PricesJson ?? "",
                    Audience = EventAudience.Normalise(x.Audience),
                    SwishNumber = x.SwishNumber ?? "",
                    ContentBlocksJson = x.ContentBlocksJson ?? "",
                    QuickLinksJson = x.QuickLinksJson ?? "",
                    Url = string.IsNullOrEmpty(ownerUrl) ? "" : $"{ownerUrl.TrimEnd('/')}/{x.UrlYear}/{x.Slug}/",
                    CreatedDate = x.CreatedDate,
                    UpdatedDate = x.UpdatedDate
                };
            }).ToList();
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private sealed class AddressRow
        {
            public int EventId { get; set; }
            public bool IsCurrent { get; set; }
        }

        private sealed class RowData
        {
            public int Id { get; set; }
            public string OwnerType { get; set; } = "";
            public int OwnerId { get; set; }
            public string? Name { get; set; }
            public string? PageName { get; set; }
            public string Slug { get; set; } = "";
            public int UrlYear { get; set; }
            public DateTime? StartsAt { get; set; }
            public DateTime? EndsAt { get; set; }
            public string? EventType { get; set; }
            public string? Venue { get; set; }
            public string? Description { get; set; }
            public string? ImageRef { get; set; }
            public string? ContactPerson { get; set; }
            public string? ContactEmail { get; set; }
            public string? ContactPhone { get; set; }
            public string? EquipmentRequired { get; set; }
            public string? TargetAudience { get; set; }
            public bool IsActive { get; set; }
            public bool IsPublished { get; set; }
            public bool RegistrationRequired { get; set; }
            public string? RegistrationUrl { get; set; }
            public int MaxParticipants { get; set; }
            public DateTime? RegistrationDeadline { get; set; }
            public bool IsMandatory { get; set; }
            public bool LanevapenOffered { get; set; }
            public string? PricesJson { get; set; }
            public string? Audience { get; set; }
            public string? SwishNumber { get; set; }
            public int? LandingPageId { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime UpdatedDate { get; set; }
            public string? ContentBlocksJson { get; set; }
            public string? QuickLinksJson { get; set; }
        }
    }
}
