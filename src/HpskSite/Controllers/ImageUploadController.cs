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

            try
            {
                var uploadsDir = Path.Combine(_webHostEnvironment.WebRootPath, "images", "uploads");
                Directory.CreateDirectory(uploadsDir);

                // Generate unique filename preserving extension
                var extension = Path.GetExtension(upload.FileName)?.ToLowerInvariant() ?? ".jpg";
                var fileName = $"{Guid.NewGuid():N}{extension}";
                var filePath = Path.Combine(uploadsDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await upload.CopyToAsync(stream);
                }

                var url = $"/images/uploads/{fileName}";
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
