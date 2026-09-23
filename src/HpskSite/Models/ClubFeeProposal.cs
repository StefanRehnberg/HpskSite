using HpskSite.Services;

namespace HpskSite.Models
{
    /// <summary>Hur en medlems medlemsavgift för året blir.</summary>
    public enum ClubFeeKind
    {
        /// <summary>Betalar sin medlemstyps avgift.</summary>
        Individual,
        /// <summary>Huvudmedlem i ett hushåll — betalar hushållets avgift.</summary>
        HouseholdPrimary,
        /// <summary>Ingår i hushållets avgift, som huvudmedlemmen betalar.</summary>
        Covered,
        /// <summary>Klubben har ingen avgift för medlemmens medlemstyp i år.</summary>
        NoCategory,
        /// <summary>Medlemmen saknar medlemstyp i klubbens register.</summary>
        NoType
    }

    public record ClubFeeProposalRow(int MemberId, ClubFeeKind Kind, decimal Amount, int? CategoryId,
                                     string? MembershipType, int? PrimaryMemberId);

    /// <summary>
    /// Vad varje medlem ska betala i medlemsavgift — utan att skriva något.
    ///
    /// <para><b>⚠️⚠️ EN REGEL, TVÅ KONSUMENTER.</b> Listan visar förslaget innan någon avgift finns,
    /// och <c>MembershipFeeService.GenerateChargesForClub</c> skapar avgifterna UR SAMMA förslag. Skrevs
    /// regeln två gånger kunde listan säga "800 kr" medan avgiften blev något annat.</para>
    ///
    /// <para>Familjeavgift: medlemmar med samma HouseholdId (fler än en) betalar EN gång — huvudmedlemmen
    /// (flaggad, annars lägsta MemberId) betalar hushållets avgift enligt sin medlemstyp; övriga ingår.
    /// Saknas avgift för huvudmedlemmens typ behandlas hushållet som individer.</para>
    /// </summary>
    public static class ClubFeeProposal
    {
        public static Dictionary<int, ClubFeeProposalRow> Build(
            IEnumerable<MembershipFeeCategory> categories, IEnumerable<MemberFeeInput> members)
        {
            var catByType = new Dictionary<string, MembershipFeeCategory>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in categories)
                if (!string.IsNullOrWhiteSpace(c.MembershipType))
                    catByType[c.MembershipType.Trim()] = c;

            var list = members.ToList();
            var result = new Dictionary<int, ClubFeeProposalRow>();

            foreach (var group in list
                         .Where(m => !string.IsNullOrWhiteSpace(m.HouseholdId))
                         .GroupBy(m => m.HouseholdId!.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
            {
                var primary = group.FirstOrDefault(m => m.HouseholdPrimary) ?? group.OrderBy(m => m.MemberId).First();
                var type = (primary.MembershipType ?? "").Trim();
                if (type.Length == 0 || !catByType.TryGetValue(type, out var fam)) continue;

                result[primary.MemberId] = new ClubFeeProposalRow(primary.MemberId, ClubFeeKind.HouseholdPrimary,
                    fam.Amount, fam.Id, type, null);
                foreach (var m in group.Where(m => m.MemberId != primary.MemberId))
                    result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.Covered, 0m, fam.Id,
                        (m.MembershipType ?? "").Trim(), primary.MemberId);
            }

            foreach (var m in list.Where(m => !result.ContainsKey(m.MemberId)))
            {
                var type = (m.MembershipType ?? "").Trim();
                if (type.Length == 0)
                    result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.NoType, 0m, null, null, null);
                else if (!catByType.TryGetValue(type, out var cat))
                    result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.NoCategory, 0m, null, type, null);
                else
                    result[m.MemberId] = new ClubFeeProposalRow(m.MemberId, ClubFeeKind.Individual, cat.Amount, cat.Id, type, null);
            }
            return result;
        }
    }
}
