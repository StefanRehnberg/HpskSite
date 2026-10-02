using FluentAssertions;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    public class ResultReviewRulesTests
    {
        private const string Data = "{\"CompetitionId\":1,\"UpdatedAt\":\"2026-09-21T09:06:26+02:00\",\"IsOfficial\":true,\"ClassGroups\":[{\"ClassName\":\"C1\",\"Shooters\":[{\"MemberId\":5,\"Results\":[{\"SeriesNumber\":1,\"Shots\":\"[10,9]\",\"EnteredAt\":\"2026-09-21T09:06:14Z\",\"LastModified\":\"2026-09-21T09:06:14Z\",\"EnteredBy\":0}]}]}]}";

        [Fact]
        public void Kontrollsumma_PaverkasInteAvOmrakningensTidsstampel()
        {
            var later = Data.Replace("2026-09-21T09:06:26+02:00", "2026-10-02T12:00:00+02:00");
            ResultReviewRules.Checksum(later).Should().Be(ResultReviewRules.Checksum(Data));
        }

        [Fact]
        public void Kontrollsumma_PaverkasInteAvPubliceringslaget() =>
            ResultReviewRules.Checksum(Data.Replace("\"IsOfficial\":true", "\"IsOfficial\":false")).Should().Be(ResultReviewRules.Checksum(Data));

        [Fact]
        public void Kontrollsumma_PaverkasInteAvRadensInmatningsstampel() =>
            ResultReviewRules.Checksum(Data.Replace("\"LastModified\":\"2026-09-21T09:06:14Z\"", "\"LastModified\":\"2026-10-01T10:00:00Z\""))
                .Should().Be(ResultReviewRules.Checksum(Data));

        [Fact]
        public void Kontrollsumma_AndrasNarEttSkottAndras() =>
            ResultReviewRules.Checksum(Data.Replace("[10,9]", "[10,8]")).Should().NotBe(ResultReviewRules.Checksum(Data));

        [Fact]
        public void Kontrollsumma_AndrasNarEnSkyttTillkommer() =>
            ResultReviewRules.Checksum(Data.Replace("\"MemberId\":5", "\"MemberId\":6")).Should().NotBe(ResultReviewRules.Checksum(Data));

        [Fact]
        public void Kontrollsumma_OgiltigJson_HashasRatt()
        {
            ResultReviewRules.Checksum("inte json").Should().HaveLength(32);
            ResultReviewRules.Checksum(null).Should().Be(ResultReviewRules.Checksum(""));
        }

        [Fact]
        public void Erbjuds_BaraNarTavlingenGerStandardmedaljer()
        {
            ResultReviewRules.Offered(true, false).Should().BeTrue();
            ResultReviewRules.Offered(false, false).Should().BeFalse();
            ResultReviewRules.Offered(true, true).Should().BeFalse();
        }

        [Fact]
        public void SistaDagen_Ar14DagarEfterTavlingen() =>
            ResultReviewRules.SendDeadline(new DateTime(2026, 5, 1, 15, 0, 0)).Should().Be(new DateTime(2026, 5, 15));

        private static CompetitionResultReview R(bool two = false, string status = ResultReviewStatus.Inskickad)
            => new() { RequiresTwo = two, Status = status };

        [Fact]
        public void EnGranskare_GodkannerDirekt() =>
            ResultReviewRules.Approve(R(), 10, "A").Should().Be(ResultReviewRules.ApproveOutcome.Approved);

        [Fact]
        public void TvaGranskare_ForstaBlirEttAvTva() =>
            ResultReviewRules.Approve(R(two: true), 10, "A").Should().Be(ResultReviewRules.ApproveOutcome.FirstOfTwo);

        [Fact]
        public void TvaGranskare_SammaPersonIgen_Vagras()
        {
            var r = R(two: true); r.FirstApproverMemberId = 10; r.FirstApproverName = "A";
            ResultReviewRules.Approve(r, 10, "A").Should().Be(ResultReviewRules.ApproveOutcome.SameReviewerTwice);
            ResultReviewRules.Approve(r, 11, "B").Should().Be(ResultReviewRules.ApproveOutcome.Approved);
        }

        [Fact]
        public void TvaGranskare_ViaLank_JamforsPaNamn()
        {
            var r = R(two: true); r.FirstApproverName = "Kalle Kretsson";
            ResultReviewRules.Approve(r, 0, " kalle kretsson ").Should().Be(ResultReviewRules.ApproveOutcome.SameReviewerTwice);
            ResultReviewRules.Approve(r, 0, "Lisa").Should().Be(ResultReviewRules.ApproveOutcome.Approved);
        }

        [Fact]
        public void RedanGodkand_KanInteGodkannasIgen() =>
            ResultReviewRules.Approve(R(status: ResultReviewStatus.Godkand), 10, "A").Should().Be(ResultReviewRules.ApproveOutcome.NotPending);

        [Fact]
        public void MedaljernaVantar_BaraMedGrind()
        {
            new CompetitionResultReview { Gated = false, Status = ResultReviewStatus.Inskickad }.MedalsPending.Should().BeFalse();
            new CompetitionResultReview { Gated = true, Status = ResultReviewStatus.Inskickad }.MedalsPending.Should().BeTrue();
            new CompetitionResultReview { Gated = true, Status = ResultReviewStatus.Godkand }.MedalsPending.Should().BeFalse();
        }

        // ── Påminnelserna ────────────────────────────────────────────────────────────────

        private static readonly DateTime End = new(2026, 9, 5);
        private static List<ResultReviewReminders.Due> Arr(int daysAfter) =>
            ResultReviewReminders.Compute(new[] { new ResultReviewReminders.Candidate(77, 3788, End) }, Array.Empty<CompetitionResultReview>(), End.AddDays(daysAfter));

        [Fact]
        public void Arrangoren_IngenPaminnelseForeTreDagar() => Arr(2).Should().BeEmpty();

        [Fact]
        public void Arrangoren_TreDagar_ForstaSteget() =>
            Arr(3).Should().ContainSingle().Which.Key.Should().Be("rr-arr3-77");

        [Fact]
        public void Arrangoren_TioDagar_AndraSteget_InteBada()
        {
            var due = Arr(12);
            due.Should().ContainSingle();
            due[0].Key.Should().Be("rr-arr10-77");
        }

        [Fact]
        public void Arrangoren_GamlaTavlingarPaminnsInte() => Arr(31).Should().BeEmpty();

        [Fact]
        public void Kretsen_EfterFemDagar_NyckelnBarDagen()
        {
            var r = R(); r.Id = 9; r.CompetitionId = 77; r.RegionId = 3788; r.UpdatedAt = new DateTime(2026, 9, 10, 14, 0, 0);
            ResultReviewReminders.Compute(Array.Empty<ResultReviewReminders.Candidate>(), new[] { r }, new DateTime(2026, 9, 14)).Should().BeEmpty();
            ResultReviewReminders.Compute(Array.Empty<ResultReviewReminders.Candidate>(), new[] { r }, new DateTime(2026, 9, 15))
                .Should().ContainSingle().Which.Key.Should().Be("rr-krets5-9-20260910");
        }

        [Fact]
        public void Kretsen_AvgjordLista_PaminnsInte()
        {
            var r = R(status: ResultReviewStatus.Godkand); r.UpdatedAt = new DateTime(2026, 9, 1);
            ResultReviewReminders.Compute(Array.Empty<ResultReviewReminders.Candidate>(), new[] { r }, new DateTime(2026, 9, 20)).Should().BeEmpty();
        }
    }
}
