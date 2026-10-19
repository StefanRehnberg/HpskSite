namespace HpskSite.Services
{
    /// <summary>
    /// Stores Standardmedalj proof files (PDF / image) under <c>App_Data/standard-medal-proofs</c>,
    /// outside the web root. App_Data survives deploys (only wwwroot is overwritten) and is not
    /// directly servable, so proof — personal data later bundled into SPSF Guldmedalj applications —
    /// is only reachable through an authorized controller. Mirrors DocumentService's file handling.
    /// </summary>
    public class StandardMedalProofStorage
    {
        private const string FolderName = "standard-medal-proofs";
        private static readonly string[] AllowedExtensions = { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };
        private const long MaxBytes = 10L * 1024 * 1024; // 10 MB — proof is a result list / diploma photo, not a large doc

        private readonly HpskSite.Services.Storage.IFileStore _store;

        public StandardMedalProofStorage(HpskSite.Services.Storage.IFileStore store)
        {
            _store = store;
        }

        public (bool Ok, string? Error) Validate(string fileName, long size)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                return (false, "Tillåtna filtyper: PDF, JPG, PNG, WEBP.");
            if (size <= 0)
                return (false, "Filen är tom.");
            if (size > MaxBytes)
                return (false, "Filen är för stor (max 10 MB).");
            return (true, null);
        }

        public async Task<string> SaveAsync(Stream stream, string originalFileName)
        {
            var ext = Path.GetExtension(originalFileName)?.ToLowerInvariant() ?? "";
            var storedFileName = $"{Guid.NewGuid():N}{ext}";
            await _store.SaveAsync(HpskSite.Services.Storage.FileStoreAreas.StandardMedalProofs, storedFileName, stream);
            return storedFileName;
        }

        /// <summary>
        /// Filens innehåll, eller null om den saknas. Vägrar allt som inte är ett naket filnamn
        /// (traverseringsskydd — namnet kommer ur databasen och ser därför betrott ut).
        /// </summary>
        public Task<Stream?> OpenReadAsync(string? storedFileName) =>
            IsBareName(storedFileName)
                ? _store.OpenReadAsync(HpskSite.Services.Storage.FileStoreAreas.StandardMedalProofs, storedFileName!)
                : Task.FromResult<Stream?>(null);

        public async Task DeleteAsync(string? storedFileName)
        {
            if (!IsBareName(storedFileName)) return;
            try { await _store.DeleteAsync(HpskSite.Services.Storage.FileStoreAreas.StandardMedalProofs, storedFileName!); }
            catch { /* best-effort cleanup */ }
        }

        private static bool IsBareName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..");

        public static string ContentTypeFor(string storedFileName) =>
            Path.GetExtension(storedFileName)?.ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
    }
}
