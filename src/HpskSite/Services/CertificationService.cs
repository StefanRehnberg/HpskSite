using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Single writer for the MemberCertifications table. Also reconciles the corresponding
    /// member-group memberships so that role-based authorization keeps working unchanged
    /// in the rest of the codebase. Reads are cheap and uncached at this stage — the table
    /// is small and updates are rare.
    /// </summary>
    public class CertificationService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly IMemberGroupService _memberGroupService;
        private readonly CertificationAuthorizationService _certAuth;
        private readonly ILogger<CertificationService> _logger;

        public CertificationService(
            IUmbracoDatabaseFactory databaseFactory,
            IMemberService memberService,
            IMemberGroupService memberGroupService,
            CertificationAuthorizationService certAuth,
            ILogger<CertificationService> logger)
        {
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _memberGroupService = memberGroupService;
            _certAuth = certAuth;
            _logger = logger;
        }

        // ── CRUD ───────────────────────────────────────────────────────

        public async Task<List<MemberCertification>> GetForMemberAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<MemberCertification>(
                "WHERE MemberId = @0 ORDER BY IsActive DESC, CertifiedAt DESC", memberId);
        }

        public async Task<List<MemberCertification>> GetActiveByTypeAsync(string certType)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<MemberCertification>(
                "WHERE CertificationType = @0 AND IsActive = 1 ORDER BY CertifiedAt DESC", certType);
        }

        public async Task<List<MemberCertification>> GetActiveForMembersAsync(IEnumerable<int> memberIds, string? certType = null)
        {
            var ids = memberIds.Distinct().ToList();
            if (ids.Count == 0) return new List<MemberCertification>();

            var paramNames = string.Join(",", ids.Select((_, i) => "@" + i));
            using var db = _databaseFactory.CreateDatabase();

            if (certType != null)
            {
                return await db.FetchAsync<MemberCertification>(
                    $"WHERE IsActive = 1 AND CertificationType = @{ids.Count} AND MemberId IN ({paramNames}) ORDER BY CertifiedAt DESC",
                    ids.Cast<object>().Concat(new object[] { certType }).ToArray());
            }
            return await db.FetchAsync<MemberCertification>(
                $"WHERE IsActive = 1 AND MemberId IN ({paramNames}) ORDER BY CertifiedAt DESC",
                ids.Cast<object>().ToArray());
        }

        public async Task<MemberCertification?> GetByIdAsync(int certId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultByIdAsync<MemberCertification>(certId);
        }

        public async Task<bool> HasActiveCertAsync(int memberId, string certType)
        {
            using var db = _databaseFactory.CreateDatabase();
            var count = await db.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM MemberCertifications
                  WHERE MemberId = @0 AND CertificationType = @1 AND IsActive = 1
                    AND (ExpiresAt IS NULL OR ExpiresAt > GETUTCDATE())",
                memberId, certType);
            return count > 0;
        }

        // ── Granting ──────────────────────────────────────────────────

        public async Task<(bool Success, int CertId, string? Message)> GrantAsync(
            GrantCertificationRequest req,
            int actingMemberId,
            bool isSiteAdmin)
        {
            if (req == null) return (false, 0, "Ogiltig begäran.");
            if (req.MemberId <= 0) return (false, 0, "Saknar medlem.");
            if (string.IsNullOrEmpty(req.CertificationType)) return (false, 0, "Saknar certifieringstyp.");

            // Authorization: either site admin, OR the acting member holds appropriate authority,
            // OR a specific grantor is named who has authority. Site-admin gets a free pass.
            if (!isSiteAdmin)
            {
                var grantorId = req.CertifiedByMemberId ?? actingMemberId;
                if (!await _certAuth.CanGrantAsync(grantorId, req.CertificationType, req.MemberId))
                    return (false, 0, "Du har inte behörighet att utfärda denna certifiering.");
            }

            // Reject duplicates (active cert of the same type already exists)
            if (await HasActiveCertAsync(req.MemberId, req.CertificationType))
                return (false, 0, $"{CertificationTypes.DisplayName(req.CertificationType)} är redan utfärdad och aktiv.");

            var entry = new MemberCertification
            {
                MemberId = req.MemberId,
                CertificationType = req.CertificationType,
                CertifiedByMemberId = req.CertifiedByMemberId,
                CertifiedAt = req.CertifiedAt ?? DateTime.UtcNow,
                ExpiresAt = req.ExpiresAt,
                CertificateNumber = req.CertificateNumber,
                IsActive = true,
                Notes = req.Notes,
                CreatedAt = DateTime.UtcNow
            };

            using var db = _databaseFactory.CreateDatabase();
            var newId = Convert.ToInt32(await db.InsertAsync(entry));

            // Vapenkontrollant / Banläggare are appointmentless — adding the cert IS the
            // authorization, so flip the global group on now.
            if (CertificationTypes.IsAppointmentless(req.CertificationType))
            {
                var group = CertificationTypes.AppointmentGroup(req.CertificationType, null);
                if (group != null)
                {
                    await EnsureGroupExistsAsync(group);
                    _memberService.AssignRole(req.MemberId, group);
                }
            }

            _logger.LogInformation(
                "Granted certification {Type} to member {MemberId} (cert id {CertId}, by {Grantor})",
                req.CertificationType, req.MemberId, newId, req.CertifiedByMemberId);

            return (true, newId, null);
        }

        // ── Revoking ──────────────────────────────────────────────────

        public async Task<(bool Success, string? Message)> RevokeAsync(int certId, int revokedBy, string? reason)
        {
            using var db = _databaseFactory.CreateDatabase();
            var cert = await db.SingleOrDefaultByIdAsync<MemberCertification>(certId);
            if (cert == null) return (false, "Certifieringen hittades inte.");
            if (!cert.IsActive) return (true, null); // already revoked

            cert.IsActive = false;
            cert.RevokedAt = DateTime.UtcNow;
            cert.RevokedByMemberId = revokedBy;
            cert.RevokedReason = reason;
            await db.UpdateAsync(cert);

            // Remove appointmentless global group
            if (CertificationTypes.IsAppointmentless(cert.CertificationType))
            {
                var group = CertificationTypes.AppointmentGroup(cert.CertificationType, null);
                if (group != null)
                {
                    _memberService.DissociateRole(cert.MemberId, group);
                }
            }
            else
            {
                // Strip every appointment of this type from the member — the underlying
                // credential is gone, so they can no longer hold the role anywhere.
                var prefix = CertificationTypes.AppointmentGroupPrefix(cert.CertificationType);
                if (!string.IsNullOrEmpty(prefix))
                {
                    var roles = _memberService.GetAllRoles(cert.MemberId) ?? Enumerable.Empty<string>();
                    foreach (var r in roles.Where(x => x.StartsWith(prefix)).ToList())
                    {
                        _memberService.DissociateRole(cert.MemberId, r);
                    }
                }
            }

            _logger.LogInformation(
                "Revoked certification {CertId} ({Type}) from member {MemberId} by {RevokedBy}: {Reason}",
                certId, cert.CertificationType, cert.MemberId, revokedBy, reason ?? "");

            return (true, null);
        }

        // ── Appointments (for instructor types only) ───────────────────

        public async Task<(bool Success, string? Message)> AppointAsync(int memberId, string certType, string scopeId)
        {
            if (CertificationTypes.IsAppointmentless(certType))
                return (false, "Den här certifieringstypen kräver ingen separat utnämning.");
            if (string.IsNullOrEmpty(scopeId))
                return (false, "Saknar scope-id för utnämningen.");

            if (!await HasActiveCertAsync(memberId, certType))
                return (false, $"Medlemmen saknar aktiv {CertificationTypes.DisplayName(certType)}-certifiering.");

            var group = CertificationTypes.AppointmentGroup(certType, scopeId);
            if (group == null) return (false, "Ogiltig certifieringstyp för utnämning.");

            await EnsureGroupExistsAsync(group);
            _memberService.AssignRole(memberId, group);

            _logger.LogInformation(
                "Appointed member {MemberId} as {Type} for {ScopeId}", memberId, certType, scopeId);
            return (true, null);
        }

        public Task<(bool Success, string? Message)> UnappointAsync(int memberId, string certType, string scopeId)
        {
            if (CertificationTypes.IsAppointmentless(certType))
                return Task.FromResult<(bool, string?)>((false, "Den här certifieringstypen har ingen utnämning att ta bort."));
            if (string.IsNullOrEmpty(scopeId))
                return Task.FromResult<(bool, string?)>((false, "Saknar scope-id."));

            var group = CertificationTypes.AppointmentGroup(certType, scopeId);
            if (group == null) return Task.FromResult<(bool, string?)>((false, "Ogiltig certifieringstyp."));

            _memberService.DissociateRole(memberId, group);

            _logger.LogInformation(
                "Unappointed member {MemberId} as {Type} for {ScopeId}", memberId, certType, scopeId);
            return Task.FromResult<(bool, string?)>((true, null));
        }

        // ── Metadata edits (cert number etc.) ──────────────────────────

        public async Task<(bool Success, string? Message)> UpdateMetaAsync(
            int certId, string? certNumber, string? notes, DateTime? expiresAt)
        {
            using var db = _databaseFactory.CreateDatabase();
            var cert = await db.SingleOrDefaultByIdAsync<MemberCertification>(certId);
            if (cert == null) return (false, "Certifieringen hittades inte.");

            cert.CertificateNumber = certNumber;
            cert.Notes = notes;
            cert.ExpiresAt = expiresAt;
            await db.UpdateAsync(cert);
            return (true, null);
        }

        // ── Helpers ───────────────────────────────────────────────────

        private async Task EnsureGroupExistsAsync(string groupName)
        {
            try
            {
                var existing = await _memberGroupService.GetByNameAsync(groupName);
                if (existing == null)
                {
                    var newGroup = new MemberGroup { Name = groupName };
                    await _memberGroupService.CreateAsync(newGroup);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to ensure member group {GroupName} exists", groupName);
            }
        }
    }

    public class GrantCertificationRequest
    {
        public int MemberId { get; set; }
        public string CertificationType { get; set; } = "";
        public int? CertifiedByMemberId { get; set; }
        public DateTime? CertifiedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? CertificateNumber { get; set; }
        public string? Notes { get; set; }
    }
}
