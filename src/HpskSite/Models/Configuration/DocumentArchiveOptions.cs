namespace HpskSite.Models.Configuration
{
    /// <summary>
    /// Configuration options for the document archive feature
    /// </summary>
    public class DocumentArchiveOptions
    {
        /// <summary>
        /// Maximum file size in MB for a single upload
        /// </summary>
        public int MaxFileSizeMB { get; set; } = 20;

        /// <summary>
        /// Comma-separated list of allowed file extensions
        /// </summary>
        public string AllowedExtensions { get; set; } = ".pdf,.doc,.docx,.xls,.xlsx,.ppt,.pptx,.txt,.csv,.zip,.jpg,.jpeg,.png";

        /// <summary>
        /// Storage path relative to App_Data
        /// </summary>
        public string StoragePath { get; set; } = "documents";

        /// <summary>
        /// Default total storage limit per club in MB
        /// </summary>
        public int DefaultClubStorageLimitMB { get; set; } = 100;

        /// <summary>
        /// Default total storage limit per region in MB
        /// </summary>
        public int DefaultRegionStorageLimitMB { get; set; } = 200;

        /// <summary>
        /// Returns the allowed extensions as an array
        /// </summary>
        public string[] GetAllowedExtensionsArray()
        {
            return AllowedExtensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
