using System.Globalization;
using HpskSite.Models;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Builds the cached, login-independent data for the "Det här händer" feed: upcoming competitions
    /// + club events (Umbraco content) and ongoing/upcoming träningsmatcher (SQL), bucketed into rolling
    /// windows and tagged with region + masking metadata. One content traversal + one DB query per build;
    /// cached for a few minutes so the public home page stays cheap. Masking/region filtering is applied
    /// later, per-request, in the view — this layer never decides who sees what.
    /// </summary>
    public class WhatsHappeningService
    {
        private readonly IUmbracoContextFactory _umbracoContextFactory;
        private readonly IScopeProvider _scopeProvider;
        private readonly AppCaches _appCaches;

        private const string CacheKey = "WhatsHappening:Data:v1";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private const int HorizonDays = 31;

        public WhatsHappeningService(
            IUmbracoContextFactory umbracoContextFactory,
            IScopeProvider scopeProvider,
            AppCaches appCaches)
        {
            _umbracoContextFactory = umbracoContextFactory;
            _scopeProvider = scopeProvider;
            _appCaches = appCaches;
        }

        public WhatsHappeningData GetData()
            => _appCaches.RuntimeCache.GetCacheItem(CacheKey, Build, CacheTtl) ?? new WhatsHappeningData();

        /// <summary>Drop the cached aggregate so the next request rebuilds it (used after seeding/edits).</summary>
        public void ClearCache() => _appCaches.RuntimeCache.ClearByKey(CacheKey);

        private WhatsHappeningData Build()
        {
            var data = new WhatsHappeningData { GeneratedAt = DateTime.Now };
            var today = DateTime.Today;
            var horizon = today.AddDays(HorizonDays);

            // clubId -> (display name, region code); reused for competitions, club events and matches.
            var clubInfo = new Dictionary<int, (string Name, string Region)>();
            var regionByCode = new Dictionary<string, WhatsHappeningRegion>(StringComparer.OrdinalIgnoreCase);
            string tmBase = "/traningsmatch/";

            // ---------- one content traversal ----------
            using (var cref = _umbracoContextFactory.EnsureUmbracoContext())
            {
                var content = cref.UmbracoContext.Content;
                if (content == null) return data;

                var all = content.GetAtRoot().SelectMany(r => r.DescendantsOrSelf()).ToList();

                foreach (var node in all)
                {
                    switch (node.ContentType.Alias)
                    {
                        case "club":
                            clubInfo[node.Id] = (
                                node.Value<string>("clubName") ?? node.Name ?? "",
                                node.Value<string>("regionalFederation") ?? "");
                            break;
                        case "regionalPage":
                            var code = node.Value<string>("regionCode") ?? "";
                            if (!string.IsNullOrWhiteSpace(code))
                                regionByCode[code] = new WhatsHappeningRegion
                                {
                                    Code = code,
                                    Name = node.Value<string>("regionName") ?? node.Name ?? code,
                                    Url = node.Url()
                                };
                            break;
                    }
                }

                var tmNode = all.FirstOrDefault(n =>
                    n.UrlSegment != null && n.UrlSegment.Contains("traningsmatch", StringComparison.OrdinalIgnoreCase));
                if (tmNode != null) tmBase = tmNode.Url();

                // club counts per region
                foreach (var ci in clubInfo.Values)
                    if (!string.IsNullOrWhiteSpace(ci.Region) && regionByCode.TryGetValue(ci.Region, out var r))
                        r.ClubCount++;

                string RegionName(string regionCode) =>
                    regionByCode.TryGetValue(regionCode, out var r) ? r.Name : regionCode;

                // competitions
                foreach (var node in all)
                {
                    if (node.ContentType.Alias != "competition") continue;
                    if (!node.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true)) continue;

                    var date = node.Value<DateTime?>("competitionDate");
                    if (date == null || date.Value.Date < today || date.Value.Date > horizon) continue;

                    var clubId = node.Value<int>("clubId");
                    var isClubOnly = node.Value<bool>("isClubOnly");
                    var regionCode = "";
                    var clubName = "";
                    if (clubId > 0 && clubInfo.TryGetValue(clubId, out var ci)) { regionCode = ci.Region; clubName = ci.Name; }
                    if (string.IsNullOrWhiteSpace(regionCode)) regionCode = node.Value<string>("regionalFederation") ?? "";

                    // competitionType may be a content-picker (IPublishedContent) OR a plain string
                    // (FlexibleDropdown / textstring storing the discipline name, e.g. "Precision").
                    // Read untyped + type-switch — Value<string>() would throw on a FlexibleDropdown.
                    var ctRaw = node.Value("competitionType");
                    var discipline = ctRaw switch
                    {
                        IPublishedContent ct => ct.Name ?? "",
                        string s => s,
                        _ => ctRaw?.ToString() ?? ""
                    };
                    if (string.IsNullOrWhiteSpace(discipline)) discipline = "Tävling";

                    data.Items.Add(new FeedItem
                    {
                        Source = FeedSource.Competition,
                        Start = date.Value,
                        Window = WindowFor(date.Value, today),
                        ShowTime = false,
                        RegionCode = regionCode,
                        RegionName = RegionName(regionCode),
                        Masked = isClubOnly,
                        SourceLabel = "Tävling",
                        TypeLabel = discipline,
                        MaskedTypeLabel = discipline,
                        Title = node.Value<string>("competitionName") ?? node.Name ?? "",
                        ClubName = clubName,
                        Venue = node.Value<string>("venue") ?? "",
                        Url = node.Url()
                    });
                }

                // club events (always masked for anonymous)
                foreach (var node in all)
                {
                    if (node.ContentType.Alias != "clubSimpleEvent") continue;
                    if (!node.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true)) continue;

                    var date = node.Value<DateTime?>("eventDate");
                    if (date == null || date.Value.Date < today || date.Value.Date > horizon) continue;

                    var clubId = node.Parent?.Id ?? node.Value<int>("clubId");
                    var regionCode = "";
                    var clubName = "";
                    if (clubInfo.TryGetValue(clubId, out var ci)) { regionCode = ci.Region; clubName = ci.Name; }

                    var eventType = node.Value<string>("eventType") ?? "Träning";

                    data.Items.Add(new FeedItem
                    {
                        Source = FeedSource.ClubEvent,
                        Start = date.Value,
                        Window = WindowFor(date.Value, today),
                        ShowTime = date.Value.TimeOfDay != TimeSpan.Zero,
                        RegionCode = regionCode,
                        RegionName = RegionName(regionCode),
                        Masked = true,
                        SourceLabel = "Klubbhändelse",
                        TypeLabel = eventType,
                        MaskedTypeLabel = NeutralEventLabel(eventType),
                        Title = node.Value<string>("eventName") ?? node.Name ?? "",
                        ClubName = clubName,
                        Venue = node.Value<string>("venue") ?? "",
                        Url = node.Url()
                    });
                }

                data.Regions = regionByCode.Values
                    .OrderBy(r => r.Name, StringComparer.Create(new CultureInfo("sv-SE"), false))
                    .ToList();
            }

            // ---------- one DB query for träningsmatcher ----------
            try
            {
                List<MatchRow> rows;
                using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                {
                    rows = scope.Database.Fetch<MatchRow>(@"
                        SELECT m.Id, m.MatchName, m.MatchCode, m.StartDate, m.ClubId, m.Discipline,
                               (SELECT COUNT(*) FROM TrainingMatchParticipants p WHERE p.TrainingMatchId = m.Id) AS ParticipantCount
                        FROM TrainingMatches m
                        WHERE m.Status = 'Active'");
                }

                var now = DateTime.Now;
                foreach (var m in rows)
                {
                    var ongoing = m.StartDate == null || m.StartDate.Value <= now;
                    var start = m.StartDate ?? now;
                    if (!ongoing && start.Date > horizon) continue; // future match beyond horizon

                    var regionCode = "";
                    var clubName = "";
                    if (m.ClubId.HasValue && clubInfo.TryGetValue(m.ClubId.Value, out var ci)) { regionCode = ci.Region; clubName = ci.Name; }

                    if (ongoing)
                    {
                        data.OngoingMatchTotal++;
                        if (!string.IsNullOrWhiteSpace(regionCode))
                            data.OngoingMatchCountByRegion[regionCode] =
                                data.OngoingMatchCountByRegion.GetValueOrDefault(regionCode) + 1;
                    }

                    var discipline = string.IsNullOrWhiteSpace(m.Discipline) ? "Precision" : m.Discipline!;

                    data.Items.Add(new FeedItem
                    {
                        Source = FeedSource.TrainingMatch,
                        Start = start,
                        Window = ongoing ? FeedWindow.Now : WindowFor(start, today),
                        ShowTime = !ongoing,
                        IsOngoing = ongoing,
                        RegionCode = regionCode,
                        RegionName = regionByCode.TryGetValue(regionCode, out var rn) ? rn.Name : regionCode,
                        Masked = false, // träningsmatcher are login-gated, not masked
                        SourceLabel = "Träningsmatch",
                        TypeLabel = discipline,
                        MaskedTypeLabel = discipline,
                        Title = string.IsNullOrWhiteSpace(m.MatchName) ? "Träningsmatch" : m.MatchName!,
                        ClubName = clubName,
                        Venue = "",
                        Url = $"{tmBase.TrimEnd('/')}/?join={m.MatchCode}",
                        ParticipantCount = m.ParticipantCount
                    });
                }
            }
            catch
            {
                // training-match table unavailable → feed still works with content sources
            }

            data.Items = data.Items.OrderBy(i => i.Start).ToList();
            return data;
        }

        private static FeedWindow WindowFor(DateTime start, DateTime today)
        {
            var d = start.Date;
            if (d < today.AddDays(7)) return FeedWindow.ThisWeek;
            if (d < today.AddDays(14)) return FeedWindow.NextWeek;
            return FeedWindow.ThisMonth;
        }

        private static string NeutralEventLabel(string eventType) => eventType switch
        {
            "Möte" => "Aktivitet",
            "Annat" => "Aktivitet",
            _ => eventType
        };

        private class MatchRow
        {
            public int Id { get; set; }
            public string? MatchName { get; set; }
            public string? MatchCode { get; set; }
            public DateTime? StartDate { get; set; }
            public int? ClubId { get; set; }
            public string? Discipline { get; set; }
            public int ParticipantCount { get; set; }
        }
    }
}
