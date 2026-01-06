using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using System.Text.Json;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Controller for video tutorial system
    /// Manages tutorial viewing, watched status tracking, and banner dismissals
    /// </summary>
    public class TutorialController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;

        public TutorialController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
        }

        #region Public Endpoints

        /// <summary>
        /// Get all available tutorials
        /// GET /umbraco/surface/Tutorial/GetAll
        /// </summary>
        [HttpGet]
        public IActionResult GetAll()
        {
            try
            {
                // Find the tutorialPage (should be under Home)
                var allContent = _contentService.GetRootContent();
                var homeNode = allContent.FirstOrDefault();

                if (homeNode == null)
                {
                    return Json(new { success = false, message = "Home node not found" });
                }

                // Find tutorialPage under Home
                var tutorialsRoot = _contentService.GetPagedChildren(homeNode.Id, 0, 100, out _)
                    .FirstOrDefault(x => x.ContentType.Alias == "tutorialPage");

                if (tutorialsRoot == null)
                {
                    return Json(new { success = false, message = "Tutorials page not found. Make sure you have created a tutorialPage under Home." });
                }

                // Get all tutorial children
                var tutorials = _contentService.GetPagedChildren(tutorialsRoot.Id, 0, 100, out _)
                    .Where(x => x.ContentType.Alias == "tutorial" && x.Published)
                    .Select(t => new
                    {
                        tutorialId = t.GetValue<string>("tutorialId"),
                        tutorialTitle = t.GetValue<string>("tutorialTitle"),
                        youtubeId = t.GetValue<string>("youtubeId"),
                        tutorialDescription = t.GetValue<string>("tutorialDescription"),
                        duration = t.GetValue<string>("duration"),
                        targetRole = t.GetValue<string>("targetRole")
                    })
                    .ToList();

                return Json(new { success = true, tutorials });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error loading tutorials: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Mark a tutorial as watched for the current member
        /// POST /umbraco/surface/Tutorial/MarkAsWatched
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> MarkAsWatched([FromBody] MarkAsWatchedRequest request)
        {
            try
            {
                var member = await _memberManager.GetCurrentMemberAsync();
                if (member == null)
                {
                    return Json(new { success = false, message = "Not authenticated" });
                }

                var umbracoMember = _memberService.GetById(member.Key);
                if (umbracoMember == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get current watched list
                var watchedJson = umbracoMember.GetValue<string>("watchedTutorials") ?? "[]";
                var watched = JsonSerializer.Deserialize<List<TutorialWatch>>(watchedJson) ?? new List<TutorialWatch>();

                // Add or update
                var existing = watched.FirstOrDefault(w => w.Id == request.TutorialId);
                if (existing != null)
                {
                    existing.ViewCount++;
                    existing.Timestamp = DateTime.UtcNow;
                }
                else
                {
                    watched.Add(new TutorialWatch
                    {
                        Id = request.TutorialId,
                        Watched = true,
                        Timestamp = DateTime.UtcNow,
                        ViewCount = 1
                    });
                }

                // Save back to member
                umbracoMember.SetValue("watchedTutorials", JsonSerializer.Serialize(watched));
                _memberService.Save(umbracoMember);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error marking tutorial as watched: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Get watched tutorials for current member
        /// GET /umbraco/surface/Tutorial/GetWatchedTutorials
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetWatchedTutorials()
        {
            try
            {
                var member = await _memberManager.GetCurrentMemberAsync();
                if (member == null)
                {
                    return Json(new { success = false, watched = new List<string>() });
                }

                var umbracoMember = _memberService.GetById(member.Key);
                var watchedJson = umbracoMember?.GetValue<string>("watchedTutorials") ?? "[]";
                var watched = JsonSerializer.Deserialize<List<TutorialWatch>>(watchedJson) ?? new List<TutorialWatch>();

                return Json(new { success = true, watched });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error getting watched tutorials: " + ex.Message,
                    watched = new List<string>()
                });
            }
        }

        /// <summary>
        /// Mark welcome tutorial as seen
        /// POST /umbraco/surface/Tutorial/MarkWelcomeSeen
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> MarkWelcomeSeen()
        {
            try
            {
                var member = await _memberManager.GetCurrentMemberAsync();
                if (member == null)
                {
                    return Json(new { success = false });
                }

                var umbracoMember = _memberService.GetById(member.Key);
                if (umbracoMember == null)
                {
                    return Json(new { success = false });
                }

                umbracoMember.SetValue("firstLoginWelcomeShown", true);
                _memberService.Save(umbracoMember);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error marking welcome as seen: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Dismiss a tutorial banner
        /// POST /umbraco/surface/Tutorial/DismissBanner
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> DismissBanner([FromBody] DismissBannerRequest request)
        {
            try
            {
                var member = await _memberManager.GetCurrentMemberAsync();
                if (member == null)
                {
                    // Not logged in - client will handle with localStorage
                    return Json(new { success = true });
                }

                var umbracoMember = _memberService.GetById(member.Key);
                if (umbracoMember == null)
                {
                    return Json(new { success = false });
                }

                // Get current dismissed banners list
                var dismissedJson = umbracoMember.GetValue<string>("dismissedTutorialBanners") ?? "[]";
                var dismissed = JsonSerializer.Deserialize<List<string>>(dismissedJson) ?? new List<string>();

                // Add banner ID if not already dismissed
                if (!dismissed.Contains(request.BannerId))
                {
                    dismissed.Add(request.BannerId);
                }

                // Save back to member
                umbracoMember.SetValue("dismissedTutorialBanners", JsonSerializer.Serialize(dismissed));
                _memberService.Save(umbracoMember);

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error dismissing banner: " + ex.Message
                });
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Tutorial watch tracking model
        /// </summary>
        public class TutorialWatch
        {
            public string Id { get; set; } = string.Empty;
            public bool Watched { get; set; }
            public DateTime Timestamp { get; set; }
            public int ViewCount { get; set; }
        }

        /// <summary>
        /// Request model for marking tutorial as watched
        /// </summary>
        public class MarkAsWatchedRequest
        {
            public string TutorialId { get; set; } = string.Empty;
        }

        /// <summary>
        /// Request model for dismissing banner
        /// </summary>
        public class DismissBannerRequest
        {
            public string BannerId { get; set; } = string.Empty;
        }

        #endregion
    }
}
