using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Åldersstegen per medlemstyp (steg 3b): vilken typ som gäller, åldersgrupper, vad en medlem får välja, validering.</summary>
    public class AgeTypeRulesTests
    {
        // Junior 0, Senior 21, Veteran 60 (SHB:s juniorgräns "till och med 20" = Senior från 21).
        private static readonly AgeStep[] Steps = { new("Junior", 0), new("Senior", 21), new("Veteran", 60) };

        [Theory]
        [InlineData(0, "Junior")]
        [InlineData(20, "Junior")]       // gräns − 1 byter inte
        [InlineData(21, "Senior")]       // gräns exakt byter
        [InlineData(59, "Senior")]
        [InlineData(60, "Veteran")]
        [InlineData(120, "Veteran")]
        public void Typen_med_hogsta_graens_under_aldern_galler(int age, string expected)
            => Assert.Equal(expected, AgeTypeRules.TypeFor(age, Steps));

        [Fact]
        public void Ordningen_i_listan_spelar_ingen_roll()
        {
            var shuffled = new AgeStep[] { new("Veteran", 60), new("Junior", 0), new("Senior", 21) };
            Assert.Equal("Senior", AgeTypeRules.TypeFor(30, shuffled));
        }

        [Fact]
        public void Under_lagsta_graens_ger_ingen_typ()
            => Assert.Null(AgeTypeRules.TypeFor(5, new AgeStep[] { new("Senior", 18) }));

        [Fact]
        public void Alder_ar_kalenderar_avgiftsaret_minus_fodelsearet()
        {
            Assert.Equal(60, AgeTypeRules.Age(2027, 1967));     // fyller 60 under 2027: 60 från 1 januari
            Assert.Equal(0, AgeTypeRules.Age(2026, 2026));
            Assert.Equal(-1, AgeTypeRules.Age(2026, 2027));
        }

        [Theory]
        [InlineData("Junior", 0, 21)]
        [InlineData("Senior", 21, 60)]
        [InlineData("veteran", 60, null)]    // skiftlägesokänsligt, översta gruppen saknar övre gräns
        public void Aldersgrupp_ar_fran_gransen_till_nasta(string key, int from, int? to)
            => Assert.Equal((from, to), AgeTypeRules.GroupOf(key, Steps));

        [Fact]
        public void Typ_utan_graens_har_ingen_grupp()
            => Assert.Null(AgeTypeRules.GroupOf("Stödjande", Steps));

        [Theory]
        [InlineData("Junior", 15, true)]
        [InlineData("Junior", 21, false)]    // för gammal för Junior
        [InlineData("Senior", 15, false)]    // för ung för Senior
        [InlineData("Senior", 40, true)]
        [InlineData("Veteran", 59, false)]
        [InlineData("Veteran", 61, true)]
        [InlineData("veteran", 59, false)]
        public void Medlemmen_far_bara_valja_inom_sin_aldersgrupp(string type, int age, bool allowed)
            => Assert.Equal(allowed, AgeTypeRules.IsAllowedForAge(type, age, Steps));

        [Theory]
        [InlineData("Stödjande", 15)]        // typ utan gräns påverkas inte av åldern
        [InlineData("x-student", 70)]
        [InlineData("Hedersmedlem", 5)]
        public void Typ_utan_graens_ar_alltid_tillaten(string type, int age)
            => Assert.True(AgeTypeRules.IsAllowedForAge(type, age, Steps));

        [Fact]
        public void Okand_alder_eller_ingen_stege_ger_dagens_beteende()
        {
            Assert.True(AgeTypeRules.IsAllowedForAge("Junior", null, Steps));
            Assert.True(AgeTypeRules.IsAllowedForAge("Junior", 50, Array.Empty<AgeStep>()));
        }

        private static readonly string[] Valid = { "Senior", "Pensionär", "Junior", "Veteran", "Familj", "Hedersmedlem", "Ständig", "Stödjande", "x-student" };

        [Fact]
        public void Tom_stege_ar_godkand_regeln_ar_av()
            => Assert.Null(AgeTypeRules.ValidateSteps(Array.Empty<AgeStep>(), Valid));

        [Fact]
        public void Giltig_stege_godkanns()
            => Assert.Null(AgeTypeRules.ValidateSteps(Steps, Valid));

        [Theory]
        [InlineData("Familj")]
        [InlineData("Hedersmedlem")]
        [InlineData("Ständig")]
        [InlineData("Stödjande")]
        public void Typer_som_aldrig_aldersstyrs_nekas(string key)
        {
            var steps = new AgeStep[] { new("Junior", 0), new(key, 30) };
            Assert.NotNull(AgeTypeRules.ValidateSteps(steps, Valid));
        }

        [Fact]
        public void Okand_typ_nekas()
            => Assert.NotNull(AgeTypeRules.ValidateSteps(new AgeStep[] { new("Junior", 0), new("finns-inte", 20) }, Valid));

        [Fact]
        public void Samma_typ_tva_ganger_nekas()
            => Assert.NotNull(AgeTypeRules.ValidateSteps(new AgeStep[] { new("Junior", 0), new("senior", 21), new("Senior", 30) }, Valid));

        [Fact]
        public void Tva_typer_pa_samma_alder_nekas()
            => Assert.NotNull(AgeTypeRules.ValidateSteps(new AgeStep[] { new("Junior", 0), new("Senior", 21), new("Veteran", 21) }, Valid));

        [Theory]
        [InlineData(-1)]
        [InlineData(121)]
        public void Graens_utanfor_0_till_120_nekas(int bad)
            => Assert.NotNull(AgeTypeRules.ValidateSteps(new AgeStep[] { new("Junior", 0), new("Senior", bad) }, Valid));

        [Fact]
        public void Lagsta_graens_maste_vara_0()
            => Assert.NotNull(AgeTypeRules.ValidateSteps(new AgeStep[] { new("Senior", 18), new("Veteran", 60) }, Valid));

        [Fact]
        public void Egen_typ_far_ha_graens()
            => Assert.Null(AgeTypeRules.ValidateSteps(new AgeStep[] { new("x-student", 0), new("Senior", 26) }, Valid));
    }
}
