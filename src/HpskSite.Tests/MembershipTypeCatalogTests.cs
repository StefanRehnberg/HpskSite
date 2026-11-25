using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbens namn på medlemstyper: lat tabell, fast nyckel, importen känner bara dagens namn.</summary>
    public class MembershipTypeCatalogTests
    {
        private static ClubMembershipType Row(int id, string key, string name, bool active = true)
            => new() { Id = id, ClubId = 1, TypeKey = key, Name = name, IsActive = active };

        [Fact]
        public void Utan_rader_ar_alla_sju_standardtyper_aktiva_med_standardnamn()
        {
            var e = MembershipTypeNames.Effective(Array.Empty<ClubMembershipType>());
            Assert.Equal(MembershipTypeKeys.Standard, e.Select(x => x.Key).ToList());
            Assert.All(e, x => { Assert.True(x.IsActive); Assert.False(x.IsCustom); Assert.Equal(x.Key, x.Name); });
        }

        [Fact]
        public void Omdopt_typ_visas_med_klubbens_namn_men_behaller_nyckeln()
        {
            var e = MembershipTypeNames.Effective(new[] { Row(1, "Senior", "Fullbetalande") });
            Assert.Equal("Fullbetalande", MembershipTypeNames.LabelFor(e, "Senior"));
            Assert.Equal("Fullbetalande", MembershipTypeNames.LabelFor(e, "senior"));
            Assert.Equal("Senior", e.First(x => x.Name == "Fullbetalande").Key);
        }

        [Fact]
        public void Dold_typ_ar_inte_aktiv_och_egen_typ_foljer_efter_standardtyperna()
        {
            var e = MembershipTypeNames.Effective(new[] { Row(1, "Junior", "Ungdom", false), Row(2, "x-abc", "Nybörjare") });
            Assert.False(e.First(x => x.Key == "Junior").IsActive);
            var custom = e.Last();
            Assert.True(custom.IsCustom);
            Assert.Equal("x-abc", custom.Key);
            Assert.Equal(8, e.Count);
        }

        [Fact]
        public void Okand_text_visas_som_den_ar_och_tom_ger_tomt()
        {
            var e = MembershipTypeNames.Effective(Array.Empty<ClubMembershipType>());
            Assert.Equal("Veteran", MembershipTypeNames.LabelFor(e, " Veteran "));
            Assert.Equal("", MembershipTypeNames.LabelFor(e, null));
        }

        [Fact]
        public void Importen_hittar_nuvarande_namn_men_inte_det_gamla_standardnamnet()
        {
            var e = MembershipTypeNames.Effective(new[] { Row(1, "Senior", "Fullbetalande") });
            Assert.Equal("Senior", MembershipTypeNames.ResolveKey(e, "fullbetalande"));
            Assert.Null(MembershipTypeNames.ResolveKey(e, "Senior"));
            Assert.Equal("Junior", MembershipTypeNames.ResolveKey(e, "JUNIOR"));
            Assert.Null(MembershipTypeNames.ResolveKey(e, ""));
            Assert.Null(MembershipTypeNames.ResolveKey(e, "Veteran"));
        }

        [Fact]
        public void Tom_namnrad_faller_tillbaka_pa_nyckeln()
        {
            var e = MembershipTypeNames.Effective(new[] { Row(1, "Pensionär", "  ") });
            Assert.Equal("Pensionär", MembershipTypeNames.LabelFor(e, "Pensionär"));
        }
    }
}
