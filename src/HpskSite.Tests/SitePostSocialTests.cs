using HpskSite.Services;
using HpskSite.Services.SitePages;
using NPoco;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Nyheter: galleri, reaktioner (gilla) och "ta bort min bild" (2026-11-19, Expertgruppen). Ren kod: layout, validering, vem som får reagera, emoji-uppsättningen,
    /// GDPR-kartan och att en stående mobilbild blir stående. Varje påstående har en motsats i samma test så att det kan falla.
    /// </summary>
    public class SitePostSocialRulesTests
    {
        private static SitePostGalleryItem G(int id, string? alt = "x", string? cap = null, bool people = false) => new(id, alt, cap, people);

        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(1, 1, 0, 0)]    // en bild: bara den stora
        [InlineData(2, 1, 1, 0)]    // två: en stor + en liten
        [InlineData(3, 1, 2, 0)]    // tre: stor + två små, inget "+N"
        [InlineData(4, 1, 2, 1)]    // fyra: "+1" på den tredje
        [InlineData(9, 1, 2, 6)]    // nio: "+6" (som Svenska Lags exempel)
        public void Galleriets_layout_är_en_stor_två_små_och_resten_bakom_plus_N(int count, int big, int small, int more)
            => Assert.Equal((big, small, more), SitePostRules.GalleryLayout(count));

        [Fact]
        public void Galleriet_tar_högst_tolv_bilder_och_ingen_två_gånger()
        {
            Assert.Null(SitePostRules.ValidateGallery(null));                                                  // null = rör inte galleriet
            Assert.Null(SitePostRules.ValidateGallery(Enumerable.Range(1, 12).Select(i => G(i)).ToList()));
            Assert.NotNull(SitePostRules.ValidateGallery(Enumerable.Range(1, 13).Select(i => G(i)).ToList()));  // 13 är för många
            Assert.NotNull(SitePostRules.ValidateGallery(new[] { G(5), G(5) }));                                // samma bild två gånger
            Assert.NotNull(SitePostRules.ValidateGallery(new[] { G(0) }));                                      // ogiltigt id
            Assert.NotNull(SitePostRules.ValidateGallery(new[] { G(1, alt: new string('x', 201)) }));
            Assert.NotNull(SitePostRules.ValidateGallery(new[] { G(1, cap: new string('x', 201)) }));
            Assert.Equal(12, SitePostRules.MaxGallery);
        }

        [Fact]
        public void Valideringen_av_nyheten_prövar_också_galleriet()
        {
            SitePostInput I(IReadOnlyList<SitePostGalleryItem>? g) => new("Hej", "<p>x</p>", null, null, null, SiteVisibility.Klubbmedlemmar, null, g);
            Assert.Null(SitePostRules.Validate(I(new[] { G(1) })));
            Assert.NotNull(SitePostRules.Validate(I(new[] { G(1), G(1) })));
        }

        [Fact]
        public void Omslaget_är_galleriets_första_bild_en_enda_skrivväg()
        {
            var noGallery = new SitePostInput("T", "", null, 77, "gammal", SiteVisibility.Alla);
            Assert.Equal((77, "gammal"), SitePostRules.EffectiveCover(noGallery));                              // utan galleri gäller omslaget som förut
            var withGallery = noGallery with { Gallery = new[] { G(5, alt: "huvud"), G(6) } };
            Assert.Equal((5, "huvud"), SitePostRules.EffectiveCover(withGallery));                               // galleriet vinner över ett medskickat omslag
            Assert.Equal(((int?)null, (string?)null), SitePostRules.EffectiveCover(noGallery with { Gallery = Array.Empty<SitePostGalleryItem>() }));   // tomt galleri = inget omslag
        }

        // ── Vem får reagera ─────────────────────────────────────────────────────────────────────────────────
        // SiteViewer(LoggedIn, IsOwnerMember, IsOwnerBoard, IsOwnerAdmin)
        [Theory]
        [InlineData(false, false, false, false, false)]   // utloggad besökare
        [InlineData(false, true, true, true, false)]      // även med ägarflaggor: utloggad är utloggad
        [InlineData(true, false, false, false, false)]    // inloggad men inte med i föreningen (en annan klubbs medlem)
        [InlineData(true, true, false, false, true)]      // medlem
        [InlineData(true, false, true, false, true)]      // styrelse
        [InlineData(true, false, false, true, true)]      // admin (inklusive sajtadmin)
        public void Bara_inloggade_medlemmar_styrelse_och_admin_får_reagera(bool logged, bool member, bool board, bool admin, bool expected)
            => Assert.Equal(expected, SitePostReactionRules.CanReact(new SiteViewer(logged, member, board, admin)));

        // ── Emoji-uppsättningen: samma fem som Träningsmatcherna ─────────────────────────────────────────────
        [Fact]
        public void Reaktionerna_är_samma_fem_emojis_som_Träningsmatcherna_i_samma_ordning()
        {
            Assert.Equal(new[] { "❤️", "👍", "🔥", "😢", "🎯" }, SitePostReactionKinds.All.Select(k => k.Emoji).ToArray());
            Assert.Equal(new[] { "heart", "thumbs", "fire", "sad", "target" }, SitePostReactionKinds.All.Select(k => k.Key).ToArray());
            Assert.True(SitePostReactionKinds.IsValid("heart"));
            Assert.False(SitePostReactionKinds.IsValid("❤️"));        // lagras aldrig som själva emojin
            Assert.False(SitePostReactionKinds.IsValid("rocket"));
            Assert.False(SitePostReactionKinds.IsValid(null));
            Assert.Equal("🎯", SitePostReactionKinds.Emoji("target"));
        }

        [Fact]
        public void Sammanfattningen_räknar_totalen_och_ger_den_vanligaste_sorten()
        {
            var s = new SitePostReactionSummary();
            Assert.Null(s.Top); Assert.Equal(0, s.Total);
            s.ByKind["fire"] = 3; s.ByKind["heart"] = 3; s.ByKind["sad"] = 1;
            Assert.Equal(7, s.Total);
            Assert.Equal("heart", s.Top);            // lika: den som står först i listan
            s.ByKind["fire"] = 4;
            Assert.Equal("fire", s.Top);
        }

        // ── Spärrarna runt: startkontroll, GDPR och gallring känner till de nya tabellerna ────────────────────
        [Fact]
        public void Startkontrollen_kräver_alla_nya_tabeller()
        {
            foreach (var t in new[] { "dbo.SitePostImage", "dbo.SitePostReaction", "dbo.SitePostComment", "dbo.SitePostImageRequest" })
                Assert.Contains(t, SiteOwnerSchemaGuardHostedService.RequiredTables);
        }

        [Fact]
        public void Reaktionen_raderas_hårt_vid_gallring_och_flyttas_vid_sammanslagning()
            => Assert.Contains(MemberDataPurgeService.SubjectTables, t => t.Table == "SitePostReaction" && t.Column == "MemberId");

        [Fact]
        public void Kommentarer_samtycke_och_bildförfrågningar_släpper_personen_men_raden_står_kvar()
        {
            var cols = MemberDataPurgeService.AssignmentColumns;
            var author = Assert.Single(cols, c => c.Table == "SitePostComment" && c.MemberColumn == "AuthorMemberId");
            Assert.Contains("AuthorName", author.ClearSql);
            foreach (var (table, col) in new[] { ("SitePostComment", "DeletedByMemberId"), ("SitePostImage", "ConsentByMemberId"), ("SitePostImageRequest", "RequesterMemberId"), ("SitePostImageRequest", "HandledByMemberId") })
            {
                var c = Assert.Single(cols, x => x.Table == table && x.MemberColumn == col);
                Assert.False(string.IsNullOrWhiteSpace(c.ClearSql), table + "." + col + ": ClearSql får inte vara tom (satsen blir ett syntaxfel)");
                Assert.DoesNotContain(col, c.ClearSql);                                  // och får inte nämna medlemskolumnen
            }
            Assert.Contains("Message", cols.Single(c => c.Table == "SitePostImageRequest" && c.MemberColumn == "RequesterMemberId").ClearSql);   // den fria texten rensas
        }

        [Fact]
        public void Raderade_nyheter_gallras_efter_trettio_dagar_som_sidor() => Assert.Equal(30, SiteRetention.DeletedPostDays);

        // ── Bilden: en stående mobilbild ska förbli stående ──────────────────────────────────────────────────
        private static byte[] JpegWithOrientation(int w, int h, ushort orientation)
        {
            using var img = new Image<Rgb24>(w, h, new Rgb24(10, 120, 200));
            img.Metadata.ExifProfile = new ExifProfile();
            img.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
            using var ms = new MemoryStream(); img.SaveAsJpeg(ms); return ms.ToArray();
        }

        [Fact]
        public async Task En_bild_med_orientering_6_bakas_om_så_att_den_ligger_rätt()
        {
            // Sensorn tog 40x20 liggande, EXIF säger "vrid 90°": den som tittar ska se 20x40 stående.
            var src = JpegWithOrientation(40, 20, 6);
            var (img, fail) = await SiteImages.ProcessAsync(new MemoryStream(src), "mobil.jpg", src.Length);
            Assert.Equal(SiteImageFailure.None, fail);
            Assert.Equal((20, 40), (img!.Width, img.Height));
        }

        [Fact]
        public async Task En_bild_utan_orientering_rörs_inte()
        {
            var src = JpegWithOrientation(40, 20, 1);
            var (img, _) = await SiteImages.ProcessAsync(new MemoryStream(src), "kamera.jpg", src.Length);
            Assert.Equal((40, 20), (img!.Width, img.Height));
        }

        [Fact]
        public async Task Bearbetningen_tar_högst_två_bilder_åt_gången_och_släpper_alltid_sin_plats()
        {
            // Tio bilder samtidigt ska alla bli klara (ingen fastnar, ingen plats läcker) — gränsen är ett minnesskydd, inte ett avslag.
            var src = JpegWithOrientation(30, 30, 1);
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SiteImages.ProcessAsync(new MemoryStream(src), "x.jpg", src.Length)));
            Assert.All(results, r => Assert.Equal(SiteImageFailure.None, r.Item2));
            Assert.Equal(2, SiteImages.MaxConcurrent);
        }
    }

    /// <summary>Galleri, reaktioner, bildförfrågningar och gallring mot SQL Server (HPSK_TEST_SQL, i en transaktion som rullas tillbaka).</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SitePostSocialStoreTests
    {
        private const int A = 990201;
        private const int B = 990202;
        private static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0);
        private static readonly SiteActor Actor = new(7);
        private static readonly SitePostAuthor Author = new(7, "Anna Andersson");

        private static void Owner(IDatabase db, int id) =>
            db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled) VALUES ('Club', @0, 1, 1)", id);

        private static int File(IDatabase db, int owner) =>
            db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteFile (OwnerType, OwnerId, FileName, StoredName, ContentType, Size, Width, Height, ContentHash, UploadedDate)
                  OUTPUT INSERTED.Id VALUES ('Club', @0, N'x.jpg', @1, N'image/jpeg', 10, 1, 1, CONVERT(BINARY(32), 0x00), SYSDATETIME())", owner, Guid.NewGuid().ToString("N"));

        private static SitePostGalleryItem G(int id, string? alt = "x", bool people = false) => new(id, alt, null, people);

        private static SitePostWrite Make(IDatabase db, int owner, IReadOnlyList<SitePostGalleryItem>? gallery, bool consent = false, string title = "Hej") =>
            SitePosts.Create(db, "Club", owner, new SitePostInput(title, "<p>Text</p>", null, null, null, SiteVisibility.Klubbmedlemmar, null, gallery, consent), Author, Now, Actor);

        private static int Published(IDatabase db, int owner, IReadOnlyList<SitePostGalleryItem>? gallery = null, bool consent = false)
        {
            var r = Make(db, owner, gallery, consent);
            Assert.Equal(SitePostResult.Ok, r.Result);
            Assert.Equal(SitePostResult.Ok, SitePosts.SetPublished(db, r.Id, "Club", owner, r.Revision, true, null, Actor, Now).Result);
            return r.Id;
        }

        // ── Galleriet ─────────────────────────────────────────────────────────────────────────────────────
        [SqlFact]
        public void Galleriet_sparas_i_ordning_och_första_bilden_blir_omslaget()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var (a, b, c) = (File(f.Db, A), File(f.Db, A), File(f.Db, A));
            var r = Make(f.Db, A, new[] { G(a, "huvud"), G(b), G(c) });
            Assert.Equal(SitePostResult.Ok, r.Result);
            Assert.Equal(new[] { a, b, c }, SitePosts.GalleryRows(f.Db, r.Id, "Club", A).Select(x => x.FileId).ToArray());
            var post = SitePosts.Get(f.Db, r.Id, "Club", A)!;
            Assert.Equal(a, post.CoverFileId);                                                 // EN skrivväg för omslaget
            Assert.Equal("huvud", post.CoverAlt);

            // Byt plats: en ny huvudbild ger nytt omslag, ordningen skrivs tätt 0..N-1
            var u = SitePosts.Update(f.Db, r.Id, "Club", A, r.Revision, new SitePostInput("Hej", "<p>Text</p>", null, null, null, SiteVisibility.Klubbmedlemmar, null, new[] { G(c, "ny"), G(a), G(b) }), Actor, Now);
            Assert.Equal(SitePostResult.Ok, u.Result);
            Assert.Equal(new[] { c, a, b }, SitePosts.GalleryRows(f.Db, r.Id, "Club", A).Select(x => x.FileId).ToArray());
            Assert.Equal(new[] { 0, 1, 2 }, SitePosts.GalleryRows(f.Db, r.Id, "Club", A).Select(x => x.SortOrder).ToArray());
            Assert.Equal(c, SitePosts.Get(f.Db, r.Id, "Club", A)!.CoverFileId);
        }

        [SqlFact]
        public void Varje_galleribild_är_refererad_så_att_bildmotorn_släpper_ut_den_och_raderingen_spärras()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var (a, b) = (File(f.Db, A), File(f.Db, A));
            var r = Make(f.Db, A, new[] { G(a), G(b) });
            Assert.Equal(new[] { a, b }.OrderBy(x => x), f.Db.Fetch<int>("SELECT FileId FROM dbo.SitePostRef WHERE PostId = @0 ORDER BY FileId", r.Id).OrderBy(x => x));
            Assert.True(SitePosts.FileInUse(f.Db, b, "Club", A));                              // en bild i galleriet får inte raderas under nyheten
            Assert.Single(SitePosts.RefsForFile(f.Db, b, "Club", A));                          // och bildmotorn ser nyheten (annars 404 för besökaren)
        }

        [SqlFact]
        public void En_sparning_utan_galleri_rör_inte_galleriet_och_dess_referenser()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var (a, b) = (File(f.Db, A), File(f.Db, A));
            var r = Make(f.Db, A, new[] { G(a), G(b) });
            var u = SitePosts.Update(f.Db, r.Id, "Club", A, r.Revision, new SitePostInput("Ny rubrik", "<p>Annan text</p>", null, null, null, SiteVisibility.Klubbmedlemmar), Actor, Now);   // Gallery = null
            Assert.Equal(SitePostResult.Ok, u.Result);
            Assert.Equal(2, SitePosts.GalleryRows(f.Db, r.Id, "Club", A).Count);
            Assert.Equal(2, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostRef WHERE PostId = @0", r.Id));   // referenserna följer med: annars 404 och ingen raderingsspärr
        }

        [SqlFact]
        public void Galleriet_vägrar_en_annan_ägares_bild_för_många_bilder_och_dubbletter()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var mine = File(f.Db, A); var theirs = File(f.Db, B);
            Assert.Equal(SitePostResult.Invalid, Make(f.Db, A, new[] { G(mine), G(theirs) }).Result);                   // en annan ägares bild
            Assert.Equal(SitePostResult.Invalid, Make(f.Db, A, new[] { G(mine), G(mine) }).Result);                     // samma bild två gånger
            var thirteen = Enumerable.Range(0, 13).Select(_ => G(File(f.Db, A))).ToList();
            Assert.Equal(SitePostResult.Invalid, Make(f.Db, A, thirteen).Result);                                        // över taket
            Assert.Equal(SitePostResult.Ok, Make(f.Db, A, thirteen.Take(12).ToList()).Result);                          // exakt taket går bra
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePost WHERE OwnerId = @0 AND Title = N'Hej' AND Id NOT IN (SELECT PostId FROM dbo.SitePostImage)", A));   // inget halvskrivet
        }

        [SqlFact]
        public void En_nyhet_från_före_galleriet_visar_omslaget_som_en_enda_bild()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var cover = File(f.Db, A);
            var r = SitePosts.Create(f.Db, "Club", A, new SitePostInput("Gammal", "<p>x</p>", null, cover, "omslag", SiteVisibility.Alla), Author, Now, Actor);   // Gallery = null: som före galleriet
            var rows = SitePosts.GalleryFor(f.Db, SitePosts.Get(f.Db, r.Id, "Club", A)!);
            var one = Assert.Single(rows);
            Assert.Equal(cover, one.FileId);
            Assert.Equal("omslag", one.AltText);
            Assert.Empty(SitePosts.GalleryRows(f.Db, r.Id, "Club", A));                                                  // inget lagrat: ingen datamigrering behövs
        }

        // ── Samtycke för bilder på personer ──────────────────────────────────────────────────────────────
        [SqlFact]
        public void En_bild_som_visar_personer_kräver_samtyckesbekräftelse_och_den_sparas_med_person_och_tid()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var a = File(f.Db, A);
            Assert.Equal(SitePostResult.Invalid, Make(f.Db, A, new[] { G(a, people: true) }, consent: false).Result);   // utan bekräftelse
            var ok = Make(f.Db, A, new[] { G(a, people: true) }, consent: true);
            Assert.Equal(SitePostResult.Ok, ok.Result);
            var row = Assert.Single(SitePosts.GalleryRows(f.Db, ok.Id, "Club", A));
            Assert.True(row.ShowsPeople);
            Assert.Equal(7, row.ConsentByMemberId);
            Assert.Equal(Now, row.ConsentDate);
            // En bild som INTE visar personer behöver ingen bekräftelse och bär inget samtycke
            var plain = Make(f.Db, A, new[] { G(File(f.Db, A)) }, consent: false, title: "Utan personer");
            Assert.Equal(SitePostResult.Ok, plain.Result);
            Assert.Null(Assert.Single(SitePosts.GalleryRows(f.Db, plain.Id, "Club", A)).ConsentDate);
        }

        [SqlFact]
        public void Ett_redan_bekräftat_samtycke_behåller_sin_tid_och_person_och_kräver_ingen_ny_bekräftelse()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var (a, b) = (File(f.Db, A), File(f.Db, A));
            var r = Make(f.Db, A, new[] { G(a, people: true) }, consent: true);
            var later = Now.AddDays(3);
            // Sparar om (med en ny bild som inte visar personer) utan att bekräfta igen — den gamla bilden är redan bekräftad
            var u = SitePosts.Update(f.Db, r.Id, "Club", A, r.Revision, new SitePostInput("Hej", "<p>Text</p>", null, null, null, SiteVisibility.Klubbmedlemmar, null, new[] { G(a, people: true), G(b) }, false), new SiteActor(99), later);
            Assert.Equal(SitePostResult.Ok, u.Result);
            var kept = SitePosts.GalleryRows(f.Db, r.Id, "Club", A).Single(x => x.FileId == a);
            Assert.Equal(Now, kept.ConsentDate);                  // tiden är den ursprungliga, inte "nu"
            Assert.Equal(7, kept.ConsentByMemberId);              // personen är den som bekräftade, inte den som sparade om
            // Men en NY markering "visar personer" på bild b kräver bekräftelse
            var bad = SitePosts.Update(f.Db, r.Id, "Club", A, u.Revision, new SitePostInput("Hej", "<p>Text</p>", null, null, null, SiteVisibility.Klubbmedlemmar, null, new[] { G(a, people: true), G(b, people: true) }, false), Actor, later);
            Assert.Equal(SitePostResult.Invalid, bad.Result);
        }

        // ── Reaktioner ────────────────────────────────────────────────────────────────────────────────────
        [SqlFact]
        public void En_reaktion_per_medlem_en_ny_byter_och_ta_bort_är_idempotent()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Published(f.Db, A);
            Assert.True(SitePostReactions.Set(f.Db, id, "Club", A, 11, "heart", Now));
            Assert.True(SitePostReactions.Set(f.Db, id, "Club", A, 11, "heart", Now));         // dubbelklick: ingen andra rad
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostReaction WHERE PostId = @0", id));
            Assert.True(SitePostReactions.Set(f.Db, id, "Club", A, 11, "fire", Now));          // byter, blir inte två
            Assert.Equal("fire", SitePostReactions.Mine(f.Db, id, "Club", A, 11));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostReaction WHERE PostId = @0", id));
            Assert.True(SitePostReactions.Set(f.Db, id, "Club", A, 12, "heart", Now));
            var s = SitePostReactions.Summaries(f.Db, "Club", A, new[] { id })[id];
            Assert.Equal(2, s.Total); Assert.Equal(1, s.Of("fire")); Assert.Equal(1, s.Of("heart"));
            SitePostReactions.Clear(f.Db, id, "Club", A, 11);
            SitePostReactions.Clear(f.Db, id, "Club", A, 11);                                  // idempotent
            Assert.Null(SitePostReactions.Mine(f.Db, id, "Club", A, 11));
            Assert.Equal(1, SitePostReactions.Summaries(f.Db, "Club", A, new[] { id })[id].Total);
        }

        [SqlFact]
        public void Det_unika_indexet_är_det_som_håller_en_reaktion_per_medlem()
        {
            using var f = new SiteSqlFixture();
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT CAST(is_unique AS INT) FROM sys.indexes WHERE name = N'UX_SitePostReaction_PostMember' AND object_id = OBJECT_ID(N'dbo.SitePostReaction')"));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'CK_SitePostReaction_Kind'"));
        }

        [SqlFact]
        public void Man_kan_bara_reagera_på_en_publicerad_icke_raderad_nyhet_hos_rätt_ägare()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var draft = Make(f.Db, A, null).Id;
            Assert.False(SitePostReactions.Set(f.Db, draft, "Club", A, 11, "heart", Now));     // utkast
            var id = Published(f.Db, A);
            Assert.False(SitePostReactions.Set(f.Db, id, "Club", B, 11, "heart", Now));        // en annan ägares id (id-lagen)
            Assert.False(SitePostReactions.Set(f.Db, id, "Club", A, 11, "rocket", Now));       // okänd sort
            Assert.False(SitePostReactions.Set(f.Db, id, "Club", A, 0, "heart", Now));         // ingen medlem
            Assert.True(SitePostReactions.Set(f.Db, id, "Club", A, 11, "heart", Now));
            SitePosts.Delete(f.Db, id, "Club", A, Actor, Now);
            Assert.False(SitePostReactions.Set(f.Db, id, "Club", A, 12, "heart", Now));        // raderad
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostReaction WHERE PostId = @0 AND MemberId = 12", id));
        }

        [SqlFact]
        public void En_reaktion_rör_aldrig_nyhetens_revision_så_skribenten_inte_får_falsk_konflikt()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Published(f.Db, A);
            var before = SitePosts.Get(f.Db, id, "Club", A)!;
            SitePostReactions.Set(f.Db, id, "Club", A, 11, "heart", Now);
            SitePostReactions.Set(f.Db, id, "Club", A, 12, "target", Now);
            var after = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.Equal(before.Revision, after.Revision);
            Assert.Equal(before.UpdatedDate, after.UpdatedDate);
            var save = SitePosts.Update(f.Db, id, "Club", A, before.Revision, new SitePostInput("Hej", "<p>Text</p>", null, null, null, SiteVisibility.Klubbmedlemmar), Actor, Now);
            Assert.Equal(SitePostResult.Ok, save.Result);                                       // skribentens sparning går igenom
        }

        [SqlFact]
        public void Antalen_hämtas_för_flera_nyheter_i_en_fråga_och_bara_för_ägarens_egna()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var (p1, p2) = (Published(f.Db, A), Published(f.Db, A)); var other = Published(f.Db, B);
            SitePostReactions.Set(f.Db, p1, "Club", A, 11, "heart", Now);
            SitePostReactions.Set(f.Db, other, "Club", B, 11, "heart", Now);
            var s = SitePostReactions.Summaries(f.Db, "Club", A, new[] { p1, p2, other });
            Assert.Equal(1, s[p1].Total);
            Assert.False(s.ContainsKey(p2));                                                    // ingen reaktion = ingen rad
            Assert.False(s.ContainsKey(other));                                                 // en annan ägares nyhet läcker inte ens med id i listan
        }

        // ── "Ta bort min bild" ────────────────────────────────────────────────────────────────────────────
        [SqlFact]
        public void En_förfrågan_kräver_att_bilden_ligger_i_galleriet_och_dubbletter_läggs_inte_in()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var (a, stray) = (File(f.Db, A), File(f.Db, A));
            var id = Published(f.Db, A, new[] { G(a, people: true) }, consent: true);
            Assert.False(SitePosts.AddImageRequest(f.Db, id, "Club", A, stray, 11, null, Now));      // bilden ligger inte i galleriet
            Assert.False(SitePosts.AddImageRequest(f.Db, id, "Club", B, a, 11, null, Now));          // en annan ägares id
            Assert.True(SitePosts.AddImageRequest(f.Db, id, "Club", A, a, 11, "Jag är barnet till vänster", Now));
            Assert.False(SitePosts.AddImageRequest(f.Db, id, "Club", A, a, 11, "igen", Now));        // samma person, samma bild, obehandlad
            Assert.True(SitePosts.AddImageRequest(f.Db, id, "Club", A, a, 12, null, Now));           // en annan person får be
            var open = SitePosts.OpenImageRequests(f.Db, id, "Club", A);
            Assert.Equal(2, open.Count);
            Assert.Equal(2, SitePosts.OpenImageRequestCounts(f.Db, "Club", A)[id]);
        }

        [SqlFact]
        public void En_förfrågan_hanteras_bara_av_rätt_ägare_och_bara_en_gång()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var a = File(f.Db, A);
            var id = Published(f.Db, A, new[] { G(a) });
            SitePosts.AddImageRequest(f.Db, id, "Club", A, a, 11, null, Now);
            var reqId = SitePosts.OpenImageRequests(f.Db, id, "Club", A).Single().Id;
            Assert.False(SitePosts.HandleImageRequest(f.Db, reqId, "Club", B, 7, Now));              // en annan ägares skribent
            Assert.True(SitePosts.HandleImageRequest(f.Db, reqId, "Club", A, 7, Now));
            Assert.False(SitePosts.HandleImageRequest(f.Db, reqId, "Club", A, 7, Now));              // redan hanterad
            Assert.Empty(SitePosts.OpenImageRequests(f.Db, id, "Club", A));
            Assert.True(SitePosts.AddImageRequest(f.Db, id, "Club", A, a, 11, null, Now.AddDays(1)));   // efter hanteringen får samma person be igen
        }

        // ── Gallring, radering och GDPR ───────────────────────────────────────────────────────────────────
        private static void Fill(IDatabase db, int post, int owner, int file, int member)
        {
            db.Execute("INSERT INTO dbo.SitePostReaction (PostId, OwnerType, OwnerId, MemberId, Kind) VALUES (@0, 'Club', @1, @2, 'heart')", post, owner, member);
            db.Execute("INSERT INTO dbo.SitePostComment (PostId, OwnerType, OwnerId, AuthorMemberId, AuthorName, Body) VALUES (@0, 'Club', @1, @2, N'Berit', N'Fint!')", post, owner, member);
            db.Execute("INSERT INTO dbo.SitePostImageRequest (PostId, FileId, OwnerType, OwnerId, RequesterMemberId, Message) VALUES (@0, @1, 'Club', @2, @3, N'Det är jag')", post, file, owner, member);
        }

        [SqlFact]
        public void Raderade_nyheter_gallras_efter_trettio_dagar_med_allt_under_dem_men_inte_nyare_eller_levande()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var (oldFile, newFile, liveFile) = (File(f.Db, A), File(f.Db, A), File(f.Db, A));
            var old = Published(f.Db, A, new[] { G(oldFile) }); var recent = Published(f.Db, A, new[] { G(newFile) }); var live = Published(f.Db, A, new[] { G(liveFile) });
            Fill(f.Db, old, A, oldFile, 11); Fill(f.Db, recent, A, newFile, 11); Fill(f.Db, live, A, liveFile, 11);
            f.Db.Execute("UPDATE dbo.SitePost SET DeletedDate = @1 WHERE Id = @0", old, Now.AddDays(-31));
            f.Db.Execute("UPDATE dbo.SitePost SET DeletedDate = @1 WHERE Id = @0", recent, Now.AddDays(-10));

            Assert.Equal(1, SiteRetention.PurgeDeletedPosts(f.Db, Now));
            foreach (var table in new[] { "SitePost", "SitePostImage", "SitePostReaction", "SitePostComment", "SitePostImageRequest", "SitePostRef" })
            {
                var col = table == "SitePost" ? "Id" : "PostId";
                Assert.Equal(0, f.Db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.{table} WHERE {col} = @0", old));       // allt under den gamla är borta (CASCADE)
                Assert.Equal(1, f.Db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.{table} WHERE {col} = @0", recent));    // den nyligen raderade står kvar
                Assert.Equal(1, f.Db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.{table} WHERE {col} = @0", live));      // den levande rörs inte
            }
            Assert.Equal(0, SiteRetention.PurgeDeletedPosts(f.Db, Now));                                                    // idempotent
        }

        [SqlFact]
        public void Radering_av_ägaren_tar_med_galleri_reaktioner_kommentarer_och_förfrågningar_men_inte_andras()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var (fa, fb) = (File(f.Db, A), File(f.Db, B));
            var pa = Published(f.Db, A, new[] { G(fa) }); var pb = Published(f.Db, B, new[] { G(fb) });
            Fill(f.Db, pa, A, fa, 11); Fill(f.Db, pb, B, fb, 11);
            Assert.Equal(1, SiteOwnerData.DeleteAll(f.Db, "Club", A));
            foreach (var table in new[] { "SitePostImage", "SitePostReaction", "SitePostComment", "SitePostImageRequest" })
            {
                Assert.Equal(0, f.Db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.{table} WHERE OwnerId = @0", A));
                Assert.Equal(1, f.Db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.{table} WHERE OwnerId = @0", B));
            }
        }

        [SqlFact]
        public void Gallringen_av_en_medlem_raderar_reaktionen_och_släpper_personen_på_resten_med_raderna_kvar()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var a = File(f.Db, A);
            var id = Published(f.Db, A, new[] { G(a, people: true) }, consent: true);   // samtycke bekräftat av medlem 7
            Fill(f.Db, id, A, a, 7);
            f.Db.Execute("UPDATE dbo.SitePostComment SET DeletedByMemberId = 7 WHERE PostId = @0", id);

            foreach (var (table, col) in MemberDataPurgeService.SubjectTables.Where(t => t.Table == "SitePostReaction"))
                f.Db.Execute($"DELETE FROM [{table}] WHERE [{col}] = @0", 7);
            foreach (var (table, col, clear) in MemberDataPurgeService.AssignmentColumns.Where(c => c.Table is "SitePostComment" or "SitePostImage" or "SitePostImageRequest"))
                f.Db.Execute($"UPDATE [{table}] SET [{col}] = NULL, {clear} WHERE [{col}] = @0", 7);

            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostReaction WHERE PostId = @0", id));              // reaktionen är borta
            var comment = f.Db.Single<(int? Author, string Name, string Body)>("SELECT AuthorMemberId, AuthorName, Body FROM dbo.SitePostComment WHERE PostId = @0", id);
            Assert.Null(comment.Author); Assert.Equal("Tidigare medlem", comment.Name); Assert.Equal("(Kommentaren är raderad på begäran)", comment.Body);   // raden står kvar, men raderingsvägen tömmer texten (steg 2, Stefans beslut 8)
            Assert.Null(f.Db.ExecuteScalar<int?>("SELECT DeletedByMemberId FROM dbo.SitePostComment WHERE PostId = @0", id));
            var img = Assert.Single(SitePosts.GalleryRows(f.Db, id, "Club", A));
            Assert.Null(img.ConsentByMemberId); Assert.NotNull(img.ConsentDate);                                                       // samtycket står kvar utan personen
            var req = f.Db.Single<(int? Who, string? Msg)>("SELECT RequesterMemberId, Message FROM dbo.SitePostImageRequest WHERE PostId = @0", id);
            Assert.Null(req.Who); Assert.Null(req.Msg);                                                                                // förfrågan står kvar, den fria texten är rensad
        }
    }
}
