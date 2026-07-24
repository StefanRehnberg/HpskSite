using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Messaging
{
    /// <summary>
    /// Resolves a competition + scope (Alla / Klass / Individ) to the set of registered member ids,
    /// for the participant (shooter-facing) notification channel.
    ///
    /// competitionRegistration nodes are Save()-only / unpublished, so this reads through the writable
    /// IContentService — NOT the published cache (which would undercount). Mirrors the enumeration in
    /// RegistrationAdminController: hub 'competitionRegistrationsHub' → 'competitionRegistration' children.
    /// </summary>
    public class ParticipantAudienceResolver
    {
        private readonly IContentService _contentService;

        public ParticipantAudienceResolver(IContentService contentService)
        {
            _contentService = contentService;
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
                matched = regs.Where(r => r.Classes.Any(c => string.Equals(c, scopeKey, StringComparison.OrdinalIgnoreCase)));
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
                .SelectMany(r => r.Classes)
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
                    if (string.IsNullOrWhiteSpace(c)) continue;
                    if (!byClass.TryGetValue(c, out var set)) { set = new HashSet<int>(); byClass[c] = set; }
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
            public List<string> Classes { get; init; } = new();
        }

        private List<Registration> GetActiveRegistrations(int competitionId)
        {
            var result = new List<Registration>();
            if (competitionId <= 0) return result;

            var comp = _contentService.GetById(competitionId);
            if (comp == null) return result;

            var hub = _contentService.GetPagedChildren(comp.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (hub == null) return result;

            var regNodes = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration");

            foreach (var reg in regNodes)
            {
                // isActive defaults true when the property is absent/unset.
                if (reg.HasProperty("isActive") && !reg.GetValue<bool>("isActive")) continue;

                var memberId = reg.GetValue<int>("memberId");
                if (memberId <= 0) continue;

                var classes = CompetitionRegistrationDocument
                    .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "")
                    .Select(sc => sc.Class)
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                // Legacy single-class fallback.
                if (classes.Count == 0)
                {
                    var single = reg.GetValue<string>("shootingClass");
                    if (!string.IsNullOrWhiteSpace(single)) classes.Add(single);
                }

                result.Add(new Registration { MemberId = memberId, Classes = classes });
            }

            return result;
        }
    }
}
