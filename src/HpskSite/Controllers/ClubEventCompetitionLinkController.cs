using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services.Events;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kopplingen mellan en händelse och de tävlingar den beskriver (<see cref="CompetitionEventLinkService"/>).
    /// Den som får redigera händelsen får koppla den — behörigheten går via ägaren
    /// (<see cref="EventOwnerResolver"/>), samma regel som för händelsens övriga redigering.
    /// </summary>
    public class ClubEventCompetitionLinkController : SurfaceController
    {
        private readonly CompetitionEventLinkService _links;
        private readonly IClubEventStore _events;
        private readonly EventOwnerResolver _owners;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public ClubEventCompetitionLinkController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CompetitionEventLinkService links, IClubEventStore events, EventOwnerResolver owners,
            IMemberManager memberManager, IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _links = links;
            _events = events;
            _owners = owners;
            _memberManager = memberManager;
            _memberService = memberService;
        }

        private async Task<(HpskSite.Models.Events.ClubEventRecord? Event, string? Error)> EventForAdminAsync(int eventId)
        {
            var ev = eventId > 0 ? _events.Get(eventId) : null;
            if (ev == null) return (null, "Händelsen hittades inte.");
            if (!await _owners.CanManageAsync(_owners.ForEventRecord(ev))) return (null, "Du har inte behörighet att ändra händelsen.");
            return (ev, null);
        }

        /// <summary>Tävlingarna händelsen kan kopplas till, och vilka som är kopplade.</summary>
        [HttpGet]
        public async Task<IActionResult> GetLinks(int eventId)
        {
            var (ev, error) = await EventForAdminAsync(eventId);
            if (ev == null) return Json(new { success = false, message = error });
            var candidates = _links.Candidates(ev).Select(c => new
            {
                id = c.Id,
                name = c.Name,
                date = c.Date?.ToString("yyyy-MM-dd"),
                host = c.HostName,
                isClubOnly = c.IsClubOnly,
                linkedHere = c.LinkedHere,
                otherEventName = c.OtherEventName
            });
            return Json(new { success = true, ownerType = ev.OwnerType, candidates });
        }

        /// <summary>Ersätter händelsens kopplingar. <paramref name="competitionIds"/> är kommaseparerade id:n.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLinks(int eventId, string? competitionIds)
        {
            var (ev, error) = await EventForAdminAsync(eventId);
            if (ev == null) return Json(new { success = false, message = error });
            var ids = (competitionIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : 0)
                .Where(id => id > 0)
                .ToList();
            var current = await _memberManager.GetCurrentMemberAsync();
            int? actor = current == null ? null : _memberService.GetByKey(current.Key)?.Id;
            var (ok, message) = _links.SetForEvent(ev, ids, actor);
            return Json(new { success = ok, message, linked = ok ? ids.Count : 0 });
        }
    }
}
