using HpskSite.Services.Storage;
using Microsoft.AspNetCore.Http;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Media
{
    /// <summary>
    /// En uppladdad fil blir ett media i Umbracos mediabibliotek. Klubbens logga och banner, seriens logga
    /// (C3-planen S7), kretsens och händelsernas bilder, tävlingens inbjudan och resultatlista går hit — en
    /// regel för mapp, filnamn och skrivväg i stället för en kopia per yta.
    ///
    /// <para>Filen skrivs genom <see cref="MediaFiles"/> (Umbracos mediafilsystem), så den hamnar i
    /// blobblagringen när den är påslagen (skalplanen D3). Adressen är alltid <c>/media/{subdir}/…</c>.</para>
    /// </summary>
    public class ImageMediaService
    {
        public const long MaxBytes = 5 * 1024 * 1024;
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

        private readonly IMediaService _mediaService;
        private readonly MediaFiles _files;

        public ImageMediaService(IMediaService mediaService, MediaFiles files)
        {
            _mediaService = mediaService;
            _files = files;
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

            var (media, url, error) = await SaveMediaAsync(file, folderName, subdir, "Image");
            return media == null ? (null, null, error) : (media.GetUdi().ToString(), url, null);
        }

        /// <summary>
        /// Sparar en fil som ett media av typen <paramref name="mediaTypeAlias"/> ("Image" eller "File") utan
        /// egen kontroll av typ och storlek — anroparen har prövat dem. Svarar med mediet och adressen, eller ett fel.
        /// </summary>
        public async Task<(IMedia? Media, string? Url, string? Error)> SaveMediaAsync(
            IFormFile file, string folderName, string subdir, string mediaTypeAlias)
        {
            var folder = _mediaService.GetRootMedia().FirstOrDefault(m => m.Name == folderName);
            if (folder == null)
            {
                folder = _mediaService.CreateMedia(folderName, -1, "Folder");
                _mediaService.Save(folder);
            }

            var fileName = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var baseName = FileStoreNames.SafeFileName(Path.GetFileNameWithoutExtension(fileName));
            var uniqueFileName = $"{baseName}_{Guid.NewGuid()}{extension}";

            var relativePath = await _files.SaveAsync(subdir, uniqueFileName, file);
            var media = _mediaService.CreateMedia(fileName, folder.Id, mediaTypeAlias);
            media.SetValue("umbracoFile", relativePath);
            media.SetValue("umbracoExtension", extension.TrimStart('.'));
            media.SetValue("umbracoBytes", file.Length.ToString());
            if (!_mediaService.Save(media).Success)
            {
                _files.DeleteByUrl(relativePath);
                return (null, null, "Filen kunde inte sparas i mediabiblioteket.");
            }
            return (media, relativePath, null);
        }
    }
}
