using HpskSite.Services;
using HpskSite.Services.Pqt;
using HpskSite.Services.SitePages;
using Microsoft.Data.SqlClient;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// /pqt (2026-11-28): gilla, kommentarer och intresseanmälan för märken. Ren kod (regler) och SQL mot dev-databasen i en transaktion som alltid rullas tillbaka
    /// (<see cref="SiteSqlFixture"/>, kräver <c>HPSK_TEST_SQL</c> och migreringen <c>create-pqt-page-tables.sql</c>). Varje påstående har en motsats i samma test så att det kan falla.
    /// </summary>
    public class PqtRulesTests
    {
        [Fact]
        public void Bara_inloggade_deltar()
        {
            Assert.True(PqtRules.CanParticipate(true));
            Assert.False(PqtRules.CanParticipate(false));
        }

        [Fact]
        public void Juniorer_och_okänd_ålder_förgranskas_men_aldrig_moderatorn_eller_en_vuxen()
        {
            Assert.True(PqtRules.NeedsApproval(birthYear: 2012, year: 2026, isModerator: false));    // 14 år
            Assert.True(PqtRules.NeedsApproval(birthYear: 2008, year: 2026, isModerator: false));    // 18 år: gränsen går vid 18
            Assert.False(PqtRules.NeedsApproval(birthYear: 2007, year: 2026, isModerator: false));   // 19 år
            Assert.True(PqtRules.NeedsApproval(birthYear: 0, year: 2026, isModerator: false));       // okänd ålder räknas som junior
            Assert.False(PqtRules.NeedsApproval(birthYear: 2012, year: 2026, isModerator: true));    // moderatorn förgranskas aldrig
        }

        [Fact]
        public void Intresseanmälan_kräver_minst_ett_märke_och_en_klubb_eller_tävling()
        {
            var (ok, err) = PqtRules.ValidateInterest(10, 5, 0, "Haaplinge PSK", "Till Kretsmästerskapet");
            Assert.Null(err);
            Assert.Equal(new PqtInterestInput(10, 5, 0, "Haaplinge PSK", "Till Kretsmästerskapet"), ok);

            Assert.NotNull(PqtRules.ValidateInterest(0, 0, 0, "Haaplinge PSK", null).Error);          // inga märken alls
            Assert.NotNull(PqtRules.ValidateInterest(1, 0, 0, "", null).Error);                        // ingen klubb eller tävling
            Assert.NotNull(PqtRules.ValidateInterest(1, 0, 0, "   ", null).Error);
            Assert.NotNull(PqtRules.ValidateInterest(-1, 2, 0, "X", null).Error);                      // negativt
            Assert.NotNull(PqtRules.ValidateInterest(1001, 0, 0, "X", null).Error);                    // över taket
            Assert.Null(PqtRules.ValidateInterest(1000, 0, 0, "X", null).Error);                       // taket själv är tillåtet
        }

        [Fact]
        public void Intresseanmälans_texter_rensas_klämms_ihop_och_kortas()
        {
            var (ok, _) = PqtRules.ValidateInterest(1, 0, 0, "Ha‮ap  linge\nPSK", "  rad ett\n\n\nrad två ");
            Assert.Equal("Haap linge PSK", ok!.ClubOrEvent);               // dolda riktningstecken bort, blanksteg ihopklämda
            Assert.Equal("rad ett rad två", ok.Note);
            var (long1, _) = PqtRules.ValidateInterest(1, 0, 0, new string('a', 300), new string('b', 900));
            Assert.Equal(PqtRules.ClubOrEventMax, long1!.ClubOrEvent.Length);
            Assert.Equal(PqtRules.NoteMax, long1.Note!.Length);
            Assert.Null(PqtRules.ValidateInterest(1, 0, 0, "X", "  ").Input!.Note);                    // tom anteckning blir null
        }

        [Fact]
        public void Raderingsvägen_känner_till_PQT_tabellerna()
        {
            Assert.Contains(MemberDataPurgeService.SubjectTables, t => t.Table == "PqtLike" && t.Column == "MemberId");
            Assert.Contains(MemberDataPurgeService.SubjectTables, t => t.Table == "PqtBadgeInterest" && t.Column == "MemberId");
            var cols = MemberDataPurgeService.AssignmentColumns;
            Assert.Contains(cols, c => c.Table == "PqtComment" && c.MemberColumn == "AuthorMemberId" && c.ClearSql.Contains("Body ="));   // texten töms vid raderingsbegäran
            Assert.Contains(cols, c => c.Table == "PqtComment" && c.MemberColumn == "DeletedByMemberId");
            Assert.Contains(cols, c => c.Table == "PqtCommentReport" && c.MemberColumn == "ReporterMemberId" && c.ClearSql.Contains("Reason = NULL"));
            Assert.Contains(cols, c => c.Table == "PqtCommentReport" && c.MemberColumn == "HandledByMemberId");
            foreach (var c in cols.Where(c => c.Table.StartsWith("Pqt")))
                Assert.DoesNotContain(c.MemberColumn, c.ClearSql);                                                                         // ClearSql får inte nämna medlemskolumnen
        }
    }

    public class PqtStoreTests
    {
        private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0);
        private const int A = 9101, B = 9102, Admin = 9103;

        [SqlFact]
        public void Gilla_är_ett_per_medlem_även_vid_dubbelklick_och_går_att_ta_tillbaka()
        {
            using var f = new SiteSqlFixture();
            var before = PqtStore.LikeCount(f.Db);
            PqtStore.Like(f.Db, A, Now); PqtStore.Like(f.Db, A, Now);                       // dubbelklick
            Assert.Equal(before + 1, PqtStore.LikeCount(f.Db));
            Assert.True(PqtStore.HasLiked(f.Db, A)); Assert.False(PqtStore.HasLiked(f.Db, B));
            PqtStore.Like(f.Db, B, Now);
            Assert.Equal(before + 2, PqtStore.LikeCount(f.Db));
            PqtStore.Unlike(f.Db, A);
            Assert.False(PqtStore.HasLiked(f.Db, A)); Assert.True(PqtStore.HasLiked(f.Db, B));   // tar bara den egna
            Assert.Equal(before + 1, PqtStore.LikeCount(f.Db));
            PqtStore.Unlike(f.Db, A);                                                        // idempotent
            Assert.Equal(before + 1, PqtStore.LikeCount(f.Db));
        }

        [SqlFact]
        public void Ett_andra_gilla_rad_kan_inte_finnas_unikt_index_är_domaren()
        {
            using var f = new SiteSqlFixture();
            f.Db.Execute("INSERT INTO dbo.PqtLike (MemberId, CreatedDate) VALUES (@0, @1)", A, Now);
            var ex = Assert.ThrowsAny<SqlException>(() => f.Db.Execute("INSERT INTO dbo.PqtLike (MemberId, CreatedDate) VALUES (@0, @1)", A, Now));
            Assert.Contains(ex.Number, new[] { 2601, 2627 });
        }

        [SqlFact]
        public void Kommentarer_syns_efter_status_och_tittare()
        {
            using var f = new SiteSqlFixture();
            var (r1, ok) = PqtStore.AddComment(f.Db, A, "Anna", "Publicerad direkt", needsApproval: false, Now);
            var (r2, pend) = PqtStore.AddComment(f.Db, B, "Junior", "Väntar", needsApproval: true, Now);
            Assert.Equal(PqtCommentAddResult.Ok, r1); Assert.Equal(PqtCommentAddResult.Ok, r2);

            Assert.Equal(1, PqtStore.VisibleCommentCount(f.Db) - Baseline(f));                                           // bara den publicerade räknas för en anonym
            var (forA, _) = PqtStore.ListComments(f.Db, moderator: false, viewerMemberId: A, 0, 50);
            Assert.DoesNotContain(forA, c => c.Id == pend);                                                               // andras väntande syns inte
            var (forB, _) = PqtStore.ListComments(f.Db, moderator: false, viewerMemberId: B, 0, 50);
            Assert.Contains(forB, c => c.Id == pend && c.IsPending);                                                      // skribenten ser sin egen väntande
            var (forMod, _) = PqtStore.ListComments(f.Db, moderator: true, viewerMemberId: Admin, 0, 50);
            Assert.Contains(forMod, c => c.Id == pend && c.IsPending); Assert.Contains(forMod, c => c.Id == ok);          // moderatorn ser allt

            Assert.False(PqtStore.Approve(f.Db, ok));                                                                     // redan publicerad
            Assert.True(PqtStore.Approve(f.Db, pend));
            var (afterApprove, _) = PqtStore.ListComments(f.Db, moderator: false, viewerMemberId: A, 0, 50);
            Assert.Contains(afterApprove, c => c.Id == pend && !c.IsPending);
            Assert.False(PqtStore.Approve(f.Db, pend));                                                                   // bara en gång
        }

        private static int Baseline(SiteSqlFixture f) => f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE Status = 1 AND DeletedDate IS NULL AND AuthorMemberId NOT IN (@0, @1)", A, B);

        [SqlFact]
        public void Bara_skribenten_ändrar_och_bara_i_femton_minuter_och_ändring_kan_skicka_tillbaka_till_granskning()
        {
            using var f = new SiteSqlFixture();
            var (_, id) = PqtStore.AddComment(f.Db, A, "Anna", "Första", false, Now);
            Assert.False(PqtStore.EditComment(f.Db, id, B, "Kapad", false, Now.AddMinutes(1)));                           // inte din
            Assert.True(PqtStore.EditComment(f.Db, id, A, "Ändrad", false, Now.AddMinutes(15)));
            Assert.False(PqtStore.EditComment(f.Db, id, A, "För sent", false, Now.AddMinutes(15).AddSeconds(30)));        // över femton minuter
            Assert.Equal("Ändrad", f.Db.ExecuteScalar<string>("SELECT Body FROM dbo.PqtComment WHERE Id = @0", id));
            Assert.True(PqtStore.EditComment(f.Db, id, A, "Ny text", needsApproval: true, Now.AddMinutes(16).AddSeconds(-90)));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT CAST(Status AS INT) FROM dbo.PqtComment WHERE Id = @0", id));   // en junior som ändrar går tillbaka till väntande
            PqtStore.SoftDelete(f.Db, id, A, A, Now);
            Assert.False(PqtStore.EditComment(f.Db, id, A, "Efter radering", false, Now.AddMinutes(1)));                  // raderad går inte att ändra
        }

        [SqlFact]
        public void Radering_egen_eller_moderator_visas_som_markering_och_återställs_bara_inom_trettio_dagar()
        {
            using var f = new SiteSqlFixture();
            var (_, id) = PqtStore.AddComment(f.Db, A, "Anna", "Hemlig text", false, Now);
            Assert.False(PqtStore.SoftDelete(f.Db, id, B, onlyAuthorId: B, Now));                                         // en annan kan inte radera din
            Assert.True(PqtStore.SoftDelete(f.Db, id, Admin, onlyAuthorId: null, Now));                                   // moderatorn kan
            Assert.False(PqtStore.SoftDelete(f.Db, id, Admin, null, Now));                                                // redan borta

            var (forOther, _) = PqtStore.ListComments(f.Db, false, B, 0, 50);
            var shown = forOther.Single(c => c.Id == id);
            Assert.True(shown.IsDeleted); Assert.Equal("", shown.Body); Assert.Equal("", shown.AuthorName); Assert.Null(shown.AuthorMemberId);   // markering utan text och namn
            var (forMod, _) = PqtStore.ListComments(f.Db, true, Admin, 0, 50);
            Assert.Equal("Hemlig text", forMod.Single(c => c.Id == id).Body);                                              // moderatorn ser texten

            Assert.True(PqtStore.Restore(f.Db, id, Now.AddDays(30)));
            PqtStore.SoftDelete(f.Db, id, Admin, null, Now);
            Assert.False(PqtStore.Restore(f.Db, id, Now.AddDays(31)));                                                    // efter trettio dagar
        }

        [SqlFact]
        public void Anmälan_kan_inte_göras_på_egen_väntande_eller_raderad_kommentar_och_bara_en_gång_per_person()
        {
            using var f = new SiteSqlFixture();
            var (_, ok) = PqtStore.AddComment(f.Db, A, "Anna", "Anmäls", false, Now);
            var (_, pending) = PqtStore.AddComment(f.Db, A, "Anna", "Väntar", true, Now);
            Assert.False(PqtStore.Report(f.Db, ok, A, "egen", Now));                                                      // inte sin egen
            Assert.False(PqtStore.Report(f.Db, pending, B, null, Now));                                                   // inte en väntande
            Assert.True(PqtStore.Report(f.Db, ok, B, "olämpligt", Now));
            Assert.False(PqtStore.Report(f.Db, ok, B, "igen", Now));                                                      // en gång per person
            Assert.True(PqtStore.Report(f.Db, ok, Admin, null, Now));                                                     // en annan person får anmäla
            var (mod, _) = PqtStore.ListComments(f.Db, true, Admin, 0, 50);
            Assert.Equal(2, mod.Single(c => c.Id == ok).OpenReports);
            var (nonMod, _) = PqtStore.ListComments(f.Db, false, B, 0, 50);
            Assert.Equal(0, nonMod.Single(c => c.Id == ok).OpenReports);                                                  // antalet syns bara för moderatorn
            Assert.Equal(2, PqtStore.HandleReports(f.Db, ok, Admin, Now));
            Assert.Equal(0, PqtStore.HandleReports(f.Db, ok, Admin, Now));                                                // idempotent
            PqtStore.SoftDelete(f.Db, ok, Admin, null, Now);
            Assert.False(PqtStore.Report(f.Db, ok, 9104, null, Now));                   // raderad går inte att anmäla
        }

        [SqlFact]
        public void Taket_stoppar_nya_kommentarer_men_en_raderad_gör_plats()
        {
            using var f = new SiteSqlFixture();
            var existing = f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE DeletedDate IS NULL");
            f.Db.Execute($"INSERT INTO dbo.PqtComment (AuthorMemberId, AuthorName, Body, CreatedDate, Status) SELECT TOP ({PqtRules.MaxPerPage - existing}) @0, N'x', N'fyll', @1, 1 FROM sys.all_objects a CROSS JOIN sys.all_objects b", A, Now);
            Assert.Equal(PqtRules.MaxPerPage, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE DeletedDate IS NULL"));
            Assert.Equal(PqtCommentAddResult.Full, PqtStore.AddComment(f.Db, B, "Bo", "Över taket", false, Now).Result);
            var any = f.Db.ExecuteScalar<int>("SELECT TOP 1 Id FROM dbo.PqtComment WHERE DeletedDate IS NULL");
            PqtStore.SoftDelete(f.Db, any, Admin, null, Now);
            Assert.Equal(PqtCommentAddResult.Ok, PqtStore.AddComment(f.Db, B, "Bo", "Plats igen", false, Now).Result);
        }

        [SqlFact]
        public void Intresseanmälan_är_en_rad_per_medlem_som_uppdateras_och_den_egna_kan_tas_bort()
        {
            using var f = new SiteSqlFixture();
            var first = new PqtInterestInput(10, 0, 0, "Haaplinge PSK", null);
            PqtStore.SaveInterest(f.Db, A, "Anna", first, Now);
            PqtStore.SaveInterest(f.Db, A, "Anna", new PqtInterestInput(20, 5, 1, "Kretsmästerskapet", "Hämtas på plats"), Now.AddHours(1));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtBadgeInterest WHERE MemberId = @0", A));   // en rad
            var mine = PqtStore.MyInterest(f.Db, A)!;
            Assert.Equal((20, 5, 1, "Kretsmästerskapet", "Hämtas på plats"), (mine.Marksman, mine.Sharpshooter, mine.Expert, mine.ClubOrEvent, mine.Note));
            Assert.Null(PqtStore.MyInterest(f.Db, B));
            PqtStore.SaveInterest(f.Db, B, "Bo", first, Now);
            Assert.True(PqtStore.ListInterest(f.Db).Count(r => r.MemberId is A or B) == 2);
            Assert.True(PqtStore.DeleteMyInterest(f.Db, A));
            Assert.Null(PqtStore.MyInterest(f.Db, A)); Assert.NotNull(PqtStore.MyInterest(f.Db, B));                          // tog bara den egna
            Assert.False(PqtStore.DeleteMyInterest(f.Db, A));
        }

        [SqlFact]
        public void Databasen_vägrar_en_intresseanmälan_utan_märken_även_om_koden_missar_det()
        {
            using var f = new SiteSqlFixture();
            Assert.ThrowsAny<SqlException>(() => f.Db.Execute("INSERT INTO dbo.PqtBadgeInterest (MemberId, MemberName, ClubOrEvent, Marksman, Sharpshooter, Expert) VALUES (@0, N'x', N'y', 0, 0, 0)", A));
        }

        [SqlFact]
        public void Gallringen_tar_raderade_kommentarer_efter_trettio_dagar_och_nollar_gamla_anmälningstexter()
        {
            using var f = new SiteSqlFixture();
            var (_, old) = PqtStore.AddComment(f.Db, A, "Anna", "Gammal", false, Now.AddDays(-60));
            var (_, recent) = PqtStore.AddComment(f.Db, A, "Anna", "Ny raderad", false, Now.AddDays(-5));
            var (_, live) = PqtStore.AddComment(f.Db, A, "Anna", "Levande", false, Now.AddDays(-60));
            PqtStore.SoftDelete(f.Db, old, Admin, null, Now.AddDays(-31));
            PqtStore.SoftDelete(f.Db, recent, Admin, null, Now.AddDays(-5));
            PqtStore.Report(f.Db, live, B, "gammal text", Now.AddDays(-120));
            PqtStore.HandleReports(f.Db, live, Admin, Now.AddDays(-100));

            Assert.Equal(1, PqtStore.PurgeDeletedComments(f.Db, Now));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE Id = @0", old));           // borta
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE Id = @0", recent));        // 5 dagar: kvar, går att återställa
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE Id = @0", live));          // levande rörs inte
            Assert.Null(f.Db.ExecuteScalar<string?>("SELECT Reason FROM dbo.PqtCommentReport WHERE CommentId = @0", live));   // hanterad för mer än 90 dagar sedan: texten nollad
            Assert.Equal(0, PqtStore.PurgeDeletedComments(f.Db, Now));                                                    // idempotent
        }

        [SqlFact]
        public void Gallring_av_en_medlem_raderar_gillning_och_anmälan_och_släpper_personen_på_kommentarerna_med_raderna_kvar()
        {
            using var f = new SiteSqlFixture();
            PqtStore.Like(f.Db, A, Now);
            PqtStore.SaveInterest(f.Db, A, "Anna", new PqtInterestInput(1, 0, 0, "X", "anteckning"), Now);
            var (_, c) = PqtStore.AddComment(f.Db, A, "Anna", "Jag heter Anna Andersson", false, Now);
            var (_, other) = PqtStore.AddComment(f.Db, B, "Bo", "Bos kommentar", false, Now);
            PqtStore.SoftDelete(f.Db, c, A, A, Now);
            PqtStore.Report(f.Db, other, A, "en text som nämner namn", Now);

            foreach (var (table, col) in MemberDataPurgeService.SubjectTables.Where(t => t.Table.StartsWith("Pqt")))
                f.Db.Execute($"DELETE FROM [{table}] WHERE [{col}] = @0", A);
            foreach (var (table, col, clear) in MemberDataPurgeService.AssignmentColumns.Where(x => x.Table.StartsWith("Pqt")))
                f.Db.Execute($"UPDATE [{table}] SET [{col}] = NULL, {clear} WHERE [{col}] = @0", A);

            Assert.False(PqtStore.HasLiked(f.Db, A)); Assert.Null(PqtStore.MyInterest(f.Db, A));                              // hård radering
            var row = f.Db.Single<(int? Author, string Name, string Body, int? DeletedBy)>("SELECT AuthorMemberId, AuthorName, Body, DeletedByMemberId FROM dbo.PqtComment WHERE Id = @0", c);
            Assert.Null(row.Author); Assert.Equal("Tidigare medlem", row.Name); Assert.Equal("(Kommentaren är raderad på begäran)", row.Body); Assert.Null(row.DeletedBy);
            Assert.Equal("Bos kommentar", f.Db.ExecuteScalar<string>("SELECT Body FROM dbo.PqtComment WHERE Id = @0", other));  // andras rörs inte
            var rep = f.Db.Single<(int? Who, string? Reason)>("SELECT ReporterMemberId, Reason FROM dbo.PqtCommentReport WHERE CommentId = @0", other);
            Assert.Null(rep.Who); Assert.Null(rep.Reason);                                                                    // anmälan står kvar utan person och fri text
        }
    }
}
