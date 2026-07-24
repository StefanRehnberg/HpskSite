using HpskSite.Models;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Messaging
{
    /// <summary>
    /// Resolves a competition + scope (Alla / Klass / Individ) to the set of registered member ids,
    /// for the participant (shooter-facing) notification channel.
    ///
    /// competitionRegistration nodes are Save()-only / unpublished. The earlier implementation walked
    /// them through the writable IContentService, which materializes a full IContent per node — fine
    /// for small comps but too heavy for hundreds of participants. This version reads the whole comp's
    /// registrations in ONE projection query against the Umbraco content tables (current draft version),
    /// then caches the parsed result briefly. Measured ~5 ms for 84 registrations; scales flat.
    ///
    /// The projection keeps each registration's parsed class entries (incl. TeamNumber), so a future
    /// Skjutlag scope is a filter over data already loaded; a per-day scope will need to join the
    /// start-list / patrol nodes (day lives there, not on the registration) — see notes on GetActiveRegistrations.
    /// </summary>
    public class ParticipantAudienceResolver
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly AppCaches _appCaches;

        // Registrations change slowly relative to a composer session (preview-as-you-type + send).
        // A short TTL makes those interactions instant without going stale for long.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        public ParticipantAudienceResolver(IScopeProvider scopeProvider, AppCaches appCaches)
        {
            _scopeProvider = scopeProvider;
            _appCaches = appCaches;
        }

        /// <summary>Distinct member ids in the audience for the given scope.</summary>
        public List<int> ResolveMemberIds(int competitionId, string scopeType, string? scopeKey)
        {
            var regs = GetActiveRegistrations(competitionId);
            IEnumerable<Registration> matched;

            if (string.Equals(scopeType, "Person", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(scopeKey, out var pid)) return new List<int>();
                matched = regs.Where(r => r.MemberId == pid);
            }
            else if (string.Equals(scopeType, "Klass", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(scopeKey)) return new List<int>();
                matched = regs.Where(r => r.Classes.Any(c => string.Equals(c.Class, scopeKey, StringComparison.OrdinalIgnoreCase)));
            }
            else if (string.Equals(scopeType, "Skjutlag", StringComparison.OrdinalIgnoreCase))
            {
                // Team/skjutlag as recorded on the registration's class entries (TeamNumber). Start-list
                // reassignments aren't reflected here yet — see GetActiveRegistrations notes.
                if (!int.TryParse(scopeKey, out var team)) return new List<int>();
                matched = regs.Where(r => r.Classes.Any(c => c.TeamNumber == team));
            }
            else // All (whole competition)
            {
                matched = regs;
            }

            return matched.Select(r => r.MemberId).Where(id => id > 0).Distinct().ToList();
        }

        public int Count(int competitionId, string scopeType, string? scopeKey)
            => ResolveMemberIds(competitionId, scopeType, scopeKey).Count;

        /// <summary>The classes a member is registered in — used to scope their inbox.</summary>
        public List<string> GetMemberClasses(int competitionId, int memberId)
            => GetActiveRegistrations(competitionId)
                .Where(r => r.MemberId == memberId)
                .SelectMany(r => r.Classes.Select(c => c.Class))
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public bool IsRegistered(int competitionId, int memberId)
            => GetActiveRegistrations(competitionId).Any(r => r.MemberId == memberId);

        /// <summary>Composer summary: total distinct registrants + per-class counts (sv-SE ordered).</summary>
        public Models.Messaging.ParticipantAudienceSummary GetAudienceSummary(int competitionId)
        {
            var regs = GetActiveRegistrations(competitionId);
            var byClass = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            var all = new HashSet<int>();
            foreach (var r in regs)
            {
                if (r.MemberId > 0) all.Add(r.MemberId);
                foreach (var c in r.Classes)
                {
                    if (string.IsNullOrWhiteSpace(c.Class)) continue;
                    if (!byClass.TryGetValue(c.Class, out var set)) { set = new HashSet<int>(); byClass[c.Class] = set; }
                    if (r.MemberId > 0) set.Add(r.MemberId);
                }
            }
            return new Models.Messaging.ParticipantAudienceSummary
            {
                Total = all.Count,
                Classes = byClass
                    .Select(kv => new Models.Messaging.ParticipantClassCount { ClassId = kv.Key, Count = kv.Value.Count })
                    .OrderBy(c => c.ClassId, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false))
                    .ToList()
            };
        }

        // --- internals ---

        private sealed class Registration
        {
            public int MemberId { get; init; }
            public List<ShootingClassEntry> Classes { get; init; } = new();
        }

        // NPoco projection row — one per registration node, its 4 relevant properties pivoted.
        private sealed class RegProjectionRow
        {
            public int NodeId { get; set; }
            public string? MemberIdRaw { get; set; }
            public string? ShootingClasses { get; set; }
            public string? ShootingClass { get; set; }
            public int? IsActive { get; set; }
        }

        /// <summary>
        /// One projection query for the whole comp's registrations (current draft version), cached briefly.
        /// NOTE for future scoping: TeamNumber comes from the registration's stored class entries; per-day
        /// scoping needs the start-list/patrol nodes (precisionStartList configurationData / FaltskyttePatrol /
        /// Springskytte start lists), which is a separate join to add when that scope ships.
        /// </summary>
        private List<Registration> GetActiveRegistrations(int competitionId)
        {
            if (competitionId <= 0) return new List<Registration>();
            var key = "pn_audience_" + competitionId;
            return _appCaches.RuntimeCache.GetCacheItem(key, () => LoadRegistrations(competitionId), CacheTtl)
                   ?? new List<Registration>();
        }

        private List<Registration> LoadRegistrations(int competitionId)
        {
            const string sql = @"
SELECT n.id AS NodeId,
  MAX(CASE WHEN pt.Alias = 'memberId'        THEN COALESCE(CAST(pd.intValue AS nvarchar(50)), pd.varcharValue, pd.textValue) END) AS MemberIdRaw,
  MAX(CASE WHEN pt.Alias = 'shootingClasses' THEN COALESCE(pd.textValue, pd.varcharValue) END) AS ShootingClasses,
  MAX(CASE WHEN pt.Alias = 'shootingClass'   THEN COALESCE(pd.varcharValue, pd.textValue) END) AS ShootingClass,
  MAX(CASE WHEN pt.Alias = 'isActive'        THEN pd.intValue END) AS IsActive
FROM umbracoNode comp
JOIN umbracoNode hub        ON hub.parentId = comp.id
JOIN umbracoContent hc      ON hc.nodeId = hub.id
JOIN cmsContentType hct     ON hct.nodeId = hc.contentTypeId AND hct.alias = 'competitionRegistrationsHub'
JOIN umbracoNode n          ON n.parentId = hub.id AND n.trashed = 0
JOIN umbracoContent rc      ON rc.nodeId = n.id
JOIN cmsContentType rct     ON rct.nodeId = rc.contentTypeId AND rct.alias = 'competitionRegistration'
JOIN umbracoContentVersion cv ON cv.nodeId = n.id AND cv.[current] = 1
JOIN umbracoPropertyData pd ON pd.versionId = cv.id
JOIN cmsPropertyType pt     ON pt.id = pd.propertyTypeId
                            AND pt.Alias IN ('memberId','shootingClasses','shootingClass','isActive')
WHERE comp.id = @0
GROUP BY n.id";

            List<RegProjectionRow> rows;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                rows = scope.Database.Fetch<RegProjectionRow>(sql, competitionId);
            }
            catch
            {
                // Never throw to the caller — an empty audience surfaces as "0 mottagare" in the composer.
                return new List<Registration>();
            }

            var result = new List<Registration>(rows.Count);
            foreach (var row in rows)
            {
                // isActive defaults true when the property row is absent (matches the doctype default).
                if (row.IsActive == 0) continue;
                if (!int.TryParse(row.MemberIdRaw, out var memberId) || memberId <= 0) continue;

                var classes = CompetitionRegistrationDocument
                    .DeserializeShootingClasses(row.ShootingClasses ?? "")
                    .Where(c => !string.IsNullOrWhiteSpace(c.Class))
                    .ToList();

                // Legacy single-class fallback.
                if (classes.Count == 0 && !string.IsNullOrWhiteSpace(row.ShootingClass))
                    classes.Add(new ShootingClassEntry { Class = row.ShootingClass });

                result.Add(new Registration { MemberId = memberId, Classes = classes });
            }
            return result;
        }
    }
}
