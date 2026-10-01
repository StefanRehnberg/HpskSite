using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    public class CompetitionLevelTests
    {
        // ── Förslaget ───────────────────────────────────────────────────────────

        [Fact] public void Klubbintern_tavling_foreslas_som_bara_egna_medlemmar() =>
            Assert.Equal(CompetitionLevel.Forening, CompetitionLevel.Suggest("", isClubOnly: true, hasClub: true, hasRegion: false));

        [Fact] public void Klubbintern_vinner_over_mastarskapet() =>
            Assert.Equal(CompetitionLevel.Forening,
                CompetitionLevel.Suggest(CompetitionScopeHelper.Kretsmasterskap, isClubOnly: true, hasClub: true, hasRegion: false));

        [Fact] public void Kretsmasterskap_foreslas_som_krets_aven_hos_en_klubb() =>
            Assert.Equal(CompetitionLevel.Krets,
                CompetitionLevel.Suggest(CompetitionScopeHelper.Kretsmasterskap, isClubOnly: false, hasClub: true, hasRegion: false));

        [Fact] public void Landsdelsmasterskap_foreslas_som_landsdel() =>
            Assert.Equal(CompetitionLevel.Landsdel,
                CompetitionLevel.Suggest(CompetitionScopeHelper.Landsdelsmasterskap, false, false, true));

        [Fact] public void SM_foreslas_som_rikstavling() =>
            Assert.Equal(CompetitionLevel.Riks,
                CompetitionLevel.Suggest(CompetitionScopeHelper.SvensktMasterskap, false, false, true));

        [Fact] public void Klubbmasterskap_hos_en_klubb_ar_bara_egna_medlemmar() =>
            Assert.Equal(CompetitionLevel.Forening,
                CompetitionLevel.Suggest(CompetitionScopeHelper.Klubbmasterskap, false, true, false));

        [Fact] public void Klubbmasterskap_utan_klubb_kan_inte_vara_klubbinternt() =>
            Assert.Equal(CompetitionLevel.Krets,
                CompetitionLevel.Suggest(CompetitionScopeHelper.Klubbmasterskap, false, false, true));

        [Fact] public void Mastarskapet_i_jsonform_kanns_igen() =>
            Assert.Equal(CompetitionLevel.Krets,
                CompetitionLevel.Suggest("[\"Kretsmästerskap\"]", false, true, false));

        [Fact] public void Oppen_klubbtavling_utan_mastarskap_foreslas_som_oppen_forening() =>
            Assert.Equal(CompetitionLevel.ForeningOppen, CompetitionLevel.Suggest("", false, true, false));

        [Fact] public void Kretsvard_utan_mastarskap_foreslas_som_krets() =>
            Assert.Equal(CompetitionLevel.Krets, CompetitionLevel.Suggest(null, false, false, true));

        [Fact] public void Ingen_vard_foreslas_som_nationell() =>
            Assert.Equal(CompetitionLevel.Nationell, CompetitionLevel.Suggest(null, false, false, false));

        [Fact] public void Klubbintern_utan_klubb_ar_inte_klubbintern() =>
            Assert.Equal(CompetitionLevel.Nationell, CompetitionLevel.Suggest(null, isClubOnly: true, hasClub: false, hasRegion: false));

        // ── Normalisering ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("krets", "Krets")]
        [InlineData(" ForeningOppen ", "ForeningOppen")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("Kretsmästerskap", "")]
        public void Normalize_ger_kanoniskt_varde_eller_tomt(string? input, string expected) =>
            Assert.Equal(expected, CompetitionLevel.Normalize(input));

        [Fact] public void Okant_varde_ar_ogiltigt() => Assert.False(CompetitionLevel.IsValid("Distrikt"));
        [Fact] public void Tomt_ar_giltigt_ej_bekraftat() => Assert.True(CompetitionLevel.IsValid(""));

        // ── Krets eller högre (märkena) ─────────────────────────────────────────

        [Theory]
        [InlineData("Krets", true)]
        [InlineData("Landsdel", true)]
        [InlineData("Nationell", true)]
        [InlineData("Riks", true)]
        [InlineData("ForeningOppen", false)]
        [InlineData("Forening", false)]
        [InlineData("", false)]
        public void Krets_eller_hogre(string level, bool expected) =>
            Assert.Equal(expected, CompetitionLevel.IsKretsOrAbove(level));

        // ── Nivån och isClubOnly får inte säga emot varandra ────────────────────

        [Fact] public void Klubbintern_med_annan_niva_vagras() =>
            Assert.NotNull(CompetitionLevel.ConsistencyError(CompetitionLevel.Krets, isClubOnly: true, hasClub: true));

        [Fact] public void Bara_egna_medlemmar_utan_klubbintern_vagras() =>
            Assert.NotNull(CompetitionLevel.ConsistencyError(CompetitionLevel.Forening, isClubOnly: false, hasClub: true));

        [Fact] public void Oppen_klubbtavling_utan_klubb_vagras() =>
            Assert.Contains("klubb", CompetitionLevel.ConsistencyError(CompetitionLevel.ForeningOppen, false, false));

        [Fact] public void Kretstavling_utan_klubb_ar_i_ordning() =>
            Assert.Null(CompetitionLevel.ConsistencyError(CompetitionLevel.Krets, false, false));

        [Fact] public void Tom_niva_ar_alltid_i_ordning() =>
            Assert.Null(CompetitionLevel.ConsistencyError("", isClubOnly: true, hasClub: true));

        [Fact] public void Varje_niva_har_etikett_och_forklaring()
        {
            foreach (var o in CompetitionLevel.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(o.Label));
                Assert.False(string.IsNullOrWhiteSpace(o.ShortLabel));
                Assert.False(string.IsNullOrWhiteSpace(o.Explanation));
                Assert.Equal(o.Value, CompetitionLevel.Normalize(o.Value));
            }
        }

        [Fact] public void Varje_mastarskap_har_ett_forslag()
        {
            foreach (var scope in CompetitionScopeHelper.All)
                Assert.True(CompetitionLevel.ScopeSuggestions.ContainsKey(scope), scope);
        }
    }
}
