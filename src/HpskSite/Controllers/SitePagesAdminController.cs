using HpskSite.Services;
using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Sajtadmins kort "Klubbsidor" under /admin-page → Underhåll (klubbsidorna plan v4, 2.7): slå på eller av den nya
    /// sidstrukturen per klubb och krets. Bara sajtadministratör. En ägare utan rad, eller AV, visar dagens vy.
    ///
    /// <para>Id:t kommer utifrån och slås därför aldrig upp direkt: sorten följer id:t, och noden måste vara en klubb
    /// (<c>club</c>) respektive en krets (<c>regionalPage</c>) som inte ligger i papperskorgen (id-lagen, regel 2).
    /// Skrivningen är en aktivering som ändrar vad alla besökare ser: antiforgery, serverprövning, loggrad (i
    /// <see cref="SiteOwnerService"/>) och ett tak på antal ägare per anrop.</para>
    /// </summary>
    public class SitePagesAdminController : SurfaceController
    {
        /// <summary>Högst så många ägare per anrop (en aktivering får inte kunna slå på hela sajten i ett svep).</summary>
        public const int MaxOwnersPerCall = 25;

        private const string NotAdmin = "Bara sajtadministratör.";
        private readonly AdminAuthorizationService _auth;
        private readonly IScopeProvider _scopes;
        private readonly SiteOwnerService _owners;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly DpaAcceptanceService _dpa;

        public SitePagesAdminController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services, AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService auth, IScopeProvider scopes, SiteOwnerService owners, IMemberManager memberManager, IMemberService memberService, DpaAcceptanceService dpa)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _scopes = scopes;
            _owners = owners;
            _memberManager = memberManager;
            _memberService = memberService;
            _dpa = dpa;
        }

        /// <summary>
        /// Brytare 2 (egen redigering) per ägare. Kräver brytare 1 PÅ. <b>Hård grind (plan v2 §7):</b> en klubb måste ha godkänt personuppgiftsbiträdesavtalet
        /// (DPA, aktuell version) innan egen redigering slås på; en krets kräver att sajtadmin uttryckligen bekräftar att PUB-/ansvarsavtal finns
        /// (<paramref name="confirmAgreement"/>) tills kretsens avtal är byggt. Att slå av kräver ingen grind. Loggas av <see cref="SiteOwnerService"/>.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: bara sajtadministratör")]
        public async Task<IActionResult> SetEditing(string ownerType, int ownerId, bool enabled, bool confirmAgreement = false)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region) return Json(new { success = false, message = "Okänd ägartyp." });
            if (ownerId <= 0) return Json(new { success = false, message = "Ogiltigt id." });

            var alias = ownerType == SiteTemplates.Club ? "club" : "regionalPage";
            int found;
            using (var scope = _scopes.CreateScope(autoComplete: true))
                found = scope.Database.ExecuteScalar<int>(@"
SELECT COUNT(*) FROM umbracoNode n JOIN umbracoContent c ON c.nodeId = n.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = @0 AND n.trashed = 0 AND n.id = @1", alias, ownerId);
            if (found != 1) return Json(new { success = false, message = "Det här är inte en " + (ownerType == SiteTemplates.Club ? "klubb" : "krets") + "." });

            if (enabled)
            {
                var row = _owners.ListAll().FirstOrDefault(r => r.OwnerType == ownerType && r.OwnerId == ownerId);
                if (row == null || !row.IsEnabled) return Json(new { success = false, message = "Slå på den nya sidstrukturen först." });
                if (ownerType == SiteTemplates.Club)
                {
                    var dpa = await _dpa.GetStatusForClubAsync(ownerId);
                    if (!dpa.Accepted)
                        return Json(new { success = false, code = "dpa", message = "Klubben har inte godkänt personuppgiftsbiträdesavtalet (version " + dpa.CurrentVersion + "). Egen redigering kan inte slås på före det." });
                }
                else if (!confirmAgreement)
                    return Json(new { success = false, code = "agreement", message = "Bekräfta att kretsen har ett ansvarsavtal innan egen redigering slås på." });
            }

            var cur = await _memberManager.GetCurrentMemberAsync();
            var memberId = cur?.Email == null ? (int?)null : _memberService.GetByEmail(cur.Email)?.Id;
            try { _owners.SetEditing(ownerType, ownerId, enabled, memberId); }
            catch (InvalidOperationException ex) { return Json(new { success = false, message = ex.Message }); }
            return Json(new { success = true });
        }

        public sealed class OwnerNodeRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Alias { get; set; } = "";
            public string? RegionName { get; set; }
        }

        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: bara sajtadministratör")]
        public async Task<IActionResult> Overview(string? q = null, string? type = null, bool onlyOn = false, int take = 100)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            take = Math.Clamp(take, 1, 200);

            List<OwnerNodeRow> nodes;
            using (var scope = _scopes.CreateScope(autoComplete: true))
            {
                nodes = scope.Database.Fetch<OwnerNodeRow>(@"
SELECT n.id AS Id, n.[text] AS Name, ct.alias AS Alias, g.[text] AS RegionName
FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
LEFT JOIN umbracoNode p ON p.id = n.parentId
LEFT JOIN umbracoNode g ON g.id = p.parentId
WHERE ct.alias IN ('club', 'regionalPage') AND n.trashed = 0
ORDER BY ct.alias, n.[text]");
            }

            Dictionary<string, SiteOwnerRow> rows;
            try { rows = _owners.ListAll().ToDictionary(r => SiteOwnerService.Key(r.OwnerType, r.OwnerId), StringComparer.Ordinal); }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Tabellen SiteOwner kunde inte läsas — är Migrations/create-site-owner-table.sql körd? (" + ex.GetBaseException().Message + ")" });
            }

            var dpa = await _dpa.GetAllStatusesAsync();
            var all = nodes.Select(n =>
            {
                var ownerType = n.Alias == "club" ? SiteTemplates.Club : SiteTemplates.Region;
                rows.TryGetValue(SiteOwnerService.Key(ownerType, n.Id), out var row);
                return new
                {
                    type = ownerType,
                    id = n.Id,
                    name = n.Name,
                    region = ownerType == SiteTemplates.Club ? n.RegionName : null,
                    enabled = row?.IsEnabled == true,
                    templateVersion = row?.TemplateVersion,
                    enabledDate = row?.EnabledDate,
                    editing = row?.IsEnabled == true && row.EditingEnabled,
                    termsAccepted = row?.TermsAcceptedDate != null,
                    dpaAccepted = ownerType == SiteTemplates.Club && dpa.TryGetValue(n.Id, out var st) && st.Accepted,
                };
            }).ToList();

            IEnumerable<dynamic> filtered = all;
            if (!string.IsNullOrWhiteSpace(type)) filtered = filtered.Where(o => string.Equals((string)o.type, type, StringComparison.OrdinalIgnoreCase));
            if (onlyOn) filtered = filtered.Where(o => (bool)o.enabled);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                filtered = filtered.Where(o => ((string)o.name).Contains(needle, StringComparison.OrdinalIgnoreCase)
                                               || (((string?)o.region) ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase));
            }
            var list = filtered.ToList();

            return Json(new
            {
                success = true,
                clubTemplateVersion = SiteTemplates.ClubTemplate.Version,
                regionTemplateVersion = SiteTemplates.RegionTemplate.Version,
                counts = new
                {
                    clubs = all.Count(o => o.type == SiteTemplates.Club),
                    clubsOn = all.Count(o => o.type == SiteTemplates.Club && o.enabled),
                    regions = all.Count(o => o.type == SiteTemplates.Region),
                    regionsOn = all.Count(o => o.type == SiteTemplates.Region && o.enabled),
                },
                total = list.Count,
                shown = Math.Min(list.Count, take),
                maxPerCall = MaxOwnersPerCall,
                owners = list.Take(take),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: bara sajtadministratör")]
        public async Task<IActionResult> Set(string ownerType, int ownerId, bool enabled)
            => await SetMany(ownerType, new[] { ownerId }, enabled);

        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: bara sajtadministratör")]
        public async Task<IActionResult> SetMany(string ownerType, int[] ownerIds, bool enabled)
        {
            if (!await _auth.IsCurrentUserAdminAsync()) return Json(new { success = false, message = NotAdmin });
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region)
                return Json(new { success = false, message = "Okänd ägartyp." });
            var ids = (ownerIds ?? Array.Empty<int>()).Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return Json(new { success = false, message = "Ingen ägare vald." });
            if (ids.Count > MaxOwnersPerCall)
                return Json(new { success = false, message = $"Högst {MaxOwnersPerCall} ägare åt gången." });

            // Id:t kommer utifrån: noden måste vara rätt sort (alias) och inte ligga i papperskorgen.
            var alias = ownerType == SiteTemplates.Club ? "club" : "regionalPage";
            var valid = new List<int>();
            using (var scope = _scopes.CreateScope(autoComplete: true))
            {
                // En fråga per id (högst 25): ingen IN-lista, som NPoco kan expandera tyst fel.
                foreach (var id in ids)
                {
                    var found = scope.Database.ExecuteScalar<int>(@"
SELECT COUNT(*) FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = @0 AND n.trashed = 0 AND n.id = @1", alias, id);
                    if (found == 1) valid.Add(id);
                }
            }
            var rejected = ids.Except(valid).ToList();
            if (rejected.Count > 0)
                return Json(new { success = false, message = "Det här är inte en " + (ownerType == SiteTemplates.Club ? "klubb" : "krets") + ": " + string.Join(", ", rejected) });

            var cur = await _memberManager.GetCurrentMemberAsync();
            var memberId = cur?.Email == null ? (int?)null : _memberService.GetByEmail(cur.Email)?.Id;
            try
            {
                foreach (var id in valid) _owners.Set(ownerType, id, enabled, memberId);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte spara — är Migrations/create-site-owner-table.sql körd? (" + ex.GetBaseException().Message + ")" });
            }
            return Json(new { success = true, changed = valid.Count, enabled });
        }
    }
}
