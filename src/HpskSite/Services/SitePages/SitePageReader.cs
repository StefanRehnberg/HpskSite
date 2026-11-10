using HpskSite.Models;
using HpskSite.Services.Addresses;
using HpskSite.Models.Events;
using HpskSite.Services.Events;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>En egen sida bakom en adress hos en ägare. <see cref="IsCurrentAddress"/> false = en gammal adress (301 till <see cref="CurrentSlug"/>).</summary>
    public sealed record SiteAddressHit(SitePageRow Page, bool IsCurrentAddress, string CurrentSlug);

    /// <summary>
    /// Läser klubbens och kretsens egna sidor för läsvägen (finder, filter, vy). Synligheten läses ALLTID ur SQL — ingen cache av
    /// sidinnehåll eller synlighet i steg 2 (plan v3 B4); bara findern slår upp adressen, och bara när ägaren har brytare 2 PÅ.
    /// Varje sid-id prövas mot den ägare det gäller (<see cref="SitePageStore"/>).
    /// </summary>
    public sealed class SitePageReader
    {
        private readonly IScopeProvider _scopes;

        public SitePageReader(IScopeProvider scopes) { _scopes = scopes; }

        /// <summary>Sidan bakom <c>/{ägare}/{slug}/</c>, eller null. En raderad eller aldrig publicerad sida är inte "funnen".</summary>
        public SiteAddressHit? Resolve(string ownerType, int ownerId, string slug)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.FirstOrDefault<PageAddressRow>(
                "SELECT TOP 1 Id, OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL AND Slug = @2 AND TargetKind = @3",
                ownerType, ownerId, slug, PageAddressKinds.SitePage);
            if (row == null) return null;
            var page = SitePageStore.GetPage(db, row.TargetId, ownerType, ownerId);
            if (page == null || page.PublishedVersionId == null) return null;
            var current = row.IsCurrent ? row.Slug
                : db.FirstOrDefault<string>("SELECT TOP 1 Slug FROM dbo.PageAddress WHERE TargetKind = @0 AND TargetId = @1 AND IsCurrent = 1", PageAddressKinds.SitePage, page.Id);
            return current == null ? null : new SiteAddressHit(page, row.IsCurrent, current);
        }

        /// <summary>Ägarens egna sidor som ligger i menyn (publicerade, ej raderade). En SQL-fråga; körs bara när ägaren har egen redigering PÅ.</summary>
        public IReadOnlyList<SiteMenuPage> MenuPages(string ownerType, int ownerId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            return SitePageStore.MenuPages(scope.Database, ownerType, ownerId)
                .Select(r => new SiteMenuPage(r.PageId, r.Title, r.Slug, (SiteVisibility)r.Visibility, r.SortOrder, r.ParentPageId, r.ParentTemplateKey)).ToList();
        }

        /// <summary>Ägarens externa länkar i menyn, i ordning. En SQL-fråga; körs bara när ägaren har egen redigering PÅ. Nivån prövas för tittaren i <see cref="SitePageView.Links"/>.</summary>
        public IReadOnlyList<SiteExternalLink> MenuLinks(string ownerType, int ownerId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            return SiteMenuLinkStore.List(scope.Database, ownerType, ownerId)
                .Select(r => new SiteExternalLink(r.Id, r.Label, r.Url, (SiteVisibility)r.Visibility)).ToList();
        }

        /// <summary>Sidans rad (för filtret efter inloggningen: synligheten läses färskt), eller null.</summary>
        public SitePageRow? Page(string ownerType, int ownerId, int pageId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            return SitePageStore.GetPage(scope.Database, pageId, ownerType, ownerId);
        }

        /// <summary>
        /// Ägarens publicerade egna text före/efter en systemsida (mallkopian), tolkad mot kopians AKTUELLA synlighet. Null = ingen kopia, inget publicerat, eller ogiltigt
        /// innehåll (då visas inget). Synligheten läses färskt ur SQL — ingen cache.
        /// </summary>
        public (SiteVisibility Visibility, SiteContentResult Content)? LoadSlots(string ownerType, int ownerId, string templateKey)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var page = SitePageStore.GetTemplateCopy(scope.Database, ownerType, ownerId, templateKey);
            if (page == null || page.PublishedVersionId == null || page.SuspendedDate != null) return null;
            var version = SitePageStore.GetPublished(scope.Database, page.Id, ownerType, ownerId);
            if (version == null) return null;
            // En omvandlad sida (schema 2) har inga platser före/efter: den ritas ur sina block, så här blir det aldrig "slots".
            if (SiteBlockSchema.KindOf(page.IsFree, version.BlocksJson) != SiteContentKind.TemplateCopy) return null;
            var content = SiteBlockSchema.Parse(version.BlocksJson, SiteContentKind.TemplateCopy, page.VisibilityLevel, SiteTemplates.For(ownerType));
            return content.Ok ? (page.VisibilityLevel, content) : null;
        }

        public sealed class ConvertedRow
        {
            public string TemplateKey { get; set; } = "";
            public byte Visibility { get; set; }
            public DateTime? SuspendedDate { get; set; }
            public string BlocksJson { get; set; } = "";
        }

        /// <summary>
        /// Ägarens omvandlade systemsidor (publicerad version i schema 2), per mallnyckel. EN SQL-fråga; synligheten läses färskt, ingen cache.
        /// En rad som inte går att tolka blir <see cref="ConvertedState.Broken"/> (felkort), aldrig tyst standardsidan.
        /// </summary>
        public IReadOnlyDictionary<string, ConvertedPage> ConvertedPages(string ownerType, int ownerId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<ConvertedRow>(
                @"SELECT p.TemplateKey, p.Visibility, p.SuspendedDate, v.BlocksJson
                  FROM dbo.SitePage p JOIN dbo.SitePageVersion v ON v.Id = p.PublishedVersionId AND v.PageId = p.Id
                  WHERE p.OwnerType = @0 AND p.OwnerId = @1 AND p.TemplateKey IS NOT NULL AND p.DeletedDate IS NULL AND v.SchemaVersion = @2",
                ownerType, ownerId, SiteBlockSchema.ConvertedSchemaVersion);
            var template = SiteTemplates.For(ownerType);
            var result = new Dictionary<string, ConvertedPage>();
            foreach (var r in rows)
            {
                if (template.PageByKey(r.TemplateKey) is not { IsAdmin: false }) continue;
                var vis = (SiteVisibility)r.Visibility;
                SiteContentResult? content = null;
                try
                {
                    if (SiteBlockSchema.KindOf(false, r.BlocksJson) == SiteContentKind.ConvertedPage)
                        content = SiteBlockSchema.Parse(r.BlocksJson, SiteContentKind.ConvertedPage, vis, template, r.TemplateKey);
                }
                catch { content = null; }
                result[r.TemplateKey] = content is { Ok: true }
                    ? new ConvertedPage(r.TemplateKey, ConvertedState.Ok, vis, r.SuspendedDate != null, content)
                    : new ConvertedPage(r.TemplateKey, ConvertedState.Broken, vis, r.SuspendedDate != null, null);
            }
            return result;
        }

        /// <summary>Det publicerade innehållet, tolkat mot sidans AKTUELLA synlighet (en lagrad rad är inte betrodd). Null om det saknas eller är ogiltigt.</summary>
        public (SitePageRow Page, SiteContentResult Content)? LoadPublished(string ownerType, int ownerId, int pageId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var page = SitePageStore.GetPage(scope.Database, pageId, ownerType, ownerId);
            if (page == null || page.SuspendedDate != null) return null;
            var version = SitePageStore.GetPublished(scope.Database, pageId, ownerType, ownerId);
            if (version == null) return null;
            var content = SiteBlockSchema.Parse(version.BlocksJson,
                SiteBlockSchema.KindOf(page.IsFree, version.BlocksJson), page.VisibilityLevel, SiteTemplates.For(ownerType), page.TemplateKey);
            return content.Ok ? (page, content) : null;
        }
    }

    /// <summary>
    /// Live-data för blocken, ENDAST för en ägare (ägar-id kommer från begäran, aldrig från ett block). Dokument som inte hör till
    /// ägaren lämnas aldrig ut; händelser är klubbens publicerade kommande (ReadVisibility per händelse kommer i 2c).
    /// </summary>
    public sealed class SiteBlockDataFactory
    {
        private readonly DocumentService _documents;
        private readonly IClubEventStore _events;
        private readonly PageAddressUrls _urls;

        private readonly IScopeProvider _scopes;
        private readonly ClubRosterService _roster;
        private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http;
        private readonly Microsoft.AspNetCore.Antiforgery.IAntiforgery _antiforgery;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.ICompetitionSeriesStore _series;
        private readonly HpskSite.Services.Competitions.CompetitionLinks _links;

        public SiteBlockDataFactory(DocumentService documents, IClubEventStore events, PageAddressUrls urls, IScopeProvider scopes,
            HpskSite.Services.Competitions.ICompetitionStore competitions, HpskSite.Services.Competitions.ICompetitionSeriesStore series,
            HpskSite.Services.Competitions.CompetitionLinks links, ClubRosterService roster,
            Microsoft.AspNetCore.Http.IHttpContextAccessor http, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery)
        {
            _http = http;
            _antiforgery = antiforgery;
            _roster = roster;
            _competitions = competitions;
            _series = series;
            _links = links;
            _scopes = scopes;
            _documents = documents;
            _events = events;
            _urls = urls;
        }

        /// <param name="pageId">Sidan som ritas (en egen sida): anmälningsblocken postar med den. 0 = ingen sida (förhandsvisning, systemsida): formulär visas då utan att gå att skicka.</param>
        public ISiteBlockData For(string ownerType, int ownerId, IPublishedContent? ownerNode, SiteViewer viewer, int pageId = 0) =>
            new Data(this, ownerType, ownerId, ownerNode, viewer, pageId);

        /// <summary>Dokumentarkivets nivå som synlighet: Publik = Alla, Inloggade, Klubbmedlemmar, Klubbadministratörer = Dold.</summary>
        internal static SiteVisibility VisibilityOf(int accessLevel) => accessLevel switch
        {
            DocumentAccessLevel.Public => SiteVisibility.Alla,
            DocumentAccessLevel.Authenticated => SiteVisibility.Inloggade,
            DocumentAccessLevel.ClubMembers => SiteVisibility.Klubbmedlemmar,
            _ => SiteVisibility.Dold,   // ClubAdmins och allt okänt: aldrig synligt för fler
        };

        private sealed class Data : ISiteBlockData
        {
            private readonly SiteBlockDataFactory _f;
            private readonly string _ownerType;
            private readonly int _ownerId;
            private readonly IPublishedContent? _node;
            private readonly SiteViewer _viewer;

            private readonly int _pageId;

            public Data(SiteBlockDataFactory f, string ownerType, int ownerId, IPublishedContent? node, SiteViewer viewer, int pageId = 0)
            { _f = f; _ownerType = ownerType; _ownerId = ownerId; _node = node; _viewer = viewer; _pageId = pageId; }

            private static readonly string[] KnownFlashCodes = { "closed", "rate", "invalid", "full", "error" };

            /// <summary>Sidans id, skyddstoken (antiforgery, som också hindrar att sidan cachas) och beskedet efter en inskickning. Ur adressen läses bara blockets id och en känd kod: ingen text därifrån skrivs någonsin ut.</summary>
            public SiteFormContext? Form()
            {
                if (_pageId <= 0) return null;
                var http = _f._http.HttpContext;
                if (http == null) return null;
                string? token = null;
                try { token = _f._antiforgery.GetAndStoreTokens(http).RequestToken; } catch { /* utan token ritas formuläret avstängt */ }
                SiteFormFlash? flash = null;
                var bid = http.Request.Query["anmalan"].ToString();
                if (bid.Length is > 0 and <= 12 && bid.All(c => char.IsAsciiLetterOrDigit(c)))
                {
                    var r = http.Request.Query["r"].ToString();
                    if (r == "ok") flash = new SiteFormFlash(bid, true, null);
                    else if (r == "err") { var e = http.Request.Query["e"].ToString(); flash = new SiteFormFlash(bid, false, KnownFlashCodes.Contains(e) ? e : "error"); }
                }
                return new SiteFormContext(_pageId, token, flash);
            }

            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids)
            {
                var ownerTypeInt = _ownerType == SiteTemplates.Club ? DocumentOwnerType.Club : DocumentOwnerType.Region;
                var result = new List<SiteDocumentInfo>();
                foreach (var id in ids.Distinct())
                {
                    var d = _f._documents.GetDocument(id);
                    if (d == null || d.OwnerType != ownerTypeInt || d.OwnerId != _ownerId) continue;   // aldrig en annan ägares dokument
                    result.Add(new SiteDocumentInfo(d.Id, d.Title, d.Description, d.FileSize, VisibilityOf(d.AccessLevel)));
                }
                return result;
            }

            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => UpcomingEvents(count, null);

            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count, string? eventType)
            {
                var evOwner = _ownerType == SiteTemplates.Club ? ClubEventRecord.OwnerClub : ClubEventRecord.OwnerRegion;   // även kretsens kalender (2d)
                var now = DateTime.Now;
                return _f._events.ListForOwner(_ownerId, HpskSite.Services.Events.EventViewer.ForOwner(_viewer))
                    .Where(e => e.StartsAt.HasValue && e.StartsAt.Value >= now && e.OwnerType == evOwner && e.OwnerId == _ownerId
                                && (eventType == null || e.EventType == eventType))
                    .OrderBy(e => e.StartsAt)
                    .Take(count)
                    .Select(e => new SiteEventInfo(e.DisplayName, e.StartsAt!.Value, string.IsNullOrWhiteSpace(e.Venue) ? null : e.Venue,
                        _f._urls.UrlFor(PageAddressKinds.Event, e.Id) ?? "", e.EventType))
                    .ToList();
            }

            public IReadOnlyList<SiteNewsInfo> News(int count)
            {
                var type = _ownerType == SiteTemplates.Club ? ClubEventRecord.OwnerClub : ClubEventRecord.OwnerRegion;
                return _f._events.ListForOwner(_ownerId, HpskSite.Services.Events.EventViewer.ForOwner(_viewer))   // läsnivån prövas i lagringen, per händelse
                    .Where(e => e.EventType == "Nyhet" && e.IsActive && e.OwnerType == type && e.OwnerId == _ownerId)
                    .OrderByDescending(e => e.StartsAt ?? e.CreatedDate)
                    .Take(count)
                    .Select(e => new SiteNewsInfo(e.DisplayName, e.StartsAt ?? e.CreatedDate, _f._urls.UrlFor(PageAddressKinds.Event, e.Id) ?? ""))
                    .ToList();
            }

            public IReadOnlyList<SiteImageInfo> Images(IReadOnlyList<int> ids)
            {
                using var scope = _f._scopes.CreateScope(autoComplete: true);
                return SiteFiles.Info(scope.Database, ids, _ownerType, _ownerId);   // alltid denna ägares bilder
            }

            // ── Kretsens block (2d). Alla frågor gäller DENNA krets (nod + regionCode ur begäran), aldrig ett id ur blocket.
            private bool IsRegion => _ownerType == SiteTemplates.Region && _node != null;
            private string RegionCode => _node?.Value<string>("regionCode") ?? "";

            public IReadOnlyList<SiteRegionClub> RegionClubs() => IsRegion ? SiteRegion.Clubs(_node) : Array.Empty<SiteRegionClub>();

            public IReadOnlyList<SiteRangeInfo> RegionRanges()
            {
                if (!IsRegion) return Array.Empty<SiteRangeInfo>();
                var clubIds = SiteRegion.Clubs(_node).Select(c => c.Id).ToList();
                if (clubIds.Count == 0) return Array.Empty<SiteRangeInfo>();
                using var scope = _f._scopes.CreateScope(autoComplete: true);
                // Begränsade banor (militära/diskreta) tas aldrig med: kretsens sida är inte den plats där de avslöjas.
                return scope.Database.Fetch<RangeRow>(
                        "SELECT r.Id, r.Name, r.City FROM ShootingRange r WHERE r.LocationSensitivity <> @0 AND r.Id IN (SELECT RangeId FROM ClubRangeLink WHERE ClubId IN (@1)) ORDER BY r.Name",
                        RangeConstants.SensRestricted, clubIds)
                    .Select(r => new SiteRangeInfo(r.Id, r.Name, r.City, $"/skjutbana/{r.Id}")).ToList();
            }

            private sealed class RangeRow { public int Id { get; set; } public string Name { get; set; } = ""; public string? City { get; set; } }

            private IEnumerable<HpskSite.Models.Competitions.CompetitionSeriesInfo> OwnSeries() =>
                _f._series.List().Where(s => s.IsPublished && s.IsVisible && s.ClubId <= 0
                    && string.Equals(s.RegionalFederation ?? "", RegionCode, StringComparison.OrdinalIgnoreCase));

            public IReadOnlyList<SiteSeriesInfo> RegionSeries(int count)
            {
                if (!IsRegion || RegionCode.Length == 0) return Array.Empty<SiteSeriesInfo>();
                return OwnSeries()
                    .OrderByDescending(s => s.SeriesStartDate ?? DateTime.MinValue).ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .Select(s => new SiteSeriesInfo(s.DisplayName, _f._links.SeriesUrl(s.Id), s.SeriesStartDate, s.SeriesEndDate))
                    .Where(s => s.Url.Length > 0)   // en serie utan adress kan inte länkas: visas inte
                    .Take(count).ToList();
            }

            public SiteRegionStats? RegionStats()
            {
                if (!IsRegion || RegionCode.Length == 0) return null;
                var clubs = SiteRegion.Clubs(_node);
                var clubIds = clubs.Select(c => c.Id).ToHashSet();
                var today = DateTime.Today;
                // Samma urval som kretsens tävlingslista: kretsens egna, samt klubbarnas som inte är klubbinterna.
                var upcoming = _f._competitions.List().Count(c => c.IsVisible && c.CompetitionDate >= today &&
                    ((c.ClubId <= 0 && string.Equals(c.RegionalFederation ?? "", RegionCode, StringComparison.OrdinalIgnoreCase))
                     || (c.ClubId > 0 && clubIds.Contains(c.ClubId) && !c.IsClubOnly)));
                return new SiteRegionStats(clubs.Count, RegionRanges().Count, OwnSeries().Count(), upcoming);
            }

            public string? AboutHtml()
            {
                if (_node == null) return null;
                var about = _ownerType == SiteTemplates.Region ? _node.Value<string>("aboutRegion") : _node.Value<string>("aboutClub");
                var markup = HpskSite.Extensions.RteHelper.SafeMarkup(about);
                return string.IsNullOrWhiteSpace(markup) ? HpskSite.Extensions.RteHelper.SafeMarkup(_node.Value<string>("description")) : markup;   // samma ordning som Om-panelen: presentationen, annars beskrivningen
            }

            // ── Biblioteksblocken tävlingar, rekord och banor. Alla frågor gäller DENNA ägare (ur begäran), aldrig ett id ur blocket.
            public IReadOnlyList<SiteCompetitionInfo> UpcomingCompetitions(int count)
            {
                var today = DateTime.Today;
                IEnumerable<HpskSite.Models.Competitions.CompetitionInfo> own;
                if (_ownerType == SiteTemplates.Club)
                    // En klubbintern tävling visas bara för klubbens medlemmar (strängare än tävlingslistan, som släpper in alla inloggade).
                    own = _f._competitions.List().Where(c => c.ClubId == _ownerId && (!c.IsClubOnly || _viewer.IsOwnerMember));
                else
                {
                    if (!IsRegion || RegionCode.Length == 0) return Array.Empty<SiteCompetitionInfo>();
                    var clubIds = SiteRegion.Clubs(_node).Select(c => c.Id).ToHashSet();
                    own = _f._competitions.List().Where(c =>
                        (c.ClubId <= 0 && string.Equals(c.RegionalFederation ?? "", RegionCode, StringComparison.OrdinalIgnoreCase))
                        || (c.ClubId > 0 && clubIds.Contains(c.ClubId) && !c.IsClubOnly));
                }
                return own.Where(c => c.IsVisible && c.CompetitionDate.HasValue && c.CompetitionDate.Value >= today)
                    .OrderBy(c => c.CompetitionDate)
                    .Select(c => new SiteCompetitionInfo(c.DisplayName, c.CompetitionDate!.Value, _f._links.Url(c.Id)))
                    .Take(count).ToList();
            }

            public IReadOnlyList<SiteRecordInfo> Records()
            {
                var level = _ownerType == SiteTemplates.Club ? HpskSite.Models.RecordLevels.Club : HpskSite.Models.RecordLevels.Region;
                var scopeId = _ownerType == SiteTemplates.Club ? _ownerId.ToString() : RegionCode;
                if (scopeId.Length == 0) return Array.Empty<SiteRecordInfo>();
                using var scope = _f._scopes.CreateScope(autoComplete: true);
                return scope.Database.Fetch<HpskSite.Models.CompetitionRecord>(
                        "WHERE Level = @0 AND ScopeId = @1 AND IsCurrent = 1 ORDER BY Discipline, RecordType, ClassCode", level, scopeId)
                    .Select(r => new SiteRecordInfo(HpskSite.Models.RecordDisciplines.DisplayName(r.Discipline),
                        HpskSite.Models.RecordClassRegistry.GetClassDisplayName(r.ClassCode), r.RecordType == HpskSite.Models.RecordTypes.Team,
                        HpskSite.Models.RecordClassRegistry.FormatScore(r.Discipline, r.RecordType, r.TotalScore, null),   // enheten reser med talet
                        string.IsNullOrWhiteSpace(r.TeamName) ? r.HolderName : r.TeamName!, r.RecordDate))
                    .ToList();
            }

            public IReadOnlyList<SiteChampionInfo> Champions()
            {
                var level = _ownerType == SiteTemplates.Club ? HpskSite.Models.RecordLevels.Club : HpskSite.Models.RecordLevels.Region;
                var scopeId = _ownerType == SiteTemplates.Club ? _ownerId.ToString() : RegionCode;
                if (scopeId.Length == 0) return Array.Empty<SiteChampionInfo>();
                using var scope = _f._scopes.CreateScope(autoComplete: true);
                // Regerande = senaste året per gren, typ och klass (samma regel som mästarpanelen).
                return scope.Database.Fetch<HpskSite.Models.CompetitionChampion>("WHERE Level = @0 AND ScopeId = @1 ORDER BY Year DESC, Id DESC", level, scopeId)
                    .GroupBy(c => new { c.Discipline, c.ChampionType, c.ClassCode }).Select(g => g.First())
                    .OrderBy(c => c.Discipline).ThenBy(c => c.ChampionType).ThenBy(c => c.ClassCode)
                    .Select(c => new SiteChampionInfo(c.Year, HpskSite.Models.RecordDisciplines.DisplayName(c.Discipline),
                        HpskSite.Models.RecordClassRegistry.GetClassDisplayName(c.ClassCode), c.ChampionType == HpskSite.Models.RecordTypes.Team,
                        HpskSite.Models.RecordClassRegistry.FormatScore(c.Discipline, c.ChampionType, c.TotalScore, c.SecondaryScore),
                        string.IsNullOrWhiteSpace(c.TeamName) ? c.HolderName : c.TeamName!))
                    .ToList();
            }

            public IReadOnlyList<SiteStatItem> Stats()
            {
                if (_ownerType == SiteTemplates.Region)
                {
                    var s = RegionStats();
                    return s == null ? Array.Empty<SiteStatItem>() : new[] { new SiteStatItem("Klubbar", s.Clubs), new SiteStatItem("Skjutbanor", s.Ranges),
                        new SiteStatItem("Serier", s.Series), new SiteStatItem("Kommande tävlingar", s.UpcomingCompetitions) };
                }
                var members = _f._roster.ActiveMemberIds(_ownerId).Count;
                // Samma urval som tävlingsblocket för en tittare utan medlemskap: klubbinterna tävlingar räknas aldrig in i en siffra som kan visas för fler.
                var upcoming = _f._competitions.List().Count(c => c.ClubId == _ownerId && c.IsVisible && !c.IsClubOnly && c.CompetitionDate.HasValue && c.CompetitionDate.Value >= DateTime.Today);
                return new[] { new SiteStatItem("Medlemmar", members), new SiteStatItem("Skjutbanor", OwnerRanges().Count), new SiteStatItem("Kommande tävlingar", upcoming) };
            }

            public IReadOnlyList<SiteRangeInfo> OwnerRanges()
            {
                if (_ownerType == SiteTemplates.Region) return RegionRanges();
                using var scope = _f._scopes.CreateScope(autoComplete: true);
                // Begränsade banor (militära/diskreta) tas aldrig med: en öppen sida är inte platsen där de avslöjas.
                return scope.Database.Fetch<RangeRow>(
                        "SELECT r.Id, r.Name, r.City FROM ShootingRange r WHERE r.LocationSensitivity <> @0 AND r.Id IN (SELECT RangeId FROM ClubRangeLink WHERE ClubId = @1) ORDER BY r.Name",
                        RangeConstants.SensRestricted, _ownerId)
                    .Select(r => new SiteRangeInfo(r.Id, r.Name, r.City, $"/skjutbana/{r.Id}")).ToList();
            }

            public SiteContactInfo? Contact() => _node == null ? null : new SiteContactInfo(
                _node.Value<string>("contactPerson"), _node.Value<string>("contactEmail"), _node.Value<string>("contactPhone"));
        }
    }
}
