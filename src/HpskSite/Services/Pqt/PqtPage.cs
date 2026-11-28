using Microsoft.Data.SqlClient;
using NPoco;
using HpskSite.Services.SitePages;

namespace HpskSite.Services.Pqt
{
    /// <summary>
    /// Reglerna för /pqt (informationssidan om PQT med gilla, kommentarer och intresseanmälan för märken). Rena funktioner, testade utan databas.
    /// PQT är ett eget experiment från pistol.nu: sidan hör inte till någon klubb, så ingen förening ansvarar för kommentarerna och inget PUB-avtal gäller.
    /// Sajtadministratören är moderator. Textreglerna (rensning, längd, 15 minuters ändring, 30 dagars återställning, juniorgranskning) är samma som på Nyheter
    /// och hämtas från <see cref="SiteCommentRules"/> — en enda regelkälla.
    /// </summary>
    public static class PqtRules
    {
        public const int MaxPerPage = 1000;
        /// <summary>Högst så här många kommentarer visas på sidan (sidan har ett tak på 1000 kommentarer; över 200 visas de första och en rad säger det).</summary>
        public const int PageSize = 200;
        public const int PerMinute = 10;
        public const int PerDay = 60;
        public const int ReportsPerDay = 10;
        public const int LikesPerMinute = 30;
        public const int InterestPerDay = 10;
        public const int MaxBadges = 1000;
        public const int ClubOrEventMax = 120;
        public const int NoteMax = 500;

        /// <summary>Får tittaren gilla, kommentera och läsa kommentarernas text? Varje inloggad medlem. Andra ser bara antalen.</summary>
        public static bool CanParticipate(bool loggedIn) => loggedIn;

        /// <summary>Ska kommentaren hållas tills en moderator godkänt den? Juniorer (och okänd ålder) granskas; moderatorn aldrig.</summary>
        public static bool NeedsApproval(int birthYear, int year, bool isModerator) =>
            SiteCommentRules.NeedsApproval(SiteCommentRules.ModerationJuniors, birthYear, year, isModerator);

        /// <summary>Intresseanmälan: antal per nivå (0–1000, minst ett märke), vem eller vad märkena är till (obligatoriskt) och en valfri rad. Ger (värden, null) eller (null, felet på svenska).</summary>
        public static (PqtInterestInput? Input, string? Error) ValidateInterest(int marksman, int sharpshooter, int expert, string? clubOrEvent, string? note)
        {
            if (marksman < 0 || sharpshooter < 0 || expert < 0 || marksman > MaxBadges || sharpshooter > MaxBadges || expert > MaxBadges)
                return (null, $"Ange 0 till {MaxBadges} märken per nivå.");
            if (marksman + sharpshooter + expert == 0) return (null, "Ange hur många märken du vill ha, minst ett.");
            var club = Clean(clubOrEvent, ClubOrEventMax);
            if (club.Length == 0) return (null, "Skriv vilken klubb eller vilken tävling märkena är till.");
            var text = Clean(note, NoteMax);
            return (new PqtInterestInput(marksman, sharpshooter, expert, club, text.Length == 0 ? null : text), null);
        }

