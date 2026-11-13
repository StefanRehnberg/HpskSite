using System.Security.Cryptography;
using HpskSite.Services.Storage;
using NPoco;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace HpskSite.Services.SitePages
{
    /// <summary>En rad i <c>SiteFile</c>: en bild som hör till en klubb eller krets (filen ligger i IFileStore-området <see cref="SiteImages.Area"/>).</summary>
    public sealed class SiteFileRow
    {
        public int Id { get; set; }
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public string FileName { get; set; } = "";
        public string StoredName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long Size { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTime UploadedDate { get; set; }
        public DateTime? DeletedDate { get; set; }

        /// <summary>Namnet i lagringen: GUID + ändelse ur den omkodade typen (aldrig ur originalet).</summary>
        public string StoredFile => StoredName.Replace(" ", "") + SiteImages.ExtensionFor(ContentType);
    }

    /// <summary>En bild som ett bildblock kan visa (hör till ägaren, inte raderad).</summary>
    public sealed record SiteImageInfo(int Id, int Width, int Height);

    /// <summary>En omkodad bild redo att lagras.</summary>
    public sealed record SiteImageResult(byte[] Bytes, string ContentType, int Width, int Height, byte[] Hash);

    public enum SiteImageFailure { None, NotAnImage, TooManyPixels, TooLarge, BadExtension }

    /// <summary>
    /// Bilder på klubbens och kretsens sidor (klubbsidorna 2c). <b>Allt som laddas upp avkodas och skrivs om</b> av oss: metadata (EXIF/GPS),
    /// dolda tillägg och filer som är två format på en gång försvinner, och typen som lagras och serveras kommer ur det AVKODADE formatet —
    /// aldrig ur ändelsen eller klientens header. Avkodningen går genom <see cref="HpskSite.Services.Media.SafeImageConfiguration"/> (TIFF avstängd).
    /// </summary>
    public static class SiteImages
    {
        public const string Area = FileStoreAreas.SitePageImages;
        public const long MaxUploadBytes = 20L * 1024 * 1024;
        public const long MaxPixels = 40_000_000;
        /// <summary>Längsta sida efter omkodning: en kamerabild på 6000 px behövs inte på en klubbsida.</summary>
        public const int MaxSide = 2400;
        /// <summary>Reservvärde när ingen gräns ges (prov). I drift gäller föreningens gemensamma gräns för dokument och bilder: <see cref="QuotaFor"/>.</summary>
        public const long QuotaBytesPerOwner = 200L * 1024 * 1024;

        /// <summary>Utrymmet bilder får ta när dokumenten redan tagit sitt av den gemensamma gränsen (aldrig negativt).</summary>
        public static long QuotaFor(long storageLimitBytes, long documentBytes) => Math.Max(0, storageLimitBytes - documentBytes);
        public const int MaxFilesPerOwner = 500;

        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

        public static string ExtensionFor(string contentType) => contentType switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => ".jpg",
        };

        /// <summary>Adressen en bild visas på. Åtkomsten prövas per anrop (<see cref="SiteImageAccess"/>); adressen bär bara id:t.</summary>
        public static string UrlFor(int id) => $"/umbraco/surface/SiteFile/Image?id={id}";

        /// <summary>Bredder en miniatyr får ha. Bara dessa tillåts (en fri bredd vore en billig väg att fylla serverns minne med varianter).</summary>
        public static readonly int[] ThumbnailWidths = { 320, 640, 1280 };

        /// <summary>Adressen till en miniatyr: samma åtkomstprövning som originalet, bara en annan storlek.</summary>
        public static string ThumbUrlFor(int id, int width) => $"/umbraco/surface/SiteFile/Image?id={id}&w={width}";

        /// <summary>
        /// En förminskad kopia av en (redan omkodad) bild, eller null när originalet ska användas: en animerad GIF behåller sina rutor, och en bild som
        /// redan är högst <paramref name="width"/> bred förstoras aldrig. Typen är oförändrad. Avkodningen går genom <see cref="HpskSite.Services.Media.SafeImageConfiguration"/>.
        /// </summary>
        public static async Task<byte[]?> ThumbnailAsync(byte[] original, string contentType, int width)
        {
            if (contentType == "image/gif" || !ThumbnailWidths.Contains(width)) return null;
            var options = new DecoderOptions { Configuration = HpskSite.Services.Media.SafeImageConfiguration.Instance, MaxFrames = 1 };
            using var image = await Image.LoadAsync(options, new MemoryStream(original));
            if (image.Width <= width) return null;
            image.Mutate(c => c.Resize(width, 0));   // 0 = behåll proportionerna
            IImageEncoder encoder = contentType switch
            {
                "image/png" => new PngEncoder(),
                "image/webp" => new WebpEncoder { Quality = 80 },
                _ => new JpegEncoder { Quality = 80 },
            };
            using var ms = new MemoryStream();
            await image.SaveAsync(ms, encoder);
            return ms.ToArray();
        }

        /// <summary>Visningsnamnet ur ett uppladdat filnamn: bara filnamnet, utan bidi- och kontrolltecken, högst 200 tecken.</summary>
        public static string DisplayName(string? original)
        {
            var name = original ?? "";
            var slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name[(slash + 1)..];
            var clean = new string(name.Where(c => !char.IsControl(c) && c is not ('‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '‎' or '‏' or '<' or '>' or '"')).ToArray()).Trim();
            if (clean.Length == 0) clean = "bild";
            return clean.Length > 200 ? clean[^200..] : clean;
        }

        /// <summary>
        /// Avkoda, kontrollera och skriv om. Ändelsen måste vara en bildändelse, pixeltaket (40 Mpx) gäller före minnet fylls, formatet måste vara JPEG,
        /// PNG, GIF eller WebP, all metadata tas bort och en större bild förminskas. En animerad GIF behåller sina bildrutor (högst 200).
        /// </summary>
        public static async Task<(SiteImageResult? Image, SiteImageFailure Failure)> ProcessAsync(Stream input, string? originalName, long length)
        {
            if (length > MaxUploadBytes) return (null, SiteImageFailure.TooLarge);
            if (!AllowedExtensions.Contains(Path.GetExtension(originalName ?? "")))
                return (null, SiteImageFailure.BadExtension);
            try
            {
                var options = new DecoderOptions { Configuration = HpskSite.Services.Media.SafeImageConfiguration.Instance, MaxFrames = 200 };
                // Först bara huvudet: pixeltaket prövas innan bilden avkodas i minnet (dekompressionsbomber).
                var info = await Image.IdentifyAsync(options, input);
                if (info == null) return (null, SiteImageFailure.NotAnImage);
                if ((long)info.Width * info.Height > MaxPixels) return (null, SiteImageFailure.TooManyPixels);
                input.Position = 0;
                using var image = await Image.LoadAsync(options, input);
                var format = image.Metadata.DecodedImageFormat;
                IImageEncoder encoder; string type;
                switch (format?.Name)
                {
                    case "JPEG": encoder = new JpegEncoder { Quality = 85 }; type = "image/jpeg"; break;
                    case "PNG": encoder = new PngEncoder(); type = "image/png"; break;
                    case "GIF": encoder = new GifEncoder(); type = "image/gif"; break;
                    case "WebP": encoder = new WebpEncoder { Quality = 85 }; type = "image/webp"; break;
                    default: return (null, SiteImageFailure.NotAnImage);   // BMP och allt annat sajten kan läsa men inte lagrar här
                }
                image.Metadata.ExifProfile = null;
                image.Metadata.IccProfile = null;
                image.Metadata.IptcProfile = null;
                image.Metadata.XmpProfile = null;
                if (Math.Max(image.Width, image.Height) > MaxSide)
                {
                    var f = (double)MaxSide / Math.Max(image.Width, image.Height);
                    image.Mutate(c => c.Resize(Math.Max(1, (int)Math.Round(image.Width * f)), Math.Max(1, (int)Math.Round(image.Height * f))));
                }
                using var ms = new MemoryStream();
                await image.SaveAsync(ms, encoder);
                var bytes = ms.ToArray();
                return (new SiteImageResult(bytes, type, image.Width, image.Height, SHA256.HashData(bytes)), SiteImageFailure.None);
            }
            catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or InvalidOperationException)
            {
                return (null, SiteImageFailure.NotAnImage);
            }
        }

        public static string Message(SiteImageFailure f) => f switch
        {
            SiteImageFailure.TooLarge => $"Bilden är för stor (högst {MaxUploadBytes / 1024 / 1024} MB).",
            SiteImageFailure.TooManyPixels => "Bilden har för många pixlar. Förminska den och försök igen.",
            SiteImageFailure.BadExtension => "Bara bildfiler (JPG, PNG, GIF, WebP) går att ladda upp.",
            _ => "Filen är ingen bild som sajten kan läsa.",
        };
    }

    public enum SiteFileStatus { Ok, NotFound, QuotaFull, TooMany, InUse }

    /// <summary>
    /// Bildraderna i SQL (skriv/läs/ta bort). Varje fråga bär ägaren raden hör till: ett fil-id utifrån prövas alltid mot den ägare anropet gäller (id-lagen).
    /// Kvoten prövas UNDER <c>UPDLOCK</c> på ägarens <c>SiteOwner</c>-rad så att två samtidiga uppladdningar inte kan passera taket tillsammans.
    /// </summary>
    public static class SiteFiles
    {
        private const string Cols = "Id, OwnerType, OwnerId, FileName, StoredName, ContentType, Size, Width, Height, UploadedDate, DeletedDate";

        /// <summary>Lägger in raden. Kvot och antal prövas under lås. <paramref name="fileId"/> är satt vid Ok.</summary>
        public static SiteFileStatus Insert(IDatabase db, string ownerType, int ownerId, string displayName, string storedName, SiteImageResult img,
            int? memberId, DateTime now, out int fileId, long quotaBytes = SiteImages.QuotaBytesPerOwner, int maxFiles = SiteImages.MaxFilesPerOwner)
        {
            fileId = 0;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteOwner WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId) != 1)
                return SiteFileStatus.NotFound;
            var (count, bytes) = Usage(db, ownerType, ownerId);
            if (count >= maxFiles) return SiteFileStatus.TooMany;
            if (bytes + img.Bytes.Length > quotaBytes) return SiteFileStatus.QuotaFull;
            fileId = db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteFile (OwnerType, OwnerId, FileName, StoredName, ContentType, Size, Width, Height, ContentHash, UploadedByMemberId, UploadedDate)
                  OUTPUT INSERTED.Id VALUES (@0, @1, @2, @3, @4, @5, @6, @7, CONVERT(BINARY(32), @8), @9, @10)",
                ownerType, ownerId, displayName, storedName, img.ContentType, (long)img.Bytes.Length, img.Width, img.Height, img.Hash, memberId, now);
            return SiteFileStatus.Ok;
        }

        public static (int Count, long Bytes) Usage(IDatabase db, string ownerType, int ownerId)
        {
            var r = db.FirstOrDefault<UsageRow>("SELECT COUNT(*) AS N, COALESCE(SUM(Size), 0) AS Bytes FROM dbo.SiteFile WHERE OwnerType = @0 AND OwnerId = @1 AND DeletedDate IS NULL", ownerType, ownerId);
            return (r?.N ?? 0, r?.Bytes ?? 0);
        }

        private sealed class UsageRow { public int N { get; set; } public long Bytes { get; set; } }

        /// <summary>Ägarens bild (id utifrån prövas mot ägaren). Null om den inte finns, hör till någon annan eller är raderad.</summary>
        public static SiteFileRow? Get(IDatabase db, int id, string ownerType, int ownerId) =>
            db.FirstOrDefault<SiteFileRow>($"SELECT {Cols} FROM dbo.SiteFile WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL", id, ownerType, ownerId);

        /// <summary>Raden bara på id (för serveringen, som vet ägaren först efter uppslaget). Null om raderad.</summary>
        public static SiteFileRow? GetAny(IDatabase db, int id) =>
            db.FirstOrDefault<SiteFileRow>($"SELECT {Cols} FROM dbo.SiteFile WHERE Id = @0 AND DeletedDate IS NULL", id);

        public static bool BelongsTo(IDatabase db, int id, string ownerType, int ownerId) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFile WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL", id, ownerType, ownerId) == 1;

        /// <summary>Id:n bland <paramref name="ids"/> som hör till ägaren (för renderingen).</summary>
        public static IReadOnlyList<SiteImageInfo> Info(IDatabase db, IReadOnlyList<int> ids, string ownerType, int ownerId)
        {
            if (ids.Count == 0) return Array.Empty<SiteImageInfo>();
            return db.Fetch<SiteFileRow>($"SELECT {Cols} FROM dbo.SiteFile WHERE OwnerType = @0 AND OwnerId = @1 AND DeletedDate IS NULL AND Id IN (@2)", ownerType, ownerId, ids.Distinct().ToArray())
                .Select(r => new SiteImageInfo(r.Id, r.Width, r.Height)).ToList();
        }

        public sealed class FileListItem
        {
            public int Id { get; set; }
            public string FileName { get; set; } = "";
            public long Size { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public DateTime UploadedDate { get; set; }
            public int UsedBy { get; set; }
        }

        /// <summary>Ägarens bilder, nyast först, med antal sidor som använder dem (utkast och publicerat, ej raderade sidor).</summary>
        public static List<FileListItem> List(IDatabase db, string ownerType, int ownerId)
        {
            var items = ListPages(db, ownerType, ownerId);
            SitePosts.AddPostUse(db, items, ownerType, ownerId);   // nyheter som använder bilden räknas också (annars vägras raderingen utan att biblioteket säger varför)
            return items;
        }

        private static List<FileListItem> ListPages(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<FileListItem>(
                @"SELECT f.Id, f.FileName, f.Size, f.Width, f.Height, f.UploadedDate,
                         (SELECT COUNT(DISTINCT r.PageId) FROM dbo.SitePageRef r JOIN dbo.SitePage p ON p.Id = r.PageId
                           WHERE r.RefKind = N'file' AND r.RefId = f.Id AND p.DeletedDate IS NULL) AS UsedBy
                  FROM dbo.SiteFile f WHERE f.OwnerType = @0 AND f.OwnerId = @1 AND f.DeletedDate IS NULL ORDER BY f.Id DESC", ownerType, ownerId);

        /// <summary>Tar bort bilden (markerar den raderad). Vägras när en sida som inte är raderad använder den. Filen i lagringen tas bort av anroparen.</summary>
        public static SiteFileStatus Delete(IDatabase db, int id, string ownerType, int ownerId, SiteActor actor, DateTime now, out SiteFileRow? row)
        {
            row = Get(db, id, ownerType, ownerId);
            if (row == null) return SiteFileStatus.NotFound;
            var inUse = db.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM dbo.SitePageRef r JOIN dbo.SitePage p ON p.Id = r.PageId
                  WHERE r.RefKind = N'file' AND r.RefId = @0 AND p.OwnerType = @1 AND p.OwnerId = @2 AND p.DeletedDate IS NULL", id, ownerType, ownerId);
            if (inUse > 0 || SitePosts.FileInUse(db, id, ownerType, ownerId)) return SiteFileStatus.InUse;
            db.Execute("UPDATE dbo.SiteFile SET DeletedDate = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId, now);
            // En raderad bild ligger inte kvar i något album (tabellen finns inte förrän albummigreringen körts).
            db.Execute("IF OBJECT_ID(N'dbo.SiteImageAlbumItem', N'U') IS NOT NULL DELETE FROM dbo.SiteImageAlbumItem WHERE FileId = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId);
            db.Execute(
                @"INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, CreatedDate) VALUES (@0, @1, @2, @3, N'file', @4, N'file-delete', @5)",
                ownerType, ownerId, actor.MemberId, actor.AsSupport, id, now);
            return SiteFileStatus.Ok;
        }

        /// <summary>Alla ägarens lagrade filnamn (även raderade), för städning när ägaren raderas permanent.</summary>
        public static List<SiteFileRow> AllForOwner(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteFileRow>($"SELECT {Cols} FROM dbo.SiteFile WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);

        public static int DeleteAllForOwner(IDatabase db, string ownerType, int ownerId) =>
            db.Execute("DELETE FROM dbo.SiteFile WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
    }

    public enum SiteImageDecision { NotFound, ServePublic, ServePrivate }

    /// <summary>
    /// EN regel för när en bild får serveras (plan v3 B1): bilden serveras om minst ett PUBLICERAT bildblock som refererar den är synligt för tittaren
    /// (blockets nivå i det striktaste av block och sida), annars 404. Bara när ALLA publicerade referenser är öppna för Alla får svaret cachas
    /// publikt; i alla andra fall <c>private, no-store</c>. Ren funktion: ingen databas.
    /// </summary>
    public static class SiteImageAccess
    {
        public static SiteImageDecision Decide(int fileId, SiteViewer viewer, IEnumerable<(SiteVisibility PageVisibility, SiteContentResult Content)> publishedPages) =>
            Decide(fileId, viewer, publishedPages, Array.Empty<SitePostFileRef>());

        /// <summary>
        /// Samma regel, med nyheterna som också refererar bilden: en PUBLICERAD nyhet bidrar med sin läsnivå, precis som ett publicerat block. Ett UTKAST bidrar
        /// aldrig till vad besökare får se, men skribenterna (ägarens styrelse och admin) ser bilder i utkast, annars syns de inte i editorn före publicering.
        /// </summary>
        public static SiteImageDecision Decide(int fileId, SiteViewer viewer, IEnumerable<(SiteVisibility PageVisibility, SiteContentResult Content)> publishedPages, IEnumerable<SitePostFileRef> postRefs)
        {
            var levels = new List<SiteVisibility>();
            foreach (var (pageVis, content) in publishedPages)
                foreach (var b in content.AllBlocks.Where(b => SiteBlockSchema.FileIdsOf(b).Contains(fileId)))
                    levels.Add(SiteAccess.Strictest(b.Visibility, pageVis));
            var draftSeenByWriter = false;
            foreach (var p in postRefs)
            {
                if (p.Status == (byte)SitePostStatus.Published) levels.Add((SiteVisibility)p.ReadVisibility);
                else if (viewer.LoggedIn && (viewer.IsOwnerBoard || viewer.IsOwnerAdmin)) draftSeenByWriter = true;
            }
            if (draftSeenByWriter) return SiteImageDecision.ServePrivate;
            if (!levels.Any(l => SiteAccess.CanSee(l, viewer))) return SiteImageDecision.NotFound;
            return levels.All(l => l == SiteVisibility.Alla) ? SiteImageDecision.ServePublic : SiteImageDecision.ServePrivate;
        }
    }

    /// <summary>
    /// Serveringen av en bild: slår upp raden, hittar de publicerade sidor som refererar den (via <c>SitePageRef</c>), och låter <see cref="SiteImageAccess"/> avgöra.
    /// Ägarens EGEN admin (och den som har ett öppet stödläge, via <see cref="SiteViewer.IsOwnerAdmin"/>) ser ägarens alla bilder, så att en bild syns i editorn före publicering.
    /// </summary>
    public sealed class SiteImageService
    {
        private readonly Umbraco.Cms.Infrastructure.Scoping.IScopeProvider _scopes;
        private readonly SitePageReader _reader;
        private readonly SiteOwnerService _owners;

        public SiteImageService(Umbraco.Cms.Infrastructure.Scoping.IScopeProvider scopes, SitePageReader reader, SiteOwnerService owners)
        {
            _scopes = scopes;
            _reader = reader;
            _owners = owners;
        }

        public const int MaxReferencingPages = 60;

        /// <summary>Raden och beslutet för tittaren. <paramref name="viewerFor"/> bygger tittaren när ägaren är känd.</summary>
        public async Task<(SiteFileRow? File, SiteImageDecision Decision)> AuthorizeAsync(int fileId, Func<string, int, Task<SiteViewer>> viewerFor)
        {
            SiteFileRow? file; List<int> pageIds; List<SitePostFileRef> postRefs;
            using (var scope = _scopes.CreateScope(autoComplete: true))
            {
                file = SiteFiles.GetAny(scope.Database, fileId);
                if (file == null) return (null, SiteImageDecision.NotFound);
                pageIds = scope.Database.Fetch<int>(
                    @"SELECT DISTINCT TOP (@2) r.PageId FROM dbo.SitePageRef r JOIN dbo.SitePage p ON p.Id = r.PageId
                      WHERE r.RefKind = N'file' AND r.RefId = @0 AND p.OwnerType = @1 AND p.OwnerId = @3 AND p.DeletedDate IS NULL", fileId, file.OwnerType, MaxReferencingPages, file.OwnerId);
                postRefs = SitePosts.RefsForFile(scope.Database, fileId, file.OwnerType, file.OwnerId);
            }
            // Brytare 2 av = ytan finns inte: ingen bild serveras (samma som sidorna).
            if (!_owners.IsEditingEnabled(file.OwnerType, file.OwnerId)) return (file, SiteImageDecision.NotFound);

            var viewer = await viewerFor(file.OwnerType, file.OwnerId);
            if (viewer.LoggedIn && viewer.IsOwnerAdmin) return (file, SiteImageDecision.ServePrivate);

            var pages = new List<(SiteVisibility, SiteContentResult)>();
            foreach (var id in pageIds)
            {
                var loaded = _reader.LoadPublished(file.OwnerType, file.OwnerId, id);   // null = stoppad, ej publicerad eller ogiltig
                if (loaded != null) pages.Add((loaded.Value.Page.VisibilityLevel, loaded.Value.Content));
            }
            return (file, SiteImageAccess.Decide(fileId, viewer, pages, postRefs));
        }
    }
}
