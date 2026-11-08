using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna 2a: skrivlagret (schema + personnummer + ägarprövning + butik + adress + referenser + audit) mot en riktig SQL
    /// Server (<c>HPSK_TEST_SQL</c>, annars hoppas de över MED NAMN). Varje prov i en transaktion som rullas tillbaka.
    /// </summary>
    [Collection(SiteSqlCollection.Name)]
    public class SitePageWriterTests
    {
        private const string Club = "Club";
        private const int Owner = 990001;
        private const int OtherOwner = 990002;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private static readonly SiteActor Admin = new(7);

        private sealed class Facts : ISiteOwnerFacts
        {
            public HashSet<(int Doc, string Type, int Owner)> Docs = new() { (5, Club, Owner), (6, Club, OtherOwner) };
            public List<string> Children = new();
            public List<string> Clubs = new();
            public bool DocumentBelongsTo(int docId, string ownerType, int ownerId) => Docs.Contains((docId, ownerType, ownerId));
            public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) => Children;
            public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId) => Clubs;
        }

        private static void SeedOwners(IDatabase db)
        {
            foreach (var id in new[] { Owner, OtherOwner })
                db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id);
        }

        private static string TextBlock(string html, int vis = 2, string id = "abcd1") =>
            "{\"schema\":1,\"blocks\":[{\"id\":\"" + id + "\",\"type\":\"text\",\"visibility\":" + vis + ",\"data\":{\"html\":\"" + html + "\"}}]}";

        private static string DocBlock(int doc, int vis = 2) =>
            "{\"schema\":1,\"blocks\":[{\"id\":\"abcd2\",\"type\":\"documents\",\"visibility\":" + vis + ",\"data\":{\"refs\":[\"doc:" + doc + "\"]}}]}";

        private static int Scalar(IDatabase db, string sql, params object[] args) => db.ExecuteScalar<int>(sql, args);

        private static SiteWriteResult NewPage(IDatabase db, Facts facts, string title = "Regler för banan", string? slug = null, SiteVisibility vis = SiteVisibility.Klubbmedlemmar)
        {
            var r = SitePageWriter.CreatePage(db, facts, Club, Owner, title, slug, vis, Admin, Now);
            Assert.True(r.Ok, r.Status + " " + r.Message);
            return r;
        }

        // ── Skapa ─────────────────────────────────────────────────────────────────

        [SqlFact]
        public void Skapa_ger_sida_adress_ur_titeln_och_en_auditrad_utan_text()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var r = NewPage(f.Db, new Facts());
            Assert.Equal("regler-for-banan", r.Slug);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind='sitepage' AND TargetId=@0 AND IsCurrent=1 AND UrlYear IS NULL AND Slug='regler-for-banan' AND OwnerId=@1", r.PageId, Owner));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectKind='page' AND ObjectId=@0 AND Action='page-create' AND ActorMemberId=7 AND ActedAsSupport=0 AND OwnerId=@1", r.PageId, Owner));
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND ContentHash IS NOT NULL", r.PageId));   // create bär ingen text
        }

        [SqlFact]
        public void Samma_titel_igen_numreras_men_en_uttryckligen_vald_krock_vägras()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts();
            NewPage(f.Db, facts);
            Assert.Equal("regler-for-banan-2", NewPage(f.Db, facts).Slug);
            var clash = SitePageWriter.CreatePage(f.Db, facts, Club, Owner, "Annan", "regler-for-banan", SiteVisibility.Dold, Admin, Now);
            Assert.Equal(SiteWriteStatus.SlugTaken, clash.Status);
            Assert.Equal(2, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0", Owner));
        }

        [SqlFact]
        public void Reserverad_eller_upptagen_adress_skapar_ingenting()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts { Children = { "gammal-nod" } };
            f.Db.Execute("INSERT INTO dbo.PageAddress (OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent) VALUES ('Club', @0, NULL, 'sommarlagret', 'event', 900001, 0)", Owner);
            foreach (var slug in new[] { "kalender", "klubbar", "2027", "gammal-nod", "sommarlagret", "Fel Form" })
            {
                var r = SitePageWriter.CreatePage(f.Db, facts, Club, Owner, "X", slug, SiteVisibility.Dold, Admin, Now);
                Assert.Equal(SiteWriteStatus.SlugTaken, r.Status);
            }
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0", Owner));
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind='sitepage' AND OwnerId=@0", Owner));
        }

        [SqlFact]
        public void Ett_reserverat_ord_som_titel_numreras_aldrig()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var r = SitePageWriter.CreatePage(f.Db, new Facts(), Club, Owner, "Kalender", null, SiteVisibility.Dold, Admin, Now);
            Assert.Equal(SiteWriteStatus.SlugTaken, r.Status);
        }

        [SqlFact]
        public void Personnummer_i_titeln_stoppar_skapandet()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var r = SitePageWriter.CreatePage(f.Db, new Facts(), Club, Owner, "Anna 19480312-1234", null, SiteVisibility.Dold, Admin, Now);
            Assert.Equal(SiteWriteStatus.PersonalNumber, r.Status);
            Assert.Equal("19480312-1234", r.Match);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0", Owner));
        }

        // ── Spara ─────────────────────────────────────────────────────────────────

        [SqlFact]
        public void Giltig_sparning_ger_Ok_normaliserad_text_och_referenser()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", DocBlock(5), null, Admin, Now);
            Assert.True(s.Ok, s.Message);
            Assert.Equal(2, s.Revision);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0 AND RefKind='doc' AND RefId=5", c.PageId));
            var t = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 2, "Regler", TextBlock("<p class=\\\"x\\\" onclick=\\\"y()\\\">Hej</p>"), null, Admin, Now);
            Assert.True(t.Ok, t.Message);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0", c.PageId));                // referensen följde innehållet
            var stored = SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner)!.BlocksJson;
            Assert.DoesNotContain("onclick", stored);                                                                          // saneringen skrevs, inte originalet
        }

        [SqlFact]
        public void Ett_dokument_hos_en_annan_ägare_vägras_och_inget_skrivs()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", DocBlock(6), null, Admin, Now);   // doc 6 ägs av OtherOwner
            Assert.Equal(SiteWriteStatus.Invalid, s.Status);
            Assert.Equal(1, SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Revision);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0", c.PageId));
            var unknown = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", DocBlock(999), null, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, unknown.Status);
        }

        [SqlFact]
        public void Ogiltigt_schema_ger_Invalid_med_felen_och_sidan_är_orörd()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            var bad = "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"embed\",\"visibility\":2,\"data\":{}}]}";
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", bad, null, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, s.Status);
            Assert.NotEmpty(s.Errors!);
            Assert.Equal(1, SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Revision);
        }

        [SqlFact]
        public void Block_öppnare_än_sidan_vägras()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts, vis: SiteVisibility.Styrelse);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>x</p>", vis: 2), null, Admin, Now).Status);
            Assert.True(SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>x</p>", vis: 3), null, Admin, Now).Ok);
        }

        [SqlFact]
        public void Personnummer_i_texten_stoppar_men_valet_publicera_ändå_loggas()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            var json = TextBlock("<p>Ring Anna 19480312-1234</p>");
            var stop = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", json, null, Admin, Now);
            Assert.Equal(SiteWriteStatus.PersonalNumber, stop.Status);
            Assert.Equal("19480312-1234", stop.Match);
            Assert.Equal(1, SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Revision);                                    // ingenting skrevs

            var ok = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", json, null, Admin, Now, allowPersonalNumber: true);
            Assert.True(ok.Ok);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='personal-number-override' AND ContentHash IS NOT NULL", c.PageId));
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE Action LIKE '%1234%'"));                    // loggen bär aldrig texten
        }

        [SqlFact]
        public void Personnummer_i_titeln_vid_sparning_stoppas()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Anna 480312-1234", TextBlock("<p>x</p>"), null, Admin, Now);
            Assert.Equal(SiteWriteStatus.PersonalNumber, s.Status);
        }

        [SqlFact]
        public void Ett_annat_sid_id_eller_en_annan_ägare_ger_NotFound()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, OtherOwner, 1, "Kapad", TextBlock("<p>x</p>"), null, new SiteActor(9), Now).Status);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.Publish(f.Db, facts, c.PageId, Club, OtherOwner, 1, new SiteActor(9), Now).Status);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.SoftDelete(f.Db, c.PageId, Club, OtherOwner, 1, new SiteActor(9), Now).Status);
            Assert.Equal("Regler för banan", SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Title);
        }

        [SqlFact]
        public void Gammal_revision_ger_Conflict_med_nuvarande_revision()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>a</p>"), null, Admin, Now);
            var stale = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>b</p>"), null, new SiteActor(8), Now);
            Assert.Equal(SiteWriteStatus.Conflict, stale.Status);
            Assert.Equal(2, stale.Revision);
        }

        // ── Publicera ─────────────────────────────────────────────────────────────

        [SqlFact]
        public void Publicera_loggar_hash_och_referenserna_blir_det_publicerades()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", DocBlock(5), null, Admin, Now);
            var p = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, 2, Admin, Now);
            Assert.True(p.Ok, p.Message);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-publish' AND ContentHash IS NOT NULL", c.PageId));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0 AND RefKind='doc' AND RefId=5", c.PageId));

            // Nytt utkast utan dokumentet: referensen finns kvar (det publicerade använder den) tills nästa publicering.
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 3, "Regler", TextBlock("<p>x</p>"), null, Admin, Now);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0 AND RefId=5", c.PageId));
            SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, 4, Admin, Now);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0", c.PageId));
        }

        [SqlFact]
        public void Publicera_prövar_om_innehållet_mot_sidans_aktuella_synlighet()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts, vis: SiteVisibility.Klubbmedlemmar);
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>x</p>", vis: 2), null, Admin, Now);
            f.Db.Execute("UPDATE dbo.SitePage SET Visibility = 3 WHERE Id = @0", c.PageId);   // sidan har blivit stängdare efter att blocket sparats
            var p = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, 2, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, p.Status);
            Assert.Null(SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner));
        }

        [SqlFact]
        public void Publicera_ett_dokument_som_inte_längre_tillhör_ägaren_vägras()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", DocBlock(5), null, Admin, Now);
            facts.Docs.Remove((5, Club, Owner));                                               // dokumentet raderades/flyttades emellan
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, 2, Admin, Now).Status);
        }

        [SqlFact]
        public void Stödläge_märks_på_versionen_och_i_loggen()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            var support = new SiteActor(42, AsSupport: true);
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>x</p>"), null, support, Now);
            SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, 2, support, Now);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePageVersion WHERE PageId=@0 AND IsPublish=1 AND SavedAsSupport=1 AND SavedByMemberId=42", c.PageId));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-publish' AND ActedAsSupport=1 AND ActorMemberId=42", c.PageId));
        }

        // ── Egen text före/efter systemsidor (mallkopia, slots) ────────────────────

        private static string Slots(string before, string after) =>
            "{\"schema\":1,\"slots\":{\"before\":[" + before + "],\"after\":[" + after + "]}}";
        private static string SlotText(string id, int vis, string canary) =>
            "{\"id\":\"" + id + "\",\"type\":\"text\",\"visibility\":" + vis + ",\"data\":{\"html\":\"<p>" + canary + "</p>\"}}";

        [SqlFact]
        public void Mallkopian_skapas_en_gång_med_systemsidans_titel_och_kan_inte_bli_en_andra()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var a = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.True(a.Ok, a.Message);
            var b = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.Equal(a.PageId, b.PageId);                                                     // idempotent
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0 AND TemplateKey='home'", Owner));
            Assert.Equal("Hem", SitePageStore.GetPage(f.Db, a.PageId, Club, Owner)!.Title);       // systemsidans namn
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='slot-create'", a.PageId));
            Assert.Empty(SitePageStore.ListFree(f.Db, Club, Owner));                              // en mallkopia är ingen fri sida i listan
            Assert.Single(SitePageStore.ListTemplateCopies(f.Db, Club, Owner));
        }

        [SqlFact]
        public void Mallkopia_vägras_för_admin_okänd_sida_och_en_synlighet_öppnare_än_systemsidan()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "admin", SiteVisibility.Dold, Admin, Now).Status);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "nope", SiteVisibility.Dold, Admin, Now).Status);
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "records", SiteVisibility.Alla, Admin, Now).Status);      // Rekord ligger bakom inloggning
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Utloggad, Admin, Now).Status);
            Assert.True(SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Alla, Admin, Now).Ok);                                          // Hem har publika block
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.CreateTemplateCopy(f.Db, Club, 123456, "calendar", SiteVisibility.Inloggade, Admin, Now).Status); // ägaren saknar rad
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0 AND TemplateKey IN ('admin','nope','records')", Owner));
        }

        [SqlFact]
        public void Slots_sparas_och_publiceras_men_bara_text_och_länkar_och_titeln_är_fast()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts();
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Klubbmedlemmar, Admin, Now);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "FÖRSÖK-ATT-BYTA-NAMN", Slots(SlotText("abcd1", 2, "FÖRE-TEXT"), SlotText("abcd2", 2, "EFTER-TEXT")), null, Admin, Now);
            Assert.True(s.Ok, s.Message);
            Assert.Equal("Hem", SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Title);       // klientens titel ignoreras
            var bad = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, s.Revision, "Hem",
                Slots("{\"id\":\"abcd3\",\"type\":\"heading\",\"visibility\":2,\"data\":{\"text\":\"x\",\"level\":2}}", ""), null, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, bad.Status);                                     // rubrik får inte ligga i en slot
            var asFree = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, s.Revision, "Hem", "{\"schema\":1,\"blocks\":[]}", null, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, asFree.Status);                                  // fri sidas form får inte skrivas på en mallkopia
            var p = SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, s.Revision, Admin, Now);
            Assert.True(p.Ok, p.Message);
            var content = SiteBlockSchema.Parse(SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.BlocksJson, SiteContentKind.TemplateCopy, SiteVisibility.Klubbmedlemmar, SiteTemplates.ClubTemplate);
            Assert.True(content.Ok); Assert.Single(content.Before); Assert.Single(content.After);
        }

        [SqlFact]
        public void En_slot_kan_inte_göras_öppnare_än_systemsidan_vid_sparning()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts();
            var c = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "records", SiteVisibility.Inloggade, Admin, Now);
            Assert.True(c.Ok);
            var open = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Rekord", Slots(SlotText("abcd1", 0, "X"), ""), SiteVisibility.Alla, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, open.Status);                                    // Alla på Rekord (bakom inloggning)
            Assert.True(SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, c.Revision, "Rekord", Slots(SlotText("abcd1", 3, "X"), ""), SiteVisibility.Styrelse, Admin, Now).Ok);
        }

        [SqlFact]
        public void En_mallkopia_kan_inte_raderas_men_en_fri_sida_kan()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var copy = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.SoftDelete(f.Db, copy.PageId, Club, Owner, copy.Revision, Admin, Now).Status);
            Assert.NotNull(SitePageStore.GetPage(f.Db, copy.PageId, Club, Owner));                // kvar
            var free = NewPage(f.Db, new Facts());
            Assert.True(SitePageWriter.SoftDelete(f.Db, free.PageId, Club, Owner, free.Revision, Admin, Now).Ok);
        }

        [SqlFact]
        public void Mallkopian_följer_ägaren_vid_radering_och_en_annan_ägares_kopia_rörs_inte()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var mine = SitePageWriter.CreateTemplateCopy(f.Db, Club, Owner, "home", SiteVisibility.Klubbmedlemmar, Admin, Now);
            var theirs = SitePageWriter.CreateTemplateCopy(f.Db, Club, OtherOwner, "home", SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.NotEqual(mine.PageId, theirs.PageId);
            SiteOwnerData.DeleteAll(f.Db, Club, Owner);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0", Owner));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SitePage WHERE OwnerId=@0 AND TemplateKey='home'", OtherOwner));
        }

        // ── Adressbyte ────────────────────────────────────────────────────────────

        [SqlFact]
        public void Adressbyte_gör_den_gamla_till_historik_loggas_och_kan_gå_tillbaka()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);                                        // regler-for-banan
            var r = SitePageWriter.SetSlug(f.Db, facts, c.PageId, Club, Owner, "banregler", Admin, Now);
            Assert.True(r.Ok, r.Message); Assert.Equal("banregler", r.Slug);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind='sitepage' AND TargetId=@0 AND IsCurrent=1 AND Slug='banregler'", c.PageId));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.PageAddress WHERE TargetKind='sitepage' AND TargetId=@0 AND IsCurrent=0 AND Slug='regler-for-banan'", c.PageId));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-slug'", c.PageId));
            Assert.True(SitePageWriter.SetSlug(f.Db, facts, c.PageId, Club, Owner, "regler-for-banan", Admin, Now).Ok);   // sidans egen gamla adress är ledig
        }

        [SqlFact]
        public void Adressbyte_vägrar_reserverat_upptaget_och_en_annan_ägares_sida()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var a = NewPage(f.Db, facts, "Sida A"); var b = NewPage(f.Db, facts, "Sida B");
            foreach (var bad in new[] { "kalender", "klubbar", "2027", "sida-a", "Fel Form", "" })
                Assert.Equal(SiteWriteStatus.SlugTaken, SitePageWriter.SetSlug(f.Db, facts, b.PageId, Club, Owner, bad, Admin, Now).Status);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.SetSlug(f.Db, facts, a.PageId, Club, OtherOwner, "kapad", Admin, Now).Status);
            Assert.Equal("sida-b", f.Db.ExecuteScalar<string>("SELECT Slug FROM dbo.PageAddress WHERE TargetKind='sitepage' AND TargetId=@0 AND IsCurrent=1", b.PageId));
        }

        // ── Versioner ─────────────────────────────────────────────────────────────

        private static int PublishWith(IDatabase db, Facts facts, int pageId, ref int rev, string canary)
        {
            var s = SitePageWriter.SaveDraft(db, facts, pageId, Club, Owner, rev, "Regler", TextBlock($"<p>{canary}</p>"), null, Admin, Now);
            Assert.True(s.Ok, s.Message);
            var p = SitePageWriter.Publish(db, facts, pageId, Club, Owner, s.Revision, Admin, Now);
            Assert.True(p.Ok, p.Message);
            rev = p.Revision;
            return rev;
        }

        [SqlFact]
        public void Versionshistoriken_har_publiceringar_och_återställningspunkter_utan_baslinje_och_utkast()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts); var rev = 1;
            PublishWith(f.Db, facts, c.PageId, ref rev, "ETT"); PublishWith(f.Db, facts, c.PageId, ref rev, "TVA");
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, rev, "Regler", TextBlock("<p>UTKAST</p>"), null, Admin, Now);   // pågående utkast
            var list = SitePageStore.ListVersions(f.Db, c.PageId, Club, Owner);
            Assert.Equal(2, list.Count);
            Assert.All(list, v => Assert.Equal("Publicerad", v.Kind));
            Assert.True(list[0].Id > list[1].Id);                                                    // senaste först
            Assert.True(SitePageWriter.SaveCheckpoint(f.Db, c.PageId, Club, Owner, Admin, Now).Ok);
            Assert.True(SitePageWriter.SaveCheckpoint(f.Db, c.PageId, Club, Owner, Admin, Now).Ok); // samma innehåll igen: ingen ny rad
            var after = SitePageStore.ListVersions(f.Db, c.PageId, Club, Owner);
            Assert.Equal(3, after.Count);
            Assert.Equal("Återställningspunkt", after[0].Kind);
            Assert.Equal(s.Revision, SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.Revision);  // en punkt rör inte revisionen
        }

        [SqlFact]
        public void Använd_den_här_versionen_blir_ett_nytt_utkast_och_rör_inte_det_publicerade()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts); var rev = 1;
            PublishWith(f.Db, facts, c.PageId, ref rev, "GAMMAL"); PublishWith(f.Db, facts, c.PageId, ref rev, "NYARE");
            var oldest = SitePageStore.ListVersions(f.Db, c.PageId, Club, Owner).Last();
            var r = SitePageWriter.RestoreVersion(f.Db, facts, c.PageId, Club, Owner, oldest.Id, rev, Admin, Now);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(rev + 1, r.Revision);
            Assert.Contains("GAMMAL", SitePageStore.GetDraft(f.Db, c.PageId, Club, Owner)!.BlocksJson);
            Assert.Contains("NYARE", SitePageStore.GetPublished(f.Db, c.PageId, Club, Owner)!.BlocksJson);   // publicerat orört
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-restore-version' AND ContentHash IS NOT NULL", c.PageId));
        }

        [SqlFact]
        public void En_annan_sidas_version_eller_gammal_revision_kan_inte_användas()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var a = NewPage(f.Db, facts, "A"); var b = NewPage(f.Db, facts, "B"); var ra = 1; var rb = 1;
            PublishWith(f.Db, facts, a.PageId, ref ra, "A-TEXT"); PublishWith(f.Db, facts, b.PageId, ref rb, "B-TEXT");
            var bVersion = SitePageStore.ListVersions(f.Db, b.PageId, Club, Owner).First();
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.RestoreVersion(f.Db, facts, a.PageId, Club, Owner, bVersion.Id, ra, Admin, Now).Status);   // (id, pageId): inte en annan sidas version
            var aVersion = SitePageStore.ListVersions(f.Db, a.PageId, Club, Owner).First();
            Assert.Equal(SiteWriteStatus.Conflict, SitePageWriter.RestoreVersion(f.Db, facts, a.PageId, Club, Owner, aVersion.Id, ra - 1, Admin, Now).Status);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.RestoreVersion(f.Db, facts, a.PageId, Club, OtherOwner, aVersion.Id, ra, Admin, Now).Status);
        }

        [SqlFact]
        public void Baslinjen_det_tomma_startutkastet_kan_inte_väljas_som_version()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts); var rev = 1;
            PublishWith(f.Db, facts, c.PageId, ref rev, "TEXT");
            var baseline = f.Db.ExecuteScalar<int>("SELECT Id FROM dbo.SitePageVersion WHERE PageId = @0 AND IsBaseline = 1", c.PageId);
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.RestoreVersion(f.Db, facts, c.PageId, Club, Owner, baseline, rev, Admin, Now).Status);
        }

        [SqlFact]
        public void En_gammal_version_som_blivit_öppnare_än_sidan_tillåter_kan_inte_tas_tillbaka()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts, vis: SiteVisibility.Klubbmedlemmar); var rev = 1;
            PublishWith(f.Db, facts, c.PageId, ref rev, "MEDLEMSTEXT");
            var v = SitePageStore.ListVersions(f.Db, c.PageId, Club, Owner).First();
            f.Db.Execute("UPDATE dbo.SitePage SET Visibility = 3 WHERE Id = @0", c.PageId);          // sidan har sedan gjorts till Styrelse
            Assert.Equal(SiteWriteStatus.Invalid, SitePageWriter.RestoreVersion(f.Db, facts, c.PageId, Club, Owner, v.Id, rev, Admin, Now).Status);
        }

        // ── Nödbromsen (sajtadmin stoppar en sida) ────────────────────────────────

        [SqlFact]
        public void Nödbromsen_stoppar_publicering_och_menyn_loggas_och_kan_hävas()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts);
            SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "Regler", TextBlock("<p>x</p>"), null, Admin, Now);
            Assert.True(SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, 2, Admin, Now).Ok);
            HpskSite.Services.Addresses.PageAddressService.SetCurrentFlat(f.Db, HpskSite.Services.Addresses.PageAddressKinds.SitePage, c.PageId, new HpskSite.Services.Addresses.PageAddressOwner(Club, Owner), "regler");
            Assert.True(SitePageStore.SetInMenu(f.Db, c.PageId, Club, Owner, true, Now));
            Assert.Single(SitePageStore.MenuPages(f.Db, Club, Owner));

            var sajtadmin = new SiteActor(4242);
            var stop = SitePageWriter.SetSuspended(f.Db, c.PageId, Club, Owner, true, sajtadmin, Now);
            Assert.True(stop.Ok);
            Assert.NotNull(SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.SuspendedDate);
            Assert.Empty(SitePageStore.MenuPages(f.Db, Club, Owner));                                   // syns inte som länk
            Assert.Equal("Stoppad av pistol.nu", SitePageStore.ListFree(f.Db, Club, Owner).Single().Status);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-suspend' AND ActorMemberId=4242 AND ActedAsSupport=1", c.PageId));

            // Föreningen kan fortsätta skriva utkast men inte publicera.
            var draft = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, stop.Revision, "Regler", TextBlock("<p>y</p>"), null, Admin, Now);
            Assert.True(draft.Ok, draft.Message);
            Assert.Equal(SiteWriteStatus.Suspended, SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, draft.Revision, Admin, Now).Status);

            // Ett andra stopp ändrar ingenting (ingen ny logg, ingen ny revision).
            var again = SitePageWriter.SetSuspended(f.Db, c.PageId, Club, Owner, true, sajtadmin, Now);
            Assert.Equal(draft.Revision, again.Revision);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-suspend'", c.PageId));

            var release = SitePageWriter.SetSuspended(f.Db, c.PageId, Club, Owner, false, sajtadmin, Now);
            Assert.True(release.Ok);
            Assert.Null(SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.SuspendedDate);
            Assert.Single(SitePageStore.MenuPages(f.Db, Club, Owner));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-release'", c.PageId));
            Assert.True(SitePageWriter.Publish(f.Db, facts, c.PageId, Club, Owner, release.Revision, Admin, Now).Ok);
        }

        [SqlFact]
        public void Nödbromsen_gäller_bara_sidan_hos_rätt_ägare()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var c = NewPage(f.Db, new Facts());
            Assert.Equal(SiteWriteStatus.NotFound, SitePageWriter.SetSuspended(f.Db, c.PageId, Club, OtherOwner, true, new SiteActor(4242), Now).Status);
            Assert.Null(SitePageStore.GetPage(f.Db, c.PageId, Club, Owner)!.SuspendedDate);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-suspend'", c.PageId));
        }

        [SqlFact]
        public void Radera_loggas_och_synlighetsbyte_loggas()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var facts = new Facts(); var c = NewPage(f.Db, facts, vis: SiteVisibility.Dold);
            var s = SitePageWriter.SaveDraft(f.Db, facts, c.PageId, Club, Owner, 1, "R", TextBlock("<p>x</p>", vis: 3), SiteVisibility.Styrelse, Admin, Now);
            Assert.True(s.Ok, s.Message);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-visibility'", c.PageId));
            Assert.True(SitePageWriter.SoftDelete(f.Db, c.PageId, Club, Owner, s.Revision, Admin, Now).Ok);
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectId=@0 AND Action='page-delete'", c.PageId));
        }
    }
}
