using Umbraco.Cms.Core.IO;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Filer i Umbracos mediamapp (<c>/media/…</c>) som sajten skriver själv — profilbilder, målbilder,
    /// klubb- och händelsebilder, inbjudningar och resultatlistor (skalplanen D3, 2026-10-19).
    ///
    /// <para>Går genom <see cref="MediaFileManager.FileSystem"/>, alltså samma filsystem som Umbracos eget
    /// mediabibliotek. På disk är det <c>wwwroot/media</c> som förut; med Umbraco.StorageProviders.AzureBlob
    /// blir det blobblagringen, och Umbraco serverar <c>/media/…</c> därifrån. Adresserna ändras inte.</para>
    ///
    /// <para>⚠️ Skriv aldrig till <c>wwwroot/media</c> med <c>File</c>/<c>FileStream</c> — den filen finns
    /// bara på en server.</para>
    /// </summary>
    public class MediaFiles
    {
        private const string UrlRoot = "/media/";
        private readonly MediaFileManager _media;

        public MediaFiles(MediaFileManager media) => _media = media;

        private IFileSystem Fs => _media.FileSystem;

        /// <summary>Sparar <c>{folder}/{fileName}</c> (skriver över) och svarar med adressen <c>/media/…</c>.</summary>
        public string Save(string folder, string fileName, Stream content)
        {
            var path = RelativePath(folder, fileName);
            Fs.AddFile(path, content, overrideIfExists: true);
            return UrlRoot + path;
        }

        public async Task<string> SaveAsync(string folder, string fileName, IFormFile file)
        {
            await using var stream = file.OpenReadStream();
            return Save(folder, fileName, stream);
        }

        /// <summary>
        /// Tar bort filen bakom en adress <c>/media/…</c>. Svarar false för en adress utanför mediamappen
        /// eller en fil som inte finns. Kastar aldrig — en kvarlämnad fil är bättre än ett avbrutet byte.
        /// </summary>
        public bool DeleteByUrl(string? url)
        {
            var path = PathFromUrl(url);
            if (path == null) return false;
            try
            {
                if (!Fs.FileExists(path)) return false;
                Fs.DeleteFile(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Filerna i en mapp som matchar mönstret (t.ex. <c>ABC123_*.jpg</c>), som relativa sökvägar.</summary>
        /// <remarks>
        /// Mönstret matchas HÄR (bara <c>*</c>), inte av filsystemet: Umbracos blobbfilsystem dokumenterar
        /// inte att det stöder ett mönster, och ett tyst ignorerat mönster hade fått städningen att ta bort
        /// alla filer i mappen.
        /// </remarks>
        public IEnumerable<string> List(string folder, string pattern)
        {
            if (!Fs.DirectoryExists(folder)) return Enumerable.Empty<string>();
            return Fs.GetFiles(folder).Where(f => Matches(Path.GetFileName(f), pattern)).ToList();
        }

        /// <summary>Mönstermatchningen i <see cref="List"/>, utbruten för testerna.</summary>
        public static bool Matches(string fileName, string pattern) =>
            new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).IsMatch(fileName);

        public void Delete(string relativePath)
        {
            if (Fs.FileExists(relativePath)) Fs.DeleteFile(relativePath);
        }

        /// <summary><c>/media/a/b.jpg</c> → <c>a/b.jpg</c>, eller null om adressen inte pekar in i mediamappen.</summary>
        public static string? PathFromUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var q = url.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) url = url.Substring(0, q);
            if (!url.StartsWith(UrlRoot, StringComparison.OrdinalIgnoreCase)) return null;
            var path = Uri.UnescapeDataString(url.Substring(UrlRoot.Length));
            return FileStoreNames.IsSafeRelativePath(path) ? path : null;
        }

        private static string RelativePath(string folder, string fileName)
        {
            var path = string.IsNullOrEmpty(folder) ? fileName : folder.Trim('/') + "/" + fileName;
            if (!FileStoreNames.IsSafeRelativePath(path)) throw new ArgumentException($"Ogiltigt filnamn: {path}");
            return path;
        }
    }
}
