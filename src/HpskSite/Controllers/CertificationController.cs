using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for certification-based roles (Föreningsinstruktör,
    /// Kretsinstruktör, Riksinstruktör, Vapenkontrollant, Banläggare).
    /// </summary>
    public class CertificationController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _authService;
        private readonly CertificationService _certService;
        private readonly CertificationAuthorizationService _certAuth;
        private readonly ILogger<CertificationController> _logger;

        public CertificationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberManager memberManager,
            AdminAuthorizationService authService,
            CertificationService certService,
            CertificationAuthorizationService certAuth,
            ILogger<CertificationController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _authService = authService;
            _certService = certService;
            _certAuth = certAuth;
            _logger = logger;
        }

        // ── Read endpoints ────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> ListForMember(int memberId)
        {
            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            // A member can always read their own certs.  Admins can read anyone's.
            bool isSelf = current.Id == memberId;
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            if (!isSelf && !isSiteAdmin)
            {
                // Allow if they administer any club/region the candidate is connected to.
                var candidate = _memberService.GetById(memberId);
                if (candidate == null) return Json(new { success = false, message = "Medlemmen hittades inte." });

                int.TryParse(candidate.GetValue<string>("primaryClubId") ?? "", out int candidateClubId);
                bool isClubAdmin = candidateClubId > 0 && await _authService.IsClubAdminForClub(candidateClubId);
                if (!isClubAdmin) return Json(new { success = false, message = "Access denied" });
            }

            var rows = await _certService.GetForMemberAsync(memberId);
            return Json(new { success = true, data = rows.Select(ProjectCert) });
        }

        [HttpGet]
        public async Task<IActionResult> ListForClub(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Access denied" });

            // Find members where primary club = this club AND they have at least one
            // active cert of any of the three club-relevant types, plus the appointed
            // Föreningsinstruktörer for this club regardless of primary club.
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
            var clubMembers = allMembers.Where(m =>
            {
                var s = m.GetValue<string>("primaryClubId");
                return !string.IsNullOrEmpty(s) && int.TryParse(s, out int cid) && cid == clubId;
            }).ToList();

            // Members appointed as Föreningsinstruktör for this club (might be from any club)
            var appointmentGroup = $"Foreningsinstruktor_{clubId}";
            var appointedIds = new HashSet<int>();
            foreach (var m in allMembers)
            {
                var roles = _memberService.GetAllRoles(m.Id);
                if (roles != null && roles.Contains(appointmentGroup)) appointedIds.Add(m.Id);
            }

            var memberLookup = allMembers.ToDictionary(m => m.Id, m => m);
            var unionIds = clubMembers.Select(m => m.Id).Union(appointedIds).ToList();

            var certs = await _certService.GetActiveForMembersAsync(unionIds);
            var byMember = certs.GroupBy(c => c.MemberId).ToDictionary(g => g.Key, g => g.ToList());

            // Build the response: for each cert-type, list rows {memberId, name, club,
            // appointed (bool), cert{...}}.
            object BuildRow(int mId, string certType)
            {
                var m = memberLookup.GetValueOrDefault(mId);
                var name = $"{m?.GetValue<string>("firstName") ?? ""} {m?.GetValue<string>("lastName") ?? ""}".Trim();
                if (string.IsNullOrEmpty(name)) name = m?.Name ?? "Okänd";
                var memberCerts = byMember.GetValueOrDefault(mId);
                var cert = memberCerts?.FirstOrDefault(c => c.CertificationType == certType);

                bool appointed = certType == CertificationTypes.Foreningsinstruktor && appointedIds.Contains(mId);

                return new
                {
                    memberId = mId,
                    name,
                    cert = cert == null ? null : ProjectCert(cert),
                    appointed
                };
            }

            // Föreningsinstruktör list = appointed for this club (with their cert info)
            var foreningsList = appointedIds
                .Select(id => BuildRow(id, CertificationTypes.Foreningsinstruktor))
                .ToList();

            // Vapenkontrollant + Banläggare lists = club members holding active cert
            var vapenList = clubMembers
                .Where(m => byMember.GetValueOrDefault(m.Id)?.Any(c => c.CertificationType == CertificationTypes.Vapenkontrollant) == true)
                .Select(m => BuildRow(m.Id, CertificationTypes.Vapenkontrollant))
                .ToList();

            var banList = clubMembers
                .Where(m => byMember.GetValueOrDefault(m.Id)?.Any(c => c.CertificationType == CertificationTypes.Banlaggare) == true)
                .Select(m => BuildRow(m.Id, CertificationTypes.Banlaggare))
                .ToList();

            return Json(new
            {
                success = true,
                foreningsinstruktorer = foreningsList,
                vapenkontrollanter = vapenList,
                banlaggare = banList
            });
        }

        [HttpGet]
        public async Task<IActionResult> ListForRegion(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode))
                return Json(new { success = false, message = "Ogiltig kretskod." });
            if (!await _authService.IsRegionalAdminForRegion(regionCode))
                return Json(new { success = false, message = "Access denied" });

            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
            var memberLookup = allMembers.ToDictionary(m => m.Id, m => m);

            // Appointed Kretsinstruktörer for this region
            var appointmentGroup = $"Kretsinstruktor_{regionCode}";
            var appointedIds = new HashSet<int>();
            foreach (var m in allMembers)
            {
                var roles = _memberService.GetAllRoles(m.Id);
                if (roles != null && roles.Contains(appointmentGroup)) appointedIds.Add(m.Id);
            }

            var clubsInRegion = _authService.GetClubsInRegions(new List<string> { regionCode });
            var clubIdSet = new HashSet<int>(clubsInRegion);
            var regionMembers = allMembers.Where(m =>
            {
                var s = m.GetValue<string>("primaryClubId");
                return !string.IsNullOrEmpty(s) && int.TryParse(s, out int cid) && clubIdSet.Contains(cid);
            }).ToList();

            var allMemberIdsToConsider = regionMembers.Select(m => m.Id).Union(appointedIds).ToList();
            var certs = await _certService.GetActiveForMembersAsync(allMemberIdsToConsider);
            var byMember = certs.GroupBy(c => c.MemberId).ToDictionary(g => g.Key, g => g.ToList());

            // Föreningsinstruktör directory grouped by club
            var foreningsByClub = new Dictionary<int, List<object>>();
            foreach (var m in allMembers)
            {
                var roles = _memberService.GetAllRoles(m.Id);
                if (roles == null) continue;
                foreach (var r in roles.Where(x => x.StartsWith("Foreningsinstruktor_")))
                {
                    if (int.TryParse(r.Substring("Foreningsinstruktor_".Length), out int cid) && clubIdSet.Contains(cid))
                    {
                        var name = $"{m.GetValue<string>("firstName") ?? ""} {m.GetValue<string>("lastName") ?? ""}".Trim();
                        if (string.IsNullOrEmpty(name)) name = m.Name ?? "Okänd";
                        var cert = byMember.GetValueOrDefault(m.Id)?.FirstOrDefault(c => c.CertificationType == CertificationTypes.Foreningsinstruktor);
                        if (!foreningsByClub.ContainsKey(cid)) foreningsByClub[cid] = new List<object>();
                        foreningsByClub[cid].Add(new
                        {
                            memberId = m.Id,
                            name,
                            cert = cert == null ? null : ProjectCert(cert)
                        });
                    }
                }
            }

            object BuildPersonRow(int mId, string certType)
            {
                var m = memberLookup.GetValueOrDefault(mId);
                var name = $"{m?.GetValue<string>("firstName") ?? ""} {m?.GetValue<string>("lastName") ?? ""}".Trim();
                if (string.IsNullOrEmpty(name)) name = m?.Name ?? "Okänd";
                var cert = byMember.GetValueOrDefault(mId)?.FirstOrDefault(c => c.CertificationType == certType);
                return new
                {
                    memberId = mId,
                    name,
                    cert = cert == null ? null : ProjectCert(cert)
                };
            }

            var krets = appointedIds.Select(id => BuildPersonRow(id, CertificationTypes.Kretsinstruktor)).ToList();
            var vapen = regionMembers
                .Where(m => byMember.GetValueOrDefault(m.Id)?.Any(c => c.CertificationType == CertificationTypes.Vapenkontrollant) == true)
                .Select(m => BuildPersonRow(m.Id, CertificationTypes.Vapenkontrollant))
                .ToList();
            var ban = regionMembers
                .Where(m => byMember.GetValueOrDefault(m.Id)?.Any(c => c.CertificationType == CertificationTypes.Banlaggare) == true)
                .Select(m => BuildPersonRow(m.Id, CertificationTypes.Banlaggare))
                .ToList();

            // Group club rollups by clubName for the UI
            var ctxAccessor = UmbracoContextAccessor;
            var clubNameMap = new Dictionary<int, string>();
            if (ctxAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                foreach (var cid in clubIdSet)
                {
                    var node = ctx.Content.GetById(cid);
                    if (node != null) clubNameMap[cid] = node.Name ?? "";
                }
            }

            // Project ALL clubs in the region — the rollup must surface clubs WITHOUT
            // appointed Föreningsinstruktörer too, so the UI can flag them in red.
            var foreningsByClubProjected = clubIdSet
                .Select(cid => new
                {
                    clubId = cid,
                    clubName = clubNameMap.GetValueOrDefault(cid, "?"),
                    persons = foreningsByClub.GetValueOrDefault(cid, new List<object>())
                })
                .OrderBy(x => x.clubName)
                .ToList();

            return Json(new
            {
                success = true,
                kretsinstruktorer = krets,
                foreningsinstruktorerByClub = foreningsByClubProjected,
                vapenkontrollanter = vapen,
                banlaggare = ban
            });
        }

        /// <summary>
        /// Public-facing (login-only) cert list for a single member. Used by the member
        /// details modal on the club members directory — visible to any logged-in member.
        /// Strips admin-only fields (cert number, notes, grantor) and only returns active
        /// certifications.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PublicListForMember(int memberId)
        {
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return Json(new { success = false, message = "Login required." });

            var rows = await _certService.GetForMemberAsync(memberId);
            var data = rows
                .Where(c => c.IsActive)
                .Select(c => new
                {
                    certificationType = c.CertificationType,
                    certificationTypeLabel = CertificationTypes.DisplayName(c.CertificationType),
                    certifiedAt = c.CertifiedAt.ToString("yyyy-MM-dd"),
                    expiresAt = c.ExpiresAt?.ToString("yyyy-MM-dd"),
                    isExpired = c.IsExpired
                })
                .ToList();

            return Json(new { success = true, data });
        }

        /// <summary>
        /// Public-facing (login-only) instructor list for a club. Used by the members-tier
        /// panel on Club.cshtml. No admin info — just names + cert type.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PublicListForClub(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return Json(new { success = false, message = "Login required." });

            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
            var foreningsRole = $"Foreningsinstruktor_{clubId}";

            string FullName(Umbraco.Cms.Core.Models.IMember m)
            {
                var n = $"{m.GetValue<string>("firstName") ?? ""} {m.GetValue<string>("lastName") ?? ""}".Trim();
                return string.IsNullOrEmpty(n) ? (m.Name ?? "Okänd") : n;
            }

            var foreningsList = new List<object>();
            var vapenList = new List<object>();
            var banList = new List<object>();

            foreach (var m in allMembers)
            {
                int.TryParse(m.GetValue<string>("primaryClubId") ?? "", out int primary);
                bool inThisClub = primary == clubId;

                var roles = _memberService.GetAllRoles(m.Id);
                if (roles == null) continue;

                if (roles.Contains(foreningsRole))
                    foreningsList.Add(new { memberId = m.Id, name = FullName(m) });
                if (inThisClub && roles.Contains("Vapenkontrollant"))
                    vapenList.Add(new { memberId = m.Id, name = FullName(m) });
                if (inThisClub && roles.Contains("Banlaggare"))
                    banList.Add(new { memberId = m.Id, name = FullName(m) });
            }

            return Json(new
            {
                success = true,
                foreningsinstruktorer = foreningsList.OrderBy(o => ((dynamic)o).name).ToList(),
                vapenkontrollanter = vapenList.OrderBy(o => ((dynamic)o).name).ToList(),
                banlaggare = banList.OrderBy(o => ((dynamic)o).name).ToList()
            });
        }

        /// <summary>
        /// Public-facing (login-only) Kretsinstruktör list for a region. Used by the
        /// members-tier panel on RegionalPage.cshtml.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PublicListForRegion(string regionCode)
        {
            if (string.IsNullOrWhiteSpace(regionCode)) return Json(new { success = false, message = "Ogiltig kretskod." });
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return Json(new { success = false, message = "Login required." });

            var role = $"Kretsinstruktor_{regionCode}";
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
            var rows = new List<object>();
            foreach (var m in allMembers)
            {
                var roles = _memberService.GetAllRoles(m.Id);
                if (roles == null || !roles.Contains(role)) continue;
                var name = $"{m.GetValue<string>("firstName") ?? ""} {m.GetValue<string>("lastName") ?? ""}".Trim();
                if (string.IsNullOrEmpty(name)) name = m.Name ?? "Okänd";
                rows.Add(new { memberId = m.Id, name });
            }
            return Json(new
            {
                success = true,
                kretsinstruktorer = rows.OrderBy(o => ((dynamic)o).name).ToList()
            });
        }

        [HttpGet]
        public async Task<IActionResult> ListForArea(string areaCode)
        {
            if (string.IsNullOrWhiteSpace(areaCode))
                return Json(new { success = false, message = "Ogiltigt områdes-id." });
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Access denied" });

            var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
            var memberLookup = allMembers.ToDictionary(m => m.Id, m => m);

            var appointmentGroup = $"Riksinstruktor_{areaCode}";
            var appointedIds = new HashSet<int>();
            foreach (var m in allMembers)
            {
                var roles = _memberService.GetAllRoles(m.Id);
                if (roles != null && roles.Contains(appointmentGroup)) appointedIds.Add(m.Id);
            }

            var certs = await _certService.GetActiveForMembersAsync(appointedIds, CertificationTypes.Riksinstruktor);
            var byMember = certs.ToDictionary(c => c.MemberId, c => c);

            var rows = appointedIds.Select(id =>
            {
                var m = memberLookup.GetValueOrDefault(id);
                var name = $"{m?.GetValue<string>("firstName") ?? ""} {m?.GetValue<string>("lastName") ?? ""}".Trim();
                if (string.IsNullOrEmpty(name)) name = m?.Name ?? "Okänd";
                var cert = byMember.GetValueOrDefault(id);
                return new
                {
                    memberId = id,
                    name,
                    cert = cert == null ? null : ProjectCert(cert)
                };
            }).ToList();

            return Json(new { success = true, data = rows });
        }

        [HttpGet]
        public async Task<IActionResult> GetGrantorsFor(string certType, int candidateMemberId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                // Anyone can fetch the grantor list — it's display data, not a secret.
                // But require login so we don't leak member IDs publicly.
                var current = await _memberManager.GetCurrentMemberAsync();
                if (current == null) return Json(new { success = false, message = "Login required." });
            }

            var ids = await _certAuth.GetAuthorizedGrantorsAsync(certType, candidateMemberId);
            var rows = ids
                .Select(id => _memberService.GetById(id))
                .Where(m => m != null)
                .Select(m => new
                {
                    memberId = m!.Id,
                    name = ($"{m.GetValue<string>("firstName") ?? ""} {m.GetValue<string>("lastName") ?? ""}".Trim() is { Length: > 0 } n ? n : m.Name ?? "Okänd")
                })
                .OrderBy(x => x.name)
                .ToList();
            return Json(new { success = true, data = rows });
        }

        // ── Write endpoints ───────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Grant([FromBody] GrantCertificationRequest req)
        {
            if (req == null) return Json(new { success = false, message = "Ogiltig begäran." });

            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var (ok, certId, msg) = await _certService.GrantAsync(req, current.Id, isSiteAdmin);
            if (!ok) return Json(new { success = false, message = msg });
            return Json(new { success = true, certId, message = $"{CertificationTypes.DisplayName(req.CertificationType)} utfärdad." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Revoke([FromBody] RevokeCertificationRequest req)
        {
            if (req == null || req.CertId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            // Authorization: only the original grantor (or anyone higher up) can revoke.
            var cert = await _certService.GetByIdAsync(req.CertId);
            if (cert == null) return Json(new { success = false, message = "Certifieringen hittades inte." });

            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            bool authorized = isSiteAdmin
                || await _certAuth.CanGrantAsync(current.Id, cert.CertificationType, cert.MemberId);
            if (!authorized) return Json(new { success = false, message = "Du har inte behörighet att återkalla denna certifiering." });

            var (ok, msg) = await _certService.RevokeAsync(req.CertId, current.Id, req.Reason);
            return Json(new { success = ok, message = ok ? "Certifieringen har återkallats." : msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Appoint([FromBody] AppointmentRequest req)
        {
            if (req == null || req.MemberId <= 0 || string.IsNullOrEmpty(req.CertificationType) || string.IsNullOrEmpty(req.ScopeId))
                return Json(new { success = false, message = "Ogiltig begäran." });

            // Authorize the appointing body for the scope.
            if (!await IsAuthorizedForAppointmentScope(req.CertificationType, req.ScopeId))
                return Json(new { success = false, message = "Du har inte behörighet att utse till denna roll i kretsen/klubben/området." });

            var (ok, msg) = await _certService.AppointAsync(req.MemberId, req.CertificationType, req.ScopeId);
            return Json(new { success = ok, message = ok ? "Utnämning klar." : msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unappoint([FromBody] AppointmentRequest req)
        {
            if (req == null || req.MemberId <= 0 || string.IsNullOrEmpty(req.CertificationType) || string.IsNullOrEmpty(req.ScopeId))
                return Json(new { success = false, message = "Ogiltig begäran." });

            if (!await IsAuthorizedForAppointmentScope(req.CertificationType, req.ScopeId))
                return Json(new { success = false, message = "Du har inte behörighet att ta bort utnämningen." });

            var (ok, msg) = await _certService.UnappointAsync(req.MemberId, req.CertificationType, req.ScopeId);
            return Json(new { success = ok, message = ok ? "Utnämningen är borttagen." : msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMeta([FromBody] UpdateMetaRequest req)
        {
            if (req == null || req.CertId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var cert = await _certService.GetByIdAsync(req.CertId);
            if (cert == null) return Json(new { success = false, message = "Certifieringen hittades inte." });

            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            bool authorized = isSiteAdmin
                || await _certAuth.CanGrantAsync(current.Id, cert.CertificationType, cert.MemberId);
            if (!authorized) return Json(new { success = false, message = "Du har inte behörighet att redigera denna certifiering." });

            var (ok, msg) = await _certService.UpdateMetaAsync(req.CertId, req.CertificateNumber, req.Notes, req.ExpiresAt);
            return Json(new { success = ok, message = ok ? "Sparat." : msg });
        }

        // ── Helpers ───────────────────────────────────────────────────

        private async Task<bool> IsAuthorizedForAppointmentScope(string certType, string scopeId)
        {
            return certType switch
            {
                CertificationTypes.Foreningsinstruktor =>
                    int.TryParse(scopeId, out int cId) && await _authService.IsClubAdminForClub(cId),
                CertificationTypes.Kretsinstruktor =>
                    await _authService.IsRegionalAdminForRegion(scopeId),
                CertificationTypes.Riksinstruktor =>
                    await _authService.IsCurrentUserAdminAsync(),
                _ => false
            };
        }

        private async Task<Umbraco.Cms.Core.Models.IMember?> GetCurrentMemberDataAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            return _memberService.GetByEmail(current.Email ?? "");
        }

        private static object ProjectCert(MemberCertification c)
        {
            return new
            {
                id = c.Id,
                memberId = c.MemberId,
                certificationType = c.CertificationType,
                certificationTypeLabel = CertificationTypes.DisplayName(c.CertificationType),
                certifiedByMemberId = c.CertifiedByMemberId,
                certifiedAt = c.CertifiedAt.ToString("yyyy-MM-dd"),
                expiresAt = c.ExpiresAt?.ToString("yyyy-MM-dd"),
                certificateNumber = c.CertificateNumber,
                isActive = c.IsActive,
                isExpired = c.IsExpired,
                notes = c.Notes
            };
        }
    }

    public class RevokeCertificationRequest
    {
        public int CertId { get; set; }
        public string? Reason { get; set; }
    }

    public class AppointmentRequest
    {
        public int MemberId { get; set; }
        public string CertificationType { get; set; } = "";
        public string ScopeId { get; set; } = "";
    }

    public class UpdateMetaRequest
    {
        public int CertId { get; set; }
        public string? CertificateNumber { get; set; }
        public string? Notes { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }
}
