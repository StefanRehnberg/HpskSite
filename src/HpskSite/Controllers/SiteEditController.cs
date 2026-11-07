using HpskSite.Services;
using HpskSite.Services.Hosting;
using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Skrivvägarna för klubbens och kretsens egna sidor (klubbsidorna 2b, "Hemsidan"). Varje action:
    /// <list type="number">
    /// <item><b>Ägaren prövas på servern</b> (<see cref="Gate"/>): noden måste vara en klubb/krets som inte ligger i papperskorgen, brytare 2 måste
    /// vara PÅ, och den som anropar måste vara ägarens EGEN administratör (<see cref="AdminAuthorizationService.IsOwnClubAdmin"/> /
    /// <see cref="AdminAuthorizationService.IsOwnRegionAdmin"/>) — varken sajt- eller kretsadmin; stödläge är inte byggt än.</item>
    /// <item>Skrivning kräver att villkoren är accepterade (<see cref="SiteOwnerData.TermsAccepted"/>).</item>
    /// <item>Antiforgery på varje skrivning, takt via <see cref="RateCounterService"/>.</item>
    /// <item>Allt innehåll går genom <see cref="SitePageWriter"/>: schema, personnummer, ägarprövning av refererade id:n, i EN transaktion.</item>
    /// </list>
    /// Sid-id:t kommer utifrån och prövas av <see cref="SitePageStore"/> mot den ägare Gate fastställt (id-lagen), aldrig mot ett ägar-id från anropet.
    /// </summary>
    public class SiteEditController : SurfaceController
    {
        public const int WritesPerMinute = 30;
        public const int CreatesPerDay = 20;

        private const string NotAllowed = "Bara klubbens egna administratörer kan ändra klubbens sidor.";
        private readonly AdminAuthorizationService _auth;
        private readonly IScopeProvider _scopes;
        private readonly SiteOwnerService _owners;
        private readonly ISiteOwnerFacts _facts;
        private readonly RateCounterService _rate;
        private readonly IContentService _content;
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;

        public SiteEditController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, IScopeProvider scopes, SiteOwnerService owners, ISiteOwnerFacts facts, RateCounterService rate,
            IContentService content, IMemberManager members, IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth; _scopes = scopes; _owners = owners; _facts = facts; _rate = rate;
            _content = content; _members = members; _memberService = memberService;
        }

        /// <summary>Resultatet av ägarprövningen: ett felsvar, eller ägarens uppgifter och den som skriver.</summary>
        private sealed record GateResult(IActionResult? Error, string OwnerType = "", int OwnerId = 0, string OwnerName = "", string OwnerUrl = "/", SiteActor? Actor = null);

        private IActionResult Fail(string message, string? code = null) => Json(new { success = false, message, code });

        private async Task<GateResult> Gate(string ownerType, int ownerId, bool write)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region) return new GateResult(Fail("Okänd ägartyp."));
            if (ownerId <= 0) return new GateResult(Fail("Okänd förening."));

            // Id:t kommer utifrån: noden måste vara rätt sort och inte ligga i papperskorgen.
            var node = _content.GetById(ownerId);
            var alias = ownerType == SiteTemplates.Club ? "club" : "regionalPage";
            if (node == null || node.Trashed || node.ContentType.Alias != alias) return new GateResult(Fail("Föreningen finns inte."));

            // Brytare 2 av = ytan finns inte (inte ett beskrivande fel som avslöjar något).
            if (!_owners.IsEditingEnabled(ownerType, ownerId)) return new GateResult(Fail("Hemsidan är inte påslagen för den här föreningen.", "off"));

            var allowed = ownerType == SiteTemplates.Club
                ? await _auth.IsOwnClubAdmin(ownerId)
                : await _auth.IsOwnRegionAdmin(node.GetValue<string>("regionCode") ?? "");
            if (!allowed) return new GateResult(Fail(NotAllowed, "denied"));

            var cur = await _members.GetCurrentMemberAsync();
            var memberId = cur?.Email == null ? (int?)null : _memberService.GetByEmail(cur.Email)?.Id;
            if (memberId == null) return new GateResult(Fail(NotAllowed, "denied"));

            if (write)
            {
                using var scope = _scopes.CreateScope(autoComplete: true);
                if (!SiteOwnerData.TermsAccepted(scope.Database, ownerType, ownerId))
                    return new GateResult(Fail("Villkoren för klubbens sidor är inte accepterade än.", "terms"));
            }
            var url = UmbracoContext.Content?.GetById(ownerId)?.Url() ?? "/";
            return new GateResult(null, ownerType, ownerId, node.Name ?? "", url, new SiteActor(memberId));
        }

        private bool Throttled(string key, int limit, TimeSpan window) => _rate.Increment(key, window) > limit;

        private IActionResult Map(SiteWriteResult r) => Json(new
        {
            success = r.Ok,
            status = r.Status.ToString(),
            pageId = r.PageId,
            revision = r.Revision,
            slug = r.Slug,
            message = r.Ok ? null : r.Message ?? StatusMessage(r.Status),
            errors = r.Errors?.Select(e => new { path = e.Path, message = e.Message }),
            match = r.Match,
        });

        private static string StatusMessage(SiteWriteStatus s) => s switch
        {
            SiteWriteStatus.Conflict => "Någon annan har ändrat sidan. Din text finns kvar; ladda om för att se deras ändring.",
            SiteWriteStatus.NotFound => "Sidan finns inte.",
            SiteWriteStatus.TooManyPages => $"Ni har nått taket på {SitePageStore.MaxFreePagesPerOwner} sidor.",
            SiteWriteStatus.NothingToPublish => "Det finns inga ändringar att publicera.",
            SiteWriteStatus.SlugTaken => "Adressen går inte att använda.",
            SiteWriteStatus.PersonalNumber => "Det här ser ut som ett personnummer.",
            _ => "Det gick inte att spara."
        };

        // ── Läsa ──────────────────────────────────────────────────────────────

        /// <summary>Hemsidans tillstånd: får jag skriva, är villkoren accepterade, sidlistan och papperskorgen.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ")]
        public async Task<IActionResult> State(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var accepted = SiteOwnerData.TermsAccepted(scope.Database, g.OwnerType, g.OwnerId);
            object Row(SitePageListItem p) => new
            {
                id = p.Id, title = p.Title, visibility = p.Visibility, status = p.Status, slug = p.Slug, revision = p.Revision, inMenu = p.InMenu,
                url = p.Slug == null ? null : g.OwnerUrl + p.Slug + "/", updated = p.UpdatedDate, deleted = p.DeletedDate,
            };
            return Json(new
            {
                success = true, ownerName = g.OwnerName, ownerUrl = g.OwnerUrl, termsAccepted = accepted, termsVersion = SiteOwnerData.CurrentTermsVersion,
                maxPages = SitePageStore.MaxFreePagesPerOwner,
                // Systemsidorna som ett länkblock får peka på (aldrig Admin, som saknar adress).
                systemPages = SiteTemplates.For(g.OwnerType).Pages.Where(p => !p.IsAdmin).OrderBy(p => p.Order).Select(p => new { key = p.Key, label = p.Label }),
                pages = SitePageStore.ListFree(scope.Database, g.OwnerType, g.OwnerId).Select(Row),
                trash = SitePageStore.ListFree(scope.Database, g.OwnerType, g.OwnerId, deleted: true).Select(Row),
            });
        }

        /// <summary>Sidan att redigera: utkastet om det finns, annars det publicerade.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ")]
        public async Task<IActionResult> Page(string ownerType, int ownerId, int pageId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var page = SitePageStore.GetPage(scope.Database, pageId, g.OwnerType, g.OwnerId);
            if (page == null || !page.IsFree) return Fail("Sidan finns inte.");
            var version = SitePageStore.GetDraft(scope.Database, pageId, g.OwnerType, g.OwnerId)
                          ?? SitePageStore.GetPublished(scope.Database, pageId, g.OwnerType, g.OwnerId);
            if (version == null) return Fail("Sidan saknar innehåll.");
            var slug = scope.Database.FirstOrDefault<string>("SELECT TOP 1 Slug FROM dbo.PageAddress WHERE TargetKind = 'sitepage' AND TargetId = @0 AND IsCurrent = 1", pageId);
            return Json(new
            {
                success = true, id = page.Id, title = page.Title, visibility = page.Visibility, revision = page.Revision, slug, inMenu = SitePageStore.ListFree(scope.Database, g.OwnerType, g.OwnerId).Any(x => x.Id == pageId && x.InMenu),
                url = slug == null ? null : g.OwnerUrl + slug + "/", hasUnpublishedChanges = page.HasUnpublishedChanges, published = page.PublishedVersionId != null,
                blocksJson = version.BlocksJson, savedDate = version.SavedDate,
            });
        }

        private sealed class DocRow
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public int AccessLevel { get; set; }
        }

        /// <summary>Föreningens dokument att välja i ett dokumentblock: bara titel och nivå, aldrig filen. Bara ägarens egna.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ")]
        public async Task<IActionResult> Documents(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            var typeInt = g.OwnerType == SiteTemplates.Club ? HpskSite.Models.DocumentOwnerType.Club : HpskSite.Models.DocumentOwnerType.Region;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var docs = scope.Database.Fetch<DocRow>(
                "SELECT Id, Title, AccessLevel FROM dbo.Documents WHERE OwnerType = @0 AND OwnerId = @1 ORDER BY Title", typeInt, g.OwnerId);
            return Json(new { success = true, documents = docs.Select(d => new { id = d.Id, title = d.Title, level = HpskSite.Models.DocumentAccessLevel.GetLabel(d.AccessLevel) }) });
        }

        // ── Skriva ────────────────────────────────────────────────────────────

        /// <summary>Accepterar villkoren för klubbens innehåll. Bara ägarens egen admin (Gate), loggas på raden.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ")]
        public async Task<IActionResult> AcceptTerms(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using (var scope = _scopes.CreateScope())
            {
                if (!SiteOwnerData.AcceptTerms(scope.Database, g.OwnerType, g.OwnerId, g.Actor!.MemberId!.Value, DateTime.Now))
                    return Fail("Föreningen saknar rad.");
                scope.Database.Execute(
                    "INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, CreatedDate) VALUES (@0, @1, @2, 0, 'owner', @1, 'terms-accepted', @3)",
                    g.OwnerType, g.OwnerId, g.Actor.MemberId, DateTime.Now);
                scope.Complete();
            }
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> CreatePage(string ownerType, int ownerId, string title, string? slug, int visibility)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitecreate:{g.OwnerType}:{g.OwnerId}", CreatesPerDay, TimeSpan.FromDays(1)))
                return Fail($"Högst {CreatesPerDay} nya sidor per dygn.", "rate");
            if (visibility < 0 || visibility > (int)SiteVisibility.Dold) return Fail("Ogiltig synlighet.");

            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.CreatePage(scope.Database, _facts, g.OwnerType, g.OwnerId, title, slug, (SiteVisibility)visibility, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Autospar av utkastet. <paramref name="allowPersonalNumber"/> = admin har svarat "Det är inget personnummer" (loggas).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SavePage(string ownerType, int ownerId, int pageId, int expectedRevision, string title, string blocksJson,
            int? visibility, bool allowPersonalNumber = false)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många sparningar på kort tid. Vänta en stund.", "rate");
            if (visibility is < 0 or > (int)SiteVisibility.Dold) return Fail("Ogiltig synlighet.");

            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.SaveDraft(scope.Database, _facts, pageId, g.OwnerType, g.OwnerId, expectedRevision, title, blocksJson,
                    visibility == null ? null : (SiteVisibility)visibility, g.Actor!, DateTime.Now, allowPersonalNumber);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> PublishPage(string ownerType, int ownerId, int pageId, int expectedRevision, bool allowPersonalNumber = false)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många sparningar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.Publish(scope.Database, _facts, pageId, g.OwnerType, g.OwnerId, expectedRevision, g.Actor!, DateTime.Now, allowPersonalNumber);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> DiscardDraft(string ownerType, int ownerId, int pageId, int expectedRevision)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                var s = SitePageStore.DiscardDraft(scope.Database, pageId, g.OwnerType, g.OwnerId, expectedRevision, g.Actor!.MemberId, DateTime.Now);
                r = new SiteWriteResult(s.Outcome == SiteStoreOutcome.Ok ? SiteWriteStatus.Ok : s.Outcome == SiteStoreOutcome.Conflict ? SiteWriteStatus.Conflict
                    : s.Outcome == SiteStoreOutcome.NotFound ? SiteWriteStatus.NotFound : SiteWriteStatus.NothingToPublish, s.PageId, s.Revision);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Sidans tidigare versioner (publiceringar och återställningspunkter), senaste först, högst tio.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ")]
        public async Task<IActionResult> Versions(string ownerType, int ownerId, int pageId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            if (SitePageStore.GetPage(scope.Database, pageId, g.OwnerType, g.OwnerId) == null) return Fail("Sidan finns inte.");
            return Json(new
            {
                success = true,
                versions = SitePageStore.ListVersions(scope.Database, pageId, g.OwnerType, g.OwnerId).Select(v => new { id = v.Id, kind = v.Kind, title = v.Title, saved = v.SavedDate, support = v.SavedAsSupport }),
            });
        }

        /// <summary>"Använd den här versionen": kopierar den in som nytt utkast (aldrig en pekarflytt).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> RestoreVersion(string ownerType, int ownerId, int pageId, int versionId, int expectedRevision)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.RestoreVersion(scope.Database, _facts, pageId, g.OwnerType, g.OwnerId, versionId, expectedRevision, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>"Spara en återställningspunkt": en version att kunna gå tillbaka till, utan att något publiceras.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SaveCheckpoint(string ownerType, int ownerId, int pageId)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.SaveCheckpoint(scope.Database, pageId, g.OwnerType, g.OwnerId, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Byter sidans adress (under "Avancerat"). Den gamla adressen svarar 301 till den nya.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SetSlug(string ownerType, int ownerId, int pageId, string slug)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.SetSlug(scope.Database, _facts, pageId, g.OwnerType, g.OwnerId, slug ?? "", g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Json(new { success = r.Ok, slug = r.Slug, url = r.Ok ? g.OwnerUrl + r.Slug + "/" : null, message = r.Ok ? null : r.Message ?? "Adressen går inte att använda.", status = r.Status.ToString() });
        }

        /// <summary>Visa sidan i menyn eller inte. En sida syns i menyn först när den är publicerad, och bara för den som får se den.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SetInMenu(string ownerType, int ownerId, int pageId, bool inMenu)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.SetInMenu(scope.Database, pageId, g.OwnerType, g.OwnerId, inMenu, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> DeletePage(string ownerType, int ownerId, int pageId, int expectedRevision)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.SoftDelete(scope.Database, pageId, g.OwnerType, g.OwnerId, expectedRevision, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> RestorePage(string ownerType, int ownerId, int pageId, int expectedRevision)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            SiteStoreResult s;
            using (var scope = _scopes.CreateScope())
            {
                s = SitePageStore.Restore(scope.Database, pageId, g.OwnerType, g.OwnerId, expectedRevision, g.Actor!.MemberId, DateTime.Now);
                if (s.Outcome == SiteStoreOutcome.Ok) scope.Complete();
            }
            return Map(new SiteWriteResult(s.Outcome == SiteStoreOutcome.Ok ? SiteWriteStatus.Ok : s.Outcome == SiteStoreOutcome.Conflict ? SiteWriteStatus.Conflict : SiteWriteStatus.NotFound, s.PageId, s.Revision));
        }
    }
}
