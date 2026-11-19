using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>En "ta bort min bild"-förfrågan som visas för skribenterna (namnet slås upp här, för högst femtio rader — aldrig lagrat på förfrågan).</summary>
    public sealed record SiteNewsImageRequestView(int Id, int FileId, string RequesterName, string? Message, DateTime CreatedDate);

    /// <summary>Det en nyhets sida behöver förutom texten: galleriet, reaktionerna, vad tittaren får göra och (för skribenter) förfrågningarna. Partialvyerna tar bara den här modellen.</summary>
    public sealed class SiteNewsPostSocial
    {
        public int PostId { get; init; }
        public string OwnerType { get; init; } = "";
        public int OwnerId { get; init; }
        public string Title { get; init; } = "";
        public IReadOnlyList<SitePostImageRow> Gallery { get; init; } = Array.Empty<SitePostImageRow>();
        public SitePostReactionSummary Reactions { get; init; } = new();
        /// <summary>Tittarens egen reaktion (nyckeln), eller null.</summary>
        public string? Mine { get; init; }
        /// <summary>Får tittaren reagera (inloggad medlem i ägaren, styrelsen, admin) — <see cref="SitePostReactionRules.CanReact"/>.</summary>
        public bool CanReact { get; init; }
        public bool LoggedIn { get; init; }
        public bool CanWrite { get; init; }
        public IReadOnlyList<SiteNewsImageRequestView> Requests { get; init; } = Array.Empty<SiteNewsImageRequestView>();
    }

    /// <summary>
    /// Bygger <see cref="SiteNewsPostSocial"/> för en nyhet tittaren redan har rätt att läsa (sidan har prövat det: <see cref="SitePosts.GetReadable"/> ger inget annars).
    /// EN gruppfråga för antalen och EN för tittarens egen reaktion — ingen uppslagning per reaktion. Namn på dem som reagerat läses aldrig ut (bara antal).
    /// </summary>
    public sealed class SiteNewsSocialFactory
    {
        private readonly IMemberManager _members;
        private readonly IMemberService _memberService;
        private readonly IScopeProvider _scopes;

        public SiteNewsSocialFactory(IMemberManager members, IMemberService memberService, IScopeProvider scopes) { _members = members; _memberService = memberService; _scopes = scopes; }

        public async Task<SiteNewsPostSocial> BuildAsync(SitePostRow post, SiteViewer viewer, bool canWrite)
        {
            // Medlemsuppslaget (async) FÖRE databasscopet: inget await inne i ett scope.
            var memberId = 0;
            if (viewer.LoggedIn)
            {
                var cur = await _members.GetCurrentMemberAsync();
                memberId = cur?.Email == null ? 0 : _memberService.GetByEmail(cur.Email)?.Id ?? 0;
            }
            using var scope = _scopes.CreateScope(autoComplete: true);
            var db = scope.Database;
            var gallery = SitePosts.GalleryFor(db, post);
            var summary = post.IsPublished
                ? SitePostReactions.Summaries(db, post.OwnerType, post.OwnerId, new[] { post.Id }).GetValueOrDefault(post.Id) ?? new SitePostReactionSummary()
                : new SitePostReactionSummary();
            var requests = new List<SiteNewsImageRequestView>();
            if (canWrite)
                foreach (var r in SitePosts.OpenImageRequests(db, post.Id, post.OwnerType, post.OwnerId))
                {
                    var who = r.RequesterMemberId is int id ? _memberService.GetById(id)?.Name : null;
                    requests.Add(new SiteNewsImageRequestView(r.Id, r.FileId, string.IsNullOrWhiteSpace(who) ? "En medlem" : who!, r.Message, r.CreatedDate));
                }
            return new SiteNewsPostSocial
            {
                PostId = post.Id, OwnerType = post.OwnerType, OwnerId = post.OwnerId, Title = post.Title,
                Gallery = gallery, Reactions = summary,
                Mine = post.IsPublished ? SitePostReactions.Mine(db, post.Id, post.OwnerType, post.OwnerId, memberId) : null,
                // Ett utkast går inte att reagera på (skriv-satsen kräver publicerad), så knapparna visas inte där.
                CanReact = post.IsPublished && SitePostReactionRules.CanReact(viewer),
                LoggedIn = viewer.LoggedIn, CanWrite = canWrite, Requests = requests,
            };
        }
    }
}
