namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Stores competition-preparation documents (sanktion, inbjudan, säkerhetsplan, ban-ritning,
    /// bokningar…) under <c>App_Data/competition-prep-docs</c>, outside the web root. App_Data survives
    /// deploys (only wwwroot is overwritten) and is not directly servable, so files are only reachable
    /// through an authorized controller. Mirrors <see cref="HpskSite.Services.StandardMedalProofStorage"/>.
    /// </summary>
    public class PrepDocumentStorage
    {
        private const string FolderName = "competition-prep-docs";
        private static readonly string[] AllowedExtensions =
            { ".pdf", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv" };
        private const long MaxBytes = 25L * 1024 * 1024; // 25 MB — a ritning/inbjudan PDF, not a video

        private readonly HpskSite.Services.Storage.IFileStore _store;

        public PrepDocumentStorage(HpskSite.Services.Storage.IFileStore store)
        {
            _store = store;
        }

        public (bool Ok, string? Error) Validate(string fileName, long size)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
                return (false, "Tillåtna filtyper: PDF, bild, Word, Excel, PowerPoint, text.");
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
            await _store.SaveAsync(HpskSite.Services.Storage.FileStoreAreas.PrepDocuments, storedFileName, stream);
            return storedFileName;
        }

        /// <summary>
        /// Filens innehåll, eller null om den saknas. Vägrar allt som inte är ett naket filnamn
        /// (traverseringsskydd — namnet kommer ur databasen och ser därför betrott ut).
        /// </summary>
        public Task<Stream?> OpenReadAsync(string? storedFileName) =>
            IsBareName(storedFileName)
                ? _store.OpenReadAsync(HpskSite.Services.Storage.FileStoreAreas.PrepDocuments, storedFileName!)
                : Task.FromResult<Stream?>(null);

        public async Task DeleteAsync(string? storedFileName)
        {
            if (!IsBareName(storedFileName)) return;
            try { await _store.DeleteAsync(HpskSite.Services.Storage.FileStoreAreas.PrepDocuments, storedFileName!); }
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
                ".gif" => "image/gif",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".csv" => "text/csv",
                ".txt" => "text/plain",
                _ => "application/octet-stream"
            };
    }
}
