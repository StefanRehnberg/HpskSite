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
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _authService;
        private readonly CertificationService _certService;
        private readonly CertificationAuthorizationService _certAuth;
        private readonly EmailService _emailService;
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
            EmailService emailService,
            ILogger<CertificationController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _memberManager = memberManager;
            _authService = authService;
            _certService = certService;
            _certAuth = certAuth;
            _emailService = emailService;
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
            var appointedIds = await GetMemberIdsInGroupAsync($"Foreningsinstruktor_{clubId}");

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
            var appointedIds = await GetMemberIdsInGroupAsync($"Kretsinstruktor_{regionCode}");

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

            // Föreningsinstruktör directory grouped by club — en SQL-fråga för alla
            // Foreningsinstruktor_*-grupper, inte en rollslagning per medlem i registret.
            var foreningsByClub = new Dictionary<int, List<object>>();
            const string foreningsPrefix = "Foreningsinstruktor_";
            var foreningsMemberships = (await GetGroupMembershipsByPrefixAsync(foreningsPrefix))
                .Select(r => new
                {
                    r.MemberId,
                    ClubId = int.TryParse(r.GroupName.Substring(foreningsPrefix.Length), out int cid) ? cid : 0
                })
                .Where(x => x.ClubId > 0 && clubIdSet.Contains(x.ClubId))
                .ToList();

            var foreningsCerts = await _certService.GetActiveForMembersAsync(
                foreningsMemberships.Select(x => x.MemberId), CertificationTypes.Foreningsinstruktor);
            var foreningsCertByMember = foreningsCerts.GroupBy(c => c.MemberId).ToDictionary(g => g.Key, g => g.First());

            foreach (var entry in foreningsMemberships)
            {
                var m = memberLookup.GetValueOrDefault(entry.MemberId) ?? _memberService.GetById(entry.MemberId);
                if (m == null || !m.IsApproved) continue;
                var cert = foreningsCertByMember.GetValueOrDefault(entry.MemberId);
                if (!foreningsByClub.ContainsKey(entry.ClubId)) foreningsByClub[entry.ClubId] = new List<object>();
                foreningsByClub[entry.ClubId].Add(new
                {
                    memberId = m.Id,
                    name = MemberDisplayName(m),
                    cert = cert == null ? null : ProjectCert(cert)
                });
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

            var foreningsRole = $"Foreningsinstruktor_{clubId}";
            var groups = await GetMemberIdsInGroupsAsync(new[] { foreningsRole, "Vapenkontrollant", "Banlaggare" });
            var foreningsIds = groups.GetValueOrDefault(foreningsRole) ?? new HashSet<int>();
            var vapenIds = groups.GetValueOrDefault("Vapenkontrollant") ?? new HashSet<int>();
            var banIds = groups.GetValueOrDefault("Banlaggare") ?? new HashSet<int>();

            var relevant = GetApprovedMembersByIds(foreningsIds.Concat(vapenIds).Concat(banIds));

            var foreningsList = new List<object>();
            var vapenList = new List<object>();
            var banList = new List<object>();

            foreach (var m in relevant)
            {
                int.TryParse(m.GetValue<string>("primaryClubId") ?? "", out int primary);
                bool inThisClub = primary == clubId;

                if (foreningsIds.Contains(m.Id))
                    foreningsList.Add(new { memberId = m.Id, name = MemberDisplayName(m) });
                if (inThisClub && vapenIds.Contains(m.Id))
                    vapenList.Add(new { memberId = m.Id, name = MemberDisplayName(m) });
                if (inThisClub && banIds.Contains(m.Id))
                    banList.Add(new { memberId = m.Id, name = MemberDisplayName(m) });
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

            var appointedIds = await GetMemberIdsInGroupAsync($"Kretsinstruktor_{regionCode}");
            var rows = GetApprovedMembersByIds(appointedIds)
                .Select(m => (object)new { memberId = m.Id, name = MemberDisplayName(m) })
                .ToList();
            return Json(new
            {
                success = true,
                kretsinstruktorer = rows.OrderBy(o => ((dynamic)o).name).ToList()
            });
        }

        /// <summary>
        /// Kretsinstruktörer for the region a club belongs to. Powers the note on the club
        /// admin Certifieringar tab that tells admins who to contact for training toward
        /// Föreningsinstruktör / Vapenkontrollant / Banläggare. Login required; contact
        /// details (email/phone) are included only for the club's own admins (and site/
        /// regional admins), matching the club-scoped contact-exposure pattern elsewhere.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> KretsinstruktorerForClub(int clubId)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return Json(new { success = false, message = "Login required." });

            var regionCode = GetRegionForClub(clubId);
            if (string.IsNullOrWhiteSpace(regionCode))
                return Json(new { success = true, regionKnown = false, kretsinstruktorer = new object[0] });

            bool canSeeContact = await _authService.IsClubAdminForClub(clubId)
                || await _authService.IsCurrentUserAdminAsync()
                || await _authService.IsRegionalAdminForRegion(regionCode);

            var appointedIds = await GetMemberIdsInGroupAsync($"Kretsinstruktor_{regionCode}");
            var rows = GetApprovedMembersByIds(appointedIds)
                .Select(m => (object)new
                {
                    memberId = m.Id,
                    name = MemberDisplayName(m),
                    email = canSeeContact ? (m.Email ?? "") : null,
                    phone = canSeeContact ? (m.GetValue<string>("phoneNumber") ?? "") : null
                })
                .ToList();

            return Json(new
            {
                success = true,
                regionKnown = true,
                regionCode,
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

            var appointedIds = await GetMemberIdsInGroupAsync($"Riksinstruktor_{areaCode}");

            var certs = await _certService.GetActiveForMembersAsync(appointedIds, CertificationTypes.Riksinstruktor);
            // GroupBy, inte ToDictionary: två aktiva cert på samma medlem får inte krascha listan.
            var byMember = certs.GroupBy(c => c.MemberId).ToDictionary(g => g.Key, g => g.First());

            var rows = GetApprovedMembersByIds(appointedIds)
                .Select(m =>
                {
                    var cert = byMember.GetValueOrDefault(m.Id);
                    return new
                    {
                        memberId = m.Id,
                        name = MemberDisplayName(m),
                        cert = cert == null ? null : ProjectCert(cert)
                    };
                })
                .OrderBy(x => x.name, StringComparer.CurrentCulture)
                .ToList();

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

        // ── Certification requests (bootstrap queue) ───────────────────

        /// <summary>
        /// A club admin requests a certification for a member whose issuing instructor is not
        /// on pistol.nu. The candidate's SPSF identity (name/email from the member record,
        /// Pistolkortnummer typed in) is captured so an approver can verify them. Lands Pending.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestCertification([FromBody] CertificationRequestDto req)
        {
            if (req == null || req.CandidateMemberId <= 0 || string.IsNullOrEmpty(req.CertificationType) || req.ClubId <= 0)
                return Json(new { success = false, message = "Ogiltig begäran." });
            if (string.IsNullOrWhiteSpace(req.Pistolkortnummer))
                return Json(new { success = false, message = "Skyttens Pistolkortnummer krävs." });
            if (string.IsNullOrWhiteSpace(req.IssuerName))
                return Json(new { success = false, message = "Utfärdarens namn krävs." });
            if (!req.CertifiedAt.HasValue)
                return Json(new { success = false, message = "Certifieringsdatum krävs." });

            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            // Only the club's admins may submit on the club's behalf.
            if (!await _authService.IsClubAdminForClub(req.ClubId))
                return Json(new { success = false, message = "Du har inte behörighet att begära certifieringar för den här klubben." });

            var candidate = _memberService.GetById(req.CandidateMemberId);
            if (candidate == null) return Json(new { success = false, message = "Medlemmen hittades inte." });

            var entry = new CertificationRequest
            {
                CandidateMemberId = req.CandidateMemberId,
                CertificationType = req.CertificationType,
                ClubId = req.ClubId,
                CandidateFullName = MemberDisplayName(candidate),
                CandidateEmail = candidate.Email,
                Pistolkortnummer = req.Pistolkortnummer.Trim(),
                IssuerName = req.IssuerName.Trim(),
                IssuerPistolkortnummer = string.IsNullOrWhiteSpace(req.IssuerPistolkortnummer) ? null : req.IssuerPistolkortnummer.Trim(),
                CertifiedAt = req.CertifiedAt.Value,
                ExpiresAt = req.ExpiresAt,
                CertificateNumber = string.IsNullOrWhiteSpace(req.CertificateNumber) ? null : req.CertificateNumber.Trim(),
                RequestedByMemberId = current.Id,
                RequestNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim()
            };

            var candidateName = entry.CandidateFullName;
            var (ok, requestId, msg) = await _certService.CreateRequestAsync(entry);

            if (!ok) return Json(new { success = false, message = msg });

            // Best-effort: notify the regional admins for the candidate's region.
            try { await NotifyApproversOfRequestAsync(req.ClubId, candidateName, req.CertificationType, MemberDisplayName(current)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to notify approvers of certification request {RequestId}", requestId); }

            return Json(new { success = true, requestId, message = $"Förfrågan om {CertificationTypes.DisplayName(req.CertificationType)} skickad för granskning." });
        }

        /// <summary>Pending requests the current user may approve — every club in the regions
        /// they administer (all regions for site admins). Powers the approver queue.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPendingRequests()
        {
            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var regions = await _authService.GetManagedRegions();
            if (regions == null || regions.Count == 0)
                return Json(new { success = false, message = "Access denied" });

            var clubIds = _authService.GetClubsInRegions(regions);
            var pending = await _certService.GetPendingRequestsForClubsAsync(clubIds);

            return Json(new { success = true, data = pending.Select(ProjectRequest).ToList() });
        }

        /// <summary>A club admin's read-only view of the requests submitted from their club.</summary>
        [HttpGet]
        public async Task<IActionResult> GetClubRequests(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Access denied" });

            // Hide approved requests — once issued, the member appears in the verified list,
            // so an "Approved" line here is redundant. Pending + Rejected stay (Rejected is the
            // only place the club admin sees the rejection reason).
            var rows = (await _certService.GetRequestsForClubAsync(clubId))
                .Where(r => r.Status != CertificationRequestStatus.Approved);
            return Json(new { success = true, data = rows.Select(ProjectRequest).ToList() });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveRequest([FromBody] RequestDecisionDto req)
        {
            if (req == null || req.RequestId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var request = await _certService.GetRequestByIdAsync(req.RequestId);
            if (request == null) return Json(new { success = false, message = "Förfrågan hittades inte." });

            if (!await CanApproveRequestAsync(request))
                return Json(new { success = false, message = "Du har inte behörighet att godkänna den här förfrågan." });

            var (ok, msg, reviewed) = await _certService.ApproveRequestAsync(req.RequestId, current.Id);
            if (!ok) return Json(new { success = false, message = msg });

            try { await NotifyRequesterOfDecisionAsync(reviewed!, approved: true, note: null); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to notify requester of approved request {RequestId}", req.RequestId); }

            return Json(new { success = true, message = $"{CertificationTypes.DisplayName(request.CertificationType)} utfärdad." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectRequest([FromBody] RequestDecisionDto req)
        {
            if (req == null || req.RequestId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });

            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var request = await _certService.GetRequestByIdAsync(req.RequestId);
            if (request == null) return Json(new { success = false, message = "Förfrågan hittades inte." });

            if (!await CanApproveRequestAsync(request))
                return Json(new { success = false, message = "Du har inte behörighet att avslå den här förfrågan." });

            var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
            var (ok, msg, reviewed) = await _certService.RejectRequestAsync(req.RequestId, current.Id, note);
            if (!ok) return Json(new { success = false, message = msg });

            try { await NotifyRequesterOfDecisionAsync(reviewed!, approved: false, note: note); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to notify requester of rejected request {RequestId}", req.RequestId); }

            return Json(new { success = true, message = "Förfrågan avslagen." });
        }

        // ── Helpers ───────────────────────────────────────────────────

        /// <summary>An approver is a site admin or a regional admin for the candidate's region.</summary>
        private async Task<bool> CanApproveRequestAsync(CertificationRequest request)
        {
            if (await _authService.IsCurrentUserAdminAsync()) return true;
            var region = GetRegionForClub(request.ClubId);
            return !string.IsNullOrEmpty(region) && await _authService.IsRegionalAdminForRegion(region);
        }

        private string? GetRegionForClub(int clubId)
        {
            if (clubId <= 0) return null;
            if (!UmbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
            var clubNode = ctx.Content.GetById(clubId);
            return clubNode?.Value<string>("regionalFederation");
        }

        private string GetClubName(int clubId)
        {
            if (clubId <= 0) return "";
            if (!UmbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return "";
            return ctx.Content.GetById(clubId)?.Name ?? "";
        }

        /// <summary>
        /// Medlems-id:n i en medlemsgrupp, med EN SQL-fråga. Ersätter mönstret
        /// GetAll(0, int.MaxValue) + GetAllRoles per medlem, som ger ett anrop till
        /// databasen per medlem i registret — det tog minuter i produktion.
        /// Använd ALLTID den här när en grupp ska översättas till medlemmar.
        /// </summary>
        private async Task<HashSet<int>> GetMemberIdsInGroupAsync(string groupName)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ids = await db.FetchAsync<int>(@"
                SELECT DISTINCT m2g.Member
                FROM cmsMember2MemberGroup m2g
                INNER JOIN umbracoNode grp ON m2g.MemberGroup = grp.id
                WHERE grp.text = @0", groupName);
            return new HashSet<int>(ids);
        }

        /// <summary>Samma sak för flera grupper på en gång — grupp → medlems-id:n.</summary>
        private async Task<Dictionary<string, HashSet<int>>> GetMemberIdsInGroupsAsync(IEnumerable<string> groupNames)
        {
            var names = groupNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0) return result;

            var paramNames = string.Join(",", names.Select((_, i) => "@" + i));
            using var db = _databaseFactory.CreateDatabase();
            var rows = await db.FetchAsync<GroupMemberRow>($@"
                SELECT grp.text AS GroupName, m2g.Member AS MemberId
                FROM cmsMember2MemberGroup m2g
                INNER JOIN umbracoNode grp ON m2g.MemberGroup = grp.id
                WHERE grp.text IN ({paramNames})", names.Cast<object>().ToArray());

            foreach (var r in rows)
            {
                if (!result.TryGetValue(r.GroupName, out var set))
                {
                    set = new HashSet<int>();
                    result[r.GroupName] = set;
                }
                set.Add(r.MemberId);
            }
            return result;
        }

        /// <summary>(grupp, medlems-id) för alla grupper vars namn börjar med prefixet,
        /// t.ex. "Foreningsinstruktor_" för hela kretsens instruktörskatalog.</summary>
        private async Task<List<GroupMemberRow>> GetGroupMembershipsByPrefixAsync(string prefix)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<GroupMemberRow>(@"
                SELECT grp.text AS GroupName, m2g.Member AS MemberId
                FROM cmsMember2MemberGroup m2g
                INNER JOIN umbracoNode grp ON m2g.MemberGroup = grp.id
                WHERE grp.text LIKE @0", prefix.Replace("%", "[%]").Replace("_", "[_]") + "%");
        }

        /// <summary>Slår upp en handfull medlemmar via id. Bara för små mängder
        /// (gruppmedlemmar) — aldrig för hela registret.</summary>
        private List<Umbraco.Cms.Core.Models.IMember> GetApprovedMembersByIds(IEnumerable<int> ids)
        {
            return ids.Distinct()
                .Select(id => _memberService.GetById(id))
                .Where(m => m != null && m.IsApproved)
                .Select(m => m!)
                .ToList();
        }

        public class GroupMemberRow
        {
            public string GroupName { get; set; } = "";
            public int MemberId { get; set; }
        }

        private static string MemberDisplayName(Umbraco.Cms.Core.Models.IMember m)
        {
            var n = $"{m.GetValue<string>("firstName") ?? ""} {m.GetValue<string>("lastName") ?? ""}".Trim();
            return string.IsNullOrEmpty(n) ? (m.Name ?? "Okänd") : n;
        }

        private object ProjectRequest(CertificationRequest r)
        {
            var requester = r.RequestedByMemberId > 0 ? _memberService.GetById(r.RequestedByMemberId) : null;
            var reviewer = r.ReviewedByMemberId.HasValue ? _memberService.GetById(r.ReviewedByMemberId.Value) : null;
            return new
            {
                id = r.Id,
                candidateMemberId = r.CandidateMemberId,
                candidateName = r.CandidateFullName,
                candidateEmail = r.CandidateEmail,
                pistolkortnummer = r.Pistolkortnummer,
                issuerName = r.IssuerName,
                issuerPistolkortnummer = r.IssuerPistolkortnummer,
                certifiedAt = r.CertifiedAt.ToString("yyyy-MM-dd"),
                expiresAt = r.ExpiresAt?.ToString("yyyy-MM-dd"),
                certificateNumber = r.CertificateNumber,
                certificationType = r.CertificationType,
                certificationTypeLabel = CertificationTypes.DisplayName(r.CertificationType),
                clubId = r.ClubId,
                clubName = GetClubName(r.ClubId),
                requestedByName = requester != null ? MemberDisplayName(requester) : "Okänd",
                requestedAt = r.RequestedAt.ToString("yyyy-MM-dd"),
                requestNote = r.RequestNote,
                status = r.Status,
                reviewedByName = reviewer != null ? MemberDisplayName(reviewer) : null,
                reviewedAt = r.ReviewedAt?.ToString("yyyy-MM-dd"),
                reviewNote = r.ReviewNote
            };
        }

        /// <summary>Best-effort email to everyone who can approve the request — the regional
        /// admins for the candidate's region plus all site admins (deduped).</summary>
        private async Task NotifyApproversOfRequestAsync(int clubId, string candidateName, string certType, string requesterName)
        {
            var region = GetRegionForClub(clubId);
            var roleGroup = string.IsNullOrEmpty(region) ? null : $"RegionalAdmin_{region}";
            var clubName = GetClubName(clubId);
            var certLabel = CertificationTypes.DisplayName(certType);

            var groupNames = new List<string> { "Administrators" };
            if (roleGroup != null) groupNames.Add(roleGroup);
            var groups = await GetMemberIdsInGroupsAsync(groupNames);
            var adminIds = groups.Values.SelectMany(v => v).Distinct();

            var admins = GetApprovedMembersByIds(adminIds)
                .Where(m => !string.IsNullOrEmpty(m.Email))
                .ToList();

            foreach (var a in admins)
            {
                await _emailService.SendCertificationRequestSubmittedAsync(
                    a.Email!, MemberDisplayName(a), requesterName, candidateName, certLabel, clubName);
            }
        }

        private async Task NotifyRequesterOfDecisionAsync(CertificationRequest request, bool approved, string? note)
        {
            var requester = request.RequestedByMemberId > 0 ? _memberService.GetById(request.RequestedByMemberId) : null;
            if (requester == null || string.IsNullOrEmpty(requester.Email)) return;

            await _emailService.SendCertificationRequestDecisionAsync(
                requester.Email!, MemberDisplayName(requester),
                request.CandidateFullName, CertificationTypes.DisplayName(request.CertificationType),
                approved, note);
        }

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

    public class CertificationRequestDto
    {
        public int CandidateMemberId { get; set; }
        public string CertificationType { get; set; } = "";
        public int ClubId { get; set; }
        public string Pistolkortnummer { get; set; } = "";
        public string IssuerName { get; set; } = "";
        public string IssuerPistolkortnummer { get; set; } = "";
        public DateTime? CertifiedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? CertificateNumber { get; set; }
        public string? Note { get; set; }
    }

    public class RequestDecisionDto
    {
        public int RequestId { get; set; }
        public string? Note { get; set; }
    }
}
