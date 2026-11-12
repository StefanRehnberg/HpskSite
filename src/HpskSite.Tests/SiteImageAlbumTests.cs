using HpskSite.Services.SitePages;
using Microsoft.Data.SqlClient;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Reglerna för album och bildbeskrivningar (ren kod).</summary>
    public class SiteImageAlbumRulesTests
    {
        [Theory]
        [InlineData("Klubbmästerskap 2025", "Klubbmästerskap 2025")]
        [InlineData("  KM 2025  ", "KM 2025")]
        public void Ett_albumnamn_trimmas_och_godtas(string raw, string expected)
        {
            var r = SiteImageAlbumRules.CheckAlbumName(raw);
            Assert.True(r.Ok); Assert.Equal(expected, r.Name);
        }

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("<b>KM</b>")] [InlineData("KM\t2025")] [InlineData("KM\n2025")] [InlineData("a‮b")]
        public void Ogiltiga_albumnamn_vägras(string? raw) => Assert.False(SiteImageAlbumRules.CheckAlbumName(raw).Ok);

        [Fact]
        public void Namn_och_beskrivning_har_gränser()
        {
            Assert.True(SiteImageAlbumRules.CheckAlbumName(new string('x', 80)).Ok);
            Assert.False(SiteImageAlbumRules.CheckAlbumName(new string('x', 81)).Ok);
            Assert.True(SiteImageAlbumRules.CheckImageName(new string('x', 200)).Ok);
            Assert.False(SiteImageAlbumRules.CheckImageName(new string('x', 201)).Ok);
            Assert.False(SiteImageAlbumRules.CheckImageName("  ").Ok);
            Assert.True(SiteImageAlbumRules.CheckDescription(new string('x', 200)).Ok);
            Assert.False(SiteImageAlbumRules.CheckDescription(new string('x', 201)).Ok);
            Assert.False(SiteImageAlbumRules.CheckDescription("<script>").Ok);
        }

        [Fact]
        public void En_tom_beskrivning_blir_ingen_beskrivning()
        {
            Assert.Null(SiteImageAlbumRules.CheckDescription("   ").Description);
            Assert.Null(SiteImageAlbumRules.CheckDescription(null).Description);
            Assert.Equal("Prisutdelningen", SiteImageAlbumRules.CheckDescription("  Prisutdelningen ").Description);
        }

        [Fact]
        public void Id_listan_tolkas_strikt_utan_dubbletter_och_med_tak()
        {
            Assert.Equal(new[] { 5, 3, 9 }, SiteImageAlbumRules.ParseIds("5, 3,5 ,9").ToArray());
            Assert.Empty(SiteImageAlbumRules.ParseIds("abc,-1,0,1.5,;"));
            Assert.Empty(SiteImageAlbumRules.ParseIds(null));
            Assert.Equal(SiteImageAlbumRules.MaxIdsPerCall, SiteImageAlbumRules.ParseIds(string.Join(",", Enumerable.Range(1, 2000))).Count);
        }
    }

    /// <summary>Album mot en riktig SQL Server: ägarisolering (även i databasen), unika namn, tak, ordning och att bilder aldrig försvinner med ett album. Rullas tillbaka.</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteImageAlbumStoreTests
    {
        private const string Club = "Club";
        private const int Owner = 990031;
        private const int OtherOwner = 990032;
        private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0);
        private static readonly SiteActor Actor = new(7);

        private static void Seed(IDatabase db) { foreach (var id in new[] { Owner, OtherOwner }) db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id); }

        private static int File(IDatabase db, string name, int owner = Owner) =>
            db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteFile (OwnerType, OwnerId, FileName, StoredName, ContentType, Size, Width, Height, ContentHash, UploadedDate)
                  VALUES (N'Club', @0, @1, @2, N'image/png', 1000, 800, 600, CONVERT(BINARY(32), @3), @4); SELECT CAST(SCOPE_IDENTITY() AS int)",
                owner, name, Guid.NewGuid().ToString("N"), new byte[32], Now);

        private static int Album(IDatabase db, string name, int owner = Owner)
        {
            var r = SiteImageAlbumWriter.Save(db, Club, owner, null, name, Actor, Now);
            Assert.True(r.Ok, r.Message); return r.Id!.Value;
        }

        private static int[] Order(IDatabase db, int album, int owner = Owner) =>
            SiteImageAlbumStore.Links(db, Club, owner).Where(l => l.AlbumId == album).Select(l => l.FileId).ToArray();

        [SqlFact]
        public void Ett_album_skapas_med_unikt_namn_per_förening_och_namnbyte_följer_samma_regel()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "KM 2025");
            Assert.False(SiteImageAlbumWriter.Save(f.Db, Club, Owner, null, "km 2025", Actor, Now).Ok);        // versaler räknas som samma namn
            Assert.True(SiteImageAlbumWriter.Save(f.Db, Club, OtherOwner, null, "KM 2025", Actor, Now).Ok);     // en annan förening får samma namn
            var b = Album(f.Db, "SM 2025");
            Assert.False(SiteImageAlbumWriter.Save(f.Db, Club, Owner, b, "KM 2025", Actor, Now).Ok);            // namnbyte till ett upptaget namn
            Assert.True(SiteImageAlbumWriter.Save(f.Db, Club, Owner, a, "KM 2025", Actor, Now).Ok);             // samma namn på sig själv är ok
            Assert.True(SiteImageAlbumWriter.Save(f.Db, Club, Owner, b, "SM 2026", Actor, Now).Ok);
            Assert.Equal(new[] { "KM 2025", "SM 2026" }, SiteImageAlbumStore.Albums(f.Db, Club, Owner).Select(x => x.Name).ToArray());
            Assert.False(SiteImageAlbumWriter.Save(f.Db, Club, Owner, a + 9999, "Finns inte", Actor, Now).Ok);
        }

        [SqlFact]
        public void Taket_är_50_album_per_förening()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            for (var i = 0; i < SiteImageAlbumRules.MaxAlbums; i++) Album(f.Db, "Album " + i);
            var over = SiteImageAlbumWriter.Save(f.Db, Club, Owner, null, "För många", Actor, Now);
            Assert.False(over.Ok); Assert.Contains("taket", over.Message);
            Assert.True(SiteImageAlbumWriter.Save(f.Db, Club, OtherOwner, null, "Annan", Actor, Now).Ok);
        }

        [SqlFact]
        public void Bilder_läggs_sist_i_angiven_ordning_dubbletter_hoppas_över_och_en_bild_kan_ligga_i_flera_album()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "A"); var b = Album(f.Db, "B");
            int f1 = File(f.Db, "1.png"), f2 = File(f.Db, "2.png"), f3 = File(f.Db, "3.png");
            var r = SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { f3, f1 }, Actor, Now);
            Assert.True(r.Ok); Assert.Equal(2, r.Count);
            Assert.Equal(new[] { f3, f1 }, Order(f.Db, a));
            Assert.Equal(1, SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { f1, f2 }, Actor, Now).Count);   // f1 låg redan där
            Assert.Equal(new[] { f3, f1, f2 }, Order(f.Db, a));
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, b, new[] { f1 }, Actor, Now);
            Assert.Equal(new[] { f1 }, Order(f.Db, b));                                                              // samma bild i två album
        }

        [SqlFact]
        public void En_annan_förenings_bild_eller_album_kan_aldrig_användas_och_databasen_vägrar_också()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var mine = Album(f.Db, "Mitt"); var theirs = Album(f.Db, "Deras", OtherOwner);
            int myFile = File(f.Db, "min.png"), theirFile = File(f.Db, "deras.png", OtherOwner);
            Assert.Equal(0, SiteImageAlbumWriter.Add(f.Db, Club, Owner, mine, new[] { theirFile }, Actor, Now).Count);     // bilden hör inte till ägaren: hoppas över
            Assert.False(SiteImageAlbumWriter.Add(f.Db, Club, Owner, theirs, new[] { myFile }, Actor, Now).Ok);           // albumet hör inte till ägaren
            Assert.False(SiteImageAlbumWriter.Delete(f.Db, Club, Owner, theirs, Actor, Now).Ok);
            Assert.False(SiteImageAlbumWriter.Save(f.Db, Club, Owner, theirs, "Kapat", Actor, Now).Ok);
            Assert.False(SiteImageAlbumWriter.UpdateImage(f.Db, Club, Owner, theirFile, "Kapad", "x", Actor, Now).Ok);
            Assert.Equal("deras.png", f.Db.ExecuteScalar<string>("SELECT FileName FROM dbo.SiteFile WHERE Id = @0", theirFile));
            Assert.Empty(Order(f.Db, theirs, OtherOwner));
            // Själva databasen: en rad som blandar ägare får inte finnas (sammansatta nycklar), vad koden än gör.
            Assert.Throws<SqlException>(() => f.Db.Execute("INSERT INTO dbo.SiteImageAlbumItem (AlbumId, FileId, OwnerType, OwnerId, SortOrder) VALUES (@0, @1, N'Club', @2, 10)", mine, theirFile, Owner));
            Assert.Throws<SqlException>(() => f.Db.Execute("INSERT INTO dbo.SiteImageAlbumItem (AlbumId, FileId, OwnerType, OwnerId, SortOrder) VALUES (@0, @1, N'Club', @2, 10)", mine, myFile, OtherOwner));
        }

        [SqlFact]
        public void Ordningen_ändras_med_up_down_first_last_och_numreras_om_och_ytterlägen_ändrar_inget()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "A");
            int f1 = File(f.Db, "1"), f2 = File(f.Db, "2"), f3 = File(f.Db, "3"), f4 = File(f.Db, "4");
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { f1, f2, f3, f4 }, Actor, Now);
            Assert.True(SiteImageAlbumWriter.Move(f.Db, Club, Owner, a, f4, "up", Actor, Now).Ok); Assert.Equal(new[] { f1, f2, f4, f3 }, Order(f.Db, a));
            SiteImageAlbumWriter.Move(f.Db, Club, Owner, a, f1, "down", Actor, Now); Assert.Equal(new[] { f2, f1, f4, f3 }, Order(f.Db, a));
            SiteImageAlbumWriter.Move(f.Db, Club, Owner, a, f3, "first", Actor, Now); Assert.Equal(new[] { f3, f2, f1, f4 }, Order(f.Db, a));
            SiteImageAlbumWriter.Move(f.Db, Club, Owner, a, f3, "last", Actor, Now); Assert.Equal(new[] { f2, f1, f4, f3 }, Order(f.Db, a));
            Assert.True(SiteImageAlbumWriter.Move(f.Db, Club, Owner, a, f2, "up", Actor, Now).Ok);          // redan först: inget fel, ingen ändring
            Assert.Equal(new[] { f2, f1, f4, f3 }, Order(f.Db, a));
            Assert.Equal(new[] { 10, 20, 30, 40 }, f.Db.Fetch<int>("SELECT SortOrder FROM dbo.SiteImageAlbumItem WHERE AlbumId = @0 ORDER BY SortOrder", a).ToArray());
            Assert.False(SiteImageAlbumWriter.Move(f.Db, Club, Owner, a, f1, "sideways", Actor, Now).Ok);
        }

        [SqlFact]
        public void Ett_album_som_tas_bort_lämnar_bilderna_kvar_och_en_raderad_bild_lämnar_albumen()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "A"); var b = Album(f.Db, "B");
            int f1 = File(f.Db, "1"), f2 = File(f.Db, "2");
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { f1, f2 }, Actor, Now);
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, b, new[] { f1 }, Actor, Now);
            Assert.True(SiteImageAlbumWriter.Delete(f.Db, Club, Owner, a, Actor, Now).Ok);
            Assert.Equal(2, SiteImageAlbumStore.Files(f.Db, Club, Owner).Count);                    // bilderna finns kvar
            Assert.Equal(new[] { f1 }, Order(f.Db, b));                                             // och ligger kvar i det andra albumet
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Delete(f.Db, f1, Club, Owner, Actor, Now, out _));
            Assert.Empty(Order(f.Db, b));                                                           // en raderad bild ligger inte kvar i något album
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbumItem WHERE FileId = @0", f1));
        }

        [SqlFact]
        public void Ta_ur_albumet_tar_inte_bort_bilden_och_rör_inte_andra_album()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "A"); var b = Album(f.Db, "B");
            int f1 = File(f.Db, "1"), f2 = File(f.Db, "2");
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { f1, f2 }, Actor, Now);
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, b, new[] { f1 }, Actor, Now);
            var r = SiteImageAlbumWriter.Remove(f.Db, Club, Owner, a, new[] { f1 }, Actor, Now);
            Assert.True(r.Ok); Assert.Equal(1, r.Count);
            Assert.Equal(new[] { f2 }, Order(f.Db, a)); Assert.Equal(new[] { f1 }, Order(f.Db, b));
            Assert.Equal(2, SiteImageAlbumStore.Files(f.Db, Club, Owner).Count);
            Assert.False(SiteImageAlbumWriter.Remove(f.Db, Club, Owner, a + 9999, new[] { f2 }, Actor, Now).Ok);
        }

        [SqlFact]
        public void Taket_är_500_bilder_per_album_och_ryms_de_inte_alla_läggs_ingen()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "Stort");
            f.Db.Execute(
                @"INSERT INTO dbo.SiteFile (OwnerType, OwnerId, FileName, StoredName, ContentType, Size, Width, Height, ContentHash, UploadedDate)
                  SELECT TOP (@0) N'Club', @1, CONCAT(N'b', ROW_NUMBER() OVER (ORDER BY (SELECT 1)), N'.png'), REPLACE(CAST(NEWID() AS NVARCHAR(36)), N'-', N''), N'image/png', 10, 1, 1, CONVERT(BINARY(32), 0x00), @2
                  FROM sys.all_columns x CROSS JOIN sys.all_columns y", SiteImageAlbumRules.MaxPerAlbum, Owner, Now);
            var all = f.Db.Fetch<int>("SELECT Id FROM dbo.SiteFile WHERE OwnerId = @0", Owner);
            Assert.Equal(SiteImageAlbumRules.MaxPerAlbum, SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, all.Take(SiteImageAlbumRules.MaxIdsPerCall).ToList(), Actor, Now).Count);
            int extra = File(f.Db, "en till");
            var r = SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { extra }, Actor, Now);
            Assert.False(r.Ok); Assert.Contains("500", r.Message);
            Assert.Equal(SiteImageAlbumRules.MaxPerAlbum, SiteImageAlbumStore.Links(f.Db, Club, Owner).Count);
        }

        [SqlFact]
        public void Namn_och_beskrivning_ändras_utan_att_filen_rörs_och_listan_bär_beskrivningen_och_användningen()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            int f1 = File(f.Db, "IMG_0001.jpg");
            var stored = f.Db.ExecuteScalar<string>("SELECT StoredName FROM dbo.SiteFile WHERE Id = @0", f1);
            Assert.True(SiteImageAlbumWriter.UpdateImage(f.Db, Club, Owner, f1, "  Prisutdelning KM 2025 ", "  Segrarna på pallen ", Actor, Now).Ok);
            var row = SiteImageAlbumStore.Files(f.Db, Club, Owner).Single();
            Assert.Equal(("Prisutdelning KM 2025", "Segrarna på pallen", 0), (row.FileName, row.Description, row.UsedBy));
            Assert.Equal(stored, f.Db.ExecuteScalar<string>("SELECT StoredName FROM dbo.SiteFile WHERE Id = @0", f1));
            Assert.True(SiteImageAlbumWriter.UpdateImage(f.Db, Club, Owner, f1, "Namn", "   ", Actor, Now).Ok);
            Assert.Null(SiteImageAlbumStore.Files(f.Db, Club, Owner).Single().Description);
            Assert.False(SiteImageAlbumWriter.UpdateImage(f.Db, Club, Owner, f1, "", "x", Actor, Now).Ok);
            Assert.False(SiteImageAlbumWriter.UpdateImage(f.Db, Club, Owner, f1 + 9999, "Namn", null, Actor, Now).Ok);
            Assert.Equal(new[] { "album-create" }.Length, 1);   // (loggraderna provas nedan)
        }

        [SqlFact]
        public void Varje_skrivning_loggas_utan_text_och_ägarens_radering_tar_albumen()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Album(f.Db, "A"); int f1 = File(f.Db, "1");
            SiteImageAlbumWriter.Save(f.Db, Club, Owner, a, "A2", Actor, Now);
            SiteImageAlbumWriter.Add(f.Db, Club, Owner, a, new[] { f1 }, Actor, Now);
            SiteImageAlbumWriter.UpdateImage(f.Db, Club, Owner, f1, "Nytt", null, Actor, Now);
            SiteImageAlbumWriter.Delete(f.Db, Club, Owner, a, Actor, Now);
            Assert.Equal(new[] { "album-create", "album-rename", "album-add", "album-delete" },
                f.Db.Fetch<string>("SELECT Action FROM dbo.SiteAudit WHERE ObjectKind = 'album' AND OwnerId = @0 ORDER BY Id", Owner).ToArray());
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectKind = 'file' AND Action = 'file-edit' AND ObjectId = @0", f1));
            var keep = Album(f.Db, "Behålls", OtherOwner); File(f.Db, "x", OtherOwner);
            SiteOwnerData.DeleteAll(f.Db, Club, Owner);
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbum WHERE OwnerId = @0", Owner));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteImageAlbum WHERE OwnerId = @0", OtherOwner));
        }
    }
}
