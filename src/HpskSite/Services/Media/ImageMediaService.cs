using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Media
{
    /// <summary>
    /// En uppladdad bild blir ett media i Umbracos mediabibliotek. Klubbens logga och banner och seriens logga
    /// (C3-planen S7) går hit — en regel för storlek, filtyper, mapp och filnamn i stället för en kopia per yta.
    /// </summary>
    public class ImageMediaService
    {
        public const long MaxBytes = 5 * 1024 * 1024;
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

        private readonly IMediaService _mediaService;
        private readonly IWebHostEnvironment _env;

        public ImageMediaService(IMediaService mediaService, IWebHostEnvironment env)
        {
            _mediaService = mediaService;
            _env = env;
        }

        /// <summary>
        /// Sparar bilden i mediamappen <paramref name="folderName"/> (skapas vid behov) och filen under
        /// <c>/media/{subdir}/</c>. Svarar med mediets UDI (värdet en mediaväljare tar) och adressen, eller ett fel.
        /// </summary>
        public async Task<(string? Udi, string? Url, string? Error)> SaveAsync(IFormFile? file, string folderName, string subdir)
        {
            if (file == null || file.Length == 0) return (null, null, "Ingen fil valdes.");
            if (file.Length > MaxBytes) return (null, null, "Filen är för stor — högst 5 MB.");
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(extension)) return (null, null, "Bara JPG, PNG, GIF och WebP går att ladda upp.");

            var folder = _mediaService.GetRootMedia().FirstOrDefault(m => m.Name == folderName);
            if (folder == null)
            {
                folder = _mediaService.CreateMedia(folderName, -1, "Folder");
                _mediaService.Save(folder);
            }

            var fileName = Path.GetFileName(file.FileName);
            var uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid()}{extension}";
            var physicalDir = Path.Combine(_env.ContentRootPath, "wwwroot", "media", subdir);
            Directory.CreateDirectory(physicalDir);
            var physicalPath = Path.Combine(physicalDir, uniqueFileName);
            using (var stream = new FileStream(physicalPath, FileMode.Create))
                await file.CopyToAsync(stream);

            var relativePath = $"/media/{subdir}/{uniqueFileName}";
            var media = _mediaService.CreateMedia(fileName, folder.Id, "Image");
            media.SetValue("umbracoFile", relativePath);
            media.SetValue("umbracoExtension", extension.TrimStart('.'));
            media.SetValue("umbracoBytes", file.Length.ToString());
            if (!_mediaService.Save(media).Success)
            {
                if (File.Exists(physicalPath)) File.Delete(physicalPath);
                return (null, null, "Bilden kunde inte sparas i mediabiblioteket.");
            }
            return (media.GetUdi().ToString(), relativePath, null);
        }
    }
}
