using HpskSite.CompetitionTypes.Common;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    public class ClassRegistrationRuleTests
    {
        private static string? C(bool dual, params string[] classes) => ClassRegistrationRule.Conflict(classes, dual);

        [Fact] public void Olika_vapengrupper_gar_ihop() => Assert.Null(C(false, "A1", "B2", "C1", "R1", "A_opt_1", "M2"));

        [Fact] public void Samma_klass_tva_ganger_vagras_aven_med_dubbel_c() =>
            Assert.Contains("en gång", C(true, "C2", "C2"));

        [Fact] public void Samma_klass_i_id_och_namnform_ar_samma_klass() =>
            Assert.NotNull(C(true, "C_Vet_Y", "C Vet Y"));

        [Fact] public void Tva_klasser_i_samma_vapengrupp_vagras() =>
            Assert.Contains("per vapengrupp", C(true, "A1", "A2"));

        [Fact] public void A_och_A_Opt_ar_olika_vapengrupper() => Assert.Null(C(false, "A1", "A_opt_1"));

        [Fact] public void Tva_C_klasser_vagras_utan_installningen() =>
            Assert.Contains("bara en C-klass", C(false, "C2", "C_Vet_Y"));

        [Fact] public void Tva_C_klasser_ur_olika_kategorier_tillats_med_installningen() =>
            Assert.Null(C(true, "C2", "C Vet Y"));

        [Fact] public void Tva_C_klasser_ur_samma_kategori_vagras_aven_med_installningen() =>
            Assert.Contains("olika kategorier", C(true, "C1", "C2"));

        [Fact] public void Veteran_yngre_och_aldre_ar_samma_kategori() =>
            Assert.NotNull(C(true, "C_Vet_Y", "C_Vet_A"));

        [Fact] public void Tre_C_klasser_vagras_alltid() =>
            Assert.Contains("Högst två", C(true, "C2", "C2_Dam", "C_Jun"));

        [Fact] public void L_foljer_samma_regel_som_C()
        {
            Assert.NotNull(C(false, "L2", "L_Vet_Y"));
            Assert.Null(C(true, "L2", "L_Vet_Y"));
        }

        [Fact] public void Okanda_klasser_raknas_bara_mot_sig_sjalva()
        {
            Assert.Null(C(false, "C-H 50", "A-H 50"));   // Springskyttes sammansatta id:n
            Assert.NotNull(C(false, "C-H 50", "C-H 50"));
        }

        [Fact] public void Formularfaltet_skrivs_till_doctypens_egenskap() =>
            Assert.Equal(ClassRegistrationRule.PropertyAlias, CompetitionFieldCatalog.PropertyAliasFor("allowDualCClass"));

        [Fact] public void Ovriga_falt_skrivs_pa_sitt_eget_namn() =>
            Assert.Equal("showLiveResults", CompetitionFieldCatalog.PropertyAliasFor("showLiveResults"));
    }
}
