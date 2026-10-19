using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// <see cref="IFileStore"/> i en Azure-blobbehållare (eller något som talar samma protokoll, t.ex.
    /// Azurite lokalt). Blobbarna heter <c>app-data/{område}/{namn}</c> respektive
    /// <c>images/{område}/{namn}</c> — samma uppdelning som på disk, så en flytt är en ren kopiering.
    ///
    /// <para>⚠️ Behållaren är PRIVAT. Publika bilder serveras av <see cref="PublicImageMiddleware"/>,
    /// inte direkt ur blobblagringen, så adresserna <c>/images/…</c> i databasen gäller oförändrade.</para>
    /// </summary>
    public class AzureBlobFileStore : IFileStore
    {
        private readonly BlobContainerClient _container;
        private readonly SemaphoreSlim _createLock = new(1, 1);
        private volatile bool _created;

        public AzureBlobFileStore(IOptions<FileStorageOptions> options)
            : this(new BlobContainerClient(options.Value.ConnectionString, options.Value.ContainerName)) { }

        public AzureBlobFileStore(BlobContainerClient container) => _container = container;

        public string ProviderName => FileStorageOptions.AzureBlobProvider;

        public static string BlobName(string area, string name) =>
            (FileStoreAreas.IsPublic(area) ? "images/" : "app-data/") + area + "/" + name;

        private async Task EnsureContainerAsync(CancellationToken ct)
        {
            if (_created) return;
            await _createLock.WaitAsync(ct);
            try
            {
                if (_created) return;
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
                _created = true;
            }
            finally { _createLock.Release(); }
        }

        private BlobClient? Blob(string area, string name) =>
            FileStoreNames.IsValidArea(area) && FileStoreNames.IsValidName(name)
                ? _container.GetBlobClient(BlobName(area, name))
                : null;

        public async Task SaveAsync(string area, string name, Stream content, CancellationToken ct = default)
        {
            FileStoreNames.Require(area, name);
            await EnsureContainerAsync(ct);
            await Blob(area, name)!.UploadAsync(content, overwrite: true, ct);
        }

        public async Task<bool> CreateIfAbsentAsync(string area, string name, Stream content, CancellationToken ct = default)
        {
            FileStoreNames.Require(area, name);
            await EnsureContainerAsync(ct);
            try
            {
                await Blob(area, name)!.UploadAsync(content,
                    new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                return false;
            }
        }

        public async Task<Stream?> OpenReadAsync(string area, string name, CancellationToken ct = default)
        {
            var blob = Blob(area, name);
            if (blob == null) return null;
            try
            {
                // Hela innehållet i minnet: filerna här är högst 25 MB, och en sökbar ström behövs för
                // intervallförfrågningar (PDF i webbläsaren).
                var download = await blob.DownloadContentAsync(ct);
                return download.Value.Content.ToStream();
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async Task<bool> ExistsAsync(string area, string name, CancellationToken ct = default)
        {
            var blob = Blob(area, name);
            if (blob == null) return false;
            try { return (await blob.ExistsAsync(ct)).Value; }
            catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
        }

        public async Task<bool> DeleteAsync(string area, string name, CancellationToken ct = default)
        {
            var blob = Blob(area, name);
            if (blob == null) return false;
            try { return (await blob.DeleteIfExistsAsync(cancellationToken: ct)).Value; }
            catch (RequestFailedException ex) when (ex.Status == 404) { return false; }
        }

        public async IAsyncEnumerable<StoredFileInfo> ListAsync(string area, string? prefix = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!FileStoreNames.IsValidArea(area)) yield break;
            var root = BlobName(area, "");
            var pages = _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, root + (prefix ?? ""), ct);
            IAsyncEnumerator<BlobItem>? e = null;
            try
            {
                e = pages.GetAsyncEnumerator(ct);
                while (true)
                {
                    bool more;
                    try { more = await e.MoveNextAsync(); }
                    catch (RequestFailedException ex) when (ex.Status == 404) { yield break; } // behållaren finns inte än
                    if (!more) yield break;
                    var item = e.Current;
                    yield return new StoredFileInfo(item.Name.Substring(root.Length), item.Properties.ContentLength ?? 0);
                }
            }
            finally
            {
                if (e != null) await e.DisposeAsync();
            }
        }
    }
}
