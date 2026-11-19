using System.Text;
using Microsoft.Data.SqlClient;
using NPoco;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Reglerna för kommentarer på en nyhet — rena funktioner (testade utan databas). Klubbstyrelsen ansvarar för kommentarerna (BBS-lagen 1998:112); därför kan de bara slås på
    /// för en förening med godkänt PUB-avtal, och därför kan styrelsen, administratören och nyhetens skribent ta bort och förgranska. Kommentarer på en KRETS nyheter byggs inte i v1.
    /// </summary>
    public static class SiteCommentRules
    {
        public const int MaxLength = 2000;
        public const int EditMinutes = 15;
        public const int MaxPerPost = 500;
        public const int PageSize = 50;
        public const int RestoreDays = 30;
        public const int PerMinute = 10;
        public const int PerDay = 60;
        public const int ReportsPerDay = 10;
        /// <summary>Förgranskningsläge på föreningen: 0 = ingen, 1 = juniorer förgranskas, 2 = alla förgranskas.</summary>
        public const byte ModerationNone = 0, ModerationJuniors = 1, ModerationAll = 2;
        /// <summary>Konservativ juniorgräns: den som fyller högst så här många år under året räknas som junior (och en okänd ålder räknas också som junior när förgranskning gäller).</summary>
        public const int JuniorMaxAge = 18;

        /// <summary>Får tittaren skriva en kommentar på den här nyheten? Alla villkor måste hålla; ingen "nästan".</summary>
        public static bool CanComment(SiteViewer viewer, string ownerType, byte postCommentsMode, bool ownerEnabled, bool dpaAccepted) =>
            ownerType == SiteTemplates.Club && ownerEnabled && dpaAccepted && postCommentsMode == 1 && SitePostReactionRules.CanReact(viewer);

        /// <summary>Får tittaren läsa kommentarernas text? Samma krets som får reagera; andra ser bara antalet.</summary>
        public static bool CanReadText(SiteViewer viewer) => SitePostReactionRules.CanReact(viewer);

        /// <summary>Moderator: klubbens administratör (inkl. sajtadmin), styrelsemedlem eller nyhetens skribent.</summary>
        public static bool CanModerate(SiteViewer viewer, bool isPostAuthor) => viewer.LoggedIn && (viewer.IsOwnerAdmin || viewer.IsOwnerBoard || isPostAuthor);

        /// <summary>Marginal för klockor och avrundning: DATETIME2(0) avrundar till närmaste sekund, så en alldeles ny kommentar kan ligga upp till en halv sekund i framtiden.</summary>
        public const int ClockSkewSeconds = 5;

        /// <summary>Kommentatorn får ändra sin egen kommentar de första femton minuterna.</summary>
        public static bool CanEdit(bool isOwn, DateTime createdDate, DateTime now) =>
            isOwn && now - createdDate <= TimeSpan.FromMinutes(EditMinutes) && createdDate - now <= TimeSpan.FromSeconds(ClockSkewSeconds);

        /// <summary>Kan en raderad kommentar återställas? Bara 30 dagar (sedan gallras den).</summary>
        public static bool CanRestore(DateTime deletedDate, DateTime now) => now - deletedDate <= TimeSpan.FromDays(RestoreDays);

        public static bool IsJunior(int birthYear, int year) => birthYear <= 0 || year - birthYear <= JuniorMaxAge;

        /// <summary>Ska den här kommentaren hållas tills en moderator godkänt den? Moderatorer och skribenten förgranskas aldrig (de kan ändå godkänna).</summary>
        public static bool NeedsApproval(byte moderationMode, int birthYear, int year, bool isModerator) =>
            !isModerator && (moderationMode == ModerationAll || (moderationMode == ModerationJuniors && IsJunior(birthYear, year)));

        /// <summary>
        /// Rensar texten: ren text, radbrytningar kvar, kontroll- och riktningstecken (bidi, nollbredd) bort, högst två tomma rader i rad. Ger (text, null) eller (null, felet på svenska).
        /// Texten lagras och visas som text (kodad) — en länk blir aldrig klickbar.
        /// </summary>
        public static (string? Body, string? Error) Validate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (null, "Skriv något först.");
            var sb = new StringBuilder(raw.Length);
            foreach (var ch in raw.Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                if (ch == '\n' || ch == '\t') { sb.Append(ch == '\t' ? ' ' : ch); continue; }
                if (char.IsControl(ch)) continue;
                if (IsHiddenFormatting(ch)) continue;
                sb.Append(ch);
            }
            var text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
            if (text.Length == 0) return (null, "Skriv något först.");
            if (text.Length > MaxLength) return (null, $"Kommentaren är för lång (högst {MaxLength} tecken).");
            return (text, null);
        }

        /// <summary>Riktnings- (bidi), nollbredds- och radseparatortecken som kan dölja eller vända text. Koderna står som tal (aldrig som tecken i källan).</summary>
        private static bool IsHiddenFormatting(char ch)
        {
            int c = ch;
            return (c >= 0x202A && c <= 0x202E) || (c >= 0x2066 && c <= 0x2069) || (c >= 0x200B && c <= 0x200F) || c == 0x061C || c == 0xFEFF || c == 0x2028 || c == 0x2029;
        }
    }

    public enum SiteCommentAddResult { Ok, Closed, Full }

    /// <summary>En kommentar som den läses ur databasen. <see cref="Body"/> är tom för en raderad kommentar när tittaren inte är moderator.</summary>
    public sealed class SitePostCommentRow
    {
        public int Id { get; set; }
        public int PostId { get; set; }
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

    /// <summary>Antalen för en nyhet i arkivet: synliga, väntande på granskning och anmälda (de två sista räknas bara för moderatorer).</summary>
    public sealed record SiteCommentCounts(int Visible, int Pending, int Reported);

    /// <summary>Föreningens inställningar för kommentarer (raden i SiteOwner).</summary>
    public sealed record SiteCommentSettings(bool Enabled, byte Moderation, int? EnabledByMemberId, DateTime? EnabledDate);

    /// <summary>
    /// Kommentarerna i SQL. Varje fråga bär ägaren och nyheten (id-lagen). En kommentar rör ALDRIG <c>SitePost.Revision</c>. Villkoren (nyheten publicerad och inte raderad, läget på, föreningens
    /// brytare på, taket) sitter i SAMMA sats som skrivningen — ingen kan kommentera en nyhet som stängts mellan kontrollen och skrivningen. Läs/skriv av föreningens brytare sker med rå SQL (inte
    /// via SiteOwner-POCO:n).
    /// </summary>
    public static class SitePostComments
    {
        public static SiteCommentSettings Settings(IDatabase db, string ownerType, int ownerId)
        {
            var r = db.FirstOrDefault<SettingsRow>(
                "SELECT NewsCommentsEnabled AS Enabled, NewsCommentsModeration AS Moderation, NewsCommentsEnabledByMemberId AS ByMemberId, NewsCommentsEnabledDate AS EnabledDate FROM dbo.SiteOwner WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId);
            return r == null ? new SiteCommentSettings(false, 0, null, null) : new SiteCommentSettings(r.Enabled, r.Moderation, r.ByMemberId, r.EnabledDate);
        }

        /// <summary>Slår på/av och sätter förgranskningsläget. Falskt = föreningen finns inte i SiteOwner.</summary>
        public static bool SetSettings(IDatabase db, string ownerType, int ownerId, bool enabled, byte moderation, int memberId, DateTime now)
        {
            if (moderation > 2) return false;
            return db.Execute(
                @"UPDATE dbo.SiteOwner SET NewsCommentsModeration = @2,
                         NewsCommentsEnabledByMemberId = CASE WHEN @3 = 1 AND NewsCommentsEnabled = 0 THEN @4 ELSE NewsCommentsEnabledByMemberId END,
                         NewsCommentsEnabledDate = CASE WHEN @3 = 1 AND NewsCommentsEnabled = 0 THEN @5 ELSE NewsCommentsEnabledDate END,
                         NewsCommentsEnabled = @3
                  WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId, (int)moderation, enabled ? 1 : 0, memberId, now) == 1;
        }

        /// <summary>
        /// Lägger in en kommentar. Villkor i samma sats: nyheten finns hos ägaren, är publicerad, ej raderad, läget är På, föreningens brytare är på och taket (500) inte nått. Status 0 = väntar.
        /// Ger Ok + id, Closed (något av villkoren) eller Full (taket).
        /// </summary>
        public static (SiteCommentAddResult Result, int Id) Add(IDatabase db, int postId, string ownerType, int ownerId, int memberId, string authorName, string body, bool needsApproval, DateTime now)
        {
            var id = db.ExecuteScalar<int?>(
                @"INSERT INTO dbo.SitePostComment (PostId, OwnerType, OwnerId, AuthorMemberId, AuthorName, Body, CreatedDate, Status)
                  OUTPUT INSERTED.Id
                  SELECT p.Id, p.OwnerType, p.OwnerId, @3, @4, @5, @6, @7
                  FROM dbo.SitePost p JOIN dbo.SiteOwner o ON o.OwnerType = p.OwnerType AND o.OwnerId = p.OwnerId
                  WHERE p.Id = @0 AND p.OwnerType = @1 AND p.OwnerId = @2 AND p.Status = 1 AND p.DeletedDate IS NULL AND p.CommentsMode = 1 AND o.NewsCommentsEnabled = 1
                    AND (SELECT COUNT(*) FROM dbo.SitePostComment c WHERE c.PostId = p.Id AND c.DeletedDate IS NULL) < @8",
                postId, ownerType, ownerId, memberId, Clip(authorName, 120), body, now, needsApproval ? 0 : 1, SiteCommentRules.MaxPerPost);
            if (id != null) return (SiteCommentAddResult.Ok, id.Value);
            var full = db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostComment WHERE PostId = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL", postId, ownerType, ownerId) >= SiteCommentRules.MaxPerPost;
            return (full ? SiteCommentAddResult.Full : SiteCommentAddResult.Closed, 0);
        }

        /// <summary>Kommentatorns ändring: bara egen, ej raderad, högst 15 minuter gammal. Kräver förgranskning ändrar den en publicerad kommentar tillbaka till väntande.</summary>
        public static bool Edit(IDatabase db, int id, int postId, string ownerType, int ownerId, int memberId, string body, bool needsApproval, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.SitePostComment SET Body = @5, EditedDate = @6, Status = CASE WHEN @7 = 1 THEN 0 ELSE Status END
                  WHERE Id = @0 AND PostId = @1 AND OwnerType = @2 AND OwnerId = @3 AND AuthorMemberId = @4 AND DeletedDate IS NULL AND CreatedDate <= DATEADD(SECOND, @9, @6) AND CreatedDate >= DATEADD(MINUTE, -@8, @6)",
                id, postId, ownerType, ownerId, memberId, body, now, needsApproval ? 1 : 0, SiteCommentRules.EditMinutes, SiteCommentRules.ClockSkewSeconds) == 1;

        /// <summary>
        /// Raderar mjukt (texten sparas 30 dagar). <paramref name="onlyAuthorId"/> satt = bara den kommentarens egen skribent (kommentatorns egen radering); null = moderator.
        /// Den som raderar sparas (DeletedByMemberId).
        /// </summary>
        public static bool SoftDelete(IDatabase db, int id, int postId, string ownerType, int ownerId, int byMemberId, int? onlyAuthorId, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.SitePostComment SET DeletedDate = @5, DeletedByMemberId = @4
                  WHERE Id = @0 AND PostId = @1 AND OwnerType = @2 AND OwnerId = @3 AND DeletedDate IS NULL AND (@6 IS NULL OR AuthorMemberId = @6)",
                id, postId, ownerType, ownerId, byMemberId, now, onlyAuthorId) == 1;

        /// <summary>Återställer en raderad kommentar (moderator), inom 30 dagar.</summary>
        public static bool Restore(IDatabase db, int id, int postId, string ownerType, int ownerId, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.SitePostComment SET DeletedDate = NULL, DeletedByMemberId = NULL
                  WHERE Id = @0 AND PostId = @1 AND OwnerType = @2 AND OwnerId = @3 AND DeletedDate IS NOT NULL AND DeletedDate >= DATEADD(DAY, -@5, @4)",
                id, postId, ownerType, ownerId, now, SiteCommentRules.RestoreDays) == 1;

        /// <summary>Godkänner en väntande kommentar (moderator).</summary>
        public static bool Approve(IDatabase db, int id, int postId, string ownerType, int ownerId) =>
            db.Execute("UPDATE dbo.SitePostComment SET Status = 1 WHERE Id = @0 AND PostId = @1 AND OwnerType = @2 AND OwnerId = @3 AND Status = 0 AND DeletedDate IS NULL", id, postId, ownerType, ownerId) == 1;

        /// <summary>
        /// Anmäler en kommentar till klubbadmin. Bara en publicerad, ej raderad kommentar som inte är ens egen. Samma person anmäler samma kommentar en gång (unikt index).
        /// </summary>
        public static bool Report(IDatabase db, int commentId, int postId, string ownerType, int ownerId, int reporterId, string? reason, DateTime now)
        {
            try
            {
                return db.Execute(
                    @"INSERT INTO dbo.SitePostCommentReport (CommentId, PostId, OwnerType, OwnerId, ReporterMemberId, Reason, CreatedDate)
                      SELECT c.Id, c.PostId, c.OwnerType, c.OwnerId, @4, @5, @6 FROM dbo.SitePostComment c
                      WHERE c.Id = @0 AND c.PostId = @1 AND c.OwnerType = @2 AND c.OwnerId = @3 AND c.Status = 1 AND c.DeletedDate IS NULL AND (c.AuthorMemberId IS NULL OR c.AuthorMemberId <> @4)",
                    commentId, postId, ownerType, ownerId, reporterId, Blank(reason, 300), now) == 1;
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627) { return false; }
        }

        /// <summary>Markerar alla obehandlade anmälningar på kommentaren som hanterade (kommentaren behålls eller har tagits bort av moderatorn).</summary>
        public static int HandleReports(IDatabase db, int commentId, int postId, string ownerType, int ownerId, int byMemberId, DateTime now) =>
            db.Execute(
                @"UPDATE dbo.SitePostCommentReport SET HandledDate = @5, HandledByMemberId = @4
                  WHERE CommentId = @0 AND PostId = @1 AND OwnerType = @2 AND OwnerId = @3 AND HandledDate IS NULL", commentId, postId, ownerType, ownerId, byMemberId, now);

        /// <summary>Kommentaren (ägarens, nyhetens) eller null.</summary>
        public static SitePostCommentRow? Get(IDatabase db, int id, int postId, string ownerType, int ownerId) =>
            db.FirstOrDefault<SitePostCommentRow>(
                @"SELECT Id, PostId, AuthorMemberId, AuthorName, Body, CreatedDate, EditedDate, Status, DeletedDate, 0 AS OpenReports
                  FROM dbo.SitePostComment WHERE Id = @0 AND PostId = @1 AND OwnerType = @2 AND OwnerId = @3", id, postId, ownerType, ownerId);

        /// <summary>
        /// Kommentarerna på en nyhet, äldst först, sidindelat (50). En moderator ser allt (även väntande och raderade, med text och antal anmälningar); en skribent ser sina egna väntande;
        /// andra ser publicerade — en raderad publicerad kommentar visas som en markering utan text och utan namn.
        /// </summary>
        public static (List<SitePostCommentRow> Rows, int Total) List(IDatabase db, int postId, string ownerType, int ownerId, bool moderator, int viewerMemberId, int offset, int take)
        {
            take = Math.Clamp(take, 1, SiteCommentRules.PageSize);
            offset = Math.Max(0, offset);
            const string where = @"c.PostId = @0 AND c.OwnerType = @1 AND c.OwnerId = @2
                AND (c.Status = 1 OR @3 = 1 OR (c.AuthorMemberId IS NOT NULL AND c.AuthorMemberId = @4 AND c.DeletedDate IS NULL))
                AND (c.DeletedDate IS NULL OR c.Status = 1 OR @3 = 1)";
            var total = db.ExecuteScalar<int>($"SELECT COUNT(*) FROM dbo.SitePostComment c WHERE {where}", postId, ownerType, ownerId, moderator ? 1 : 0, viewerMemberId);
            var rows = db.Fetch<SitePostCommentRow>(
                $@"SELECT c.Id, c.PostId,
                          CASE WHEN c.DeletedDate IS NOT NULL AND @3 = 0 THEN NULL ELSE c.AuthorMemberId END AS AuthorMemberId,
                          CASE WHEN c.DeletedDate IS NOT NULL AND @3 = 0 THEN N'' ELSE c.AuthorName END AS AuthorName,
                          CASE WHEN c.DeletedDate IS NOT NULL AND @3 = 0 THEN N'' ELSE c.Body END AS Body,
                          c.CreatedDate, c.EditedDate, c.Status, c.DeletedDate,
                          CASE WHEN @3 = 1 THEN (SELECT COUNT(*) FROM dbo.SitePostCommentReport r WHERE r.CommentId = c.Id AND r.PostId = c.PostId AND r.OwnerType = c.OwnerType AND r.OwnerId = c.OwnerId AND r.HandledDate IS NULL) ELSE 0 END AS OpenReports
                   FROM dbo.SitePostComment c WHERE {where} ORDER BY c.Id OFFSET @5 ROWS FETCH NEXT @6 ROWS ONLY",
                postId, ownerType, ownerId, moderator ? 1 : 0, viewerMemberId, offset, take);
            return (rows, total);
        }

        /// <summary>Antalen per nyhet i arkivet, i EN gruppfråga. Väntande och anmälda räknas bara när <paramref name="moderator"/>.</summary>
        public static Dictionary<int, SiteCommentCounts> Counts(IDatabase db, string ownerType, int ownerId, bool moderator)
        {
            var rows = db.Fetch<CountRow>(
                @"SELECT x.PostId, SUM(x.Visible) AS Visible, SUM(x.Pending) AS Pending, SUM(x.Reported) AS Reported
                  FROM (SELECT c.PostId,
                               CASE WHEN c.Status = 1 AND c.DeletedDate IS NULL THEN 1 ELSE 0 END AS Visible,
                               CASE WHEN @2 = 1 AND c.Status = 0 AND c.DeletedDate IS NULL THEN 1 ELSE 0 END AS Pending,
                               CASE WHEN @2 = 1 AND c.DeletedDate IS NULL AND EXISTS (SELECT 1 FROM dbo.SitePostCommentReport r WHERE r.CommentId = c.Id AND r.PostId = c.PostId AND r.OwnerType = c.OwnerType AND r.OwnerId = c.OwnerId AND r.HandledDate IS NULL) THEN 1 ELSE 0 END AS Reported
                        FROM dbo.SitePostComment c WHERE c.OwnerType = @0 AND c.OwnerId = @1) x
                  GROUP BY x.PostId", ownerType, ownerId, moderator ? 1 : 0);
            return rows.ToDictionary(r => r.PostId, r => new SiteCommentCounts(r.Visible, r.Pending, r.Reported));
        }

        /// <summary>Nyhetens läge (0 av, 1 på, 2 stängd) och fästningen. Sparas utan att röra Revision (skribentens konfliktkontroll).</summary>
        public static bool SetPostOptions(IDatabase db, int postId, string ownerType, int ownerId, byte commentsMode, bool pinned) =>
            commentsMode <= 2 && db.Execute(
                "UPDATE dbo.SitePost SET CommentsMode = @3, IsPinned = @4 WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND DeletedDate IS NULL",
                postId, ownerType, ownerId, (int)commentsMode, pinned ? 1 : 0) == 1;

        private static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length <= max ? s : s[..max]; }
        private static string? Blank(string? s, int max) { s = (s ?? "").Trim(); return s.Length == 0 ? null : (s.Length <= max ? s : s[..max]); }

        private sealed class SettingsRow { public bool Enabled { get; set; } public byte Moderation { get; set; } public int? ByMemberId { get; set; } public DateTime? EnabledDate { get; set; } }
        private sealed class CountRow { public int PostId { get; set; } public int Visible { get; set; } public int Pending { get; set; } public int Reported { get; set; } }
    }
}
