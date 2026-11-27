using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    public class ClubFeeSettingsRulesTests
    {
        private static readonly string[] Keys = { "Senior", "Pensionär", "Junior", "Familj", "x-1" };
        private static readonly HashSet<int> Opts = new() { 5, 6 };

        private static string? Part(string? label = "Förbundsavgift", decimal amount = 100m,
            string? cond = MembershipFeePartCondition.Always, int? opt = null, string[]? types = null)
            => ClubFeeSettingsRules.ValidatePart(label, amount, cond, opt, types ?? Array.Empty<string>(), Opts, Keys);

        [Fact] public void Giltigt_tillagg_och_avdrag_godkanns()
        {
            Assert.Null(Part());
            Assert.Null(Part(amount: -50m, cond: MembershipFeePartCondition.NotPrimaryClub));
            Assert.Null(Part(cond: MembershipFeePartCondition.Option, opt: 5, types: new[] { "Senior" }));
        }

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("   ")]
        public void Namn_krävs(string? label) => Assert.NotNull(Part(label: label));

        private static MembershipFeePart P(string label, decimal amount = 100m, string cond = MembershipFeePartCondition.Always, int? opt = null)
            => new() { Label = label, Amount = amount, Condition = cond, OptionId = opt };

        [Fact] public void Samma_tillagg_ar_samma_namn_villkor_tillval_och_typurval_oavsett_belopp_och_skiftlage()
        {
            Assert.True(ClubFeeSettingsRules.IsSamePart(P("Förbundsavgift", 100m), new[] { "Senior" }, P(" förbundsavgift ", 150m), new[] { "senior" }));
            Assert.True(ClubFeeSettingsRules.IsSamePart(P("X"), new[] { "Senior", "Junior" }, P("X"), new[] { "Junior", "Senior" }));
        }

        [Fact] public void Olika_namn_villkor_tillval_eller_typurval_ar_inte_samma_tillagg()
        {
            Assert.False(ClubFeeSettingsRules.IsSamePart(P("A"), Array.Empty<string>(), P("B"), Array.Empty<string>()));
            Assert.False(ClubFeeSettingsRules.IsSamePart(P("A"), Array.Empty<string>(), P("A", cond: MembershipFeePartCondition.PrimaryClub), Array.Empty<string>()));
            Assert.False(ClubFeeSettingsRules.IsSamePart(P("A", cond: MembershipFeePartCondition.Option, opt: 5), Array.Empty<string>(), P("A", cond: MembershipFeePartCondition.Option, opt: 6), Array.Empty<string>()));
            Assert.False(ClubFeeSettingsRules.IsSamePart(P("A"), new[] { "Senior" }, P("A"), Array.Empty<string>()));
        }

        [Fact] public void Typnamn_unikt_utan_hansyn_till_skiftlage_och_accent()
        {
            Assert.NotNull(ClubFeeSettingsRules.ValidateTypeName("pensionar", new[] { "Pensionär" }));
            Assert.NotNull(ClubFeeSettingsRules.ValidateTypeName("  SENIOR ", new[] { "Senior" }));
            Assert.Null(ClubFeeSettingsRules.ValidateTypeName("Veteran", new[] { "Senior", "Pensionär" }));
            Assert.NotNull(ClubFeeSettingsRules.ValidateTypeName("", new string[0]));
            Assert.NotNull(ClubFeeSettingsRules.ValidateTypeName(new string('x', 51), new string[0]));
        }

        [Fact] public void Familj_kan_varken_doepas_om_eller_doljas()
        {
            Assert.NotNull(ClubFeeSettingsRules.ValidateTypeEdit("Familj", "Hushåll", "Familj", true));
            Assert.NotNull(ClubFeeSettingsRules.ValidateTypeEdit("Familj", "Familj", "Familj", false));
            Assert.Null(ClubFeeSettingsRules.ValidateTypeEdit("Familj", "Familj", "Familj", true));
            Assert.Null(ClubFeeSettingsRules.ValidateTypeEdit("Senior", "Veteran", "Senior", false));
        }

        [Fact] public void Belopp_noll_avvisas() => Assert.NotNull(Part(amount: 0m));
        [Fact] public void Belopp_med_tre_decimaler_avvisas() => Assert.NotNull(Part(amount: 1.234m));

        [Fact] public void Option_kraver_klubbens_tillval()
        {
            Assert.NotNull(Part(cond: MembershipFeePartCondition.Option, opt: null));
            Assert.NotNull(Part(cond: MembershipFeePartCondition.Option, opt: 99)); // annan klubbs tillval
        }

        [Fact] public void Tillval_utan_option_villkor_avvisas() =>
            Assert.NotNull(Part(cond: MembershipFeePartCondition.Always, opt: 5));

        [Fact] public void Okant_villkor_avvisas() => Assert.NotNull(Part(cond: "Whatever"));
        [Fact] public void Okand_medlemstyp_avvisas() => Assert.NotNull(Part(types: new[] { "Veteran" }));

        [Fact] public void Tillvalsnamn_ska_vara_unikt_skiftlagesokansligt()
        {
            Assert.Null(ClubFeeSettingsRules.ValidateOptionName("Sportskytte", new[] { "Aktiv i SPSF" }));
            Assert.NotNull(ClubFeeSettingsRules.ValidateOptionName("aktiv i spsf", new[] { "Aktiv i SPSF" }));
            Assert.NotNull(ClubFeeSettingsRules.ValidateOptionName(" ", Array.Empty<string>()));
        }

        [Fact] public void Typurval_normaliseras_till_kanonisk_stavning_utan_dubbletter()
        {
            var r = ClubFeeSettingsRules.NormalizeTypeKeys(new[] { "senior", "SENIOR", "x-1", "okänd" }, Keys);
            Assert.Equal(new[] { "Senior", "x-1" }, r);
        }
    }
}
