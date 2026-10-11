using System.Globalization;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Banans kalender (2026-10-11): platsväljaren i tränings- och händelsedialogerna, kalendersidan
    /// <c>/skjutbana/kalender?r=</c>, krockvarningen och förvaltarens externa bokningar.
    ///
    /// <para><b>Läsning:</b> inloggad medlem, som resten av skjutbanedatabasen. <b>Externa
    /// bokningar:</b> banans förvaltare eller sajtadmin (<c>CanManageRangeAsync</c>) — samma krets som
    /// redigerar banans tider.</para>
    /// </summary>
    public class RangeCalendarApiController : SurfaceController
    {
        /// <summary>DataProtection-syftet för prenumerationslänken. En del av signaturen — höj
        /// versionen i stället för att ändra strängen, annars slutar varje utdelad länk fungera.</summary>
        public const string IcsPurpose = "HpskSite.RangeCalendar.Ics.v1";

        /// <summary>Längsta fönster en läsning får omfatta — ett år plus marginal.</summary>
        private const int MaxWindowDays = 400;

        private readonly RangeCalendarService _calendar;
        private readonly ShootingRangeService _ranges;
        private readonly MemberClubService _memberClubs;
        private readonly IMemberService _memberService;
        private readonly IDataProtectionProvider _protection;

        public RangeCalendarApiController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            RangeCalendarService calendar,
            ShootingRangeService ranges,
            MemberClubService memberClubs,
            IMemberService memberService,
            IDataProtectionProvider protection)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _calendar = calendar;
            _ranges = ranges;
            _memberClubs = memberClubs;
            _memberService = memberService;
            _protection = protection;
        }

        /// <summary>Klubbens relation till banan, i den ordning väljaren listar dem. "Huvudman" förekommer
        /// i äldre data och betyder detsamma som ägare.</summary>
        private static readonly string[] RelationOrder =
            { RangeConstants.RelationOwner, "Huvudman", RangeConstants.RelationPrimaryUser, RangeConstants.RelationUser, RangeConstants.RelationTenant };
        /// <summary>Relationerna som gör en bana till klubbens självklara förval.</summary>
        private const int PreferredRank = 2;

        /// <summary>
        /// Klubbens banor för platsväljaren, med vallar och ett FÖRVAL: ägd bana eller huvudbana; annars
        /// banan klubbens träningar oftast ligger på; annars den enda banan. De flesta klubbar har en
        /// eller två banor, och då ska valet redan vara gjort — det är det som gör att kopplingen inte
        /// kostar något klick. <c>clubId</c> 0 (en krets) ger en tom lista; väljaren erbjuder då sökning.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPlaceOptions(int clubId)
        {
            if (await _ranges.GetCurrentMemberIdAsync() == null)
                return Json(new { success = false, message = "Inloggning krävs." });
            if (clubId <= 0) return Json(new { success = true, ranges = Array.Empty<object>(), defaultRangeId = 0 });

            var links = await _ranges.GetClubLinksAsync(clubId);
            var rangesById = (await _ranges.GetByIdsAsync(links.Select(l => l.RangeId))).ToDictionary(r => r.Id);
            var sections = _calendar.SectionsFor(rangesById.Keys);
            var ordered = links
                .Where(l => rangesById.ContainsKey(l.RangeId))
                .GroupBy(l => l.RangeId).Select(g => g.OrderBy(l => Rank(l.RelationType)).First())
                .OrderBy(l => Rank(l.RelationType))
                .ThenBy(l => rangesById[l.RangeId].Name, StringComparer.Create(new CultureInfo("sv-SE"), true))
                .ToList();

            int defaultId = ordered.FirstOrDefault(l => Rank(l.RelationType) <= PreferredRank)?.RangeId ?? 0;
            if (defaultId == 0)
            {
                var used = _calendar.MostUsedRange(clubId);
                defaultId = ordered.Any(l => l.RangeId == used) ? used : (ordered.Count == 1 ? ordered[0].RangeId : 0);
            }

            return Json(new
            {
                success = true,
                defaultRangeId = defaultId,
                ranges = ordered.Select(l =>
                {
                    var r = rangesById[l.RangeId];
                    return new
                    {
                        id = r.Id, name = r.Name, city = r.City, relation = l.RelationType,
                        sections = (sections.GetValueOrDefault(r.Id) ?? new()).Select(s => new { id = s.Id, label = s.Label })
                    };
                })
            });

            static int Rank(string? relation)
            {
                var i = Array.IndexOf(RelationOrder, relation);
                return i < 0 ? RelationOrder.Length : i;
            }
        }

        /// <summary>En banas namn och vallar — för en bana utanför klubbens lista (sökning, lagrat val).</summary>
        [HttpGet]
        public async Task<IActionResult> GetRangeInfo(int rangeId)
        {
            if (await _ranges.GetCurrentMemberIdAsync() == null)
                return Json(new { success = false, message = "Inloggning krävs." });
            var (range, sections) = _calendar.GetRangeWithSections(rangeId);
            if (range == null) return Json(new { success = false, message = "Skjutbanan finns inte." });
            return Json(new
            {
                success = true,
                range = new { id = range.Id, name = range.Name, city = range.City },
                sections = sections.Select(s => new { id = s.Id, label = s.Label })
            });
        }

        /// <summary>Banans kalender för [from, to]. Inloggad medlem.</summary>
        [HttpGet]
        public async Task<IActionResult> GetCalendar(int rangeId, string? from, string? to)
        {
            var me = await _ranges.GetCurrentMemberIdAsync();
            if (me == null) return Json(new { success = false, message = "Inloggning krävs." });
            var (range, sections) = _calendar.GetRangeWithSections(rangeId);
            if (range == null) return Json(new { success = false, message = "Skjutbanan finns inte." });

            var f = ParseDay(from) ?? DateTime.Today.AddDays(-7);
            var t = ParseDay(to) ?? f.AddMonths(2);
            if (t < f) return Json(new { success = false, message = "Perioden slutar före den börjar." });
            if ((t - f).TotalDays > MaxWindowDays) t = f.AddDays(MaxWindowDays);

            var member = _memberService.GetById(me.Value);
            var myClubs = _memberClubs.GetAllClubIds(member);
            var canManage = await _ranges.CanManageRangeAsync(rangeId, me);
            // Banans andra klubbar och medlemmens egna banor — för växlingen överst på sidan.
            var clubsOnRange = (await _ranges.GetLinksAsync(rangeId)).Select(l => l.ClubId).Distinct().ToList();
            var myRanges = new List<object>();
            var seen = new HashSet<int>();
            foreach (var cid in myClubs)
                foreach (var r in await _ranges.GetRangesForClubAsync(cid))
                    if (seen.Add(r.Id))
                        myRanges.Add(new { id = r.Id, name = r.Name });

            return Json(new
            {
                success = true,
                range = new { id = range.Id, name = range.Name, city = range.City, municipality = range.Municipality },
                sections = sections.Select(s => new { id = s.Id, label = s.Label }),
                from = f.ToString("yyyy-MM-dd"), to = t.ToString("yyyy-MM-dd"),
                items = _calendar.Build(rangeId, f, t),
                canManage,
                myClubIds = myClubs,
                clubsOnRange,
                myRanges
            });
        }

        /// <summary>
        /// Allt annat på banan samma dag — underlaget för krockvarningen i dialogerna. Läser bara.
        /// <paramref name="excludeKind"/>/<paramref name="excludeId"/> = posten som redigeras.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDay(int rangeId, int? sectionId, string? date, string? start, string? end,
            string? excludeKind, int excludeId = 0, int clubId = 0)
        {
            if (await _ranges.GetCurrentMemberIdAsync() == null)
                return Json(new { success = false, message = "Inloggning krävs." });
            var day = ParseDay(date);
            if (rangeId <= 0 || day == null) return Json(new { success = true, items = Array.Empty<object>() });
            var items = _calendar.Day(rangeId, sectionId, day.Value, Clean(start), Clean(end), excludeKind, excludeId, clubId)
                .Select(d => new
                {
                    kind = d.Item.Kind, title = d.Item.Title, ownerName = d.Item.OwnerName, clubId = d.Item.ClubId,
                    start = d.Item.Start, end = d.Item.End, sectionLabel = d.Item.SectionLabel,
                    relation = d.Relation, ownClub = d.OwnClub
                });
            return Json(new { success = true, items });
        }

        public class SaveExternalRequest
        {
            public int Id { get; set; }
            public int RangeId { get; set; }
            public int? RangeSectionId { get; set; }
            public string? Date { get; set; }
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            public string? Title { get; set; }
            public string? Note { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveExternal([FromBody] SaveExternalRequest req)
        {
            var me = await _ranges.GetCurrentMemberIdAsync();
            if (me == null || req == null) return Json(new { success = false, message = "Inloggning krävs." });
            if (!await _ranges.CanManageRangeAsync(req.RangeId, me))
                return Json(new { success = false, message = "Bara banans förvaltare kan lägga in bokningar för andra." });
            var (id, error) = _calendar.SaveExternal(req.Id, req.RangeId, req.RangeSectionId, req.Date,
                req.StartTime, req.EndTime, req.Title, req.Note, me.Value);
            return Json(error == null ? new { success = true, id, message = (string?)null } : new { success = false, id = 0, message = (string?)error });
        }

        public class DeleteExternalRequest
        {
            public int Id { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteExternal([FromBody] DeleteExternalRequest req)
        {
            var me = await _ranges.GetCurrentMemberIdAsync();
            if (me == null || req == null) return Json(new { success = false, message = "Inloggning krävs." });
            // Behörigheten prövas mot BOKNINGENS bana, aldrig mot en bana klienten anger.
            var rangeId = _calendar.RangeOfExternal(req.Id);
            if (rangeId == null) return Json(new { success = false, message = "Bokningen finns inte." });
            if (!await _ranges.CanManageRangeAsync(rangeId.Value, me))
                return Json(new { success = false, message = "Bara banans förvaltare kan ta bort bokningen." });
            var error = _calendar.DeleteExternal(req.Id, rangeId.Value);
            return Json(new { success = error == null, message = error });
        }

        /// <summary>
        /// Prenumerationslänken: en signerad, icke gissningsbar adress per bana. En kalenderapp kan inte
        /// logga in, så länken ÄR behörigheten — den ges bara till inloggade medlemmar.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetIcsLink(int rangeId)
        {
            if (await _ranges.GetCurrentMemberIdAsync() == null)
                return Json(new { success = false, message = "Inloggning krävs." });
            if (_calendar.GetRangeWithSections(rangeId).Range == null)
                return Json(new { success = false, message = "Skjutbanan finns inte." });
            var token = _protection.CreateProtector(IcsPurpose).Protect("range:" + rangeId);
            var url = $"{Request.Scheme}://{Request.Host}/skjutbana/kalender.ics?t={Uri.EscapeDataString(token)}";
            return Json(new { success = true, url, webcal = "webcal://" + url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..] });
        }

        private static DateTime? ParseDay(string? s)
            => DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.Date : null;

        private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
