using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.CompetitionTypes.Springskytte.Controllers;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.Helpers;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Standalone Springskytte start-list page.
    ///
    /// Replaces the fragile name-derived Umbraco child-node URL (which 404'd for nested /
    /// club-hosted competitions — e.g. /competitions/.../seniorer/) with a stable routed URL:
    ///   /startlista/{competitionId}          → hub listing every start list for the competition
    ///   /startlista/{competitionId}/{slug}   → one shareable / printable list
    /// Same as /station, /live, /patrullista — a routed MVC controller, no content node.
    ///
    /// Not login-gated server-side (matches the old PrecisionStartList.cshtml behaviour, which
    /// only highlighted the logged-in member); the competition-page button keeps its login nudge.
    /// </summary>
    [Route("startlista/{competitionId:int}")]
    public class SpringskytteStartListPageController : Controller
    {
        private readonly IContentService _contentService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;

        public SpringskytteStartListPageController(
            IContentService contentService,
            IMemberManager memberManager,
            IMemberService memberService,
            ClubService clubService)
        {
            _contentService = contentService;
            _memberManager = memberManager;
            _memberService = memberService;
            _clubService = clubService;
        }

        [HttpGet("")]
        public Task<IActionResult> Index(int competitionId) => Render(competitionId, null);

        [HttpGet("{slug}")]
        public Task<IActionResult> Single(int competitionId, string slug) => Render(competitionId, slug);

        private async Task<IActionResult> Render(int competitionId, string? slug)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null || competition.ContentType.Alias != "competition") return NotFound();
            if ((competition.GetValue<string>("competitionType") ?? "") != "Springskytte") return NotFound();

            var (name, club) = await GetViewerAsync();

            var lists = LoadLists(competition.Id);

            if (!string.IsNullOrEmpty(slug)
                && !lists.Any(l => string.Equals(l.Slug, slug, StringComparison.OrdinalIgnoreCase)))
            {
                return NotFound();
            }

            var model = new SpringskytteStartListPageModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.GetValue<string>("competitionName")
                                  ?? competition.Name ?? "Tävling",
                Lists = lists,
                SelectedSlug = slug,
                CurrentMemberName = name,
                CurrentMemberClub = club
            };

            return View("~/Views/SpringskytteStartList.cshtml", model);
        }

        private List<SpringskytteStartListView> LoadLists(int competitionId)
        {
            var children = _contentService
                .GetPagedChildren(competitionId, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "precisionStartList")
                .ToList();

            var lists = new List<SpringskytteStartListView>();
            var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in children)
            {
                var json = node.GetValue<string>("configurationData");
                if (string.IsNullOrEmpty(json)) continue;

                var isOfficial = node.HasProperty("isOfficialStartList") && node.GetValue<bool>("isOfficialStartList");
                var generated = node.HasProperty("generatedDate") ? node.GetValue<DateTime>("generatedDate") : node.UpdateDate;

                // Stafett (relay) lists carry Teams, not Starters, and are tagged teamFormat.
                if (SpringskytteController.IsStafettConfig(json))
                {
                    SpringskytteStafettStartListConfig? scfg = null;
                    try { scfg = JsonConvert.DeserializeObject<SpringskytteStafettStartListConfig>(json); }
                    catch { }
                    if (scfg?.Teams == null || scfg.Teams.Count == 0) continue;

                    var sName = !string.IsNullOrWhiteSpace(scfg.ListName) ? scfg.ListName
                              : (!string.IsNullOrWhiteSpace(node.Name) ? node.Name : "Stafett");
                    var sBase = SlugHelper.Slugify(sName);
                    if (string.IsNullOrEmpty(sBase)) sBase = "lista-" + node.Id;
                    var sSlug = sBase; var sn = 2;
                    while (!usedSlugs.Add(sSlug)) sSlug = $"{sBase}-{sn++}";

                    lists.Add(new SpringskytteStartListView
                    {
                        NodeId = node.Id,
                        ListName = sName,
                        Slug = sSlug,
                        IsOfficial = isOfficial,
                        GeneratedDate = generated,
                        IsStafett = true,
                        StafettConfig = scfg
                    });
                    continue;
                }

                SpringskytteStartListConfig? cfg = null;
                try { cfg = JsonConvert.DeserializeObject<SpringskytteStartListConfig>(json); }
                catch { }
                // Only Springskytte (individual-start) lists have Starters; skip Precision team configs.
                if (cfg?.Starters == null || cfg.Starters.Count == 0) continue;

                var listName = !string.IsNullOrWhiteSpace(cfg.ListName) ? cfg.ListName
                             : (!string.IsNullOrWhiteSpace(node.Name) ? node.Name : "Startlista");

                var baseSlug = SlugHelper.Slugify(listName);
                if (string.IsNullOrEmpty(baseSlug)) baseSlug = "lista-" + node.Id;
                var uniqueSlug = baseSlug;
                var n = 2;
                while (!usedSlugs.Add(uniqueSlug)) uniqueSlug = $"{baseSlug}-{n++}";

                lists.Add(new SpringskytteStartListView
                {
                    NodeId = node.Id,
                    ListName = listName,
                    Slug = uniqueSlug,
                    IsOfficial = isOfficial,
                    GeneratedDate = generated,
                    Config = cfg
                });
            }

            // Stable, human order: earliest first start first, then name.
            return lists
                .OrderBy(l => (l.IsStafett
                    ? l.StafettConfig?.Teams?.FirstOrDefault()?.StartTime
                    : l.Config.Starters.FirstOrDefault()?.StartTime) ?? "")
                .ThenBy(l => l.ListName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<(string name, string club)> GetViewerAsync()
        {
            var member = await _memberManager.GetCurrentMemberAsync();
            if (member == null) return ("", "");

            var club = "";
            var data = string.IsNullOrEmpty(member.Email) ? null : _memberService.GetByEmail(member.Email);
            if (data != null)
            {
                var pc = data.GetValue<string>("primaryClubId");
                if (!string.IsNullOrEmpty(pc) && int.TryParse(pc, out var cid))
                    club = _clubService.GetClubNameById(cid) ?? "";
            }
            return (member.Name ?? "", club);
        }
    }
}
