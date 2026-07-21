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

        private readonly IWebHostEnvironment _environment;

        public PrepDocumentStorage(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        private string GetStorageDir()
        {
            var dir = Path.Combine(_environment.ContentRootPath, "App_Data", FolderName);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
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
            var path = Path.Combine(GetStorageDir(), storedFileName);
            using var output = new FileStream(path, FileMode.Create);
            await stream.CopyToAsync(output);
            return storedFileName;
        }

        /// <summary>Absolute path for a stored file, or null if missing. Rejects non-bare names (traversal guard).</summary>
        public string? GetFilePath(string? storedFileName)
        {
            if (string.IsNullOrWhiteSpace(storedFileName)) return null;
            if (storedFileName.Contains('/') || storedFileName.Contains('\\') || storedFileName.Contains(".."))
                return null;
            var path = Path.Combine(GetStorageDir(), storedFileName);
            return File.Exists(path) ? path : null;
        }

        public void Delete(string? storedFileName)
        {
            var path = GetFilePath(storedFileName);
            if (path != null)
            {
                try { File.Delete(path); } catch { /* best-effort cleanup */ }
            }
        }

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
