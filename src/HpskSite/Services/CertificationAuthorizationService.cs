using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Validates whether a given member has the authority to grant a certification of a
    /// given type to another member. Hierarchy:
    ///
    ///   Riksinstruktör cert      ← only site admins
    ///   Kretsinstruktör cert     ← active Riksinstruktör appointed to the candidate's area, or site admin
    ///   Föreningsinstruktör cert ← any active Krets/Riks instructor, or site admin
    ///   Vapenkontrollant cert    ← any active Krets/Riks instructor, or site admin
    ///   Banläggare cert          ← any active Krets/Riks instructor, or site admin
    ///
    /// Appointment authority (assigning the role to a club/region/area) lives in the existing
    /// scope-admin checks (IsClubAdminForClub, IsRegionalAdminForRegion, IsCurrentUserAdminAsync)
    /// — this service is purely about who can issue the personal credential.
    /// </summary>
    public class CertificationAuthorizationService
    {
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _adminAuth;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public CertificationAuthorizationService(
            IMemberService memberService,
            AdminAuthorizationService adminAuth,
            IUmbracoContextAccessor umbracoContextAccessor)
        {
            _memberService = memberService;
            _adminAuth = adminAuth;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        public Task<bool> CanGrantAsync(int grantorMemberId, string certType, int candidateMemberId)
        {
            if (grantorMemberId <= 0) return Task.FromResult(false);

            var grantorRoles = _memberService.GetAllRoles(grantorMemberId)?.ToList() ?? new List<string>();

            // Members of the Administrators group can grant any cert.
            if (grantorRoles.Contains("Administrators")) return Task.FromResult(true);

            switch (certType)
            {
                case CertificationTypes.Riksinstruktor:
                    // Only site admins (handled above) may grant. A Riksinstruktör cannot
                    // grant another Riksinstruktör cert.
                    return Task.FromResult(false);

                case CertificationTypes.Kretsinstruktor:
                    {
                        // Kretsinstruktör is certified by SPSF — the site is just recording it.
                        // Acceptable grantors:
                        //   1. An appointed Riksinstruktör for the candidate's area (the actual issuer), OR
                        //   2. The regional admin for the candidate's primary club's region (recording on SPSF's behalf).
                        var candidateRegionCode = GetRegionForCandidate(candidateMemberId);
                        if (!string.IsNullOrEmpty(candidateRegionCode)
                            && grantorRoles.Contains($"RegionalAdmin_{candidateRegionCode}"))
                        {
                            return Task.FromResult(true);
                        }
                        var candidateAreaCode = string.IsNullOrEmpty(candidateRegionCode)
                            ? null : _adminAuth.GetAreaForRegion(candidateRegionCode);
                        if (!string.IsNullOrEmpty(candidateAreaCode)
                            && grantorRoles.Contains($"Riksinstruktor_{candidateAreaCode}"))
                        {
                            return Task.FromResult(true);
                        }
                        return Task.FromResult(false);
                    }

                case CertificationTypes.Foreningsinstruktor:
                case CertificationTypes.Vapenkontrollant:
                case CertificationTypes.Banlaggare:
                    {
                        // Any appointed Krets or Riks instructor (any region/area) qualifies.
                        var ok = grantorRoles.Any(r =>
                            r.StartsWith("Kretsinstruktor_") || r.StartsWith("Riksinstruktor_"));
                        return Task.FromResult(ok);
                    }

                default:
                    return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Returns the list of member IDs that may grant the given cert type to the given
        /// candidate. Used to populate the "Certifierad av" dropdown in the assign modal.
        /// </summary>
        public async Task<List<int>> GetAuthorizedGrantorsAsync(string certType, int candidateMemberId)
        {
            // Walk every approved member checking roles. Counts are in the low thousands
            // even on large installations — fine for an interactive admin form.
            var allMembers = _memberService.GetAll(0, int.MaxValue, out _);
            var result = new List<int>();
            foreach (var m in allMembers)
            {
                if (!m.IsApproved) continue;
                if (await CanGrantAsync(m.Id, certType, candidateMemberId))
                {
                    result.Add(m.Id);
                }
            }
            return result;
        }

        private string? GetAreaForCandidate(int candidateMemberId)
        {
            var regionCode = GetRegionForCandidate(candidateMemberId);
            return string.IsNullOrEmpty(regionCode) ? null : _adminAuth.GetAreaForRegion(regionCode);
        }

        private string? GetRegionForCandidate(int candidateMemberId)
        {
            try
            {
                var candidate = _memberService.GetById(candidateMemberId);
                if (candidate == null) return null;

                var primaryClubIdStr = candidate.GetValue<string>("primaryClubId");
                if (string.IsNullOrEmpty(primaryClubIdStr) || !int.TryParse(primaryClubIdStr, out int clubId) || clubId <= 0)
                    return null;

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;

                var clubNode = ctx.Content.GetById(clubId);
                return clubNode?.Value<string>("regionalFederation");
            }
            catch
            {
                return null;
            }
        }
    }
}
