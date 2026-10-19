namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Konfigurationen <c>FileStorage</c> (skalplanen D3). Utan sektion = disken, exakt som förut.
    ///
    /// <code>
    /// "FileStorage": { "Provider": "AzureBlob", "ConnectionString": "...", "ContainerName": "hpsk-files" }
    /// </code>
    ///
    /// <para>⚠️ Umbracos mediabibliotek styrs SEPARAT av <c>Umbraco:Storage:AzureBlob:Media</c> (paketet
    /// Umbraco.StorageProviders.AzureBlob). De två slås på tillsammans — se körordningen D6.</para>
    /// </summary>
    public class FileStorageOptions
    {
        public const string SectionName = "FileStorage";
        public const string DiskProvider = "Disk";
        public const string AzureBlobProvider = "AzureBlob";

        public string Provider { get; set; } = DiskProvider;
        public string? ConnectionString { get; set; }
        public string ContainerName { get; set; } = "hpsk-files";

        public bool UsesBlob =>
            string.Equals(Provider, AzureBlobProvider, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(ConnectionString);
    }
}
