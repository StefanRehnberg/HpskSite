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
    /// Skrivytan för föreningens nyheter (<c>/{ägare}/nyheter/skriv/</c>). Egen controller: styrelsen får skriva nyheter men kommer aldrig in i Hemsidan
    /// (som är ägarens EGEN admin). Varje anrop prövar ägaren på nytt (id:t kommer utifrån) och rätten ur <see cref="SiteViewerResolver.NewsRightsAsync"/>:
    /// ägarens egen admin, sajtadmin och styrelsen skriver, bara ägarens egen admin och sajtadmin tar bort. Villkoren för klubbens sidor måste vara
    /// accepterade (samma som Hemsidan). Nyhets-id:t slås upp bara i <see cref="SitePosts"/>, alltid mot den ägare Gate fastställt.
    /// </summary>
    public class SiteNewsController : SurfaceController
    {
        public const int WritesPerMinute = 30;
        public const int UploadsPerHour = 60;

        private readonly IScopeProvider _scopes;
        private readonly SiteOwnerService _owners;
        private readonly RateCounterService _rate;
        private readonly IContentService _content;
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;
        private readonly SiteViewerResolver _viewers;

        public SiteNewsController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            IScopeProvider scopes, SiteOwnerService owners, RateCounterService rate, IContentService content, IMemberManager members,
            IMemberService memberService, SiteViewerResolver viewers)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _scopes = scopes; _owners = owners; _rate = rate; _content = content; _members = members; _memberService = memberService; _viewers = viewers;
        }

        private sealed record Gate(IActionResult? Error, string OwnerType = "", int OwnerId = 0, string OwnerUrl = "/", SiteActor? Actor = null, string AuthorName = "", bool CanDelete = false);

        private IActionResult Fail(string message, string? code = null) => Json(new { success = false, message, code });

        private bool Throttled(string key, int limit, TimeSpan window) => _rate.Increment(key, window) > limit;

        private async Task<Gate> GateAsync(string ownerType, int ownerId, bool write, bool needsDelete = false)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region) return new Gate(Fail("Okänd ägartyp."));
            if (ownerId <= 0) return new Gate(Fail("Okänd förening."));
            var node = _content.GetById(ownerId);
            var alias = ownerType == SiteTemplates.Club ? "club" : "regionalPage";
            if (node == null || node.Trashed || node.ContentType.Alias != alias) return new Gate(Fail("Föreningen finns inte."));
            if (!_owners.IsEditingEnabled(ownerType, ownerId)) return new Gate(Fail("Nyheterna är inte påslagna för den här föreningen.", "off"));

            var regionCode = node.GetValue<string>("regionCode");
            var viewer = await _viewers.ResolveAsync(ownerType, ownerId, regionCode);
            var (canWrite, canDelete) = await _viewers.NewsRightsAsync(ownerType, ownerId, regionCode, viewer);
            if (!canWrite || (needsDelete && !canDelete))
                return new Gate(Fail(needsDelete ? "Bara klubbens administratör kan ta bort en nyhet." : "Bara klubbens administratörer och styrelse kan skriva nyheter.", "denied"));

            var cur = await _members.GetCurrentMemberAsync();
            var member = cur?.Email == null ? null : _memberService.GetByEmail(cur.Email);
            if (member == null) return new Gate(Fail("Du måste vara inloggad.", "denied"));

            if (write)
            {
                using var scope = _scopes.CreateScope(autoComplete: true);
                if (!SiteOwnerData.TermsAccepted(scope.Database, ownerType, ownerId))
                    return new Gate(Fail("Villkoren för klubbens sidor är inte accepterade än. Klubbens administratör godkänner dem i Hemsidan.", "terms"));
            }
            var url = UmbracoContext.Content?.GetById(ownerId)?.Url() ?? "/";
            return new Gate(null, ownerType, ownerId, url, new SiteActor(member.Id), member.Name ?? "", canDelete);
        }

        private static string PostUrl(Gate g, string slug) => g.OwnerUrl.TrimEnd('/') + "/nyheter/" + slug + "/";

        private object Dto(Gate g, SitePostRow p) => new
        {
            id = p.Id, title = p.Title, slug = p.Slug, excerpt = p.Excerpt, bodyHtml = SiteHtmlSanitizerSafe(p.BodyHtml), coverFileId = p.CoverFileId, coverAlt = p.CoverAlt,
            status = (int)p.Status, visibility = (int)p.ReadVisibility, revision = p.Revision, publishedDate = p.PublishedDate, authorName = p.AuthorName,
            url = PostUrl(g, p.Slug), canDelete = g.CanDelete, mailedDate = p.MailedDate, mailedCount = p.MailedCount,
        };

        private static string SiteHtmlSanitizerSafe(string html) => HpskSite.Extensions.SiteHtmlSanitizer.Sanitize(html);   // saneras vid rendering också: en lagrad rad är inte betrodd

        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ")]
        public async Task<IActionResult> Get(string ownerType, int ownerId, int id)
        {
            var g = await GateAsync(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var p = SitePosts.Get(scope.Database, id, g.OwnerType, g.OwnerId);
            return p == null ? Fail("Nyheten finns inte.", "notfound") : Json(new { success = true, post = Dto(g, p) });
        }

        /// <summary>Skapar (id 0) eller ändrar en nyhet. En gammal flik (fel revision) får ett besked och ändrar ingenting.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> Save(string ownerType, int ownerId, int id, int revision, string? title, string? bodyHtml, string? excerpt, int? coverFileId, string? coverAlt, int visibility, string? publishedDate = null)
        {
            var g = await GateAsync(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitenews:{g.OwnerType}:{g.OwnerId}", WritesPerMinute, TimeSpan.FromMinutes(1))) return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            if (!Enum.IsDefined(typeof(SiteVisibility), visibility)) return Fail("Välj vem som får läsa nyheten.", "invalid");
            DateTime? shownDate = null;
            if (!string.IsNullOrWhiteSpace(publishedDate))
            {
                if (!DateTime.TryParseExact(publishedDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var pd)) return Fail("Datumet går inte att läsa.", "invalid");
                shownDate = pd.Date == DateTime.Today ? DateTime.Now : pd.Date.AddHours(12);
            }
            var input = new SitePostInput(title, bodyHtml, excerpt, coverFileId is > 0 ? coverFileId : null, coverAlt, (SiteVisibility)visibility, shownDate);
            SitePostWrite r;
            using (var scope = _scopes.CreateScope())
            {
                r = id <= 0
                    ? SitePosts.Create(scope.Database, g.OwnerType, g.OwnerId, input, new SitePostAuthor(g.Actor!.MemberId, g.AuthorName), DateTime.Now, g.Actor)
                    : SitePosts.Update(scope.Database, id, g.OwnerType, g.OwnerId, revision, input, g.Actor!, DateTime.Now);
                if (r.Result == SitePostResult.Ok) scope.Complete();
            }
            return Map(g, r);
        }

        /// <summary>Publicerar eller drar tillbaka. <paramref name="publishedDate"/> (valfritt, yyyy-MM-dd) är datumet nyheten visas med.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> SetPublished(string ownerType, int ownerId, int id, int revision, bool publish, string? publishedDate)
        {
            var g = await GateAsync(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitenews:{g.OwnerType}:{g.OwnerId}", WritesPerMinute, TimeSpan.FromMinutes(1))) return Fail("För många ändringar på kort tid. Vänta en stund.", "rate");
            DateTime? date = null;
            if (!string.IsNullOrWhiteSpace(publishedDate))
            {
                if (!DateTime.TryParseExact(publishedDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) return Fail("Datumet går inte att läsa.", "invalid");
                date = d.Date == DateTime.Today ? DateTime.Now : d.Date.AddHours(12);
            }
            SitePostWrite r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePosts.SetPublished(scope.Database, id, g.OwnerType, g.OwnerId, revision, publish, date, g.Actor!, DateTime.Now);
                if (r.Result == SitePostResult.Ok) scope.Complete();
            }
            return Map(g, r);
        }

        /// <summary>Tar bort en nyhet (mjukt). Bara ägarens egen admin och sajtadmin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin eller sajtadmin (SiteNewsRules.CanDelete), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> Delete(string ownerType, int ownerId, int id)
        {
            var g = await GateAsync(ownerType, ownerId, write: true, needsDelete: true);
            if (g.Error != null) return g.Error;
            SitePostResult r;
            using (var scope = _scopes.CreateScope())
            {
                r = SitePosts.Delete(scope.Database, id, g.OwnerType, g.OwnerId, g.Actor!, DateTime.Now);
                if (r == SitePostResult.Ok) scope.Complete();
            }
            return r == SitePostResult.Ok ? Json(new { success = true, url = g.OwnerUrl.TrimEnd('/') + "/nyheter/" }) : Fail("Nyheten finns inte.", "notfound");
        }

        private IEnumerable<int> RegionClubIds(Gate g) =>
            g.OwnerType == SiteTemplates.Region ? SiteRegion.Clubs(UmbracoContext.Content?.GetById(g.OwnerId)).Select(c => c.Id).ToList() : Enumerable.Empty<int>();

        /// <summary>Hur många som skulle få nyheten i mejlet, och om den går att mejla. Skickar ingenting.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ")]
        public async Task<IActionResult> MailPreview(string ownerType, int ownerId, int id, [FromServices] SiteNewsMailer mailer)
        {
            var g = await GateAsync(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            SitePostRow? p;
            using (var scope = _scopes.CreateScope(autoComplete: true)) p = SitePosts.Get(scope.Database, id, g.OwnerType, g.OwnerId);
            if (p == null) return Fail("Nyheten finns inte.", "notfound");
            var plan = mailer.Plan(p, g.OwnerType, g.OwnerId, RegionClubIds(g));
            return Json(new { success = true, canMail = plan.CanMail, reason = plan.Reason, count = plan.Recipients.Count, mailedDate = p.MailedDate, mailedCount = p.MailedCount });
        }

        /// <summary>Mejlar nyheten till medlemmarna. Högst en gång per nyhet; svaret säger hur många som fick den.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> MailMembers(string ownerType, int ownerId, int id, [FromServices] SiteNewsMailer mailer)
        {
            var g = await GateAsync(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            if (Throttled($"sitenewsmail:{g.OwnerType}:{g.OwnerId}", 3, TimeSpan.FromDays(1))) return Fail("Det har skickats flera nyhetsmejl i dag. Försök igen i morgon.", "rate");
            SitePostRow? p;
            using (var scope = _scopes.CreateScope(autoComplete: true)) p = SitePosts.Get(scope.Database, id, g.OwnerType, g.OwnerId);
            if (p == null) return Fail("Nyheten finns inte.", "notfound");
            var node = UmbracoContext.Content?.GetById(g.OwnerId);
            var ownerName = node?.Value<string>("clubName") ?? node?.Value<string>("regionName") ?? node?.Name ?? "";
            var url = Request.PublicBase() + PostUrl(g, p.Slug);
            var r = await mailer.SendAsync(g.OwnerType, g.OwnerId, p, ownerName, url, RegionClubIds(g));
            return r.Started
                ? Json(new { success = true, sent = r.Sent, failed = r.Failed })
                : Fail(r.Message ?? "Det gick inte att mejla.", "refused");
        }

        /// <summary>Föreningens bilder (för omslaget och bilder i texten). Samma bibliotek som Hemsidan.</summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ")]
        public async Task<IActionResult> Files(string ownerType, int ownerId, [FromServices] DocumentService docs)
        {
            var g = await GateAsync(ownerType, ownerId, write: false);
            if (g.Error != null) return g.Error;
            using var scope = _scopes.CreateScope(autoComplete: true);
            var (count, imageBytes) = SiteFiles.Usage(scope.Database, g.OwnerType, g.OwnerId);
            var docType = g.OwnerType == SiteTemplates.Region ? HpskSite.Models.DocumentOwnerType.Region : HpskSite.Models.DocumentOwnerType.Club;
            var bytes = imageBytes + docs.GetDocumentBytes(docType, g.OwnerId);
            List<SiteLibraryFile> files; object albums;
            try
            {
                files = SiteImageAlbumStore.Files(scope.Database, g.OwnerType, g.OwnerId);
                var links = SiteImageAlbumStore.Links(scope.Database, g.OwnerType, g.OwnerId).GroupBy(l => l.AlbumId).ToDictionary(x => x.Key, x => x.Select(l => l.FileId).ToList());
                albums = SiteImageAlbumStore.Albums(scope.Database, g.OwnerType, g.OwnerId).Select(a => new { id = a.Id, name = a.Name, fileIds = links.GetValueOrDefault(a.Id) ?? new List<int>() }).ToList();
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                albums = Array.Empty<object>();
                files = SiteFiles.List(scope.Database, g.OwnerType, g.OwnerId).Select(f => new SiteLibraryFile { Id = f.Id, FileName = f.FileName, Size = f.Size, Width = f.Width, Height = f.Height, UploadedDate = f.UploadedDate, UsedBy = f.UsedBy }).ToList();
            }
            return Json(new
            {
                success = true,
                files = files.Select(f => new { id = f.Id, name = f.FileName, description = f.Description, size = f.Size, width = f.Width, height = f.Height, usedBy = f.UsedBy, url = SiteImages.UrlFor(f.Id) }),
                albums, count, bytes, maxBytes = docs.GetStorageLimitBytes(docType, g.OwnerId), maxFiles = SiteImages.MaxFilesPerOwner,
            });
        }

        /// <summary>Laddar upp en bild till föreningens bibliotek (samma bearbetning och kvot som Hemsidan: avkodas, metadata bort, förminskas).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [RequestSizeLimit(25 * 1024 * 1024)]
        [HpskSite.Services.Security.InCodeAuth("Gate: ägarens egen admin, sajtadmin eller styrelsen (SiteNewsRules), brytare 2 PÅ, villkoren accepterade")]
        public async Task<IActionResult> UploadImage(string ownerType, int ownerId, IFormFile? upload, [FromServices] HpskSite.Services.Storage.IFileStore store, [FromServices] DocumentService docs)
        {
            var g = await GateAsync(ownerType, ownerId, write: true);
            if (g.Error != null) return g.Error;
            var docType = g.OwnerType == SiteTemplates.Region ? HpskSite.Models.DocumentOwnerType.Region : HpskSite.Models.DocumentOwnerType.Club;
            var limitBytes = docs.GetStorageLimitBytes(docType, g.OwnerId);
            var quotaBytes = SiteImages.QuotaFor(limitBytes, docs.GetDocumentBytes(docType, g.OwnerId));
            if (Throttled($"sitefile-up:{g.OwnerType}:{g.OwnerId}", UploadsPerHour, TimeSpan.FromHours(1))) return Fail("För många uppladdningar på kort tid. Försök igen om en stund.", "rate");
            if (upload == null || upload.Length == 0) return Fail("Ingen fil vald.");

            SiteImageResult? img; SiteImageFailure failure;
            await using (var input = upload.OpenReadStream())
                (img, failure) = await SiteImages.ProcessAsync(input, upload.FileName, upload.Length);
            if (img == null) return Fail(SiteImages.Message(failure));

            var stored = Guid.NewGuid().ToString("N");
            var fileName = stored + SiteImages.ExtensionFor(img.ContentType);
            await using (var ms = new MemoryStream(img.Bytes))
                await store.SaveAsync(SiteImages.Area, fileName, ms);

            SiteFileStatus status; int fileId;
            var now = DateTime.Now;
            using (var scope = _scopes.CreateScope())
            {
                status = SiteFiles.Insert(scope.Database, g.OwnerType, g.OwnerId, SiteImages.DisplayName(upload.FileName), stored, img, g.Actor!.MemberId, now, out fileId, quotaBytes);
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
                    SiteFileStatus.QuotaFull => Fail($"Utrymmet är fullt ({limitBytes / 1024 / 1024} MB för bilder och dokument tillsammans). Ta bort bilder eller dokument som inte används.", "quota"),
                    SiteFileStatus.TooMany => Fail($"Ni har nått taket på {SiteImages.MaxFilesPerOwner} bilder. Ta bort bilder som inte används.", "quota"),
                    _ => Fail("Föreningen finns inte."),
                };
            }
            return Json(new { success = true, id = fileId, name = SiteImages.DisplayName(upload.FileName), width = img.Width, height = img.Height, thumb = SiteImages.ThumbUrlFor(fileId, 320), url = SiteImages.UrlFor(fileId) });
        }

        private IActionResult Map(Gate g, SitePostWrite r)
        {
            if (r.Result == SitePostResult.Ok) return Json(new { success = true, id = r.Id, revision = r.Revision, url = null as string, message = (string?)null, post = LoadDto(g, r.Id) });
            var message = r.Result switch
            {
                SitePostResult.Conflict => "Någon annan har ändrat nyheten. Din text finns kvar; ladda om sidan för att se deras ändring.",
                SitePostResult.NotFound => "Nyheten finns inte.",
                _ => r.Error ?? "Det gick inte att spara.",
            };
            return Json(new { success = false, status = r.Result.ToString(), message });
        }

        private object? LoadDto(Gate g, int id)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var p = SitePosts.Get(scope.Database, id, g.OwnerType, g.OwnerId);
            return p == null ? null : Dto(g, p);
        }
    }
}
