using NPoco;

namespace HpskSite.Services.SitePages
{
    public enum SiteAlbumResult { Ok, NotFound, Duplicate, Full }

    /// <summary>Ett album i bildbiblioteket: en platt, namngiven samling bilder (en bild kan ligga i flera album).</summary>
    public sealed class SiteAlbumRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>Vilken bild som ligger var i ett album (i albumets ordning).</summary>
    public sealed class SiteAlbumLink
    {
        public int AlbumId { get; set; }
        public int FileId { get; set; }
    }

    /// <summary>En bild i bibliotekets lista: som <see cref="SiteFiles.FileListItem"/> men med beskrivningen.</summary>
    public sealed class SiteLibraryFile
    {
        public int Id { get; set; }
        public string FileName { get; set; } = "";
        public string? Description { get; set; }
        public long Size { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTime UploadedDate { get; set; }
        public int UsedBy { get; set; }
    }

    /// <summary>Reglerna för album och bildbeskrivningar. Ren kod.</summary>
    public static class SiteImageAlbumRules
    {
        public const int MaxAlbums = 50;
        public const int MaxName = 80;
        public const int MaxDescription = 200;
        public const int MaxPerAlbum = 500;
        public const int MaxIdsPerCall = 500;

        private static bool Bad(char c) => char.IsControl(c) || c is '<' or '>' or '"' or '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩' or '‎' or '‏';

        /// <summary>Albumnamn: 1–80 tecken, vanlig text (inga styr-, riktnings- eller &lt;&gt;-tecken). Svar: det rensade namnet.</summary>
        public static (bool Ok, string? Error, string Name) CheckAlbumName(string? name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return (false, "Skriv ett namn på albumet.", n);
            if (n.Length > MaxName) return (false, $"Namnet får vara högst {MaxName} tecken.", n);
            if (n.Any(Bad)) return (false, "Namnet innehåller tecken som inte går att använda.", n);
            return (true, null, n);
        }

        /// <summary>Bildens namn i biblioteket (inte filnamnet på disk): 1–200 tecken, rensat som uppladdade filnamn.</summary>
        public static (bool Ok, string? Error, string Name) CheckImageName(string? name)
        {
            var raw = (name ?? "").Trim();
            if (raw.Length == 0) return (false, "Skriv ett namn på bilden.", raw);
            if (raw.Length > 200) return (false, "Namnet får vara högst 200 tecken.", raw);
            if (raw.Any(Bad)) return (false, "Namnet innehåller tecken som inte går att använda.", raw);
            return (true, null, raw);
        }

        /// <summary>Beskrivning: valfri, högst 200 tecken, vanlig text. Tom = ingen beskrivning (null).</summary>
        public static (bool Ok, string? Error, string? Description) CheckDescription(string? description)
        {
            var d = (description ?? "").Trim();
            if (d.Length == 0) return (true, null, null);
            if (d.Length > MaxDescription) return (false, $"Beskrivningen får vara högst {MaxDescription} tecken.", null);
            if (d.Any(Bad)) return (false, "Beskrivningen innehåller tecken som inte går att använda.", null);
            return (true, null, d);
        }

        /// <summary>Tolkar en kommaseparerad lista med id:n (högst 500, bara positiva heltal, utan dubbletter, i ordning).</summary>
        public static List<int> ParseIds(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var v) && v > 0 ? v : 0).Where(v => v > 0).Distinct().Take(MaxIdsPerCall).ToList();
    }

