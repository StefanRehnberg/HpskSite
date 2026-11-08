using HpskSite.Services.Security;
using HpskSite.Services.SitePages;
using HpskSite.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Serverar bilderna på klubbens och kretsens egna sidor (klubbsidorna 2c). Åtkomsten avgörs av <see cref="SiteImageAccess"/> vid VARJE anrop
    /// (en publicerad, för tittaren synlig referens, annars 404 — aldrig 403, som avslöjar att bilden finns). Bilden läses ur det privata området
    /// <see cref="SiteImages.Area"/> som en ström; innehållet är redan omkodat av oss och typen kommer ur raden, aldrig ur en ändelse eller klientens header.
    /// </summary>
    public class SiteFileController : SurfaceController
    {
        private readonly SiteImageService _images;
        private readonly SiteViewerResolver _viewers;
        private readonly IFileStore _store;
        private readonly IContentService _content;
        private readonly ILogger<SiteFileController> _logger;

        public SiteFileController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            SiteImageService images, SiteViewerResolver viewers, IFileStore store, IContentService content, ILogger<SiteFileController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _images = images; _viewers = viewers; _store = store; _content = content; _logger = logger;
        }

        [HttpGet]
        [PublicEndpoint("Bilden serveras bara om en publicerad, för tittaren synlig referens finns (SiteImageAccess); annars 404")]
        public async Task<IActionResult> Image(int id)
        {
            try
            {
                if (id <= 0) return NotFound();
                var (file, decision) = await _images.AuthorizeAsync(id, async (type, ownerId) =>
                {
                    string? code = null;
                    if (type == SiteTemplates.Region) code = _content.GetById(ownerId)?.GetValue<string>("regionCode");
                    return await _viewers.ResolveAsync(type, ownerId, code);
                });
                if (file == null || decision == SiteImageDecision.NotFound) return NotFound();

                var stream = await _store.OpenReadAsync(SiteImages.Area, file.StoredFile);
                if (stream == null) { _logger.LogWarning("Bildraden {FileId} finns men filen {Name} saknas i lagringen", file.Id, file.StoredFile); return NotFound(); }

                UploadedContent.AddHardeningHeaders(Response.Headers);
                Response.Headers["Content-Disposition"] = "inline";
                // Bara när varje publicerad referens är öppen för Alla får bilden cachas publikt; allt annat är privat och cachas aldrig.
                Response.Headers["Cache-Control"] = decision == SiteImageDecision.ServePublic ? "public, max-age=3600" : "private, no-store";
                if (decision == SiteImageDecision.ServePrivate) Response.Headers["X-Robots-Tag"] = "noindex";
                else Response.Headers["Vary"] = "Cookie";
                return File(stream, file.ContentType);   // typen ur raden (omkodad av oss), aldrig ur klienten
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SiteFile/Image {Id} kunde inte prövas — svarar 404", id);
                return NotFound();
            }
        }
    }
}
