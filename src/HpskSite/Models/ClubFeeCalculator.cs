using HpskSite.Services;

namespace HpskSite.Models
{
    /// <summary>En rad på en klubbavgift, som kalkylatorn räknat fram. Skrivs till <see cref="MembershipFeeChargeLine"/>.</summary>
    public record FeeLineDraft(string Kind, string Description, decimal Amount, int? PartId, int? SubjectMemberId);

    /// <summary>Avgiftens rader och summa. <c>Total</c> är ALLTID summan av raderna.</summary>
    public record FeeCalculation(decimal Total, IReadOnlyList<FeeLineDraft> Lines)
    {
        public static readonly FeeCalculation None = new(0m, Array.Empty<FeeLineDraft>());
    }

    /// <summary>
    /// Avgiften = grundavgiften för medlemstypen + tillägg/avdrag, aldrig under 0 kr (en synlig justeringsrad tar överskottet).
    /// Rena funktioner — inget läser eller skriver här.
    ///
    /// <para>⚠️ Utan grundavgift inga rader (ett tillägg kan inte ensamt göra en avgift). Ett hushåll prövas per PERSON:
    /// varje persons typ, huvudklubb och tillval avgör vilka tillägg som gäller henne, och raden bär hennes
    /// <c>SubjectMemberId</c>. Grundavgiften är hushållets och bär ingen person.</para>
    /// </summary>
    public static class ClubFeeCalculator
    {
        public const string BaseKind = "fee-base";
        public const string PartKind = "fee-part";
        public const string AdjustKind = "fee-adjust";

        public static bool Applies(MembershipFeePart part, ISet<string> partTypeKeys, MemberFeeInput person)
        {
            if (partTypeKeys.Count > 0)
            {
                var key = MembershipTypeKeys.StandardKey(person.MembershipType) ?? person.MembershipType?.Trim();
                if (string.IsNullOrEmpty(key) || !partTypeKeys.Contains(key)) return false;
            }
            return part.Condition switch
            {
                MembershipFeePartCondition.Always => true,
                MembershipFeePartCondition.PrimaryClub => person.HomeClub == PrimaryClubState.Ours,
                MembershipFeePartCondition.NotPrimaryClub => person.HomeClub == PrimaryClubState.Other,
                MembershipFeePartCondition.Option => part.OptionId.HasValue && person.OptionIds.Contains(part.OptionId.Value),
                _ => false
            };
        }

        /// <summary>
        /// Avgiften per medlem för ett helt förslag — EN väg för listan och för skapandet, så de aldrig kan säga olika.
        /// Bara Individual och HouseholdPrimary har en avgift; övriga (Covered, NoCategory, NoType, ChooseType) får
        /// <see cref="FeeCalculation.None"/> (0 kr, inga rader).
        /// </summary>
        public static Dictionary<int, FeeCalculation> ForProposal(
            IReadOnlyDictionary<int, ClubFeeProposalRow> proposal, IEnumerable<MembershipFeeCategory> categories,
            IEnumerable<MemberFeeInput> members, IEnumerable<MembershipFeePart> parts, ILookup<int, string> partTypes)
        {
            var catById = categories.ToDictionary(c => c.Id);
            var list = members.ToList();
            var partList = parts.ToList();
            var result = new Dictionary<int, FeeCalculation>();
            foreach (var (memberId, row) in proposal)
            {
                if (row.Kind is not (ClubFeeKind.Individual or ClubFeeKind.HouseholdPrimary)
                    || row.CategoryId is not int catId || !catById.TryGetValue(catId, out var cat))
                {
                    result[memberId] = FeeCalculation.None;
                    continue;
                }

                var self = list.FirstOrDefault(m => m.MemberId == memberId);
                List<MemberFeeInput> subjects;
                if (row.Kind == ClubFeeKind.HouseholdPrimary && self is not null && !string.IsNullOrWhiteSpace(self.HouseholdId))
                    subjects = list.Where(m => string.Equals((m.HouseholdId ?? "").Trim(), self.HouseholdId!.Trim(),
                        StringComparison.OrdinalIgnoreCase)).ToList();
                else
                    subjects = self is null ? new() : new() { self };

                var label = string.IsNullOrWhiteSpace(cat.Label) ? cat.MembershipType : cat.Label;
                result[memberId] = Calculate(row.Amount, label, subjects, row.Kind == ClubFeeKind.HouseholdPrimary, partList, partTypes);
            }
            return result;
        }

        /// <param name="baseAmount">Grundavgiften, eller null när medlemmen inte har någon.</param>
        /// <param name="baseLabel">Grundavgiftens text (kategorins etikett).</param>
        /// <param name="subjects">Den som betalar, eller för ett hushåll alla personer i det.</param>
        /// <param name="partTypes">Delens typnycklar; ingen post = delen gäller alla typer.</param>
        public static FeeCalculation Calculate(decimal? baseAmount, string baseLabel,
            IEnumerable<MemberFeeInput> subjects, bool isHousehold,
            IEnumerable<MembershipFeePart> parts, ILookup<int, string> partTypes)
        {
            if (baseAmount is null) return FeeCalculation.None;

            var lines = new List<FeeLineDraft> { new(BaseKind, baseLabel, baseAmount.Value, null, null) };
            foreach (var person in subjects.OrderBy(s => s.MemberId))
                foreach (var part in parts.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
                {
                    var keys = partTypes[part.Id].ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!Applies(part, keys, person)) continue;
                    lines.Add(new FeeLineDraft(PartKind, part.Label, part.Amount, part.Id, isHousehold ? person.MemberId : null));
                }

            var sum = lines.Sum(l => l.Amount);
            if (sum < 0m)
            {
                lines.Add(new FeeLineDraft(AdjustKind, "Avgiften kan inte bli lägre än 0 kr", -sum, null, null));
                sum = 0m;
            }
            return new FeeCalculation(sum, lines);
        }
    }
}