    /// <summary>
    /// Lagringen av album. Varje fråga bär ägaren: ett album-id eller fil-id utifrån slås alltid upp mot den ägare anropet gäller (id-lagen), och databasen tvingar
    /// dessutom att albumet och bilden i det hör till samma ägare (sammansatta nycklar). Skrivningar sker under ägarens lås, så taken inte kan passeras samtidigt.
    /// </summary>
    public static class SiteImageAlbumStore
    {
        public static List<SiteAlbumRow> Albums(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteAlbumRow>("SELECT Id, Name FROM dbo.SiteImageAlbum WHERE OwnerType = @0 AND OwnerId = @1 ORDER BY Name", ownerType, ownerId);

        /// <summary>Alla medlemskap i albumens ordning. Bara bilder som inte är raderade.</summary>
        public static List<SiteAlbumLink> Links(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteAlbumLink>(
                @"SELECT i.AlbumId, i.FileId FROM dbo.SiteImageAlbumItem i JOIN dbo.SiteFile f ON f.Id = i.FileId AND f.OwnerType = i.OwnerType AND f.OwnerId = i.OwnerId
                  WHERE i.OwnerType = @0 AND i.OwnerId = @1 AND f.DeletedDate IS NULL ORDER BY i.AlbumId, i.SortOrder, i.FileId", ownerType, ownerId);

        /// <summary>Ägarens bilder med beskrivning och antal sidor som använder dem, nyast först.</summary>
        public static List<SiteLibraryFile> Files(IDatabase db, string ownerType, int ownerId) =>
            db.Fetch<SiteLibraryFile>(
                @"SELECT f.Id, f.FileName, f.Description, f.Size, f.Width, f.Height, f.UploadedDate,
                         (SELECT COUNT(DISTINCT r.PageId) FROM dbo.SitePageRef r JOIN dbo.SitePage p ON p.Id = r.PageId
                           WHERE r.RefKind = N'file' AND r.RefId = f.Id AND p.DeletedDate IS NULL) AS UsedBy
                  FROM dbo.SiteFile f WHERE f.OwnerType = @0 AND f.OwnerId = @1 AND f.DeletedDate IS NULL ORDER BY f.Id DESC", ownerType, ownerId);

        private static bool NameTaken(IDatabase db, string ownerType, int ownerId, string name, int exceptId) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbum WHERE OwnerType = @0 AND OwnerId = @1 AND Name = @2 AND Id <> @3", ownerType, ownerId, name, exceptId) > 0;

        public static SiteAlbumResult Create(IDatabase db, string ownerType, int ownerId, string name, DateTime now, out int id)
        {
            id = 0;
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return SiteAlbumResult.NotFound;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbum WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId) >= SiteImageAlbumRules.MaxAlbums) return SiteAlbumResult.Full;
            if (NameTaken(db, ownerType, ownerId, name, 0)) return SiteAlbumResult.Duplicate;
            id = db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteImageAlbum (OwnerType, OwnerId, Name, CreatedDate) VALUES (@0, @1, @2, @3); SELECT CAST(SCOPE_IDENTITY() AS int)", ownerType, ownerId, name, now);
            return SiteAlbumResult.Ok;
        }

        public static SiteAlbumResult Rename(IDatabase db, int id, string ownerType, int ownerId, string name)
        {
            if (!SitePageStore.LockOwner(db, ownerType, ownerId)) return SiteAlbumResult.NotFound;
            if (NameTaken(db, ownerType, ownerId, name, id)) return SiteAlbumResult.Duplicate;
            return db.Execute("UPDATE dbo.SiteImageAlbum SET Name = @3 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId, name) > 0 ? SiteAlbumResult.Ok : SiteAlbumResult.NotFound;
        }

        /// <summary>Tar bort albumet (inte bilderna): medlemskapen följer med (CASCADE).</summary>
        public static bool Delete(IDatabase db, int id, string ownerType, int ownerId) =>
            db.Execute("DELETE FROM dbo.SiteImageAlbum WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId) > 0;

        public static bool Exists(IDatabase db, int id, string ownerType, int ownerId) =>
            db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbum WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2", id, ownerType, ownerId) > 0;

        /// <summary>
        /// Lägger bilderna sist i albumet, i den ordning de anges. Bara ägarens egna, ej raderade bilder som inte redan ligger där räknas; resten hoppas över.
        /// Taket är 500 bilder per album: ryms de inte alla läggs ingen (Full). <paramref name="added"/> = hur många som lades till.
        /// </summary>
        public static SiteAlbumResult AddFiles(IDatabase db, int albumId, string ownerType, int ownerId, IReadOnlyList<int> fileIds, DateTime now, out int added)
        {
            added = 0;
            if (!SitePageStore.LockOwner(db, ownerType, ownerId) || !Exists(db, albumId, ownerType, ownerId)) return SiteAlbumResult.NotFound;
            if (fileIds.Count == 0) return SiteAlbumResult.Ok;
            var valid = db.Fetch<int>(
                @"SELECT f.Id FROM dbo.SiteFile f WHERE f.OwnerType = @0 AND f.OwnerId = @1 AND f.DeletedDate IS NULL AND f.Id IN (@2)
                    AND NOT EXISTS (SELECT 1 FROM dbo.SiteImageAlbumItem i WHERE i.AlbumId = @3 AND i.FileId = f.Id)", ownerType, ownerId, fileIds.ToArray(), albumId).ToHashSet();
            var toAdd = fileIds.Where(valid.Contains).ToList();
            if (toAdd.Count == 0) return SiteAlbumResult.Ok;
            var count = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbumItem WHERE AlbumId = @0", albumId);
            if (count + toAdd.Count > SiteImageAlbumRules.MaxPerAlbum) return SiteAlbumResult.Full;
            var sort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM dbo.SiteImageAlbumItem WHERE AlbumId = @0", albumId) ?? 0;
            foreach (var fid in toAdd)
            {
                sort += 10;
                db.Execute("INSERT INTO dbo.SiteImageAlbumItem (AlbumId, FileId, OwnerType, OwnerId, SortOrder, AddedDate) VALUES (@0, @1, @2, @3, @4, @5)", albumId, fid, ownerType, ownerId, sort, now);
            }
            added = toAdd.Count;
            return SiteAlbumResult.Ok;
        }

        public static int RemoveFiles(IDatabase db, int albumId, string ownerType, int ownerId, IReadOnlyList<int> fileIds)
        {
            if (fileIds.Count == 0) return 0;
            return db.Execute("DELETE FROM dbo.SiteImageAlbumItem WHERE AlbumId = @0 AND OwnerType = @1 AND OwnerId = @2 AND FileId IN (@3)", albumId, ownerType, ownerId, fileIds.ToArray());
        }

        /// <summary>Flyttar en bild i albumet: <c>up</c>, <c>down</c>, <c>first</c> eller <c>last</c>. Ordningen numreras om (10, 20, …). Returnerar false om bilden inte ligger i albumet eller redan står där.</summary>
        public static bool Move(IDatabase db, int albumId, string ownerType, int ownerId, int fileId, string to)
        {
            if (!SitePageStore.LockOwner(db, ownerType, ownerId) || !Exists(db, albumId, ownerType, ownerId)) return false;
            var ids = db.Fetch<int>("SELECT FileId FROM dbo.SiteImageAlbumItem WHERE AlbumId = @0 ORDER BY SortOrder, FileId", albumId);
            var at = ids.IndexOf(fileId);
            if (at < 0) return false;
            var target = to switch { "up" => at - 1, "down" => at + 1, "first" => 0, "last" => ids.Count - 1, _ => at };
            target = Math.Clamp(target, 0, ids.Count - 1);
            if (target == at) return false;
            ids.RemoveAt(at); ids.Insert(target, fileId);
            for (var i = 0; i < ids.Count; i++)
                db.Execute("UPDATE dbo.SiteImageAlbumItem SET SortOrder = @2 WHERE AlbumId = @0 AND FileId = @1", albumId, ids[i], (i + 1) * 10);
            return true;
        }

        /// <summary>Bildens namn och beskrivning i biblioteket. Filen på disk och dess lagrade namn rörs aldrig.</summary>
        public static bool UpdateImage(IDatabase db, int fileId, string ownerType, int ownerId, string name, string? description) =>
            db.Execute("UPDATE dbo.SiteFile SET FileName = @3, Description = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL", fileId, ownerType, ownerId, name, description) > 0;
    }

    /// <summary>Skriver album: prövar reglerna, skriver och loggar (utan text) i samma transaktion. Anroparen äger scopet.</summary>
    public static class SiteImageAlbumWriter
    {
        public sealed record Result(bool Ok, string? Message = null, int? Id = null, int Count = 0);

        private static Result Fail(string m) => new(false, m);
        private static Result NotFound => Fail("Albumet finns inte.");

        public static Result Save(IDatabase db, string ownerType, int ownerId, int? albumId, string? name, SiteActor actor, DateTime now)
        {
            var c = SiteImageAlbumRules.CheckAlbumName(name);
            if (!c.Ok) return Fail(c.Error!);
            if (albumId is null or 0)
            {
                var r = SiteImageAlbumStore.Create(db, ownerType, ownerId, c.Name, now, out var id);
                if (r == SiteAlbumResult.Duplicate) return Fail("Det finns redan ett album med det namnet.");
                if (r == SiteAlbumResult.Full) return Fail($"Ni har nått taket på {SiteImageAlbumRules.MaxAlbums} album. Ta bort ett album först.");
                if (r != SiteAlbumResult.Ok) return NotFound;
                SitePageWriter.Audit(db, ownerType, ownerId, actor, "album", id, "album-create", null, now);
                return new(true, Id: id);
            }
            var rr = SiteImageAlbumStore.Rename(db, albumId.Value, ownerType, ownerId, c.Name);
            if (rr == SiteAlbumResult.Duplicate) return Fail("Det finns redan ett album med det namnet.");
            if (rr != SiteAlbumResult.Ok) return NotFound;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "album", albumId.Value, "album-rename", null, now);
            return new(true, Id: albumId);
        }

        public static Result Delete(IDatabase db, string ownerType, int ownerId, int albumId, SiteActor actor, DateTime now)
        {
            if (!SiteImageAlbumStore.Delete(db, albumId, ownerType, ownerId)) return NotFound;
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "album", albumId, "album-delete", null, now);
            return new(true, Id: albumId);
        }

        public static Result Add(IDatabase db, string ownerType, int ownerId, int albumId, IReadOnlyList<int> fileIds, SiteActor actor, DateTime now)
        {
            var r = SiteImageAlbumStore.AddFiles(db, albumId, ownerType, ownerId, fileIds, now, out var added);
            if (r == SiteAlbumResult.Full) return Fail($"Ett album rymmer högst {SiteImageAlbumRules.MaxPerAlbum} bilder.");
            if (r != SiteAlbumResult.Ok) return NotFound;
            if (added > 0) SitePageWriter.Audit(db, ownerType, ownerId, actor, "album", albumId, "album-add", null, now);
            return new(true, Id: albumId, Count: added);
        }

        public static Result Remove(IDatabase db, string ownerType, int ownerId, int albumId, IReadOnlyList<int> fileIds, SiteActor actor, DateTime now)
        {
            if (!SiteImageAlbumStore.Exists(db, albumId, ownerType, ownerId)) return NotFound;
            var n = SiteImageAlbumStore.RemoveFiles(db, albumId, ownerType, ownerId, fileIds);
            if (n > 0) SitePageWriter.Audit(db, ownerType, ownerId, actor, "album", albumId, "album-remove", null, now);
            return new(true, Id: albumId, Count: n);
        }

        public static Result Move(IDatabase db, string ownerType, int ownerId, int albumId, int fileId, string? to, SiteActor actor, DateTime now)
        {
            if (to is not ("up" or "down" or "first" or "last")) return Fail("Okänd riktning.");
            if (!SiteImageAlbumStore.Exists(db, albumId, ownerType, ownerId)) return NotFound;
            if (SiteImageAlbumStore.Move(db, albumId, ownerType, ownerId, fileId, to)) SitePageWriter.Audit(db, ownerType, ownerId, actor, "album", albumId, "album-move", null, now);
            return new(true, Id: albumId);   // redan ytterst är inget fel
        }

        public static Result UpdateImage(IDatabase db, string ownerType, int ownerId, int fileId, string? name, string? description, SiteActor actor, DateTime now)
        {
            var n = SiteImageAlbumRules.CheckImageName(name);
            if (!n.Ok) return Fail(n.Error!);
            var d = SiteImageAlbumRules.CheckDescription(description);
            if (!d.Ok) return Fail(d.Error!);
            if (!SiteImageAlbumStore.UpdateImage(db, fileId, ownerType, ownerId, n.Name, d.Description)) return Fail("Bilden finns inte.");
            SitePageWriter.Audit(db, ownerType, ownerId, actor, "file", fileId, "file-edit", null, now);
            return new(true, Id: fileId);
        }
    }
}
