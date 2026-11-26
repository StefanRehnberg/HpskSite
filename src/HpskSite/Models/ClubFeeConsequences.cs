namespace HpskSite.Models
{
    /// <summary>
    /// Vad en ändring av taxa, tillägg, förbund eller typer fick för följder för årets avgifter (expertgruppen 2026-11-26, K6).
    /// Kassören ska se det direkt efter sparningen: "7 oskickade avgifter räknas om. 3 skickade ändras inte och avviker nu med +450 kr."
    /// </summary>
    public record ClubFeeConsequences(int UnsentCleared, int SentDiverging, int PaidDiverging, decimal DivergingSum)
    {
        public static readonly ClubFeeConsequences None = new(0, 0, 0, 0m);

        /// <summary>
        /// Skickade och betalda avgifter är frysta. De som nu skulle blivit ett annat belopp enligt dagens taxa räknas här,
        /// ur SAMMA förslag och kalkyl som listan visar (<see cref="ClubFeeProposal"/> + <see cref="ClubFeeCalculator"/>), aldrig en egen regel.
        /// Hushållets "ingår"-rader och kretsavgifter räknas inte.
        /// </summary>
        public static ClubFeeConsequences Compute(int unsentCleared, IEnumerable<MembershipFeeCharge> charges,
            IReadOnlyDictionary<int, ClubFeeProposalRow> proposal, IReadOnlyDictionary<int, FeeCalculation> calcs)
        {
            int sent = 0, paid = 0;
            decimal sum = 0m;
            foreach (var c in charges)
            {
                if (c.IsRegionFee || c.HouseholdCoveredByChargeId.HasValue) continue;
                var isPaid = c.PaymentStatus == "Paid";
                if (!isPaid && !c.IsRequestSent) continue;      // oskickade är redan rensade och räknas om
                if (!proposal.TryGetValue(c.MemberId, out var row)) continue;
                if (row.Kind is not (ClubFeeKind.Individual or ClubFeeKind.HouseholdPrimary)) continue;
                var now = calcs.TryGetValue(c.MemberId, out var calc) ? calc.Total : 0m;
                var diff = now - c.Amount;
                if (Math.Abs(diff) < 0.005m) continue;
                if (isPaid) paid++; else sent++;
                sum += diff;
            }
            return new ClubFeeConsequences(unsentCleared, sent, paid, sum);
        }
    }
}
