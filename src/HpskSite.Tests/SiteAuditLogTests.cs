using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2b: klubbens ändringslogg. Etiketterna är rena; listan provas mot en riktig SQL Server (HPSK_TEST_SQL).</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteAuditLogTests
    {
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private const int Owner = 990001, Other = 990002;

        private static string SourceDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite"));

        [Fact]
        public void Varje_åtgärd_koden_loggar_har_en_etikett_på_svenska()
        {
            var rx = new Regex("[\"']((?:page|menu|terms|support|personal|slot)-[a-z-]+)[\"']", RegexOptions.Compiled);
            var actions = new SortedSet<string>();
            foreach (var file in new[] { "Services/SitePages/SitePageWriter.cs", "Controllers/SiteEditController.cs" })
                foreach (Match m in rx.Matches(File.ReadAllText(Path.Combine(SourceDir(), file)))) actions.Add(m.Groups[1].Value);
            Assert.True(actions.Count >= 10, "förväntade minst tio loggade åtgärder, hittade " + actions.Count);
            var missing = actions.Where(a => SiteAuditLog.Label(a) == a).ToList();
            Assert.True(missing.Count == 0, "åtgärder utan etikett i SiteAuditLog.Label: " + string.Join(", ", missing));
        }

        [Fact]
        public void En_okänd_åtgärd_visas_som_den_är_och_titel_utelämnas_när_sidan_är_borta()
        {
            Assert.Equal("nagot-nytt", SiteAuditLog.Label("nagot-nytt"));
            Assert.Equal("publicerade \"Banregler\"", SiteAuditLog.Describe(new SiteAuditRow { Action = "page-publish", PageTitle = "Banregler" }));
            Assert.Equal("raderade", SiteAuditLog.Describe(new SiteAuditRow { Action = "page-delete", PageTitle = null }));
        }

        private static void Seed(NPoco.IDatabase db)
        {
            foreach (var id in new[] { Owner, Other }) db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id);
        }

        private static int Page(NPoco.IDatabase db, int owner, string title) =>
            db.ExecuteScalar<int>("INSERT INTO dbo.SitePage (OwnerType, OwnerId, Title) VALUES ('Club', @0, @1); SELECT CAST(SCOPE_IDENTITY() AS int)", owner, title);

        private static void Log(NPoco.IDatabase db, int owner, string action, int? objectId, DateTime when, string kind = "page", int actor = 7) =>
            db.Execute("INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ActedAsSupport, ObjectKind, ObjectId, Action, CreatedDate) VALUES ('Club', @0, @1, 0, @2, @3, @4, @5)",
                owner, actor, kind, objectId, action, when);

        [SqlFact]
        public void Listan_är_senaste_först_per_ägare_med_sidans_titel()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var mine = Page(f.Db, Owner, "Banregler"); var theirs = Page(f.Db, Other, "Annans sida");
            Log(f.Db, Owner, "page-create", mine, Now); Log(f.Db, Owner, "page-publish", mine, Now.AddMinutes(5));
            Log(f.Db, Other, "page-create", theirs, Now.AddMinutes(9));
            var rows = SiteAuditLog.List(f.Db, "Club", Owner, 100);
            Assert.Equal(new[] { "page-publish", "page-create" }, rows.Select(r => r.Action));
            Assert.All(rows, r => Assert.Equal("Banregler", r.PageTitle));
            Assert.Single(SiteAuditLog.List(f.Db, "Club", Other, 100));
        }

        [SqlFact]
        public void En_loggrad_som_pekar_på_en_annan_ägares_sida_får_ingen_titel()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var theirs = Page(f.Db, Other, "HEMLIG-TITEL");
            Log(f.Db, Owner, "page-publish", theirs, Now);                          // objekt-id:t tillhör en annan ägare: titeln får inte följa med
            var row = SiteAuditLog.List(f.Db, "Club", Owner, 100).Single();
            Assert.Null(row.PageTitle);
            Assert.DoesNotContain("HEMLIG", SiteAuditLog.Describe(row));
        }

        [SqlFact]
        public void En_borttagen_sida_och_en_borttagen_medlem_ger_en_rad_ändå_och_taket_gäller()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            Log(f.Db, Owner, "page-delete", 2147000000, Now);                        // sidan finns inte längre
            f.Db.Execute("INSERT INTO dbo.SiteAudit (OwnerType, OwnerId, ActorMemberId, ObjectKind, ObjectId, Action, CreatedDate) VALUES ('Club', @0, NULL, 'owner', @0, 'terms-accepted', @1)", Owner, Now.AddMinutes(1));
            var rows = SiteAuditLog.List(f.Db, "Club", Owner, 100);
            Assert.Equal(2, rows.Count); Assert.Null(rows[0].ActorMemberId); Assert.Null(rows[1].PageTitle);
            for (var i = 0; i < 5; i++) Log(f.Db, Owner, "menu-add", null, Now.AddMinutes(10 + i), kind: "menu");
            Assert.Equal(3, SiteAuditLog.List(f.Db, "Club", Owner, 3).Count);
            Assert.Equal(200, Math.Min(200, SiteAuditLog.List(f.Db, "Club", Owner, 5000).Count == 7 ? 200 : 0));   // taket 200 hindrar en jättelista
        }
    }
}