        /// <summary>Samma rensning som kommentarer (kontroll- och riktningstecken bort), ihopklämd till en rad, avkortad.</summary>
        internal static string Clean(string? raw, int max)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var (text, _) = SiteCommentRules.Validate(raw);
            if (text == null) return "";
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > max ? text[..max].TrimEnd() : text;
        }
    }

    public sealed record PqtInterestInput(int Marksman, int Sharpshooter, int Expert, string ClubOrEvent, string? Note);

    public enum PqtCommentAddResult { Ok, Full }

    /// <summary>En kommentar som den läses ur databasen. <see cref="Body"/> är tom för en raderad kommentar när tittaren inte är moderator.</summary>
    public sealed class PqtCommentRow
    {
        public int Id { get; set; }
        public int? AuthorMemberId { get; set; }
        public string AuthorName { get; set; } = "";
        public string Body { get; set; } = "";
        public DateTime CreatedDate { get; set; }
        public DateTime? EditedDate { get; set; }
        public byte Status { get; set; }
        public DateTime? DeletedDate { get; set; }
        public int OpenReports { get; set; }
        public bool IsPending => Status == 0;
        public bool IsDeleted => DeletedDate != null;
    }

    public sealed class PqtInterestRow
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string ClubOrEvent { get; set; } = "";
        public int Marksman { get; set; }
        public int Sharpshooter { get; set; }
        public int Expert { get; set; }
        public string? Note { get; set; }
        public DateTime UpdatedDate { get; set; }
    }

    /// <summary>
    /// Sidans tabeller i SQL. Gillningen och intresseanmälan har ett UNIKT index på medlemmen — indexet är domaren, inte koden: två flikar eller ett dubbelklick ger en rad.
    /// Villkoren (ej raderad, eget, taket) sitter i SAMMA sats som skrivningen.
    /// </summary>
    public static class PqtStore
    {
        // ── Gilla ─────────────────────────────────────────────────────────────
        public static int LikeCount(IDatabase db) => db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtLike");

        public static bool HasLiked(IDatabase db, int memberId) => db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtLike WHERE MemberId = @0", memberId) > 0;

        /// <summary>Gillar. Idempotent: en andra gång (dubbelklick, två flikar) ändrar inget.</summary>
        public static void Like(IDatabase db, int memberId, DateTime now)
        {
            try { db.Execute("IF NOT EXISTS (SELECT 1 FROM dbo.PqtLike WHERE MemberId = @0) INSERT INTO dbo.PqtLike (MemberId, CreatedDate) VALUES (@0, @1)", memberId, now); }
            catch (SqlException ex) when (ex.Number is 2601 or 2627) { /* en annan flik hann före: samma resultat */ }
        }

        public static void Unlike(IDatabase db, int memberId) => db.Execute("DELETE FROM dbo.PqtLike WHERE MemberId = @0", memberId);

        // ── Kommentarer ───────────────────────────────────────────────────────
        public static (PqtCommentAddResult Result, int Id) AddComment(IDatabase db, int memberId, string authorName, string body, bool needsApproval, DateTime now)
        {
            var id = db.ExecuteScalar<int?>(
                @"INSERT INTO dbo.PqtComment (AuthorMemberId, AuthorName, Body, CreatedDate, Status)
                  OUTPUT INSERTED.Id
                  SELECT @0, @1, @2, @3, @4
                  WHERE (SELECT COUNT(*) FROM dbo.PqtComment WHERE DeletedDate IS NULL) < @5",
                memberId, Clip(authorName, 120), body, now, needsApproval ? 0 : 1, PqtRules.MaxPerPage);
            return id != null ? (PqtCommentAddResult.Ok, id.Value) : (PqtCommentAddResult.Full, 0);
        }

        /// <summary>Kommentatorns ändring: bara egen, ej raderad, högst 15 minuter gammal. En kommentar som kräver granskning går tillbaka till väntande.</summary>
        public static bool EditComment(IDatabase db, int id, int memberId, string body, bool needsApproval, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.PqtComment SET Body = @2, EditedDate = @3, Status = CASE WHEN @4 = 1 THEN 0 ELSE Status END
                  WHERE Id = @0 AND AuthorMemberId = @1 AND DeletedDate IS NULL AND CreatedDate <= DATEADD(SECOND, @6, @3) AND CreatedDate >= DATEADD(MINUTE, -@5, @3)",
                id, memberId, body, now, needsApproval ? 1 : 0, SiteCommentRules.EditMinutes, SiteCommentRules.ClockSkewSeconds) == 1;

        /// <summary>Raderar mjukt (texten sparas 30 dagar). <paramref name="onlyAuthorId"/> satt = bara kommentarens egen skribent; null = moderator.</summary>
        public static bool SoftDelete(IDatabase db, int id, int byMemberId, int? onlyAuthorId, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.PqtComment SET DeletedDate = @2, DeletedByMemberId = @1
                  WHERE Id = @0 AND DeletedDate IS NULL AND (@3 IS NULL OR AuthorMemberId = @3)", id, byMemberId, now, onlyAuthorId) == 1;

        public static bool Restore(IDatabase db, int id, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.PqtComment SET DeletedDate = NULL, DeletedByMemberId = NULL
                  WHERE Id = @0 AND DeletedDate IS NOT NULL AND DeletedDate >= DATEADD(DAY, -@2, @1)", id, now, SiteCommentRules.RestoreDays) == 1;

        public static bool Approve(IDatabase db, int id) =>
            db.Execute("UPDATE dbo.PqtComment SET Status = 1 WHERE Id = @0 AND Status = 0 AND DeletedDate IS NULL", id) == 1;

        /// <summary>Anmäler en publicerad, ej raderad kommentar som inte är ens egen. Samma person anmäler samma kommentar en gång (unikt index).</summary>
        public static bool Report(IDatabase db, int commentId, int reporterId, string? reason, DateTime now)
        {
            try
            {
                return db.Execute(
                    @"INSERT INTO dbo.PqtCommentReport (CommentId, ReporterMemberId, Reason, CreatedDate)
                      SELECT c.Id, @1, @2, @3 FROM dbo.PqtComment c
                      WHERE c.Id = @0 AND c.Status = 1 AND c.DeletedDate IS NULL AND (c.AuthorMemberId IS NULL OR c.AuthorMemberId <> @1)",
                    commentId, reporterId, Blank(reason, 300), now) == 1;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627) { return false; }
        }

        public static int HandleReports(IDatabase db, int commentId, int byMemberId, DateTime now) =>
            db.Execute("UPDATE dbo.PqtCommentReport SET HandledDate = @2, HandledByMemberId = @1 WHERE CommentId = @0 AND HandledDate IS NULL", commentId, byMemberId, now);

        /// <summary>
        /// Kommentarerna, äldst först, sidindelat. En moderator ser allt (även väntande och raderade, med text och antal anmälningar); en skribent ser sina egna väntande;
        /// andra ser publicerade — en raderad publicerad kommentar visas som en markering utan text och namn.
        /// </summary>
        public static (List<PqtCommentRow> Rows, int Total) ListComments(IDatabase db, bool moderator, int viewerMemberId, int offset, int take)
        {
            take = Math.Clamp(take, 1, PqtRules.PageSize);
            offset = Math.Max(0, offset);
            const string where = @"(c.Status = 1 OR @0 = 1 OR (c.AuthorMemberId IS NOT NULL AND c.AuthorMemberId = @1 AND c.DeletedDate IS NULL))
                AND (c.DeletedDate IS NULL OR c.Status = 1 OR @0 = 1)";
            var total = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.PqtComment c WHERE {where}", moderator ? 1 : 0, viewerMemberId);
            var rows = db.Fetch<PqtCommentRow>(
                $@"SELECT c.Id,
                          CASE WHEN c.DeletedDate IS NOT NULL AND @0 = 0 THEN NULL ELSE c.AuthorMemberId END AS AuthorMemberId,
                          CASE WHEN c.DeletedDate IS NOT NULL AND @0 = 0 THEN N'' ELSE c.AuthorName END AS AuthorName,
                          CASE WHEN c.DeletedDate IS NOT NULL AND @0 = 0 THEN N'' ELSE c.Body END AS Body,
                          c.CreatedDate, c.EditedDate, c.Status, c.DeletedDate,
                          CASE WHEN @0 = 1 THEN (SELECT COUNT(*) FROM dbo.PqtCommentReport r WHERE r.CommentId = c.Id AND r.HandledDate IS NULL) ELSE 0 END AS OpenReports
                   FROM dbo.PqtComment c WHERE {where} ORDER BY c.Id OFFSET @2 ROWS FETCH NEXT @3 ROWS ONLY",
                moderator ? 1 : 0, viewerMemberId, offset, take);
            return (rows, total);
        }

        /// <summary>Antalet synliga kommentarer (publicerade, ej raderade) — det anonyma tittare ser.</summary>
        public static int VisibleCommentCount(IDatabase db) => db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.PqtComment WHERE Status = 1 AND DeletedDate IS NULL");

        // ── Intresseanmälan för märken ───────────────────────────────────────
        /// <summary>Lägger in eller uppdaterar medlemmens anmälan (en rad per medlem).</summary>
        public static void SaveInterest(IDatabase db, int memberId, string memberName, PqtInterestInput input, DateTime now)
        {
            // ⚠️ Inget @@ROWCOUNT här: NPoco läser det som parametern @ROWCOUNT ("Must declare the scalar variable"). EXISTS gör samma sak.
            const string sql = @"
                IF EXISTS (SELECT 1 FROM dbo.PqtBadgeInterest WHERE MemberId = @0)
                    UPDATE dbo.PqtBadgeInterest SET MemberName = @1, ClubOrEvent = @2, Marksman = @3, Sharpshooter = @4, Expert = @5, Note = @6, UpdatedDate = @7 WHERE MemberId = @0;
                ELSE
                    INSERT INTO dbo.PqtBadgeInterest (MemberId, MemberName, ClubOrEvent, Marksman, Sharpshooter, Expert, Note, CreatedDate, UpdatedDate) VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @7);";
            try { db.Execute(sql, memberId, Clip(memberName, 120), input.ClubOrEvent, input.Marksman, input.Sharpshooter, input.Expert, input.Note, now); }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                db.Execute("UPDATE dbo.PqtBadgeInterest SET MemberName = @1, ClubOrEvent = @2, Marksman = @3, Sharpshooter = @4, Expert = @5, Note = @6, UpdatedDate = @7 WHERE MemberId = @0",
                    memberId, Clip(memberName, 120), input.ClubOrEvent, input.Marksman, input.Sharpshooter, input.Expert, input.Note, now);
            }
        }

        public static PqtInterestRow? MyInterest(IDatabase db, int memberId) =>
            db.FirstOrDefault<PqtInterestRow>("SELECT Id, MemberId, MemberName, ClubOrEvent, Marksman, Sharpshooter, Expert, Note, UpdatedDate FROM dbo.PqtBadgeInterest WHERE MemberId = @0", memberId);

        public static bool DeleteMyInterest(IDatabase db, int memberId) => db.Execute("DELETE FROM dbo.PqtBadgeInterest WHERE MemberId = @0", memberId) > 0;

        /// <summary>Alla anmälningar, senast ändrad först — bara för sajtadministratören.</summary>
        public static List<PqtInterestRow> ListInterest(IDatabase db) =>
            db.Fetch<PqtInterestRow>("SELECT Id, MemberId, MemberName, ClubOrEvent, Marksman, Sharpshooter, Expert, Note, UpdatedDate FROM dbo.PqtBadgeInterest ORDER BY UpdatedDate DESC, Id DESC");

        // ── Gallring ──────────────────────────────────────────────────────────
        /// <summary>Raderade kommentarer är slutligt borta 30 dagar efter radering. Hanterade anmälningars fria text nollas efter 90 dagar. Körs bara när tabellerna finns.</summary>
        public static int PurgeDeletedComments(IDatabase db, DateTime now)
        {
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(N'dbo.PqtComment', N'U') IS NULL OR OBJECT_ID(N'dbo.PqtCommentReport', N'U') IS NULL THEN 0 ELSE 1 END") == 0) return 0;
            db.Execute("UPDATE dbo.PqtCommentReport SET Reason = NULL WHERE Reason IS NOT NULL AND HandledDate IS NOT NULL AND HandledDate < @0", now.AddDays(-90));
            return db.Execute("DELETE FROM dbo.PqtComment WHERE DeletedDate IS NOT NULL AND DeletedDate < @0", now.AddDays(-SiteCommentRules.RestoreDays));
        }

        private static string Clip(string? s, int max)
        {
            var t = (s ?? "").Trim();
            return t.Length <= max ? t : t[..max];
        }

        private static string? Blank(string? s, int max)
        {
            var t = Clip(s, max);
            return t.Length == 0 ? null : t;
        }
    }
}
