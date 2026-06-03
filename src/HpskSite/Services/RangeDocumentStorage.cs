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

        private readonly IWebHostEnvironment _environment;

        public RangeDocumentStorage(IWebHostEnvironment environment)
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
            var path = Path.Combine(GetStorageDir(), storedFileName);
            using var output = new FileStream(path, FileMode.Create);
            await stream.CopyToAsync(output);
            return storedFileName;
        }

        /// <summary>Resolves a stored reference to an absolute path, or null if missing. Rejects
        /// anything that isn't a bare file name (path-traversal guard).</summary>
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
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                _ => "image/jpeg"
            };
    }
}
