using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    public class ImageUploadController : SurfaceController
    {
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly ILogger<ImageUploadController> _logger;

        private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/jpg", "image/png", "image/gif", "image/webp"
        };

        /// <summary>Ändelser vi sparar bilder med (säkerhetsplanen 2026-11-02) — aldrig .html/.svg/.xml.</summary>
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp"
        };

        private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB

        public ImageUploadController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService authorizationService,
            IWebHostEnvironment webHostEnvironment,
            ILogger<ImageUploadController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _authorizationService = authorizationService;
            _webHostEnvironment = webHostEnvironment;
            _logger = logger;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(IFormFile upload)
        {
            // Require at least club admin level access
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var managedClubs = await _authorizationService.GetManagedClubIds();
            if (!isSiteAdmin && !managedClubs.Any())
            {
                return Json(new { error = new { message = "Åtkomst nekad" } });
            }

            if (upload == null || upload.Length == 0)
            {
                return Json(new { error = new { message = "Ingen fil vald" } });
            }

            if (upload.Length > MaxFileSize)
            {
                return Json(new { error = new { message = "Filen är för stor (max 5 MB)" } });
            }

            if (!AllowedContentTypes.Contains(upload.ContentType))
            {
                return Json(new { error = new { message = "Endast bildfiler (JPG, PNG, GIF, WebP) är tillåtna" } });
            }

            // ⚠️ Säkerhetsplanen 2026-11-02 (A6/H5): klientens Content-Type-header är inget bevis, och ändelsen togs förut
            // ur filnamnet — en klubbadmin kunde ladda upp x.html med skript som image/png och få det serverat från
            // sajtens adress. Nu: ändelsen måste vara en bildändelse OCH innehållet måste vara en riktig bild
            // (kända bildformat, TIFF avstängd).
            var extension = Path.GetExtension(upload.FileName)?.ToLowerInvariant() ?? "";
            if (!AllowedExtensions.Contains(extension))
            {
                _logger.LogWarning("SEC: Bilduppladdning nekades — ändelsen {Extension} är ingen bildändelse", extension);
                return Json(new { error = new { message = "Endast bildfiler (JPG, PNG, GIF, WebP) är tillåtna" } });
            }
            try
            {
                await using var probe = upload.OpenReadStream();
                var info = await SixLabors.ImageSharp.Image.IdentifyAsync(
                    new SixLabors.ImageSharp.Formats.DecoderOptions { Configuration = HpskSite.Services.Media.SafeImageConfiguration.Instance },
                    probe);
                if (info == null) throw new SixLabors.ImageSharp.UnknownImageFormatException("okänt bildformat");
            }
            catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException)
            {
                _logger.LogWarning("SEC: Bilduppladdning nekades — innehållet är ingen bild som sajten kan läsa");
                return Json(new { error = new { message = "Filen är ingen giltig bild" } });
            }

            try
            {
                // Generate unique filename preserving extension — stored through the file store
                // (skalplanen D3); the URL /images/uploads/… is unchanged.
                if (!HpskSite.Services.Storage.FileStoreNames.IsValidName("x" + extension)) extension = ".jpg";
                var fileName = $"{Guid.NewGuid():N}{extension}";
                await using (var stream = upload.OpenReadStream())
                    await HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Storage.IFileStore>().SaveAsync(HpskSite.Services.Storage.FileStoreAreas.Uploads, fileName, stream);

                var url = HpskSite.Services.Storage.FileStoreAreas.PublicUrl(HpskSite.Services.Storage.FileStoreAreas.Uploads, fileName);
                _logger.LogInformation("Image uploaded: {FileName} ({Size} bytes)", fileName, upload.Length);

                // CKEditor 5 expects { url: "..." }
                return Json(new { url });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading image");
                return Json(new { error = new { message = "Ett fel uppstod vid uppladdning" } });
            }
        }
    }
}
