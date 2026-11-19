using HpskSite.Services;
using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Nyheter, steg 2: kommentarer (2026-11-19). Ren kod: vem som får skriva, läsa, moderera och ändra; förgranskning och juniorgräns; textrensning. Varje påstående har en motsats i samma test
    /// så att det kan falla (en regel som alltid är sann fångar ingenting).
    /// </summary>
    public class SiteCommentRulesTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer NonMember = new(true, false, false, false);
        private static readonly SiteViewer Board = new(true, false, true, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);

        [Fact]
        public void Skriva_kräver_medlem_läge_på_brytare_på_PUB_godkänt_och_en_klubb()
        {
            Assert.True(SiteCommentRules.CanComment(Member, "Club", 1, true, true));
            Assert.True(SiteCommentRules.CanComment(Board, "Club", 1, true, true));
            Assert.True(SiteCommentRules.CanComment(Admin, "Club", 1, true, true));
            Assert.False(SiteCommentRules.CanComment(Anon, "Club", 1, true, true));          // aldrig anonym
            Assert.False(SiteCommentRules.CanComment(NonMember, "Club", 1, true, true));     // inloggad men inte i klubben
            Assert.False(SiteCommentRules.CanComment(Member, "Club", 0, true, true));        // läget Av
            Assert.False(SiteCommentRules.CanComment(Member, "Club", 2, true, true));        // Stängd: går att läsa, inga nya
            Assert.False(SiteCommentRules.CanComment(Member, "Club", 1, false, true));       // klubbens brytare av
            Assert.False(SiteCommentRules.CanComment(Member, "Club", 1, true, false));       // PUB-avtalet inte godkänt (BBS-lagen: styrelsen ansvarar)
            Assert.False(SiteCommentRules.CanComment(Member, "Region", 1, true, true));      // kretsens nyheter byggs inte i v1
        }

        [Fact]
        public void Texten_läses_av_samma_krets_som_får_reagera_ingen_annan()
        {
            Assert.True(SiteCommentRules.CanReadText(Member)); Assert.True(SiteCommentRules.CanReadText(Board)); Assert.True(SiteCommentRules.CanReadText(Admin));
            Assert.False(SiteCommentRules.CanReadText(Anon)); Assert.False(SiteCommentRules.CanReadText(NonMember));
        }

        [Fact]
        public void Moderator_är_admin_styrelse_eller_nyhetens_skribent_aldrig_en_vanlig_medlem_eller_en_utloggad()
        {
            Assert.True(SiteCommentRules.CanModerate(Admin, false));
            Assert.True(SiteCommentRules.CanModerate(Board, false));
            Assert.True(SiteCommentRules.CanModerate(Member, isPostAuthor: true));
            Assert.False(SiteCommentRules.CanModerate(Member, false));
            Assert.False(SiteCommentRules.CanModerate(NonMember, false));
            Assert.False(SiteCommentRules.CanModerate(Anon, true));                           // utloggad är aldrig moderator, hur skribentflaggan än står
        }

        [Fact]
        public void Egen_kommentar_ändras_i_femton_minuter_och_inte_en_annans()
        {
            var t = new DateTime(2026, 11, 19, 12, 0, 0);
            Assert.True(SiteCommentRules.CanEdit(true, t, t));
            Assert.True(SiteCommentRules.CanEdit(true, t, t.AddMinutes(15)));
            Assert.False(SiteCommentRules.CanEdit(true, t, t.AddMinutes(15).AddSeconds(1)));
            Assert.False(SiteCommentRules.CanEdit(false, t, t.AddMinutes(1)));
            Assert.True(SiteCommentRules.CanEdit(true, t, t.AddSeconds(-3)));                 // DATETIME2(0) avrundar: en alldeles ny kommentar kan ligga en halv sekund fram
            Assert.False(SiteCommentRules.CanEdit(true, t, t.AddMinutes(-1)));                // en kommentar från framtiden är aldrig ändringsbar
        }

        [Fact]
        public void Återställning_går_i_trettio_dagar()
        {
            var t = new DateTime(2026, 11, 19);
            Assert.True(SiteCommentRules.CanRestore(t, t.AddDays(30)));
            Assert.False(SiteCommentRules.CanRestore(t, t.AddDays(30).AddSeconds(1)));
        }

        [Theory]
        [InlineData(0, 2026, true)]     // okänd ålder räknas som junior (säkrast)
        [InlineData(2010, 2026, true)]  // 16
        [InlineData(2008, 2026, true)]  // 18: konservativ gräns
        [InlineData(2007, 2026, false)] // 19
        [InlineData(1980, 2026, false)]
        public void Junior_är_högst_arton_eller_okänd_ålder(int birthYear, int year, bool junior) => Assert.Equal(junior, SiteCommentRules.IsJunior(birthYear, year));

        [Fact]
        public void Förgranskning_följer_läget_och_gäller_aldrig_moderatorer()
        {
            Assert.False(SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationNone, 2010, 2026, false));       // ingen granskning
            Assert.True(SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationJuniors, 2010, 2026, false));     // junior granskas
            Assert.True(SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationJuniors, 0, 2026, false));        // okänd ålder granskas
            Assert.False(SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationJuniors, 1980, 2026, false));    // vuxen granskas inte
            Assert.True(SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationAll, 1980, 2026, false));         // alla granskas
            Assert.False(SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationAll, 2010, 2026, true));         // en moderator granskas aldrig
        }

        [Fact]
        public void Texten_rensas_och_begränsas_men_länkar_förblir_vanlig_text()
        {
            Assert.Equal("Hej", SiteCommentRules.Validate("  Hej \r\n ").Body);
            Assert.NotNull(SiteCommentRules.Validate("   ").Error);
            Assert.NotNull(SiteCommentRules.Validate(null).Error);
            Assert.Null(SiteCommentRules.Validate(new string('a', 2000)).Error);
            Assert.NotNull(SiteCommentRules.Validate(new string('a', 2001)).Error);
            Assert.Equal("a\n\nb", SiteCommentRules.Validate("a\n\n\n\n\nb").Body);                                  // högst en tom rad i rad
            var bidi = "a" + (char)0x202E + "b" + (char)0x200B + "c" + (char)0x2066 + "d" + (char)0x0007 + "e";    // vänd riktning, nollbredd, isolering, kontrolltecken
            Assert.Equal("abcde", SiteCommentRules.Validate(bidi).Body);
            Assert.Equal("Se https://exempel.se/<b>x</b>", SiteCommentRules.Validate("Se https://exempel.se/<b>x</b>").Body);   // texten lagras som den är; vyn kodar den, så den blir aldrig en klickbar länk eller HTML
            Assert.NotNull(SiteCommentRules.Validate(((char)0x202E).ToString()).Error);                              // bara osynliga tecken = tomt
        }

        [Fact]
        public void Initialer_ur_namn()
        {
            Assert.Equal("AA", SiteNewsCommentsFactory.InitialsOf("Anna Andersson"));
            Assert.Equal("AB", SiteNewsCommentsFactory.InitialsOf("anna Maria berg"));
            Assert.Equal("A", SiteNewsCommentsFactory.InitialsOf("Anna"));
            Assert.Equal("?", SiteNewsCommentsFactory.InitialsOf(""));
        }

        [Fact]
        public void GDPR_raderingen_tömmer_kommentarens_text_och_anmälans_fria_text()
        {
            var cols = MemberDataPurgeService.AssignmentColumns;
            var author = Assert.Single(cols, c => c.Table == "SitePostComment" && c.MemberColumn == "AuthorMemberId");
            Assert.Contains("Body", author.ClearSql);                                                              // en raderingsbegäran tar texten
            Assert.Contains("Tidigare medlem", author.ClearSql);
            Assert.Contains("Reason", Assert.Single(cols, c => c.Table == "SitePostCommentReport" && c.MemberColumn == "ReporterMemberId").ClearSql);
            foreach (var (t, c) in new[] { ("SitePostCommentReport", "HandledByMemberId"), ("SiteOwner", "NewsCommentsEnabledByMemberId") })
            {
                var x = Assert.Single(cols, y => y.Table == t && y.MemberColumn == c);
                Assert.False(string.IsNullOrWhiteSpace(x.ClearSql), t + "." + c);
                Assert.DoesNotContain(c, x.ClearSql);
            }
        }

        [Fact]
        public void Startkontrollen_kräver_anmälningstabellen()
            => Assert.Contains("dbo.SitePostCommentReport", SiteOwnerSchemaGuardHostedService.RequiredTables);
    }

    /// <summary>Kommentarerna mot SQL Server (HPSK_TEST_SQL, i en transaktion som rullas tillbaka).</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SitePostCommentStoreTests
    {
        private const int A = 990301;
        private const int B = 990302;
        private static readonly DateTime Now = new(2026, 11, 19, 10, 0, 0);
        private static readonly SiteActor Actor = new(7);
        private static readonly SitePostAuthor Author = new(7, "Anna Andersson");

        private static void Owner(IDatabase db, int id, bool commentsOn = true) =>
            db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled, NewsCommentsEnabled) VALUES ('Club', @0, 1, 1, @1)", id, commentsOn ? 1 : 0);

        /// <summary>En publicerad nyhet med kommentarsläget <paramref name="mode"/>.</summary>
        private static int Post(IDatabase db, int owner, byte mode = 1, bool publish = true)
        {
            var r = SitePosts.Create(db, "Club", owner, new SitePostInput("Hej", "<p>Text</p>", null, null, null, SiteVisibility.Klubbmedlemmar), Author, Now, Actor);
            Assert.Equal(SitePostResult.Ok, r.Result);
            if (publish) Assert.Equal(SitePostResult.Ok, SitePosts.SetPublished(db, r.Id, "Club", owner, r.Revision, true, null, Actor, Now).Result);
            Assert.True(SitePostComments.SetPostOptions(db, r.Id, "Club", owner, mode, false));
            return r.Id;
        }

        private static (SiteCommentAddResult R, int Id) Add(IDatabase db, int post, int owner, int member = 11, string body = "Fint!", bool needsApproval = false) =>
            SitePostComments.Add(db, post, "Club", owner, member, "Berit Berg", body, needsApproval, Now);

        [SqlFact]
        public void En_kommentar_läggs_in_och_en_kommentar_rör_aldrig_nyhetens_Revision()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var before = SitePosts.Get(f.Db, p, "Club", A)!;
            var (r, id) = Add(f.Db, p, A);
            Assert.Equal(SiteCommentAddResult.Ok, r); Assert.True(id > 0);
            Assert.Equal(before.Revision, SitePosts.Get(f.Db, p, "Club", A)!.Revision);
            Assert.Equal(before.UpdatedDate, SitePosts.Get(f.Db, p, "Club", A)!.UpdatedDate);
            var (rows, total) = SitePostComments.List(f.Db, p, "Club", A, moderator: false, viewerMemberId: 99, 0, 50);
            Assert.Equal(1, total); Assert.Equal("Fint!", rows[0].Body); Assert.Equal("Berit Berg", rows[0].AuthorName);
        }

        [SqlFact]
        public void Villkoren_sitter_i_SQL_satsen_läge_av_stängd_utkast_brytare_av_och_taket()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B, commentsOn: false);
            Assert.Equal(SiteCommentAddResult.Closed, Add(f.Db, Post(f.Db, A, mode: 0), A).R);                        // läget Av
            Assert.Equal(SiteCommentAddResult.Closed, Add(f.Db, Post(f.Db, A, mode: 2), A).R);                        // Stängd
            Assert.Equal(SiteCommentAddResult.Closed, Add(f.Db, Post(f.Db, A, publish: false), A).R);                 // utkast
            Assert.Equal(SiteCommentAddResult.Closed, Add(f.Db, Post(f.Db, B), B).R);                                 // föreningens brytare av
            var p = Post(f.Db, A);
            for (var i = 0; i < SiteCommentRules.MaxPerPost; i++) Assert.Equal(SiteCommentAddResult.Ok, Add(f.Db, p, A).R);
            Assert.Equal(SiteCommentAddResult.Full, Add(f.Db, p, A).R);                                                // taket
        }

        [SqlFact]
        public void En_kommentar_kan_aldrig_läggas_på_en_annan_ägares_nyhet()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var pa = Post(f.Db, A);
            Assert.Equal(SiteCommentAddResult.Closed, SitePostComments.Add(f.Db, pa, "Club", B, 11, "X", "Hej", false, Now).Result);
            var (_, id) = Add(f.Db, pa, A);
            Assert.Null(SitePostComments.Get(f.Db, id, pa, "Club", B));                                                // id utifrån prövas mot ägaren
            Assert.False(SitePostComments.SoftDelete(f.Db, id, pa, "Club", B, 5, null, Now));
            Assert.False(SitePostComments.Approve(f.Db, id, pa, "Club", B));
            Assert.False(SitePostComments.Report(f.Db, id, pa, "Club", B, 12, null, Now));
            Assert.Equal(0, SitePostComments.HandleReports(f.Db, id, pa, "Club", B, 5, Now));
            Assert.Empty(SitePostComments.List(f.Db, pa, "Club", B, true, 5, 0, 50).Rows);
        }

        [SqlFact]
        public void Förgranskade_kommentarer_syns_bara_för_moderatorn_och_skribenten_tills_de_godkänts()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var (_, id) = Add(f.Db, p, A, member: 11, needsApproval: true);
            Assert.Empty(SitePostComments.List(f.Db, p, "Club", A, false, 99, 0, 50).Rows);                           // en annan medlem ser den inte
            Assert.Single(SitePostComments.List(f.Db, p, "Club", A, false, 11, 0, 50).Rows);                          // skribenten ser sin egen
            Assert.Single(SitePostComments.List(f.Db, p, "Club", A, true, 5, 0, 50).Rows);                            // moderatorn ser den
            Assert.Equal(1, SitePostComments.Counts(f.Db, "Club", A, moderator: true)[p].Pending);
            Assert.Equal(0, SitePostComments.Counts(f.Db, "Club", A, moderator: false)[p].Pending);                    // antalet väntande avslöjas bara för moderatorer
            Assert.True(SitePostComments.Approve(f.Db, id, p, "Club", A));
            Assert.False(SitePostComments.Approve(f.Db, id, p, "Club", A));                                           // redan godkänd
            Assert.Single(SitePostComments.List(f.Db, p, "Club", A, false, 99, 0, 50).Rows);
        }

        [SqlFact]
        public void Femton_minutersfönstret_och_egen_kommentar_i_Edit()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var (_, id) = Add(f.Db, p, A, member: 11);
            Assert.False(SitePostComments.Edit(f.Db, id, p, "Club", A, 99, "Kapat", false, Now.AddMinutes(1)));        // någon annans kommentar
            Assert.True(SitePostComments.Edit(f.Db, id, p, "Club", A, 11, "Ändrat", false, Now.AddMinutes(14)));
            var row = SitePostComments.Get(f.Db, id, p, "Club", A)!;
            Assert.Equal("Ändrat", row.Body); Assert.NotNull(row.EditedDate);
            Assert.True(SitePostComments.Edit(f.Db, id, p, "Club", A, 11, "Ändrat", false, Now.AddSeconds(-2)));       // en hårsmån "före" skapandet (avrundning/klockor) går igenom
            Assert.False(SitePostComments.Edit(f.Db, id, p, "Club", A, 11, "Från framtiden", false, Now.AddMinutes(-2)));   // men inte två minuter före
            Assert.False(SitePostComments.Edit(f.Db, id, p, "Club", A, 11, "För sent", false, Now.AddMinutes(16)));    // efter femton minuter
            Assert.Equal("Ändrat", SitePostComments.Get(f.Db, id, p, "Club", A)!.Body);
            // Ändring i en förgranskad förening skickar tillbaka en publicerad kommentar till väntande (annars kringgås granskningen)
            Assert.True(SitePostComments.Edit(f.Db, id, p, "Club", A, 11, "Ny text", needsApproval: true, Now.AddMinutes(10)));
            Assert.True(SitePostComments.Get(f.Db, id, p, "Club", A)!.IsPending);
        }

        [SqlFact]
        public void Borttagen_kommentar_visas_utan_text_och_namn_för_andra_men_med_text_för_moderatorn_och_kan_återställas_i_trettio_dagar()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var (_, id) = Add(f.Db, p, A, member: 11, body: "Hemlig text");
            Assert.False(SitePostComments.SoftDelete(f.Db, id, p, "Club", A, 99, onlyAuthorId: 99, Now));              // en annans kommentar: bara moderatorn
            Assert.True(SitePostComments.SoftDelete(f.Db, id, p, "Club", A, 11, onlyAuthorId: 11, Now));               // den egna: alltid
            var tomb = Assert.Single(SitePostComments.List(f.Db, p, "Club", A, false, 99, 0, 50).Rows);
            Assert.True(tomb.IsDeleted); Assert.Equal("", tomb.Body); Assert.Equal("", tomb.AuthorName); Assert.Null(tomb.AuthorMemberId);   // en markering, ingen text
            var mod = Assert.Single(SitePostComments.List(f.Db, p, "Club", A, true, 5, 0, 50).Rows);
            Assert.Equal("Hemlig text", mod.Body);
            Assert.Equal(0, SitePostComments.Counts(f.Db, "Club", A, false)[p].Visible);                                // räknas inte
            Assert.False(SitePostComments.Restore(f.Db, id, p, "Club", A, Now.AddDays(31)));                            // för sent
            Assert.True(SitePostComments.Restore(f.Db, id, p, "Club", A, Now.AddDays(29)));
            Assert.Equal("Hemlig text", Assert.Single(SitePostComments.List(f.Db, p, "Club", A, false, 99, 0, 50).Rows).Body);
        }

        [SqlFact]
        public void Anmälan_en_per_person_inte_egen_inte_borttagen_och_hanteras_samlat()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var (_, id) = Add(f.Db, p, A, member: 11);
            Assert.False(SitePostComments.Report(f.Db, id, p, "Club", A, 11, null, Now));                              // inte sin egen
            Assert.True(SitePostComments.Report(f.Db, id, p, "Club", A, 12, "Olämpligt", Now));
            Assert.False(SitePostComments.Report(f.Db, id, p, "Club", A, 12, null, Now));                             // samma person en gång
            Assert.True(SitePostComments.Report(f.Db, id, p, "Club", A, 13, null, Now));
            Assert.Equal(2, SitePostComments.List(f.Db, p, "Club", A, true, 5, 0, 50).Rows[0].OpenReports);
            Assert.Equal(0, SitePostComments.List(f.Db, p, "Club", A, false, 12, 0, 50).Rows[0].OpenReports);          // en vanlig medlem ser inga anmälningar
            Assert.Equal(1, SitePostComments.Counts(f.Db, "Club", A, true)[p].Reported);
            Assert.Equal(2, SitePostComments.HandleReports(f.Db, id, p, "Club", A, 5, Now));
            Assert.Equal(0, SitePostComments.Counts(f.Db, "Club", A, true)[p].Reported);
            Assert.True(SitePostComments.SoftDelete(f.Db, id, p, "Club", A, 5, null, Now));
            Assert.False(SitePostComments.Report(f.Db, id, p, "Club", A, 14, null, Now));                             // en borttagen kommentar anmäls inte
        }

        [SqlFact]
        public void Gallringen_tar_raderade_kommentarer_efter_trettio_dagar_och_bara_dem_och_nollar_gamla_anmälningstexter()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var (_, old) = Add(f.Db, p, A); var (_, recent) = Add(f.Db, p, A); var (_, alive) = Add(f.Db, p, A);
            SitePostComments.SoftDelete(f.Db, old, p, "Club", A, 5, null, Now.AddDays(-31));
            SitePostComments.SoftDelete(f.Db, recent, p, "Club", A, 5, null, Now.AddDays(-5));
            SitePostComments.Report(f.Db, alive, p, "Club", A, 12, "Skäl med namn", Now.AddDays(-100));
            SitePostComments.HandleReports(f.Db, alive, p, "Club", A, 5, Now.AddDays(-100));
            Assert.Equal(1, SiteRetention.PurgeDeletedComments(f.Db, Now));
            Assert.Equal(new[] { recent, alive }.OrderBy(x => x), f.Db.Fetch<int>("SELECT Id FROM dbo.SitePostComment WHERE PostId = @0 ORDER BY Id", p).OrderBy(x => x));
            Assert.Null(f.Db.ExecuteScalar<string?>("SELECT Reason FROM dbo.SitePostCommentReport WHERE CommentId = @0", alive));   // den fria texten är borta
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostCommentReport WHERE CommentId = @0", alive));  // själva anmälan (datum) står kvar
        }

        [SqlFact]
        public void Hård_radering_av_nyheten_tar_kommentarer_och_anmälningar_med_sig_en_cascade_väg()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var p = Post(f.Db, A);
            var (_, id) = Add(f.Db, p, A);
            Assert.True(SitePostComments.Report(f.Db, id, p, "Club", A, 12, null, Now));
            f.Db.Execute("DELETE FROM dbo.SitePost WHERE Id = @0", p);
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostComment WHERE PostId = @0", p));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostCommentReport WHERE PostId = @0", p));
        }

        [SqlFact]
        public void Brytaren_läses_och_skrivs_med_rå_SQL_och_den_som_slog_på_sparas()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A, commentsOn: false);
            Assert.False(SitePostComments.Settings(f.Db, "Club", A).Enabled);
            Assert.True(SitePostComments.SetSettings(f.Db, "Club", A, true, SiteCommentRules.ModerationJuniors, 7, Now));
            var s = SitePostComments.Settings(f.Db, "Club", A);
            Assert.True(s.Enabled); Assert.Equal(SiteCommentRules.ModerationJuniors, s.Moderation); Assert.Equal(7, s.EnabledByMemberId); Assert.Equal(Now, s.EnabledDate);
            Assert.True(SitePostComments.SetSettings(f.Db, "Club", A, true, SiteCommentRules.ModerationAll, 8, Now.AddDays(1)));   // ändrat läge ändrar inte vem som slog på
            Assert.Equal(7, SitePostComments.Settings(f.Db, "Club", A).EnabledByMemberId);
            Assert.False(SitePostComments.SetSettings(f.Db, "Club", A, true, 3, 7, Now));                                       // okänt läge
            Assert.False(SitePostComments.SetSettings(f.Db, "Club", 990399, true, 0, 7, Now));                                  // föreningen finns inte
        }

        [SqlFact]
        public void Tidigare_medlem_är_den_som_lämnat_klubben_inte_en_administratör_som_aldrig_var_medlem()
        {
            using var f = new SiteSqlFixture();
            void Row(int member, int club, string status) => f.Db.Execute(
                "INSERT INTO dbo.ClubMembership (MemberId, ClubId, MembershipStatus, BackgroundCheckApproved, RegisteredInMap, HouseholdPrimary, CreatedDate) VALUES (@0, @1, @2, 0, 0, 0, SYSDATETIME())", member, club, status);
            Row(990401, A, "Utträdd"); Row(990402, A, "Aktiv"); Row(990403, A, "Avliden"); Row(990404, B, "Utträdd");
            var left = ClubRosterService.LeftMemberIds(f.Db, A, new[] { 990401, 990402, 990403, 990404, 990405 });
            Assert.Equal(new[] { 990401, 990403 }, left.OrderBy(x => x).ToArray());                                          // utträdd och avliden
            Assert.DoesNotContain(990402, left);                                                                              // aktiv
            Assert.DoesNotContain(990404, left);                                                                              // lämnade en ANNAN klubb
            Assert.DoesNotContain(990405, left);                                                                              // aldrig medlem (t.ex. en administratör): behåller sitt namn
            Assert.Empty(ClubRosterService.LeftMemberIds(f.Db, A, Array.Empty<int>()));
        }

        [SqlFact]
        public void Fäst_nyhet_står_först_bland_publicerade()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var older = Post(f.Db, A, mode: 0); var newer = Post(f.Db, A, mode: 0);
            var order0 = SitePosts.List(f.Db, "Club", A, new SiteViewer(true, true, false, false), false, 0, 10).Rows.Select(r => r.Id).ToArray();
            Assert.Equal(new[] { newer, older }, order0);
            Assert.True(SitePostComments.SetPostOptions(f.Db, older, "Club", A, 0, pinned: true));
            var order1 = SitePosts.List(f.Db, "Club", A, new SiteViewer(true, true, false, false), false, 0, 10).Rows.Select(r => r.Id).ToArray();
            Assert.Equal(new[] { older, newer }, order1);
            Assert.False(SitePostComments.SetPostOptions(f.Db, older, "Club", B, 0, true));                                   // fel ägare
            Assert.False(SitePostComments.SetPostOptions(f.Db, older, "Club", A, 3, false));                                  // okänt läge
        }
    }
}
