using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Kopplar in fillagringen (skalplanen D3). Utan konfiguration: disken, exakt som förut.
    ///
    /// <list type="bullet">
    /// <item><c>FileStorage:Provider = AzureBlob</c> → <see cref="AzureBlobFileStore"/> för sajtens egna
    /// filer, och <see cref="PublicImageMiddleware"/> för <c>/images/{område}/…</c>.</item>
    /// <item><c>Umbraco:Storage:AzureBlob:Media:ConnectionString</c> satt → Umbracos mediabibliotek i
    /// blobblagringen (paketet Umbraco.StorageProviders.AzureBlob). <see cref="MediaFiles"/> följer med
    /// av sig själv.</item>
    /// </list>
    /// </summary>
    public class FileStorageComposer : IComposer
    {
        public const string MediaConnectionKey = "Umbraco:Storage:AzureBlob:Media:ConnectionString";

        public void Compose(IUmbracoBuilder builder)
        {
            var section = builder.Config.GetSection(FileStorageOptions.SectionName);
            builder.Services.Configure<FileStorageOptions>(section);
            var options = section.Get<FileStorageOptions>() ?? new FileStorageOptions();

            if (options.UsesBlob)
            {
                builder.Services.AddSingleton<IFileStore, AzureBlobFileStore>();
                builder.Services.Configure<UmbracoPipelineOptions>(o => o.AddFilter(new UmbracoPipelineFilter(
                    "PublicImagesFromFileStore",
                    prePipeline: app => app.UseMiddleware<PublicImageMiddleware>())));
            }
            else
            {
                builder.Services.AddSingleton<IFileStore, DiskFileStore>();
            }
            // Den lokala disken finns alltid också — flytten läser därifrån.
            builder.Services.AddSingleton<DiskFileStore>();
            builder.Services.AddSingleton<MediaFiles>();
            builder.Services.AddScoped<FileStorageMigrationService>();
            builder.Services.AddHostedService<FileStorageStartupService>();

            if (!string.IsNullOrWhiteSpace(builder.Config[MediaConnectionKey]))
                builder.AddAzureBlobMediaFileSystem();
        }
    }
}
