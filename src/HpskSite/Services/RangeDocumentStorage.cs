namespace HpskSite.Services
{
    /// <summary>
    /// Stores shooting-range compliance documents (permits, besiktningsprotokoll, bullerutredning,
    /// markundersökning, skötselplan …) under <c>App_Data/range-documents</c>, outside the web root.
    /// App_Data survives deploys (only wwwroot is overwritten) and isn't directly servable, so the
    /// files are reachable only through an authorized, steward-gated controller endpoint.
    /// Mirrors <see cref="StandardMedalProofStorage"/>.
    /// </summary>
    public class RangeDocumentStorage
    {
        private const string FolderName = "range-documents";
        private static readonly string[] AllowedExtensions = { ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".doc", ".docx" };
        private const long MaxBytes = 25L * 1024 * 1024; // 25 MB — permits/protokoll/bullerutredningar can be sizeable

        private readonly HpskSite.Services.Storage.IFileStore _store;

        public RangeDocumentStorage(HpskSite.Services.Storage.IFileStore store)
        {
            _store = store;
        }

        public (bool Ok, string? Error) Validate(string fileName, long size)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                return (false, "Tillåtna filtyper: PDF, JPG, PNG, WEBP, DOC, DOCX.");
            if (size <= 0)
                return (false, "Filen är tom.");
            if (size > MaxBytes)
                return (false, "Filen är för stor (max 25 MB).");
            return (true, null);
        }

        public async Task<string> SaveAsync(Stream stream, string originalFileName)
        {
            var ext = Path.GetExtension(originalFileName)?.ToLowerInvariant() ?? "";
            var storedFileName = $"{Guid.NewGuid():N}{ext}";
            await _store.SaveAsync(HpskSite.Services.Storage.FileStoreAreas.RangeDocuments, storedFileName, stream);
            return storedFileName;
        }

        /// <summary>
        /// Filens innehåll, eller null om den saknas. Vägrar allt som inte är ett naket filnamn
        /// (traverseringsskydd — namnet kommer ur databasen och ser därför betrott ut).
        /// </summary>
        public Task<Stream?> OpenReadAsync(string? storedFileName) =>
            IsBareName(storedFileName)
                ? _store.OpenReadAsync(HpskSite.Services.Storage.FileStoreAreas.RangeDocuments, storedFileName!)
                : Task.FromResult<Stream?>(null);

        public async Task DeleteAsync(string? storedFileName)
        {
            if (!IsBareName(storedFileName)) return;
            try { await _store.DeleteAsync(HpskSite.Services.Storage.FileStoreAreas.RangeDocuments, storedFileName!); }
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
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                _ => "image/jpeg"
            };
    }
}
