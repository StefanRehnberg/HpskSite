using HpskSite.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>En kommentar som vyn ritar. Allt tittaren får göra är redan avgjort här (vyn avgör ingenting om behörighet).</summary>
    public sealed class SiteCommentView
    {
        public int Id { get; init; }
        public string DisplayName { get; init; } = "";
        public string Initials { get; init; } = "";
        public string Body { get; init; } = "";
        public DateTime CreatedDate { get; init; }
        public DateTime? EditedDate { get; init; }
        public bool IsPending { get; init; }
        public bool IsDeleted { get; init; }
        public bool IsMine { get; init; }
        public bool CanEdit { get; init; }
        public bool CanDelete { get; init; }
        public bool CanRestore { get; init; }
        public bool CanApprove { get; init; }
        public bool CanReport { get; init; }
        public int OpenReports { get; init; }
    }

    /// <summary>Det kommentarsavsnittet på en nyhets sida behöver. Partialvyn tar bara den här modellen.</summary>
    public sealed class SiteNewsCommentsModel
    {
        public int PostId { get; init; }
        public string OwnerType { get; init; } = "";
        public int OwnerId { get; init; }
        public string PostUrl { get; init; } = "";
        /// <summary>Nyhetens läge: 1 = på, 2 = stängd för nya men läsbar.</summary>
        public byte Mode { get; init; }
        public bool LoggedIn { get; init; }
        public bool CanComment { get; init; }
        public bool CanReadText { get; init; }
        public bool IsModerator { get; init; }
        /// <summary>Förgranskas tittarens kommentarer (visas som en upplysning under skrivrutan)?</summary>
        public bool WillBePreModerated { get; init; }
        public int VisibleCount { get; init; }
        public int Total { get; init; }
        public int Page { get; init; } = 1;
        public int PageCount { get; init; } = 1;
        public IReadOnlyList<SiteCommentView> Comments { get; init; } = Array.Empty<SiteCommentView>();
    }

    /// <summary>
    /// Bygger <see cref="SiteNewsCommentsModel"/> för en nyhet tittaren redan har rätt att läsa. Texten läses bara ut för samma krets som får reagera (<see cref="SiteCommentRules.CanReadText"/>);
    /// andra får bara antalet. Namnet är en ögonblicksbild på raden (ingen uppslagning per kommentar); en skribent som inte längre är aktiv medlem visas som "Tidigare medlem" —
    /// utträde anonymiserar vid visning utan att data ändras. EN fråga för kommentarerna, EN för antalet och EN för klubbens aktiva medlemmar. Returnerar null när avsnittet inte ska visas
    /// (kommentarer av för föreningen eller nyheten, en krets nyhet, ett utkast).
    /// </summary>
    public sealed class SiteNewsCommentsFactory
    {
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;
        private readonly IScopeProvider _scopes;
        private readonly DpaAcceptanceService _dpa;
        private readonly ClubRosterService _roster;

        public SiteNewsCommentsFactory(IMemberManager members, IMemberService memberService, IScopeProvider scopes, DpaAcceptanceService dpa, ClubRosterService roster)
        {
            _members = members; _memberService = memberService; _scopes = scopes; _dpa = dpa; _roster = roster;
        }

        public async Task<SiteNewsCommentsModel?> BuildAsync(SitePostRow post, SiteViewer viewer, string postUrl, int page)
        {
            if (post.OwnerType != SiteTemplates.Club || !post.IsPublished || post.CommentsMode == 0) return null;
            // Allt async FÖRE databasscopet.
            var memberId = 0; var birthYear = 0;
            if (viewer.LoggedIn)
            {
                var cur = await _members.GetCurrentMemberAsync();
                var m = cur?.Email == null ? null : _memberService.GetByEmail(cur.Email);
                memberId = m?.Id ?? 0;
                birthYear = Marken.BirthYearFromPersonNumber(m?.GetValue("personNumber")?.ToString(), DateTime.Now.Year);
            }
            var dpaOk = (await _dpa.GetStatusForClubAsync(post.OwnerId)).Accepted;

            using var scope = _scopes.CreateScope(autoComplete: true);
            var db = scope.Database;
            var settings = SitePostComments.Settings(db, post.OwnerType, post.OwnerId);
            if (!settings.Enabled) return null;

            var canRead = SiteCommentRules.CanReadText(viewer) && memberId > 0;
            var moderator = canRead && SiteCommentRules.CanModerate(viewer, post.AuthorMemberId == memberId);
            var canComment = canRead && SiteCommentRules.CanComment(viewer, post.OwnerType, post.CommentsMode, settings.Enabled, dpaOk);
            var visible = SitePostComments.Counts(db, post.OwnerType, post.OwnerId, moderator: false).GetValueOrDefault(post.Id)?.Visible ?? 0;

            var views = new List<SiteCommentView>();
            var total = 0; var pageCount = 1; page = Math.Max(1, page);
            if (canRead)
            {
                total = SitePostComments.List(db, post.Id, post.OwnerType, post.OwnerId, moderator, memberId, 0, 1).Total;   // totalen först, så att sidan kan klämmas till den sista
                pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)SiteCommentRules.PageSize));
                page = Math.Min(page, pageCount);
                var (rows, _) = SitePostComments.List(db, post.Id, post.OwnerType, post.OwnerId, moderator, memberId, (page - 1) * SiteCommentRules.PageSize, SiteCommentRules.PageSize);
                var left = _roster.LeftMemberIds(post.OwnerId, rows.Where(r => r.AuthorMemberId != null).Select(r => r.AuthorMemberId!.Value).ToList());
                var now = DateTime.Now;
                foreach (var r in rows)
                {
                    var own = memberId > 0 && r.AuthorMemberId == memberId;
                    // Ett utträde anonymiserar vid visning (namnet står kvar i databasen, texten rörs inte); en GDPR-radering har redan nollat AuthorMemberId och bytt namnet.
                    var name = r.IsDeleted && !moderator ? "" : (r.AuthorMemberId is not int aid || left.Contains(aid) || string.IsNullOrWhiteSpace(r.AuthorName) ? "Tidigare medlem" : r.AuthorName);
                    views.Add(new SiteCommentView
                    {
                        Id = r.Id, DisplayName = name, Initials = InitialsOf(name), Body = r.Body, CreatedDate = r.CreatedDate, EditedDate = r.EditedDate,
                        IsPending = r.IsPending, IsDeleted = r.IsDeleted, IsMine = own,
                        CanEdit = !r.IsDeleted && SiteCommentRules.CanEdit(own, r.CreatedDate, now),
                        CanDelete = !r.IsDeleted && (own || moderator),
                        CanRestore = r.IsDeleted && moderator && SiteCommentRules.CanRestore(r.DeletedDate!.Value, now),
                        CanApprove = moderator && r.IsPending && !r.IsDeleted,
                        CanReport = !r.IsDeleted && !r.IsPending && !own,
                        OpenReports = moderator ? r.OpenReports : 0,
                    });
                }
            }
            return new SiteNewsCommentsModel
            {
                PostId = post.Id, OwnerType = post.OwnerType, OwnerId = post.OwnerId, PostUrl = postUrl, Mode = post.CommentsMode,
                LoggedIn = viewer.LoggedIn, CanComment = canComment, CanReadText = canRead, IsModerator = moderator,
                WillBePreModerated = canComment && SiteCommentRules.NeedsApproval(settings.Moderation, birthYear, DateTime.Now.Year, moderator),
                VisibleCount = visible, Total = total, Page = page, PageCount = pageCount, Comments = views,
            };
        }

        /// <summary>Initialerna (högst två) ur ett namn; "?" för ett tomt namn. Ren funktion.</summary>
        public static string InitialsOf(string? name)
        {
            var parts = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return "?";
            var s = parts.Length == 1 ? parts[0][..1] : parts[0][..1] + parts[^1][..1];
            return s.ToUpperInvariant();
        }
    }
}
