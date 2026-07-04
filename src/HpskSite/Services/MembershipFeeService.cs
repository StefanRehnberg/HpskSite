using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// One member's inputs for fee-charge generation. HouseholdId/HouseholdPrimary drive
    /// familjeavgift (§4.1); an empty HouseholdId means the member is billed individually.
    /// </summary>
    public class MemberFeeInput
    {
        public int MemberId { get; set; }
        public string? MembershipType { get; set; }
        public string? HouseholdId { get; set; }
        public bool HouseholdPrimary { get; set; }
    }

    /// <summary>
    /// Membership-fee (medlemsavgift) data access — fee categories per club/year and
    /// per-member charges. Follows the IScopeProvider CRUD pattern (see BoardRoleService)
    /// and reuses the invoice two-state claim/received model.
    /// See Documentation/MEMBER_DATABASE.md §4.
    /// </summary>
    public class MembershipFeeService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;

        public MembershipFeeService(IScopeProvider scopeProvider, IMemberService memberService)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
        }

        // ── Categories ────────────────────────────────────────────────

        public List<MembershipFeeCategory> GetCategories(int clubId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<MembershipFeeCategory>(
                "SELECT * FROM MembershipFeeCategory WHERE ClubId = @0 AND Year = @1 ORDER BY MembershipType, Label",
                clubId, year);
        }

        /// <summary>Insert (Id == 0) or update a fee category. Returns the saved row.</summary>
        public MembershipFeeCategory SaveCategory(MembershipFeeCategory cat)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            if (cat.Id > 0)
            {
                db.Update(cat);
            }
            else
            {
                cat.CreatedDate = DateTime.UtcNow;
                db.Insert(cat);
            }
            return cat;
        }

        public bool DeleteCategory(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var cat = db.SingleOrDefaultById<MembershipFeeCategory>(id);
            if (cat == null) return false;
            db.Delete(cat);
            return true;
        }

        // ── Charges ───────────────────────────────────────────────────

        public MembershipFeeCharge? GetCharge(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charge = scope.Database.SingleOrDefaultById<MembershipFeeCharge>(id);
            if (charge != null) ResolveMemberInfo(new List<MembershipFeeCharge> { charge });
            return charge;
        }

        public List<MembershipFeeCharge> GetChargesForClubYear(int clubId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charges = scope.Database.Fetch<MembershipFeeCharge>(
                "SELECT * FROM MembershipFeeCharge WHERE ClubId = @0 AND Year = @1 ORDER BY Id",
                clubId, year);
            ResolveMemberInfo(charges);
            return charges;
        }

        public MembershipFeeCharge? GetChargeForMemberYear(int memberId, int clubId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var charge = scope.Database.FirstOrDefault<MembershipFeeCharge>(
                "SELECT * FROM MembershipFeeCharge WHERE MemberId = @0 AND ClubId = @1 AND Year = @2",
                memberId, clubId, year);
            if (charge != null) ResolveMemberInfo(new List<MembershipFeeCharge> { charge });
            return charge;
        }

        /// <summary>
        /// Resolve MemberName + MemberEmail for a set of charges, batching distinct member
        /// lookups to avoid an N+1 cascade (same pattern as BoardRoleService.ResolveMemberNames).
        /// </summary>
        private void ResolveMemberInfo(List<MembershipFeeCharge> charges)
        {
            var byId = new Dictionary<int, (string Name, string Email)>();
            foreach (var memberId in charges.Select(c => c.MemberId).Distinct())
            {
                var member = _memberService.GetById(memberId);
                if (member == null) continue;
                var first = member.GetValue<string>("firstName") ?? "";
                var last = member.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                byId[memberId] = (string.IsNullOrEmpty(name) ? member.Name : name, member.Email ?? "");
            }

            foreach (var charge in charges)
                if (byId.TryGetValue(charge.MemberId, out var info))
                {
                    charge.MemberName = info.Name;
                    charge.MemberEmail = info.Email;
                }
        }

        /// <summary>
        /// Create a charge for each supplied member that does not yet have one for the year,
        /// using the club's fee-category amount matching the member's membershipType. Members
        /// with no matching category are skipped. Returns the number of charges created.
        ///
        /// Familjeavgift (§4.1): members sharing a non-empty HouseholdId are billed as one
        /// household — the primary member (HouseholdPrimary, else the lowest MemberId) gets a
        /// single charge for the household's fee category; the other household members get a
        /// 0 kr "covered" charge referencing the primary's charge via HouseholdCoveredByChargeId,
        /// so they show as included rather than owing a separate fee.
        /// </summary>
        public int GenerateChargesForClub(int clubId, int year, IEnumerable<MemberFeeInput> members)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var categories = db.Fetch<MembershipFeeCategory>(
                "SELECT * FROM MembershipFeeCategory WHERE ClubId = @0 AND Year = @1", clubId, year);
            if (categories.Count == 0) return 0;

            // Case-insensitive lookup: membershipType -> category.
            var catByType = new Dictionary<string, MembershipFeeCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c.MembershipType))
                    catByType[c.MembershipType.Trim()] = c;

            // Members that already have a charge this year (skip them).
            var existingMemberIds = db.Fetch<int>(
                "SELECT MemberId FROM MembershipFeeCharge WHERE ClubId = @0 AND Year = @1", clubId, year)
                .ToHashSet();

            var memberList = members.ToList();
            var created = 0;

            // Household groups: members with a non-empty HouseholdId, billed together.
            var households = memberList
                .Where(m => !string.IsNullOrWhiteSpace(m.HouseholdId))
                .GroupBy(m => m.HouseholdId!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1); // a lone member in a "household" is just an individual

            var householdMemberIds = new HashSet<int>();
            foreach (var group in households)
            {
                // Primary = the flagged member, else the lowest MemberId (deterministic).
                var primary = group.FirstOrDefault(m => m.HouseholdPrimary)
                              ?? group.OrderBy(m => m.MemberId).First();
                var primaryType = (primary.MembershipType ?? "").Trim();
                if (string.IsNullOrEmpty(primaryType) || !catByType.TryGetValue(primaryType, out var famCat))
                    continue; // no category for the household's membership type → leave as individuals

                foreach (var m in group) householdMemberIds.Add(m.MemberId);

                // Ensure the primary has a charge, and capture its id to link the covered members.
                int primaryChargeId;
                var existingPrimary = db.FirstOrDefault<MembershipFeeCharge>(
                    "SELECT * FROM MembershipFeeCharge WHERE MemberId = @0 AND ClubId = @1 AND Year = @2",
                    primary.MemberId, clubId, year);
                if (existingPrimary != null)
                {
                    primaryChargeId = existingPrimary.Id;
                }
                else
                {
                    var primaryCharge = new MembershipFeeCharge
                    {
                        MemberId = primary.MemberId,
                        ClubId = clubId,
                        Year = year,
                        CategoryId = famCat.Id,
                        Amount = famCat.Amount,
                        PaymentStatus = "Pending",
                        CreatedDate = DateTime.UtcNow
                    };
                    db.Insert(primaryCharge);
                    primaryChargeId = primaryCharge.Id;
                    existingMemberIds.Add(primary.MemberId);
                    created++;
                }

                // Covered members: 0 kr charge referencing the primary's charge.
                foreach (var m in group)
                {
                    if (m.MemberId == primary.MemberId) continue;
                    if (existingMemberIds.Contains(m.MemberId)) continue;
                    var covered = new MembershipFeeCharge
                    {
                        MemberId = m.MemberId,
                        ClubId = clubId,
                        Year = year,
                        CategoryId = famCat.Id,
                        Amount = 0m,
                        PaymentStatus = "Pending",
                        HouseholdCoveredByChargeId = primaryChargeId,
                        CreatedDate = DateTime.UtcNow
                    };
                    db.Insert(covered);
                    existingMemberIds.Add(m.MemberId);
                    created++;
                }
            }

            // Individuals (no household, or household without a matching category).
            foreach (var m in memberList)
            {
                if (householdMemberIds.Contains(m.MemberId)) continue;
                if (existingMemberIds.Contains(m.MemberId)) continue;

                var type = (m.MembershipType ?? "").Trim();
                if (string.IsNullOrEmpty(type)) continue;
                if (!catByType.TryGetValue(type, out var cat)) continue; // no matching category → skip

                var charge = new MembershipFeeCharge
                {
                    MemberId = m.MemberId,
                    ClubId = clubId,
                    Year = year,
                    CategoryId = cat.Id,
                    Amount = cat.Amount,
                    PaymentStatus = "Pending",
                    CreatedDate = DateTime.UtcNow
                };
                db.Insert(charge);
                existingMemberIds.Add(m.MemberId);
                created++;
            }

            return created;
        }

        /// <summary>
        /// Payer claim: record that the payer says they've paid. Does NOT set Paid — only the
        /// club admin confirms received (see MarkPaid).
        /// </summary>
        public bool SetPaymentSent(int chargeId, string sentBy)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
            if (charge == null) return false;
            if (charge.PaymentStatus == "Paid") return true; // already settled — nothing to claim

            charge.PaymentSentDate = DateTime.UtcNow;
            charge.PaymentSentBy = sentBy;
            db.Update(charge);
            return true;
        }

        public bool MarkPaid(int chargeId, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
            if (charge == null) return false;

            charge.PaymentStatus = "Paid";
            charge.PaidDate = DateTime.UtcNow;
            charge.PaidConfirmedByMemberId = byMemberId;
            db.Update(charge);
            return true;
        }

        public bool MarkUnpaid(int chargeId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var charge = db.SingleOrDefaultById<MembershipFeeCharge>(chargeId);
            if (charge == null) return false;

            charge.PaymentStatus = "Pending";
            charge.PaidDate = null;
            charge.PaidConfirmedByMemberId = null;
            db.Update(charge);
            return true;
        }
    }
}
