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
            affected = r.Affected,
        });

        private static string StatusMessage(SiteWriteStatus s) => s switch
        {
            SiteWriteStatus.Conflict => "Någon annan har ändrat sidan. Din text finns kvar; ladda om för att se deras ändring.",
            SiteWriteStatus.NotFound => "Sidan finns inte.",
            SiteWriteStatus.TooManyPages => $"Ni har nått taket på {SitePageStore.MaxFreePagesPerOwner} sidor.",
            SiteWriteStatus.NothingToPublish => "Det finns inga ändringar att publicera.",
            SiteWriteStatus.SlugTaken => "Adressen går inte att använda.",
            SiteWriteStatus.PersonalNumber => "Det här ser ut som ett personnummer.",
            SiteWriteStatus.Suspended => "Sidan är stoppad av pistol.nu och kan inte publiceras. Kontakta oss om du undrar varför.",
            SiteWriteStatus.ConvertOff => "Det går inte att göra om sidorna för den här föreningen än.",
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
            var convertEnabled = SiteOwnerData.ConvertEnabled(scope.Database, g.OwnerType, g.OwnerId);
            // Läget per vanlig sida (K5a). Bara ägarens EGEN admin kan göra om: stödläget får se men aldrig ändra (GateMode.OwnOnly).
            var sysStates = SiteSystemPages.Describe(g.OwnerType, copies, convertEnabled, canWrite: g.Grant == null);
            object Row(SitePageListItem p) => new
            {
                id = p.Id, title = p.Title, visibility = p.Visibility, status = p.Status, slug = p.Slug, revision = p.Revision, inMenu = p.InMenu, suspended = p.SuspendedDate != null,
                url = p.Slug == null ? null : g.OwnerUrl + p.Slug + "/", updated = p.UpdatedDate, deleted = p.DeletedDate,
                parentId = p.ParentPageId, sortOrder = p.SortOrder, childCount = p.ChildCount,
            };
            // Antal nya anmälningar (märket vid Anmälningar i rälsen). Saknas tabellen (migreringen inte körd) faller inte Hemsidan: platsen visar ett fel i stället.
            int signupNew = 0; var signupReady = true;
            try { signupNew = SiteSignupStore.NewCount(scope.Database, g.OwnerType, g.OwnerId); } catch (Microsoft.Data.SqlClient.SqlException) { signupReady = false; }
            return Json(new
            {
                success = true, signupNew, signupReady, ownerName = g.OwnerName, ownerUrl = g.OwnerUrl, termsAccepted = accepted,
                support = g.Grant == null ? null : new { reason = g.Grant.Reason, expires = g.Grant.ExpiresDate, minutes = SiteSupport.Minutes }, termsVersion = SiteOwnerData.CurrentTermsVersion,
                maxPages = SitePageStore.MaxFreePagesPerOwner,
                // Systemsidorna som ett länkblock får peka på (aldrig Admin, som saknar adress).
                // Varje vanlig sida bär sitt läge (mode: asBefore | draft | converted, word = orden användaren ser), sidans id/revision när den har en rad, och
                // om "Ändra den här sidan" får visas. panels = de paneler ägaren får välja på sidan (för kortet och dialogerna).
                convertEnabled,
                // Golv-tabellen (det öppnaste varje blocktyp får vara hos den här ägartypen) och utgångna typer: editorn har ingen egen kopia av reglerna.
                blockFloors = SiteBlockSchema.FloorTable(g.OwnerType).ToDictionary(kv => kv.Key, kv => (int)kv.Value),
                retiredBlocks = SiteBlockSchema.Types.Values.Where(t => t.Retired).Select(t => t.Name),
                systemPages = sysStates.Select(s => new
                {
                    key = s.Key, label = s.Label, mode = SiteSystemPages.Code(s.Mode), word = SiteSystemPages.Word(s.Mode),
                    pageId = s.PageId, revision = s.Revision, pendingChanges = s.PendingChanges, canConvert = s.CanConvert, level = s.Level,
                    panels = SiteSystemPages.PanelsFor(g.OwnerType, s.Key),
                }),
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
                menuLinks = SiteMenuLinkStore.List(scope.Database, g.OwnerType, g.OwnerId).Select(l => new { id = l.Id, label = l.Label, url = l.Url, visibility = (int)l.Visibility }),
                maxLinks = SiteMenuLinkRules.MaxLinks,
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
            // Sidor under sidor: en undersida har sin förälders nivå som golv (aldrig öppnare), och en förälder vet hur många undersidor den har.
            var parentRow = page.IsFree && page.ParentPageId is int parentId ? SitePageStore.GetPage(scope.Database, parentId, g.OwnerType, g.OwnerId) : null;
            var childCount = page.IsFree ? SitePageStore.ChildCount(scope.Database, pageId) : 0;
            return Json(new
            {
                success = true, id = page.Id, title = page.Title, visibility = page.Visibility, revision = page.Revision, slug,
                parentId = parentRow?.Id, parentTitle = parentRow == null ? null : (parentRow.PublishedTitle ?? parentRow.Title), childCount,
                kind = SiteBlockSchema.KindOf(page.IsFree, version.BlocksJson) switch { SiteContentKind.FreePage => "free", SiteContentKind.ConvertedPage => "converted", _ => "slots" }, templateKey = page.TemplateKey,
                minVisibility = page.IsFree ? (int?)parentRow?.Visibility ?? 0 : (int)(SiteBlockSchema.KindOf(page.IsFree, version.BlocksJson) == SiteContentKind.ConvertedPage ? SiteConversion.PageFloor(g.OwnerType, page.TemplateKey!) : SiteTemplates.For(g.OwnerType).MostOpenLevel(page.TemplateKey!)!.Value), inMenu = SitePageStore.ListFree(scope.Database, g.OwnerType, g.OwnerId).Any(x => x.Id == pageId && x.InMenu),
                url = slug == null ? null : g.OwnerUrl + slug + "/", hasUnpublishedChanges = page.HasUnpublishedChanges, published = page.PublishedVersionId != null,
                blocksJson = version.BlocksJson, savedDate = version.SavedDate,
                // En omgjord sida: alla paneler som får finnas på den (namn, regler, nivå), och om omvandlingen redan är publicerad (då ångras den med Lägg tillbaka, annars med Avbryt).
                panels = !page.IsFree && page.TemplateKey != null ? SiteSystemPages.CatalogFor(g.OwnerType, page.TemplateKey) : null,
                pageKey = page.TemplateKey,
                convertedLive = !page.IsFree && SitePageStore.GetPublished(scope.Database, pageId, g.OwnerType, g.OwnerId)?.SchemaVersion == SiteBlockSchema.ConvertedSchemaVersion,
            });
        }

        // ── Bilder (2c) ──────────────────────────────────────────────────────

        public const int UploadsPerHour = 40;

        /// <summary>Ägarens bilder (med var de används) och kvoten.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> Files(string ownerType, int ownerId)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var (count, bytes) = SiteFiles.Usage(scope.Database, g.OwnerType, g.OwnerId);
            return Json(new
            {
                success = true,
                files = SiteFiles.List(scope.Database, g.OwnerType, g.OwnerId).Select(f => new
                {
                    id = f.Id, name = f.FileName, size = f.Size, width = f.Width, height = f.Height, uploaded = f.UploadedDate, usedBy = f.UsedBy,
                    url = SiteImages.UrlFor(f.Id),
                }),
                count, bytes, maxBytes = SiteImages.QuotaBytesPerOwner, maxFiles = SiteImages.MaxFilesPerOwner,
            });
        }

        /// <summary>Laddar upp en bild. Bilden avkodas och skrivs om (metadata bort, förminskas), lagras privat och får en rad; kvoten prövas under lås.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(25 * 1024 * 1024)]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> UploadImage(string ownerType, int ownerId, IFormFile? upload, [FromServices] HpskSite.Services.Storage.IFileStore store)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitefile-up:{g.OwnerType}:{g.OwnerId}", UploadsPerHour, TimeSpan.FromHours(1))) return Fail("För många uppladdningar på kort tid. Försök igen om en stund.", "rate");
            if (upload == null || upload.Length == 0) return Fail("Ingen fil vald.");

            SiteImageResult? img; SiteImageFailure failure;
            await using (var input = upload.OpenReadStream())
                (img, failure) = await SiteImages.ProcessAsync(input, upload.FileName, upload.Length);
            if (img == null) return Fail(SiteImages.Message(failure));

            var stored = Guid.NewGuid().ToString("N");
            var fileName = stored + SiteImages.ExtensionFor(img.ContentType);
            // Filen före raden: en rad utan fil syns som ett fel, en fil utan rad är bara skräp som kan städas.
            await using (var ms = new MemoryStream(img.Bytes))
                await store.SaveAsync(SiteImages.Area, fileName, ms);

            SiteFileStatus status; int fileId;
            var now = DateTime.Now;
            using (var scope = _scopes.CreateScope())
            {
                status = SiteFiles.Insert(scope.Database, g.OwnerType, g.OwnerId, SiteImages.DisplayName(upload.FileName), stored, img, g.Actor!.MemberId, now, out fileId);
                if (status == SiteFileStatus.Ok)
                {
                    scope.Database.Execute(
                        @"INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, ContentHash, CreatedDate)
                          VALUES (@0, @1, @2, @3, N'file', @4, N'file-upload', CONVERT(BINARY(32), @5), @6)",
                        g.OwnerType, g.OwnerId, g.Actor.MemberId, g.Actor.AsSupport, fileId, img.Hash, now);
                    scope.Complete();
                }
            }
            if (status != SiteFileStatus.Ok)
            {
                await store.DeleteAsync(SiteImages.Area, fileName);
                return status switch
                {
                    SiteFileStatus.QuotaFull => Fail($"Utrymmet för bilder är fullt ({SiteImages.QuotaBytesPerOwner / 1024 / 1024} MB). Ta bort bilder som inte används.", "quota"),
                    SiteFileStatus.TooMany => Fail($"Ni har nått taket på {SiteImages.MaxFilesPerOwner} bilder. Ta bort bilder som inte används.", "quota"),
                    _ => Fail("Föreningen finns inte."),
                };
            }
            return Json(new { success = true, id = fileId, @ref = $"file:{fileId}", name = SiteImages.DisplayName(upload.FileName), width = img.Width, height = img.Height, size = img.Bytes.Length, url = SiteImages.UrlFor(fileId) });
        }

        /// <summary>Tar bort en bild som ingen sida använder. En bild i bruk vägras och säger vilka sidor som använder den.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> DeleteFile(string ownerType, int ownerId, int fileId, [FromServices] HpskSite.Services.Storage.IFileStore store)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}", WritesPerMinute, TimeSpan.FromMinutes(1))) return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteFileStatus status; SiteFileRow? row;
            using (var scope = _scopes.CreateScope())
            {
                status = SiteFiles.Delete(scope.Database, fileId, g.OwnerType, g.OwnerId, g.Actor!, DateTime.Now, out row);
                if (status == SiteFileStatus.Ok) scope.Complete();
            }
            if (status == SiteFileStatus.NotFound) return Fail("Bilden finns inte.");
            if (status == SiteFileStatus.InUse) return Fail("Bilden används på en sida. Ta bort den från sidan först.", "inuse");
            try { await store.DeleteAsync(SiteImages.Area, row!.StoredFile); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Bildfilen {0} kunde inte tas bort ur lagringen: {1}", row!.StoredFile, ex.Message); }   // raden är raderad: bilden serveras inte längre
            return Json(new { success = true });
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
        /// "Förhandsvisa" under en besökare i Vem ser sidan?: ger en signerad biljett (<see cref="SitePreviewTicket"/>) och en adress till den RIKTIGA sidan med biljetten, att öppna i en
        /// ny flik. Fliken behandlas då som den besökaren (<see cref="SitePreviewMiddleware"/>): bara läsning, bara föreningens egna sidor, aldrig Ekonomi, Styrelsearbete eller
        /// Hemsidan. POST med antiforgery, aldrig en parameter på en publik adress.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> StartPreview(string ownerType, int ownerId, string persona, string url, [FromServices] SitePreviewTickets tickets)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            Response.Headers["Cache-Control"] = "private, no-store";
            if (Throttled($"sitepreview:{g.OwnerType}:{g.OwnerId}", 60, TimeSpan.FromMinutes(1))) return Fail("För många förhandsvisningar på kort tid.", "rate");
            var who = SitePagePreview.Persona(g.OwnerType, persona);
            if (who == null) return Fail("Okänd besökare.");

            // Adressen kommer utifrån: bara en adress under föreningens egen, utan fråga eller ankare, och som förhandsläget själv tillåter (aldrig Ekonomi, Styrelse, Hemsidan eller adminsidor).
            if (string.IsNullOrEmpty(url) || url.Length > 300 || !url.StartsWith('/') || url.StartsWith("//") || url.Contains('?') || url.Contains('#')) return Fail("Sidans adress är ogiltig.");
            var probe = new SitePreviewTicket(g.OwnerType, g.OwnerId, who.Key, g.OwnerUrl, who.Key == "anon" ? null : url, "-", 0);
            if (!SitePreviewPolicy.PathAllowed(probe, url)) return Fail("Den sidan går inte att förhandsvisa.");

            // En annan besökare än utloggad förhandsvisas bara på en PUBLICERAD egen sida (den ritas på servern för just den tittaren). Övriga sidor hämtar sina delar med den
            // inloggades egen session och kan därför inte visas som en annan person.
            if (who.Key != "anon")
            {
                var slug = url.Trim('/').Split('/').Last();
                if (!string.Equals(url.TrimEnd('/'), g.OwnerUrl.TrimEnd('/') + "/" + slug, StringComparison.OrdinalIgnoreCase)) return Fail("Den sidan går inte att förhandsvisa.");
                int found;
                using (var scope = _scopes.CreateScope(autoComplete: true))
                    found = scope.Database.ExecuteScalar<int>(
                        @"SELECT COUNT(*) FROM dbo.PageAddress a JOIN dbo.SitePage p ON p.Id = a.TargetId
                          WHERE a.TargetKind = 'sitepage' AND a.IsCurrent = 1 AND a.OwnerType = @0 AND a.OwnerId = @1 AND a.Slug = @2
                            AND p.TemplateKey IS NULL AND p.PublishedVersionId IS NOT NULL AND p.DeletedDate IS NULL", g.OwnerType, g.OwnerId, slug);
                if (found == 0) return Fail("Bara en publicerad egen sida kan förhandsvisas som den besökaren. Publicera sidan först.");
            }

            // Biljetten knyts till den inloggade (samma läsning av kakan som förhandsläget gör vid varje anrop).
            var auth = await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(HttpContext, Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);
            var member = auth?.Succeeded == true ? auth.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value : null;
            if (string.IsNullOrEmpty(member)) return Fail(NotAllowed, "denied");

            var token = tickets.Issue(g.OwnerType, g.OwnerId, who.Key, g.OwnerUrl, who.Key == "anon" ? null : url, member, DateTimeOffset.UtcNow);
            HttpContext.RequestServices.GetService<Microsoft.Extensions.Logging.ILogger<SiteEditController>>()?
                .LogInformation("SITEPREVIEW: {Owner} {OwnerId} som {Persona} på {Url}", g.OwnerType, g.OwnerId, who.Key, url);   // spår: vem som öppnade ett förhandsläge och för vilken besökare
            return Json(new { success = true, url = url + "?" + SitePreviewPolicy.QueryKey + "=" + Uri.EscapeDataString(token), persona = who.Key });
        }

        /// <summary>
        /// "Vem ser sidan?": en tabell över vad Utloggad, Inloggad, Medlem och Styrelse ser av sidan (sidan i menyn, varje panel och varje eget block), ur samma regler som besökarna
        /// möter (<see cref="SitePagePreview.Matrix"/>). Läser utkastet, annars den publicerade versionen (<c>basis</c> säger vilket). <c>pageId</c> = en fri eller omgjord sida,
        /// annars <c>templateKey</c> = en vanlig sida som inte är omgjord. Inget ritas och inget skrivs, så inga personuppgifter eller paneldata lämnar servern.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör eller öppet stödläge, brytare 2 PÅ")]
        public async Task<IActionResult> Visibility(string ownerType, int ownerId, int pageId, string? templateKey)
        {
            var g = await Gate(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            Response.Headers["Cache-Control"] = "private, no-store";
            if (Throttled($"sitepreview:{g.OwnerType}:{g.OwnerId}", 60, TimeSpan.FromMinutes(1))) return Fail("För många förfrågningar på kort tid.", "rate");
            var template = SiteTemplates.For(g.OwnerType);

            if (pageId <= 0)
            {
                var def = template.PageByKey(templateKey ?? "");
                if (def == null || def.IsAdmin || PanelCatalog.Standard(g.OwnerType, def.Key, true).Count == 0) return Fail("Den sidan går inte att granska.");
                var std = SitePagePreview.Matrix(g.OwnerType, def.Label, def.Key, SiteConversion.PageVisibility(g.OwnerType, def.Key), SiteConversion.Build(g.OwnerType, def.Key, null));
                return Json(new { success = true, kind = "standard", basis = "standard", columns = std.Columns, rows = std.Rows, summary = std.Summary, publicUrl = g.OwnerUrl + (def.IsHome ? "" : def.Slug + "/") });
            }

            SitePageRow? page; SitePageVersionRow? version; bool isDraft;
            using (var scope = _scopes.CreateScope(autoComplete: true))
            {
                page = SitePageStore.GetPage(scope.Database, pageId, g.OwnerType, g.OwnerId);
                var draft = page == null ? null : SitePageStore.GetDraft(scope.Database, pageId, g.OwnerType, g.OwnerId);
                version = draft ?? (page == null ? null : SitePageStore.GetPublished(scope.Database, pageId, g.OwnerType, g.OwnerId));
                isDraft = draft != null;
            }
            if (page == null || version == null) return Fail("Sidan finns inte.");
            var kind = SiteBlockSchema.KindOf(page.IsFree, version.BlocksJson);
            if (kind == SiteContentKind.TemplateCopy) return Fail("Sidan finns inte.");
            var content = SiteBlockSchema.Parse(version.BlocksJson, kind, page.VisibilityLevel, template, page.TemplateKey);
            if (!content.Ok) return Fail("Sidan kan inte granskas ännu: " + content.Errors[0].Message);
            var m = SitePagePreview.Matrix(g.OwnerType, version.Title, kind == SiteContentKind.ConvertedPage ? page.TemplateKey : null, page.VisibilityLevel, content);
            string? url = null;
            if (kind == SiteContentKind.ConvertedPage && page.TemplateKey != null)
            {
                var d = template.PageByKey(page.TemplateKey);
                if (d != null) url = g.OwnerUrl + (d.IsHome ? "" : d.Slug + "/");
            }
            return Json(new { success = true, kind = kind == SiteContentKind.ConvertedPage ? "converted" : "free", basis = isDraft ? "draft" : "published", published = page.PublishedVersionId != null, columns = m.Columns, rows = m.Rows, summary = m.Summary, publicUrl = url });
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
        public async Task<IActionResult> CreatePage(string ownerType, int ownerId, string title, string? slug, int visibility, int? parentPageId = null)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitecreate:{g.OwnerType}:{g.OwnerId}", CreatesPerDay, TimeSpan.FromDays(1)))
                return Fail($"Högst {CreatesPerDay} nya sidor per dygn.", "rate");
            if (visibility < 0 || visibility > (int)SiteVisibility.Dold) return Fail("Ogiltig synlighet.");

            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.CreatePage(scope.Database, _facts, g.OwnerType, g.OwnerId, title, slug, (SiteVisibility)visibility, g.Actor!, DateTime.Now, parentPageId is > 0 ? parentPageId : null);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Lägger en egen sida under en annan egen sida (<c>parentPageId</c>), eller gör den till egen nivå (utan <c>parentPageId</c>). Reglerna prövas i <see cref="SitePageTree"/>.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SetParent(string ownerType, int ownerId, int pageId, int? parentPageId)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.SetParent(scope.Database, pageId, g.OwnerType, g.OwnerId, parentPageId is > 0 ? parentPageId : null, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Flyttar en egen sida ett steg upp (<c>direction</c> -1) eller ner (+1) bland sina syskon.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> MovePage(string ownerType, int ownerId, int pageId, int direction)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (direction is not (-1 or 1)) return Fail("Okänd riktning.");
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:{pageId}", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.MoveInOrder(scope.Database, pageId, g.OwnerType, g.OwnerId, direction, g.Actor!, DateTime.Now);
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

        // ── Göra om en systemsida (konvertera-planen K2) ─────────────────────────
        // Bara ägarens EGEN admin (OwnOnly): ett stödläge gör aldrig om en förenings sidor. Brytare 3 prövas av skrivlagret under ägarens lås.

        /// <summary>Gör om en systemsida till en blocksida: ett utkast med dagens paneler. Den levande sidan är oförändrad tills utkastet publiceras.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade; brytare 3 prövas i skrivlagret")]
        public async Task<IActionResult> ConvertPage(string ownerType, int ownerId, string templateKey)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            if (Throttled($"siteconvert:{g.OwnerType}:{g.OwnerId}", WritesPerMinute, TimeSpan.FromMinutes(1))) return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.ConvertTemplatePage(scope.Database, _facts, g.OwnerType, g.OwnerId, templateKey ?? "", g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Avbryt en omvandling som inte är publicerad: utkastet kastas, den levande sidan rörs inte.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> CancelConversion(string ownerType, int ownerId, int pageId, int expectedRevision)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.CancelConversion(scope.Database, _facts, pageId, g.OwnerType, g.OwnerId, expectedRevision, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return Map(r);
        }

        /// <summary>Lägg tillbaka: sidan är en vanlig systemsida igen (dess tidigare version publiceras; allt finns kvar i historiken).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> RevertConversion(string ownerType, int ownerId, int pageId, int expectedRevision)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            SiteWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePageWriter.RevertConversion(scope.Database, _facts, pageId, g.OwnerType, g.OwnerId, expectedRevision, g.Actor!, DateTime.Now);
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

        // ── Anmälningar (blocket Anmälan/ansökan). Personuppgifter: bara föreningens EGEN administratör (GateMode.OwnOnly), aldrig stödläget. ──────────
        private static byte? StatusFilter(string? s) => s switch { "new" => 0, "queued" => 1, "denied" => 2, "approved" => 3, _ => null };

        private static SiteSignupStore.Filter FilterOf(string? status, int? pageId, string? blockId, string? q, bool archived) =>
            new(StatusFilter(status), pageId, blockId is { Length: > 0 and <= 12 } ? blockId : null, q is { Length: > 0 } ? (q.Length > 100 ? q[..100] : q) : null, archived);

        /// <summary>Anmälningarna, en sida i taget (50), med antal per status och formulären att filtrera på. Personnumret ingår inte i listan, bara i en enskild anmälan.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ")]
        public async Task<IActionResult> SignupEntries(string ownerType, int ownerId, string? status, int? pageId, string? blockId, string? q, bool archived = false, int skip = 0)
        {
            var g = await Gate(ownerType, ownerId, write: false, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            try
            {
                using var scope = _scopes.CreateScope(autoComplete: true);
                var f = FilterOf(status, pageId, blockId, q, archived);
                var rows = SiteSignupStore.List(scope.Database, g.OwnerType, g.OwnerId, f, Math.Max(0, skip), SiteSignupStore.PageSize);
                var (byStatus, archivedCount) = SiteSignupStore.Counts(scope.Database, g.OwnerType, g.OwnerId);
                return Json(new
                {
                    success = true,
                    items = rows.Select(r => new
                    {
                        id = r.Id, pageId = r.PageId, blockId = r.BlockId, formTitle = r.FormTitle, choice = r.Choice, status = (int)r.Status, name = r.Name, email = r.Email, phone = r.Phone,
                        created = r.CreatedDate, handled = r.HandledDate, hasNote = r.AdminNote != null, hasPersonalNumber = r.PersonalNumber != null,
                    }),
                    total = SiteSignupStore.Count(scope.Database, g.OwnerType, g.OwnerId, f), pageSize = SiteSignupStore.PageSize,
                    counts = new { @new = byStatus[0], queued = byStatus[1], denied = byStatus[2], approved = byStatus[3], archived = archivedCount },
                    forms = SiteSignupStore.Forms(scope.Database, g.OwnerType, g.OwnerId).Select(x => new { pageId = x.PageId, blockId = x.BlockId, title = x.FormTitle, total = x.Total, newCount = x.NewCount }),
                });
            }
            catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException)
            {
                return Fail("Anmälningarna kan inte läsas just nu. Kontakta pistol.nu om det fortsätter.", "schema");
            }
        }

        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ")]
        public async Task<IActionResult> SignupEntry(string ownerType, int ownerId, int id)
        {
            var g = await Gate(ownerType, ownerId, write: false, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var r = SiteSignupStore.Get(scope.Database, id, g.OwnerType, g.OwnerId);
            if (r == null) return Fail("Anmälan finns inte.");
            return Json(new
            {
                success = true,
                entry = new
                {
                    id = r.Id, formTitle = r.FormTitle, choice = r.Choice, status = (int)r.Status, name = r.Name, email = r.Email, phone = r.Phone, gender = r.Gender, personalNumber = r.PersonalNumber,
                    address = r.Address, postalCode = r.PostalCode, city = r.City, message = r.Message, adminNote = r.AdminNote, created = r.CreatedDate, handled = r.HandledDate, archived = r.ArchivedDate,
                },
            });
        }

        /// <summary>Själva skrivningen, EFTER att Gate prövat ägaren: tak per minut, en transaktion, ett svar. Tar emot det redan prövade GateResult.</summary>
        private IActionResult SignupWrite(GateResult g, Func<NPoco.IDatabase, DateTime, bool> act, string notFound)
        {
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:signup", WritesPerMinute * 3, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            bool ok;
            using (var scope = _scopes.CreateScope())
            {
                ok = act(scope.Database, DateTime.Now);
                if (ok) scope.Complete();
            }
            return ok ? Json(new { success = true }) : Fail(notFound);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SetSignupStatus(string ownerType, int ownerId, int id, int status)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            if (status is < 0 or > 3) return Fail("Okänd status.");
            return SignupWrite(g, (db, now) => SiteSignupWriter.Status(db, g.OwnerType, g.OwnerId, id, (SiteSignupStatus)status, g.Actor!, now), "Anmälan finns inte.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SaveSignupNote(string ownerType, int ownerId, int id, string? note)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            if ((note?.Length ?? 0) > 500) return Fail("Anteckningen får vara högst 500 tecken.");
            return SignupWrite(g, (db, now) => SiteSignupWriter.Note(db, g.OwnerType, g.OwnerId, id, note, g.Actor!, now), "Anmälan finns inte.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> ArchiveSignup(string ownerType, int ownerId, int id, bool archive)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            return SignupWrite(g, (db, now) => SiteSignupWriter.Archive(db, g.OwnerType, g.OwnerId, id, archive, g.Actor!, now), "Anmälan finns inte.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> DeleteSignup(string ownerType, int ownerId, int id)
        {
            var g = await Gate(ownerType, ownerId, write: true, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            return SignupWrite(g, (db, now) => SiteSignupWriter.Delete(db, g.OwnerType, g.OwnerId, id, g.Actor!, now), "Anmälan finns inte.");
        }

        /// <summary>Anmälningarna som en CSV-fil för Excel (samma filter som listan, alla sidor). Exporten loggas.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens EGEN administratör (inte stödläge), brytare 2 PÅ")]
        public async Task<IActionResult> ExportSignups(string ownerType, int ownerId, string? status, int? pageId, string? blockId, string? q, bool archived = false)
        {
            var g = await Gate(ownerType, ownerId, write: false, GateMode.OwnOnly);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:export", 20, TimeSpan.FromHours(1))) return Fail("För många exporter på kort tid. Vänta en stund.", "rate");
            string csv;
            using (var scope = _scopes.CreateScope())
            {
                var f = FilterOf(status, pageId, blockId, q, archived);
                var rows = new List<SiteSignupRow>();
                for (var skip = 0; skip < SiteSignupStore.MaxPerOwner; skip += 500)   // alla sidor: en export får aldrig tyst kapas
                {
                    var part = SiteSignupStore.List(scope.Database, g.OwnerType, g.OwnerId, f, skip, 500);
                    rows.AddRange(part);
                    if (part.Count < 500) break;
                }
                csv = SiteSignupStore.ToCsv(rows);
                SitePageWriter.Audit(scope.Database, g.OwnerType, g.OwnerId, g.Actor!, "form", 0, "entry-export", null, DateTime.Now);
                scope.Complete();
            }
            Response.Headers["Cache-Control"] = "private, no-store";
            return File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"anmalningar-{DateTime.Now:yyyy-MM-dd}.csv");
        }

        // ── Externa länkar i menyn (Hemsidan → Länkar i menyn) ───────────────────────────────────────────────
        private IActionResult MapLink(SiteLinkWriteResult r) => Json(new { success = r.Ok, message = r.Ok ? null : r.Message, id = r.Id });

        /// <summary>Lägger till (<c>linkId</c> utelämnad) eller ändrar en extern länk i menyn: namn, https-adress och vem som ser den.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SaveMenuLink(string ownerType, int ownerId, int? linkId, string? label, string? url, int visibility)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:links", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteLinkWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SiteMenuLinkWriter.Save(scope.Database, g.OwnerType, g.OwnerId, linkId, label, url, visibility, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return MapLink(r);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> RemoveMenuLink(string ownerType, int ownerId, int linkId)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:links", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteLinkWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SiteMenuLinkWriter.Remove(scope.Database, g.OwnerType, g.OwnerId, linkId, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return MapLink(r);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen administratör, brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> MoveMenuLink(string ownerType, int ownerId, int linkId, int direction)
        {
            var g = await Gate(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitewrite:{g.OwnerType}:{g.OwnerId}:links", WritesPerMinute, TimeSpan.FromMinutes(1)))
                return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            SiteLinkWriteResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SiteMenuLinkWriter.Move(scope.Database, g.OwnerType, g.OwnerId, linkId, direction, g.Actor!, DateTime.Now);
                if (r.Ok) scope.Complete();
            }
            return MapLink(r);
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
