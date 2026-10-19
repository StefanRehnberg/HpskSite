using System.Runtime.CompilerServices;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// <see cref="IFileStore"/> på serverns disk — samma kataloger som sajten alltid använt:
    /// privata områden under <c>App_Data/{område}</c>, publika under <c>wwwroot/images/{område}</c>.
    /// </summary>
    public class DiskFileStore : IFileStore
    {
        private readonly string _appDataRoot;
        private readonly string _imagesRoot;

        public DiskFileStore(IWebHostEnvironment env)
            : this(Path.Combine(env.ContentRootPath, "App_Data"),
                   Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "images")) { }

        public DiskFileStore(string appDataRoot, string imagesRoot)
        {
            _appDataRoot = Path.GetFullPath(appDataRoot);
            _imagesRoot = Path.GetFullPath(imagesRoot);
        }

        public string ProviderName => FileStorageOptions.DiskProvider;

        /// <summary>Områdets katalog (skapas inte).</summary>
        public string AreaRoot(string area) =>
            Path.Combine(FileStoreAreas.IsPublic(area) ? _imagesRoot : _appDataRoot, area);

        private string? PathFor(string area, string name)
        {
            if (!FileStoreNames.IsValidArea(area) || !FileStoreNames.IsValidName(name)) return null;
            var root = Path.GetFullPath(AreaRoot(area));
            var full = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            // Andra spärren: namnregeln släpper redan inget som kan peka ut, men en sökväg som ändå
            // hamnar utanför området vägras här.
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        public async Task SaveAsync(string area, string name, Stream content, CancellationToken ct = default)
        {
            FileStoreNames.Require(area, name);
            var path = PathFor(area, name) ?? throw new ArgumentException($"Ogiltigt filnamn: {name}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Skriv till en temporär fil och flytta: en läsare ser aldrig en halvskriven fil.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                    await content.CopyToAsync(output, ct);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        public async Task<bool> CreateIfAbsentAsync(string area, string name, Stream content, CancellationToken ct = default)
        {
            FileStoreNames.Require(area, name);
            var path = PathFor(area, name) ?? throw new ArgumentException($"Ogiltigt filnamn: {name}");
            if (File.Exists(path)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
                    await content.CopyToAsync(output, ct);
                try { File.Move(tmp, path, overwrite: false); }
                catch (IOException) when (File.Exists(path)) { return false; }
                return true;
            }
            finally
            {
                if (File.Exists(tmp)) try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }

        public Task<Stream?> OpenReadAsync(string area, string name, CancellationToken ct = default)
        {
            var path = PathFor(area, name);
            if (path == null || !File.Exists(path)) return Task.FromResult<Stream?>(null);
            return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }

        public Task<bool> ExistsAsync(string area, string name, CancellationToken ct = default)
        {
            var path = PathFor(area, name);
            return Task.FromResult(path != null && File.Exists(path));
        }

        public Task<bool> DeleteAsync(string area, string name, CancellationToken ct = default)
        {
            var path = PathFor(area, name);
            if (path == null || !File.Exists(path)) return Task.FromResult(false);
            File.Delete(path);
            return Task.FromResult(true);
        }

        public async IAsyncEnumerable<StoredFileInfo> ListAsync(string area, string? prefix = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!FileStoreNames.IsValidArea(area)) yield break;
            var root = Path.GetFullPath(AreaRoot(area));
            if (!Directory.Exists(root)) yield break;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (prefix != null && !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                // Ett namn som inte klarar namnregeln listas ändå — flytten ska säga att den inte gick,
                // inte tyst hoppa över den.
                yield return new StoredFileInfo(name, new FileInfo(file).Length);
            }
            await Task.CompletedTask;
        }
    }
}
