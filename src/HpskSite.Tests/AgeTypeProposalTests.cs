using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Förslagslistan (steg 3b): vem som föreslås byta, vem som aldrig rörs, och varför en medlem inte går att placera.</summary>
    public class AgeTypeProposalTests
    {
        private const int Year = 2027;
        private static readonly AgeStep[] Steps = { new("Junior", 0), new("Senior", 21), new("Veteran", 60) };
        private static readonly string[] Active = { "Junior", "Senior", "Veteran", "Pensionär", "Familj", "Hedersmedlem", "Ständig", "Stödjande" };

        private static BirthYearResult Born(int year) => new(year, BirthYearStatus.Ok);

        private static AgeMemberInput M(int id, string? type, BirthYearResult by, bool household = false, bool kept = false, bool active = true)
            => new(id, type, active, household, kept, by);

        private static List<AgeProposalRow> Build(params AgeMemberInput[] ms) => AgeTypeProposal.Build(Year, ms, Steps, Active);
        private static AgeProposalRow Only(List<AgeProposalRow> rows) => Assert.Single(rows);

        [Fact]
        public void Junior_som_fyller_21_foreslas_Senior()
        {
            var r = Only(Build(M(1, "Junior", Born(2006))));        // 2027 − 2006 = 21
            Assert.Equal(AgeProposalKind.Change, r.Kind);
            Assert.Equal("Junior", r.FromKey);
            Assert.Equal("Senior", r.ToKey);
            Assert.Equal(21, r.Age);
        }

        [Fact]
        public void Junior_som_fyller_20_byter_inte()
            => Assert.Empty(Build(M(1, "Junior", Born(2007))));    // 20 = junior "till och med"

        [Fact]
        public void Senior_som_fyller_60_foreslas_Veteran()
        {
            var r = Only(Build(M(1, "Senior", Born(1967))));
            Assert.Equal("Veteran", r.ToKey);
        }

        [Fact]
        public void Redan_ratt_typ_ger_ingen_rad()
            => Assert.Empty(Build(M(1, "senior", Born(1990))));    // skiftläge ignoreras

        [Fact]
        public void Typlos_med_kant_alder_foreslas_en_typ_som_Untyped()
        {
            var r = Only(Build(M(1, null, Born(1990))));
            Assert.Equal(AgeProposalKind.Untyped, r.Kind);
            Assert.Equal("Senior", r.ToKey);
            Assert.Null(r.FromKey);
        }

        [Theory]
        [InlineData("Familj")]
        public void Familj_och_hushall_rors_aldrig_men_listas(string type)
        {
            var r = Only(Build(M(1, type, Born(1990))));
            Assert.Equal(AgeProposalKind.Household, r.Kind);
            Assert.Null(r.ToKey);
        }

        [Fact]
        public void Hushallsmedlem_med_annan_typ_rors_inte()
        {
            var r = Only(Build(M(1, "Senior", Born(1990), household: true)));
            Assert.Equal(AgeProposalKind.Household, r.Kind);
            Assert.Null(r.ToKey);
        }

        [Theory]
        [InlineData("Hedersmedlem")]
        [InlineData("Ständig")]
        [InlineData("Stödjande")]
        [InlineData("x-student")]            // egen typ utanför stegen
        [InlineData("Pensionär")]            // standardtyp utan gräns
        public void Typ_utanfor_stegen_rors_aldrig(string type)
            => Assert.Empty(Build(M(1, type, Born(1950))));

        [Fact]
        public void Utrada_och_avlidna_hoppas_over()
            => Assert.Empty(Build(M(1, "Junior", Born(2000), active: false)));

        [Fact]
        public void Kassorens_val_att_behalla_ger_Kept_inte_Change()
        {
            var r = Only(Build(M(1, "Junior", Born(2000), kept: true)));
            Assert.Equal(AgeProposalKind.Kept, r.Kind);
            Assert.Null(r.ToKey);
        }

        [Theory]
        [InlineData(BirthYearStatus.Missing)]
        [InlineData(BirthYearStatus.Invalid)]
        [InlineData(BirthYearStatus.Conflict)]
        [InlineData(BirthYearStatus.Implausible)]
        [InlineData(BirthYearStatus.Future)]
        public void Utan_anvandbart_fodelsear_kan_inte_placeras(BirthYearStatus status)
        {
            var r = Only(Build(M(1, "Senior", new BirthYearResult(null, status))));
            Assert.Equal(AgeProposalKind.CannotPlace, r.Kind);
            Assert.Equal(status, r.BirthStatus);
            Assert.False(string.IsNullOrEmpty(r.Reason));
            Assert.Null(r.ToKey);
        }

        [Fact]
        public void Typlos_utan_fodelsear_kan_ocksa_inte_placeras()
            => Assert.Equal(AgeProposalKind.CannotPlace, Only(Build(M(1, null, new BirthYearResult(null, BirthYearStatus.Missing)))).Kind);

        [Fact]
        public void Osaker_alder_foreslar_men_markerar_det()
        {
            var r = Only(Build(M(1, null, new BirthYearResult(1990, BirthYearStatus.Uncertain))));
            Assert.Equal(AgeProposalKind.Untyped, r.Kind);
            Assert.Equal(BirthYearStatus.Uncertain, r.BirthStatus);
        }

        [Fact]
        public void Fodd_efter_avgiftsaret_ger_negativ_alder_och_kan_inte_placeras()
        {
            var r = Only(Build(M(1, null, Born(2028))));
            Assert.Equal(AgeProposalKind.CannotPlace, r.Kind);
        }

        [Fact]
        public void Dold_maltyp_ar_aldrig_mal()
        {
            var activeWithoutVeteran = Active.Where(k => k != "Veteran").ToArray();
            var rows = AgeTypeProposal.Build(Year, new[] { M(1, "Senior", Born(1960)) }, Steps, activeWithoutVeteran);
            var r = Only(rows);
            Assert.Equal(AgeProposalKind.CannotPlace, r.Kind);
            Assert.Null(r.ToKey);
        }

        [Fact]
        public void Utan_stege_ar_regeln_av_och_listan_tom()
            => Assert.Empty(AgeTypeProposal.Build(Year, new[] { M(1, "Junior", Born(1990)) }, Array.Empty<AgeStep>(), Active));

        [Fact]
        public void Aldersgrans_exakt_byter_och_ett_ar_yngre_byter_inte()
        {
            Assert.Single(Build(M(1, "Senior", Born(1967))));      // 60
            Assert.Empty(Build(M(2, "Senior", Born(1968))));       // 59
        }

        [Fact]
        public void Forslaget_ar_deterministiskt_oavsett_ordning()
        {
            var a = Build(M(1, "Junior", Born(2000)), M(2, "Senior", Born(1960)));
            var b = Build(M(2, "Senior", Born(1960)), M(1, "Junior", Born(2000)));
            Assert.Equal(a.OrderBy(x => x.MemberId).Select(x => (x.MemberId, x.ToKey)), b.OrderBy(x => x.MemberId).Select(x => (x.MemberId, x.ToKey)));
        }
    }
}
