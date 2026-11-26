using HpskSite.Services;

namespace HpskSite.Models
{
    /// <summary>Svaret på "vad skulle den här personen (eller familjen) betala?" — utan att något skrivs.</summary>
    public record ClubFeePreviewResult(ClubFeeKind Kind, FeeCalculation Calculation);

    /// <summary>
    /// Exempelräkningen på Avgiftsregler: syntetiska personer genom SAMMA väg som listan och skapandet
    /// (<see cref="ClubFeeProposal.Build"/> + <see cref="ClubFeeCalculator.ForProposal"/>). Ingen egen regel här —
    /// då kan exemplet aldrig säga något annat än den riktiga avgiften.
    ///
    /// <para>En person = individ. Fler än en = ett hushåll där den första är huvudmedlem (som i listan).</para>
    /// </summary>
    public static class ClubFeePreview
    {
        private const string HouseholdKey = "preview-household";

        public static ClubFeePreviewResult Compute(
            IReadOnlyList<MembershipFeeCategory> categories, IReadOnlyList<MembershipFeePart> parts,
            ILookup<int, string> partTypes, IReadOnlyList<MemberFeeInput> people)
        {
            if (people.Count == 0) return new ClubFeePreviewResult(ClubFeeKind.NoType, FeeCalculation.None);

            // Egna kopior: ids 1..n så att inget kan kollidera med verkliga medlemmar, och hushållet sätts här.
            var inputs = people.Select((p, i) => new MemberFeeInput
            {
                MemberId = i + 1,
                MembershipType = p.MembershipType,
                HomeClub = p.HomeClub,
                OptionIds = p.OptionIds,
                HouseholdId = people.Count > 1 ? HouseholdKey : null,
                HouseholdPrimary = people.Count > 1 && i == 0,
            }).ToList();

            var proposal = ClubFeeProposal.Build(categories, inputs);
            var calcs = ClubFeeCalculator.ForProposal(proposal, categories, inputs, parts, partTypes);
            // Saknas avgift för huvudmedlemmens typ behandlas hushållet som individer: summera då alla.
            var primary = proposal[1];
            if (people.Count > 1 && primary.Kind != ClubFeeKind.HouseholdPrimary)
            {
                var lines = calcs.Values.SelectMany(c => c.Lines).ToList();
                return new ClubFeePreviewResult(primary.Kind, new FeeCalculation(calcs.Values.Sum(c => c.Total), lines));
            }
            return new ClubFeePreviewResult(primary.Kind, calcs[1]);
        }
    }
}
