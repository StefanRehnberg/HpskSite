using HpskSite.Models;
using HpskSite.Models.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// Händelserna som Umbraco-noder: <c>club|regionalPage › clubSimpleEvent</c> (sajtstrukturen fas 2a).
    ///
    /// <para><b>Två läsvägar, med flit.</b> Listor för besökare läser den publicerade cachen — de anropas
    /// för varje klubb på statistik- och kalendersidor, och en databasfråga per klubb hade varit för
    /// dyrt. Enstaka händelser och administratörens listor läser innehållstjänsten, eftersom en
    /// opublicerad händelse fortfarande ska gå att redigera. Båda mappas till samma post med samma regler.</para>
    ///
    /// <para><b>Skrivningen skriver alla kända fält.</b> Varje anropare läser posten, ändrar och sparar,
    /// så ett fält som anroparen inte rör har sitt lagrade värde. Fält posten inte känner (äldre
    /// <c>feeAmount</c>, <c>clubId</c>) rörs aldrig.</para>
    /// </summary>
    public class NodeClubEventStore : IClubEventStore
    {
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<NodeClubEventStore> _logger;
        private ClubEventFieldSupport? _support;

        public NodeClubEventStore(IContentService contentService, IContentTypeService contentTypeService,
            IUmbracoContextAccessor umbracoContextAccessor, ILogger<NodeClubEventStore> logger,
            IScopeProvider scopeProvider)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public ClubEventFieldSupport Support => _support ??= ReadSupport();

        private ClubEventFieldSupport ReadSupport()
        {
            var type = _contentTypeService.Get(ClubEvents.EventAlias);
            var aliases = new HashSet<string>(
                type?.CompositionPropertyTypes.Select(p => p.Alias) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            return new ClubEventFieldSupport(
                aliases.Contains(ClubEvents.MandatoryProperty),
                aliases.Contains(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty),
                aliases.Contains(ClubEvents.DeadlineProperty),
                aliases.Contains(EventPrices.Property),
                aliases.Contains(ClubEvents.SwishProperty),
                aliases.Contains(EventAudience.Property));
        }

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<ClubEventRecord> GetMany(IReadOnlyCollection<int> ids)
            => ids.Distinct().Select(Get).Where(e => e != null).Select(e => e!).ToList();

        public ClubEventRecord? Get(int id)
        {
            if (id <= 0) return null;
            var node = _contentService.GetById(id);
            if (node == null || node.Trashed || node.ContentType.Alias != ClubEvents.EventAlias) return null;
            var parent = node.ParentId > 0 ? _contentService.GetById(node.ParentId) : null;
            var ownerType = OwnerTypeOf(parent?.ContentType.Alias);
            if (ownerType == null) return null;
            var r = Map(node, ownerType, parent!.Id);
            var published = PublishedNode(id);
            r.Url = published?.Url() ?? "";
            r.ImageUrl = ImageUrlOf(published);
            return r;
        }

        public IReadOnlyList<ClubEventRecord> ListForOwner(int ownerId, bool publishedOnly = true)
        {
            if (ownerId <= 0) return Array.Empty<ClubEventRecord>();
            if (publishedOnly)
            {
                var owner = PublishedNode(ownerId);
                var ownerType = OwnerTypeOf(owner?.ContentType.Alias);
                if (owner == null || ownerType == null) return Array.Empty<ClubEventRecord>();
                return owner.Children()
                    .Where(c => c.ContentType.Alias == ClubEvents.EventAlias)
                    .Select(c => MapPublished(c, ownerType, ownerId))
                    .ToList();
            }

            var ownerNode = _contentService.GetById(ownerId);
            var type = OwnerTypeOf(ownerNode?.ContentType.Alias);
            if (ownerNode == null || type == null) return Array.Empty<ClubEventRecord>();
            return _contentService.GetPagedChildren(ownerId, 0, int.MaxValue, out _)
                .Where(c => !c.Trashed && c.ContentType.Alias == ClubEvents.EventAlias)
                .Select(c =>
                {
                    var r = Map(c, type, ownerId);
                    var p = PublishedNode(c.Id);
                    r.Url = p?.Url() ?? "";
                    r.ImageUrl = ImageUrlOf(p);
                    return r;
                })
                .ToList();
        }

        public IReadOnlyList<ClubEventRecord> ListPublishedBetween(DateTime from, DateTime to)
            => ListAllPublished().Where(e => e.StartsAt >= from && e.StartsAt < to).ToList();

        private IReadOnlyList<ClubEventRecord> ListAllPublished()
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return Array.Empty<ClubEventRecord>();
            var result = new List<ClubEventRecord>();
            foreach (var root in ctx.Content.GetAtRoot())
            {
                foreach (var owner in root.DescendantsOrSelf()
                             .Where(n => n.ContentType.Alias is ClubEvents.OwnerClubAlias or ClubEvents.OwnerRegionAlias))
                {
                    var ownerType = OwnerTypeOf(owner.ContentType.Alias)!;
                    result.AddRange(owner.Children()
                        .Where(c => c.ContentType.Alias == ClubEvents.EventAlias)
                        .Select(c => MapPublished(c, ownerType, owner.Id)));
                }
            }
            return result;
        }

        public IReadOnlyList<ClubEventRecord> ListAllForOwnerType(string ownerType)
        {
            var ownerAlias = ownerType == ClubEventRecord.OwnerRegion ? ClubEvents.OwnerRegionAlias : ClubEvents.OwnerClubAlias;
            List<(int Id, int ParentId)> ids;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                ids = scope.Database.Fetch<IdParent>(@"
SELECT n.id AS Id, n.parentId AS ParentId
FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias = @0
JOIN umbracoNode pn ON pn.id = n.parentId
JOIN umbracoContent pc ON pc.nodeId = pn.id
JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId AND pct.alias = @1
WHERE n.trashed = 0", ClubEvents.EventAlias, ownerAlias).Select(r => (r.Id, r.ParentId)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Händelserna för ägartypen {OwnerType} kunde inte läsas", ownerType);
                return Array.Empty<ClubEventRecord>();
            }
            if (ids.Count == 0) return Array.Empty<ClubEventRecord>();
            var parentOf = ids.ToDictionary(x => x.Id, x => x.ParentId);
            return _contentService.GetByIds(ids.Select(x => x.Id))
                .Where(n => !n.Trashed && n.ContentType.Alias == ClubEvents.EventAlias)
                .Select(n =>
                {
                    // Trädadressen behövs av flytten: den blir den gamla adressen som 301:as.
                    var r = Map(n, ownerType, parentOf[n.Id]);
                    var p = PublishedNode(n.Id);
                    r.Url = p?.Url() ?? "";
                    r.ImageUrl = ImageUrlOf(p);
                    return r;
                })
                .ToList();
        }

        private sealed class IdParent
        {
            public int Id { get; set; }
            public int ParentId { get; set; }
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public async Task<(int Id, string? Error)> SaveAsync(ClubEventRecord r)
        {
            ArgumentNullException.ThrowIfNull(r);
            var missing = ClubEventInput.UnsupportedValues(r, Support);
            if (missing.Count > 0) return (0, ClubEventInput.MissingPropertiesMessage(missing));

            IContent node;
            bool isNew = r.Id <= 0;
            if (isNew)
            {
                var owner = _contentService.GetById(r.OwnerId);
                if (owner == null || OwnerTypeOf(owner.ContentType.Alias) != r.OwnerType)
                    return (0, r.IsRegionOwned ? "Kretsen hittades inte." : "Klubben hittades inte.");
                var nodeName = !string.IsNullOrWhiteSpace(r.PageName) ? r.PageName : r.Name;
                node = _contentService.Create(nodeName, r.OwnerId, ClubEvents.EventAlias);
            }
            else
            {
                node = _contentService.GetById(r.Id)!;
                if (node == null || node.ContentType.Alias != ClubEvents.EventAlias)
                    return (0, "Händelsen hittades inte.");
                if (!string.IsNullOrWhiteSpace(r.PageName)) node.Name = r.PageName;
            }

            Write(node, r);

            if (r.IsPublished)
            {
                var (published, error) = await ContentPublishHelper.SaveAndPublishAsync(_contentService, node);
                if (!published)
                {
                    if (isNew)
                    {
                        // Lämna inte ett sparat men opublicerat föräldralöst utkast — det syns inte på
                        // klubbsidan, så ett nytt försök hade tyst skapat dubbletter. Läs om först: en
                        // tidsgräns hos klienten kan dölja en publicering som faktiskt gick igenom.
                        var persisted = node.Id > 0 ? _contentService.GetById(node.Id) : null;
                        if (persisted != null && persisted.Published) { r.Id = persisted.Id; return (persisted.Id, null); }
                        if (persisted != null) { try { _contentService.Delete(persisted); } catch { /* best effort */ } }
                    }
                    return (0, "Kunde inte publicera händelsen: " + error);
                }
            }
            else
            {
                var saved = _contentService.Save(node);
                if (!saved.Success) return (0, "Kunde inte spara händelsen.");
                if (node.Published) _contentService.Unpublish(node);
            }

            r.Id = node.Id;
            return (node.Id, null);
        }

        public bool Delete(int id)
        {
            var node = id > 0 ? _contentService.GetById(id) : null;
            if (node == null || node.ContentType.Alias != ClubEvents.EventAlias) return false;
            if (node.Published) _contentService.Unpublish(node);
            return _contentService.Delete(node).Success;
        }

        public bool SetPublished(int id, bool published)
        {
            var node = id > 0 ? _contentService.GetById(id) : null;
            if (node == null || node.ContentType.Alias != ClubEvents.EventAlias) return false;
            if (published) return _contentService.Publish(node, new[] { "*" }, -1).Success;
            return !node.Published || _contentService.Unpublish(node).Success;
        }

        // ── Nodformen ──────────────────────────────────────────────────────────────────────

        private static string? OwnerTypeOf(string? alias) => alias switch
        {
            ClubEvents.OwnerClubAlias => ClubEventRecord.OwnerClub,
            ClubEvents.OwnerRegionAlias => ClubEventRecord.OwnerRegion,
            _ => null
        };

        private IPublishedContent? PublishedNode(int id)
            => _umbracoContextAccessor.TryGetUmbracoContext(out var ctx) ? ctx.Content?.GetById(id) : null;

        private static string ImageUrlOf(IPublishedContent? node)
        {
            try { return node?.Value<IPublishedContent>("eventImage")?.Url() ?? ""; }
            catch { return ""; }
        }

        private static ClubEventRecord Map(IContent n, string ownerType, int ownerId) => new()
        {
            Id = n.Id,
            OwnerType = ownerType,
            OwnerId = ownerId,
            Name = n.GetValue<string>("eventName") ?? "",
            PageName = n.Name ?? "",
            StartsAt = ClubEvents.RealDate(n.GetValue<DateTime?>("eventDate")),
            EndsAt = ClubEvents.RealDate(n.GetValue<DateTime?>("eventEndDate")),
            EventType = n.GetValue<string>("eventType") ?? "",
            Venue = n.GetValue<string>("venue") ?? "",
            Description = n.GetValue<string>("description") ?? "",
            ContactPerson = n.GetValue<string>("contactPerson") ?? "",
            ContactEmail = n.GetValue<string>("contactEmail") ?? "",
            ContactPhone = n.GetValue<string>("contactPhone") ?? "",
            EquipmentRequired = n.GetValue<string>("equipmentRequired") ?? "",
            TargetAudience = n.GetValue<string>("targetAudience") ?? "",
            ImageRef = n.GetValue<string>("eventImage") ?? "",
            IsActive = !n.HasProperty("isActive") || n.GetValue<object>("isActive") == null || n.GetValue<bool>("isActive"),
            IsPublished = n.Published,
            RegistrationRequired = n.GetValue<bool>("registrationRequired"),
            RegistrationUrl = n.GetValue<string>("registrationUrl") ?? "",
            MaxParticipants = n.GetValue<int>("maxParticipants"),
            RegistrationDeadline = ClubEvents.RealDate(n.GetValue<DateTime?>(ClubEvents.DeadlineProperty)),
            IsMandatory = n.GetValue<bool>(ClubEvents.MandatoryProperty),
            LanevapenOffered = n.HasProperty(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty)
                && n.GetValue<bool>(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty),
            PricesJson = n.GetValue<string>(EventPrices.Property) ?? "",
            Audience = EventAudience.Normalise(n.GetValue<string>(EventAudience.Property)),
            SwishNumber = (n.GetValue<string>(ClubEvents.SwishProperty) ?? "").Trim(),
            ContentBlocksJson = n.GetValue<string>("contentBlocks") ?? "",
            QuickLinksJson = n.GetValue<string>("quickLinks") ?? "",
            CreatedDate = UmbracoTime.Local(n.CreateDate),
            UpdatedDate = UmbracoTime.Local(n.UpdateDate)
        };

        private static ClubEventRecord MapPublished(IPublishedContent n, string ownerType, int ownerId)
        {
            string S(string alias) => n.Value<string>(alias) ?? "";
            string imageUrl;
            try { imageUrl = n.Value<IPublishedContent>("eventImage")?.Url() ?? ""; } catch { imageUrl = ""; }
            return new ClubEventRecord
            {
                Id = n.Id,
                OwnerType = ownerType,
                OwnerId = ownerId,
                Name = S("eventName"),
                PageName = n.Name ?? "",
                StartsAt = ClubEvents.RealDate(n.Value<DateTime?>("eventDate")),
                EndsAt = ClubEvents.RealDate(n.Value<DateTime?>("eventEndDate")),
                EventType = S("eventType"),
                Venue = S("venue"),
                Description = n.Value<object>("description")?.ToString() ?? "",
                ContactPerson = S("contactPerson"),
                ContactEmail = S("contactEmail"),
                ContactPhone = S("contactPhone"),
                EquipmentRequired = S("equipmentRequired"),
                TargetAudience = S("targetAudience"),
                // Källvärdet (UDI), inte det upplösta mediet — en post ur cachen som sparas får inte
                // tappa sin bild.
                ImageRef = n.GetProperty("eventImage")?.GetSourceValue()?.ToString() ?? "",
                ImageUrl = imageUrl,
                IsActive = n.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true),
                IsPublished = true,
                RegistrationRequired = n.Value<bool>("registrationRequired"),
                RegistrationUrl = S("registrationUrl"),
                MaxParticipants = n.Value<int>("maxParticipants"),
                RegistrationDeadline = ClubEvents.RealDate(n.Value<DateTime?>(ClubEvents.DeadlineProperty)),
                IsMandatory = n.Value<bool>(ClubEvents.MandatoryProperty),
                LanevapenOffered = n.HasProperty(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty)
                    && n.Value<bool>(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty),
                PricesJson = S(EventPrices.Property),
                Audience = EventAudience.Normalise(n.Value<string>(EventAudience.Property)),
                SwishNumber = S(ClubEvents.SwishProperty).Trim(),
                ContentBlocksJson = S("contentBlocks"),
                QuickLinksJson = S("quickLinks"),
                Url = n.Url(),
                CreatedDate = UmbracoTime.Local(n.CreateDate),
                UpdatedDate = UmbracoTime.Local(n.UpdateDate)
            };
        }

        private static void Write(IContent n, ClubEventRecord r)
        {
            void Set(string alias, object? value) { if (n.HasProperty(alias)) n.SetValue(alias, value); }
            Set("eventName", r.Name ?? "");
            Set("eventType", r.EventType ?? "");
            Set("description", r.Description ?? "");
            Set("venue", r.Venue ?? "");
            Set("contactPerson", r.ContactPerson ?? "");
            Set("contactEmail", r.ContactEmail ?? "");
            Set("contactPhone", r.ContactPhone ?? "");
            Set("equipmentRequired", r.EquipmentRequired ?? "");
            Set("targetAudience", r.TargetAudience ?? "");
            Set("eventDate", r.StartsAt);
            Set("eventEndDate", r.EndsAt);
            Set("isActive", r.IsActive);
            Set("eventImage", string.IsNullOrWhiteSpace(r.ImageRef) ? null : r.ImageRef);
            Set("registrationRequired", r.RegistrationRequired);
            Set("registrationUrl", r.RegistrationUrl ?? "");
            Set("maxParticipants", r.MaxParticipants > 0 ? r.MaxParticipants : 0);
            Set(ClubEvents.MandatoryProperty, r.IsMandatory);
            Set(HpskSite.Services.Firearms.LoanWeaponClubRules.EventOfferedProperty, r.LanevapenOffered);
            Set(ClubEvents.DeadlineProperty, r.RegistrationDeadline);
            Set(EventPrices.Property, r.PricesJson ?? "");
            Set(ClubEvents.SwishProperty, (r.SwishNumber ?? "").Trim());
            Set(EventAudience.Property, EventAudience.Normalise(r.Audience));
            Set("contentBlocks", r.ContentBlocksJson ?? "");
            Set("quickLinks", r.QuickLinksJson ?? "");
        }
    }
}
