using Azure.Storage.Blobs.Models;
using HpskSite.Services.Hosting;
using Microsoft.Extensions.Options;
using Umbraco.StorageProviders.AzureBlob.IO;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Startkontroll för fillagringen (skalplanen D3). Bara när blobblagringen är påslagen:
    /// skapar behållarna om de saknas (Umbracos mediepaket gör det inte själv — då faller varje
    /// uppladdning av en bild) och provar att skriva och läsa. Går det inte larmar den Critical: då
    /// syns inga kvitton, dokument eller bilder.
    /// </summary>
    public class FileStorageStartupService : IsolatedBackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<FileStorageStartupService> _logger;

        public FileStorageStartupService(IServiceProvider services, ILogger<FileStorageStartupService> logger)
        {
            _services = services;
            _logger = logger;
        }

        /// <summary>Skapar Umbracos mediebehållare om den saknas. Gör ingenting när mediet ligger på disk.</summary>
        public static void EnsureMediaContainer(IServiceProvider services, IConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(config[FileStorageComposer.MediaConnectionKey])) return;
            var options = services.GetRequiredService<IOptionsMonitor<AzureBlobFileSystemOptions>>()
                .Get(AzureBlobFileSystemOptions.MediaFileSystemName);
            AzureBlobFileSystem.CreateIfNotExists(options, PublicAccessType.None);
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            var config = _services.GetRequiredService<IConfiguration>();
            var store = _services.GetRequiredService<IFileStore>();
            _logger.LogInformation("Fillagringen: sajtens filer i {Store}, mediabiblioteket i {Media}.",
                store.ProviderName,
                string.IsNullOrWhiteSpace(config[FileStorageComposer.MediaConnectionKey]) ? "Disk" : "AzureBlob");

            try
            {
                EnsureMediaContainer(_services, config);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "FILLAGRINGEN SVARAR INTE: mediabibliotekets blobbehållare kunde inte nås eller skapas. Bilder och dokument i /media syns inte och kan inte laddas upp.");
            }

            if (store is DiskFileStore) return;
            try
            {
                const string probe = "startup-probe.txt";
                var marker = Guid.NewGuid().ToString("N");
                await store.SaveAsync(FileStoreAreas.AiChatIndex, probe, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(marker)), stoppingToken);
                await using var read = await store.OpenReadAsync(FileStoreAreas.AiChatIndex, probe, stoppingToken);
                using var reader = read == null ? null : new StreamReader(read);
                var back = reader == null ? null : await reader.ReadToEndAsync(stoppingToken);
                if (back != marker)
                    _logger.LogCritical("FILLAGRINGEN SVARAR FEL: en provfil i blobblagringen gick inte att läsa tillbaka.");
                await store.DeleteAsync(FileStoreAreas.AiChatIndex, probe, stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "FILLAGRINGEN SVARAR INTE: blobblagringen för sajtens filer kunde inte nås. Kvitton, dokument, bevis och kursmaterial syns inte.");
            }
        }
    }
}
