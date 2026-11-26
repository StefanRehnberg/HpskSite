using System.Runtime.CompilerServices;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Följderna av en inställningsändring (expertgruppen 2026-11-26, K2/K6): vilka skickade och betalda avgifter som nu avviker, ur samma
    /// kalkyl som listan. Och en vaktkontroll: varje ändpunkt som rensar oskickade avgifter tar samma lås som skapandet och utskicket.
    /// </summary>
    public class ClubFeeConsequencesTests
    {
        private static MembershipFeeCharge Charge(int member, decimal amount, bool sent = false, bool paid = false, bool covered = false) => new()
        {
            Id = member * 10, MemberId = member, Amount = amount, IssuerType = MembershipFeeIssuer.Club,
            RequestSentDate = sent ? DateTime.Today : null, PaymentStatus = paid ? "Paid" : "Pending",
            HouseholdCoveredByChargeId = covered ? 1 : null
        };

        private static ClubFeeProposalRow Row(int member, ClubFeeKind kind = ClubFeeKind.Individual) => new(member, kind, 0m, 1, "Senior", null);

        private static FeeCalculation Calc(decimal total) => new(total, Array.Empty<FeeLineDraft>());

        [Fact]
        public void Skickad_och_betald_som_avviker_raknas_var_for_sig_med_summan()
        {
            var charges = new[] { Charge(1, 800, sent: true), Charge(2, 800, paid: true), Charge(3, 800, sent: true) };
            var proposal = new Dictionary<int, ClubFeeProposalRow> { [1] = Row(1), [2] = Row(2), [3] = Row(3) };
            var calcs = new Dictionary<int, FeeCalculation> { [1] = Calc(1100), [2] = Calc(1100), [3] = Calc(800) };
            var c = ClubFeeConsequences.Compute(7, charges, proposal, calcs);
            Assert.Equal((7, 1, 1, 600m), (c.UnsentCleared, c.SentDiverging, c.PaidDiverging, c.DivergingSum));
        }

        [Fact]
        public void Oskickad_ingar_i_familjen_och_kretsavgift_raknas_inte()
        {
            var region = Charge(4, 500, sent: true); region.IssuerType = MembershipFeeIssuer.Region;
            var charges = new[] { Charge(1, 800), Charge(2, 0, sent: true, covered: true), region };
            var proposal = new Dictionary<int, ClubFeeProposalRow> { [1] = Row(1), [2] = Row(2, ClubFeeKind.Covered), [4] = Row(4) };
            var calcs = new Dictionary<int, FeeCalculation> { [1] = Calc(1100), [2] = Calc(0), [4] = Calc(900) };
            Assert.Equal(ClubFeeConsequences.None with { UnsentCleared = 0 }, ClubFeeConsequences.Compute(0, charges, proposal, calcs));
        }

        [Fact]
        public void Lika_belopp_ar_ingen_avvikelse_och_negativ_skillnad_raknas_med_tecken()
        {
            var charges = new[] { Charge(1, 800, sent: true), Charge(2, 900, sent: true) };
            var proposal = new Dictionary<int, ClubFeeProposalRow> { [1] = Row(1), [2] = Row(2) };
            var calcs = new Dictionary<int, FeeCalculation> { [1] = Calc(800), [2] = Calc(600) };
            var c = ClubFeeConsequences.Compute(0, charges, proposal, calcs);
            Assert.Equal((1, -300m), (c.SentDiverging, c.DivergingSum));
        }

        private static string Src(string rel, [CallerFilePath] string thisFile = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "HpskSite", rel)));

        [Fact]
        public void Varje_andpunkt_som_rensar_oskickade_avgifter_tar_installningslaset()
        {
            var src = Src(Path.Combine("Controllers", "MembershipFeeAdminController.cs"));
            var starts = new List<int>();
            for (var i = src.IndexOf("public async Task<IActionResult> ", StringComparison.Ordinal); i >= 0;
                 i = src.IndexOf("public async Task<IActionResult> ", i + 10, StringComparison.Ordinal)) starts.Add(i);
            var bad = new List<string>();
            for (var n = 0; n < starts.Count; n++)
            {
                var body = src.Substring(starts[n], (n + 1 < starts.Count ? starts[n + 1] : src.Length) - starts[n]);
                if (!body.Contains("ClearUnsentClubCharges(", StringComparison.Ordinal)) continue;
                if (!body.Contains("TryLockSettings(", StringComparison.Ordinal))
                    bad.Add(body.Substring(0, Math.Min(80, body.Length)).Split('\n')[0].Trim());
            }
            Assert.True(bad.Count == 0, "Ändpunkter som rensar avgifter utan låset: " + string.Join(" | ", bad));
        }

        [Fact]
        public void Utskicket_haller_samma_las()
        {
            var src = Src(Path.Combine("Controllers", "MembershipFeeAdminController.cs"));
            var i = src.IndexOf("public async Task<IActionResult> SendPaymentRequests(", StringComparison.Ordinal);
            Assert.True(i >= 0);
            Assert.Contains("TryLockSettings(", src.Substring(i, 1200));
        }

        [Fact]
        public void SaveCategory_harleder_aret_ur_raden_och_prover_dubblett()
        {
            var src = Src(Path.Combine("Controllers", "MembershipFeeAdminController.cs"));
            var i = src.IndexOf("public async Task<IActionResult> SaveCategory(", StringComparison.Ordinal);
            var body = src.Substring(i, 3500);
            Assert.Contains("GetCategoryYear(id)", body);
            Assert.Contains("har redan en avgift", body);
        }
    }
}
