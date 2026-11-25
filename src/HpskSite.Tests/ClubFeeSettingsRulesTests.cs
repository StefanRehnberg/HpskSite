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
