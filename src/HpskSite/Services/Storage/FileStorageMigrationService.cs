using System.Diagnostics;
using HpskSite.Models.Configuration;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.IO;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Flyttar befintliga filer från serverns disk till blobblagringen (skalplanen D3). Körs från
    /// /admin-page → Underhåll → Fillagring efter att blobblagringen slagits på.
    ///
    /// <para><b>Kopierar, raderar aldrig.</b> Diskens filer ligger kvar; en fil som redan finns med samma
    /// storlek hoppas över, så flytten tål att köras om och kan köras i omgångar (varje anrop har en
    /// tidsbudget). Sajten fungerar under tiden: en publik bild som inte flyttats än serveras från disken
    /// (<see cref="PublicImageMiddleware"/> går vidare), men en PRIVAT fil som inte flyttats syns inte —
    /// kör därför flytten direkt efter bytet, och kör den igen tills ingenting återstår.</para>
    /// </summary>
    public class FileStorageMigrationService
    {
        private readonly IFileStore _store;
        private readonly DiskFileStore _disk;
        private readonly MediaFileManager _media;
        private readonly IWebHostEnvironment _env;
        private readonly DocumentArchiveOptions _documents;
        private readonly IServiceProvider _services;
        private readonly IConfiguration _config;
        private readonly ILogger<FileStorageMigrationService> _logger;

        public FileStorageMigrationService(IFileStore store, DiskFileStore disk, MediaFileManager media,
            IWebHostEnvironment env, IOptions<DocumentArchiveOptions> documents, IServiceProvider services,
            IConfiguration config, ILogger<FileStorageMigrationService> logger)
        {
            _services = services;
            _config = config;
            _store = store;
            _disk = disk;
            _media = media;
            _env = env;
            _documents = documents.Value;
            _logger = logger;
        }

        public const string MediaArea = "media";

        public bool StoreIsBlob => _store is not DiskFileStore;
        public bool MediaIsBlob => !(_media.FileSystem is PhysicalFileSystem);

        /// <summary>Alla sajtens egna områden, dokumentarkivets inräknat.</summary>
        public IReadOnlyList<string> Areas => new[]
        {
            FileStoreAreas.StandardMedalProofs, FileStoreAreas.RangeDocuments, FileStoreAreas.PrepDocuments,
            FileStoreAreas.LedgerAttachments, FileStoreAreas.Utbildning, FileStoreAreas.AiChatIndex,
            _documents.StoragePath,
        }.Concat(FileStoreAreas.PublicAreas).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public sealed record AreaStatus(string Area, bool Public, int LocalFiles, long LocalBytes, int ToCopy, int Invalid, List<string> InvalidNames);
        public sealed record Status(string Provider, bool StoreIsBlob, bool MediaIsBlob, List<AreaStatus> Areas);
        public sealed record RunResult(int Copied, long CopiedBytes, int Remaining, bool TimedOut, List<string> Failures);

        private string MediaRoot => Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "media");

        public async Task<Status> PreviewAsync(CancellationToken ct = default)
        {
            var areas = new List<AreaStatus>();
            if (StoreIsBlob)
                foreach (var area in Areas)
                    areas.Add(await AreaStatusAsync(area, ct));
            if (MediaIsBlob)
                areas.Add(MediaStatus());
            return new Status(_store.ProviderName, StoreIsBlob, MediaIsBlob, areas);
        }

        private async Task<AreaStatus> AreaStatusAsync(string area, CancellationToken ct)
        {
            var target = new Dictionary<string, long>(StringComparer.Ordinal);
            await foreach (var f in _store.ListAsync(area, null, ct)) target[f.Name] = f.Size;
            int local = 0, toCopy = 0;
            long bytes = 0;
            var invalid = new List<string>();
            await foreach (var f in _disk.ListAsync(area, null, ct))
            {
                local++;
                bytes += f.Size;
                if (!FileStoreNames.IsValidName(f.Name)) { invalid.Add(f.Name); continue; }
                if (!target.TryGetValue(f.Name, out var size) || size != f.Size) toCopy++;
            }
            return new AreaStatus(area, FileStoreAreas.IsPublic(area), local, bytes, toCopy, invalid.Count, invalid.Take(20).ToList());
        }

        private IEnumerable<(string Rel, string Full, long Size)> LocalMedia()
        {
            if (!Directory.Exists(MediaRoot)) yield break;
            foreach (var file in Directory.EnumerateFiles(MediaRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(MediaRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                yield return (rel, file, new FileInfo(file).Length);
            }
        }

        private bool MediaUpToDate(string rel, long size)
        {
            try { return _media.FileSystem.FileExists(rel) && _media.FileSystem.GetSize(rel) == size; }
            catch { return false; }
        }

        private AreaStatus MediaStatus()
        {
            int local = 0, toCopy = 0;
            long bytes = 0;
            var invalid = new List<string>();
            foreach (var (rel, _, size) in LocalMedia())
            {
                local++;
                bytes += size;
                if (!FileStoreNames.IsSafeRelativePath(rel)) { invalid.Add(rel); continue; }
                if (!MediaUpToDate(rel, size)) toCopy++;
            }
            return new AreaStatus(MediaArea, true, local, bytes, toCopy, invalid.Count, invalid.Take(20).ToList());
        }

        /// <summary>Kopierar det som saknas, tills allt är kopierat eller tidsbudgeten är slut.</summary>
        public async Task<RunResult> RunAsync(TimeSpan budget, CancellationToken ct = default)
        {
            var clock = Stopwatch.StartNew();
            int copied = 0;
            long copiedBytes = 0;
            var failures = new List<string>();
            var timedOut = false;

            if (StoreIsBlob)
            {
                foreach (var area in Areas)
                {
                    var target = new Dictionary<string, long>(StringComparer.Ordinal);
                    await foreach (var f in _store.ListAsync(area, null, ct)) target[f.Name] = f.Size;
                    await foreach (var f in _disk.ListAsync(area, null, ct))
                    {
                        if (clock.Elapsed > budget) { timedOut = true; break; }
                        if (!FileStoreNames.IsValidName(f.Name)) continue;
                        if (target.TryGetValue(f.Name, out var size) && size == f.Size) continue;
                        try
                        {
                            await using var stream = await _disk.OpenReadAsync(area, f.Name, ct);
                            if (stream == null) continue;
                            await _store.SaveAsync(area, f.Name, stream, ct);
                            copied++;
                            copiedBytes += f.Size;
                        }
                        catch (Exception ex)
                        {
                            failures.Add($"{area}/{f.Name}: {ex.Message}");
                            _logger.LogWarning(ex, "Fillagringen: {Area}/{Name} kunde inte kopieras.", area, f.Name);
                        }
                    }
                    if (timedOut) break;
                }
            }

            if (MediaIsBlob && !timedOut)
            {
                // Umbracos mediepaket skapar inte behållaren själv.
                FileStorageStartupService.EnsureMediaContainer(_services, _config);
                foreach (var (rel, full, size) in LocalMedia())
                {
                    if (clock.Elapsed > budget) { timedOut = true; break; }
                    if (!FileStoreNames.IsSafeRelativePath(rel) || MediaUpToDate(rel, size)) continue;
                    try
                    {
                        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                        _media.FileSystem.AddFile(rel, stream, overrideIfExists: true);
                        copied++;
                        copiedBytes += size;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"media/{rel}: {ex.Message}");
                        _logger.LogWarning(ex, "Fillagringen: media/{Path} kunde inte kopieras.", rel);
                    }
                }
            }

            var after = await PreviewAsync(ct);
            var remaining = after.Areas.Sum(a => a.ToCopy);
            _logger.LogWarning("Fillagringen: {Copied} filer ({Bytes} byte) kopierade till blobblagringen, {Remaining} kvar, {Failures} fel.",
                copied, copiedBytes, remaining, failures.Count);
            return new RunResult(copied, copiedBytes, remaining, timedOut, failures.Take(50).ToList());
        }
    }
}
