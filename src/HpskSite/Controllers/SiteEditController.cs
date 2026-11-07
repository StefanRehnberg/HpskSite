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
        private readonly SiteViewerResolver _viewers;
        private readonly ISiteSupportNotifier _notifier;

        public SiteEditController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, IScopeProvider scopes, SiteOwnerService owners, ISiteOwnerFacts facts, RateCounterService rate,
            IContentService content, IMemberManager members, IMemberService memberService, SiteViewerResolver viewers, ISiteSupportNotifier notifier)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth; _scopes = scopes; _owners = owners; _facts = facts; _rate = rate;
            _content = content; _members = members; _memberService = memberService; _viewers = viewers; _notifier = notifier;
        }

        /// <summary>Resultatet av ägarprövningen: ett felsvar, eller ägarens uppgifter och den som skriver.</summary>
        private sealed record GateResult(IActionResult? Error, string OwnerType = "", int OwnerId = 0, string OwnerName = "", string OwnerUrl = "/", SiteActor? Actor = null,
            SupportGrantRow? Grant = null, string? RegionCode = null);

        /// <summary>Normal = ägarens egen admin ELLER öppet stödläge. OwnOnly = bara ägarens egen admin (t.ex. att godkänna villkoren). StartSupport = även den som FÅR öppna ett stödläge.</summary>
        private enum GateMode { Normal, OwnOnly, StartSupport }

        private IActionResult Fail(string message, string? code = null) => Json(new { success = false, message, code });

        private async Task<GateResult> Gate(string ownerType, int ownerId, bool write, GateMode mode = GateMode.Normal)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region) return new GateResult(Fail("Okänd ägartyp."));
            if (ownerId <= 0) return new GateResult(Fail("Okänd förening."));

            // Id:t kommer utifrån: noden måste vara rätt sort och inte ligga i papperskorgen.
            var node = _content.GetById(ownerId);
            var alias = ownerType == SiteTemplates.Club ? "club" : "regionalPage";
            if (node == null || node.Trashed || node.ContentType.Alias != alias) return new GateResult(Fail("Föreningen finns inte."));

            // Brytare 2 av = ytan finns inte (inte ett beskrivande fel som avslöjar något).
            if (!_owners.IsEditingEnabled(ownerType, ownerId)) return new GateResult(Fail("Hemsidan är inte påslagen för den här föreningen.", "off"));

            var regionCode = node.GetValue<string>("regionCode");
            var own = ownerType == SiteTemplates.Club
                ? await _auth.IsOwnClubAdmin(ownerId)
                : await _auth.IsOwnRegionAdmin(regionCode ?? "");

            var cur = await _members.GetCurrentMemberAsync();
            var memberId = cur?.Email == null ? (int?)null : _memberService.GetByEmail(cur.Email)?.Id;
            if (memberId == null) return new GateResult(Fail(NotAllowed, "denied"));

            // Ägarens egen admin skriver som sig själv. Alla andra måste ha ett ÖPPET stödläge (loggat, 60 min) — och bara de som FÅR öppna ett.
            SupportGrantRow? grant = null;
            if (!own)
            {
                if (mode == GateMode.OwnOnly) return new GateResult(Fail(NotAllowed, "denied"));
                if (!await _viewers.IsSupportEligibleAsync(ownerType, ownerId)) return new GateResult(Fail(NotAllowed, "denied"));
                using var gs = _scopes.CreateScope(autoComplete: true);
                grant = SiteSupport.Active(gs.Database, memberId.Value, ownerType, ownerId, DateTime.Now);
                if (grant == null && mode != GateMode.StartSupport)
                    return new GateResult(Fail("Du är inte föreningens egen administratör. Öppna ett stödläge, med en anledning, för att ändra föreningens sidor.", "support"));
            }

            if (write)
            {
                using var scope = _scopes.CreateScope(autoComplete: true);
                if (!SiteOwnerData.TermsAccepted(scope.Database, ownerType, ownerId))
                    return new GateResult(Fail("Villkoren för klubbens sidor är inte accepterade än.", "terms"));
            }
            var url = UmbracoContext.Content?.GetById(ownerId)?.Url() ?? "/";
            return new GateResult(null, ownerType, ownerId, node.Name ?? "", url, new SiteActor(memberId, AsSupport: grant != null), grant, regionCode);
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
            var copies = SitePageStore.ListTemplateCopies(scope.Database, g.OwnerType, g.OwnerId);
            object Row(SitePageListItem p) => new
            {
                id = p.Id, title = p.Title, visibility = p.Visibility, status = p.Status, slug = p.Slug, revision = p.Revision, inMenu = p.InMenu,
                url = p.Slug == null ? null : g.OwnerUrl + p.Slug + "/", updated = p.UpdatedDate, deleted = p.DeletedDate,
            };
            return Json(new
            {
                success = true, ownerName = g.OwnerName, ownerUrl = g.OwnerUrl, termsAccepted = accepted,
                support = g.Grant == null ? null : new { reason = g.Grant.Reason, expires = g.Grant.ExpiresDate, minutes = SiteSupport.Minutes }, termsVersion = SiteOwnerData.CurrentTermsVersion,
                maxPages = SitePageStore.MaxFreePagesPerOwner,
                // Systemsidorna som ett länkblock får peka på (aldrig Admin, som saknar adress).
                systemPages = SiteTemplates.For(g.OwnerType).Pages.Where(p => !p.IsAdmin).OrderBy(p => p.Order).Select(p => new { key = p.Key, label = p.Label }),
                // Egen text före/efter systemsidorna: varje sida som kan ha det, hur öppen texten får vara, och ägarens kopia om den finns.
                slots = SiteTemplates.For(g.OwnerType).Pages.Where(p => SiteTemplates.For(g.OwnerType).CanHaveSlots(p.Key)).OrderBy(p => p.Order).Select(p =>
                {
                    var copy = copies.FirstOrDefault(c => c.Slug == p.Key);   // ListTemplateCopies lägger mallnyckeln i Slug
                    return new
                    {
                        key = p.Key, label = p.Label, minVisibility = (int)SiteTemplates.For(g.OwnerType).MostOpenLevel(p.Key)!.Value,
                        page = copy == null ? null : new { id = copy.Id, status = copy.Status, visibility = copy.Visibility, revision = copy.Revision },
                    };
                }),
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
            if (page == null) return Fail("Sidan finns inte.");
            var version = SitePageStore.GetDraft(scope.Database, pageId, g.OwnerType, g.OwnerId)
                          ?? SitePageStore.GetPublished(scope.Database, pageId, g.OwnerType, g.OwnerId);
            if (version == null) return Fail("Sidan saknar innehåll.");
            var slug = scope.Database.FirstOrDefault<string>("SELECT TOP 1 Slug FROM dbo.PageAddress WHERE TargetKind = 'sitepage' AND TargetId = @0 AND IsCurrent = 1", pageId);
            return Json(new
            {
                success = true, id = page.Id, title = page.Title, visibility = page.Visibility, revision = page.Revision, slug,
                kind = page.IsFree ? "free" : "slots", templateKey = page.TemplateKey,
                minVisibility = page.IsFree ? 0 : (int)SiteTemplates.For(g.OwnerType).MostOpenLevel(page.TemplateKey!)!.Value, inMenu = SitePageStore.ListFree(scope.Database, g.OwnerType, g.OwnerId).Any(x => x.Id == pageId && x.InMenu),
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

        /// <summary>
        /// Öppnar ett stödläge (60 min) för den som FÅR göra det (kretsens admin, sajtadmin) men inte är föreningens egen admin. Kräver en tydlig anledning,
        /// loggas i SiteAudit (ActedAsSupport) och föreningens egna administratörer får mejl. Ett misslyckat mejl hindrar inte stödläget (det är loggat ändå).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: den som får öppna stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> StartSupport(string ownerType, int ownerId, string reason)
        {
            var g = await Gate(ownerType, ownerId, write: false, GateMode.StartSupport);
            if (g.Error != null) return g.Error;
            if (!g.Actor!.AsSupport && g.Grant == null)
            {
                // Ägarens egen admin behöver inget stödläge.
                var own = g.OwnerType == SiteTemplates.Club ? await _auth.IsOwnClubAdmin(g.OwnerId) : await _auth.IsOwnRegionAdmin(g.RegionCode ?? "");
                if (own) return Fail("Du är föreningens egen administratör och behöver inget stödläge.");
            }
            if (Throttled($"sitesupport:{g.Actor!.MemberId}", 6, TimeSpan.FromHours(1))) return Fail("För många stödlägen på kort tid. Försök igen om en stund.", "rate");
            var problem = SiteSupport.ReasonProblem(reason);
            if (problem != null) return Fail(problem, "reason");

            SupportGrantRow? grant;
            var now = DateTime.Now;
            using (var scope = _scopes.CreateScope())
            {
                SiteSupport.Start(scope.Database, g.Actor.MemberId!.Value, g.OwnerType, g.OwnerId, reason, now);
                grant = SiteSupport.Active(scope.Database, g.Actor.MemberId.Value, g.OwnerType, g.OwnerId, now);
                scope.Database.Execute(
                    "INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, CreatedDate) VALUES (@0, @1, @2, 1, 'owner', @1, 'support-start', @3)",
                    g.OwnerType, g.OwnerId, g.Actor.MemberId, now);
                scope.Complete();
            }
            var cur = await _members.GetCurrentMemberAsync();
            var notified = await _notifier.NotifyStartedAsync(g.OwnerType, g.OwnerId, g.OwnerName, g.RegionCode, cur?.Name ?? "En administratör", reason.Trim(), grant!.ExpiresDate);
            return Json(new { success = true, expires = grant.ExpiresDate, minutes = SiteSupport.Minutes, notified });
        }

        /// <summary>
        /// "Se som": renderar sidans utkast (annars publicerade innehåll) för en vald FAST tittare (Utloggad, Inloggad, Medlem, Styrelse) och ger ett dokument till en
        /// sandlådad iframe. Samma renderare och åtkomstregler som besökarna; adminens egen roll påverkar inte bilden. POST med antiforgery och <c>no-store</c> — aldrig en
        /// parameter på den publika adressen.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> Preview(string ownerType, int ownerId, int pageId, string persona, [FromServices] SiteBlockDataFactory blockData)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            Response.Headers["Cache-Control"] = "private, no-store";
            if (Throttled($"sitepreview:{g.OwnerType}:{g.OwnerId}", 60, TimeSpan.FromMinutes(1))) return Fail("För många förhandsvisningar på kort tid.", "rate");
            var who = SitePagePreview.Persona(g.OwnerType, persona);
            if (who == null) return Fail("Okänd tittare.");

            SitePageRow? page; SitePageVersionRow? version;
            using (var scope = _scopes.CreateScope(autoComplete: true))
            {
                page = SitePageStore.GetPage(scope.Database, pageId, g.OwnerType, g.OwnerId);
                version = page == null ? null
                    : SitePageStore.GetDraft(scope.Database, pageId, g.OwnerType, g.OwnerId) ?? SitePageStore.GetPublished(scope.Database, pageId, g.OwnerType, g.OwnerId);
            }
            if (page == null || !page.IsFree || version == null) return Fail("Sidan finns inte.");
            var content = SiteBlockSchema.Parse(version.BlocksJson, SiteContentKind.FreePage, page.VisibilityLevel, SiteTemplates.For(g.OwnerType));
            if (!content.Ok) return Fail("Sidan kan inte visas ännu: " + content.Errors[0].Message);

            var node = UmbracoContext.Content?.GetById(g.OwnerId);
            var r = SitePagePreview.Build(g.OwnerType, version.Title, page.VisibilityLevel, content, who, blockData.For(g.OwnerType, g.OwnerId, node), g.OwnerUrl);
            return Json(new { success = true, decision = r.Decision.ToString(), notice = r.Notice, document = r.Document, persona = who.Key });
        }

        /// <summary>Tittarna man kan förhandsvisa som, för den här ägarens typ.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> PreviewPersonas(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            return Json(new { success = true, personas = SitePagePreview.Personas(g.OwnerType).Select(p => new { key = p.Key, label = p.Label }) });
        }

        /// <summary>
        /// Klubbens ändringslogg: vem gjorde vad och när (senaste 100). Bara ägarens EGEN admin — stödläget läser inte loggen, som också visar stödpersonens egna spår.
        /// Namnet slås upp live; en borttagen medlem visas som "Borttagen medlem" (raden står kvar). Aldrig innehåll, bara åtgärd, sida och tid.
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ")]
        public async Task<IActionResult> Audit(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            List<SiteAuditRow> rows;
            using (var scope = _scopes.CreateScope(autoComplete: true))
                rows = SiteAuditLog.List(scope.Database, g.OwnerType, g.OwnerId, 100);
            var names = new Dictionary<int, string>();
            foreach (var id in rows.Where(r => r.ActorMemberId != null).Select(r => r.ActorMemberId!.Value).Distinct())
                names[id] = _memberService.GetById(id)?.Name ?? "Borttagen medlem";
            return Json(new
            {
                success = true,
                rows = rows.Select(r => new
                {
                    when = r.CreatedDate,
                    who = r.ActorMemberId == null ? "Borttagen medlem" : names[r.ActorMemberId.Value],
                    support = r.ActedAsSupport,
                    text = SiteAuditLog.Describe(r),
                }),
            });
        }

        /// <summary>Avslutar det egna stödläget i förtid.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin eller öppet stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> EndSupport(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            int ended;
            using (var scope = _scopes.CreateScope())
            {
                ended = SiteSupport.End(scope.Database, g.Actor!.MemberId!.Value, g.OwnerType, g.OwnerId, DateTime.Now);
                if (ended > 0)
                    scope.Database.Execute(
                        "INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, CreatedDate) VALUES (@0, @1, @2, 1, 'owner', @1, 'support-end', @3)",
                        g.OwnerType, g.OwnerId, g.Actor.MemberId, DateTime.Now);
                scope.Complete();
            }
            return Json(new { success = true, ended });
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
            var g = await Gate(ownerType, ownerId, write: false, GateMode.OwnOnly);
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

        /// <summary>
        /// Skapar föreningens egen text före/efter en systemsida (en mallkopia). Idempotent: finns den redan returneras den. Synligheten får inte vara öppnare än
        /// systemsidan själv (servern avgör, <see cref="SiteTemplate.MostOpenLevel"/>).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> CreateSlotPage(string ownerType, int ownerId, string templateKey, int visibility)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitecreate:{g.OwnerType}:{g.OwnerId}", CreatesPerDay, TimeSpan.FromDays(1))) return Fail($"Högst {CreatesPerDay} nya sidor per dygn.", "rate");
            if (visibility < 0 || visibility > (int)SiteVisibility.Dold) return Fail("Ogiltig synlighet.");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.CreateTemplateCopy(scope.Database, g.OwnerType, g.OwnerId, templateKey ?? "", (SiteVisibility)visibility, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
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
