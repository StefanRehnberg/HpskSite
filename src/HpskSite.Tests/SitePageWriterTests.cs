using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna 2a: skrivlagret (schema + personnummer + ägarprövning + butik + adress + referenser + audit) mot en riktig SQL
    /// Server (<c>HPSK_TEST_SQL</c>, annars hoppas de över MED NAMN). Varje prov i en transaktion som rullas tillbaka.
    /// </summary>
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
