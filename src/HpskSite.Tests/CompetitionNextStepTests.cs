using HpskSite.CompetitionTypes.Common;
using Xunit;
using P = HpskSite.CompetitionTypes.Common.CompetitionNextStep.Phase;

namespace HpskSite.Tests
{
    public class CompetitionNextStepTests
    {
        private static CompetitionNextStep.GroupInput G(string g, int qualExp, int qualEnt,
            bool finals = false, int finExp = 0, int finEnt = 0) =>
            new() { Group = g, Starts = qualExp > 0 ? 1 : 0, QualExpected = qualExp, QualEntered = qualEnt,
                    HasFinalsList = finals, FinalsExpected = finExp, FinalsEntered = finEnt };

        private static P R(int finalSeries, bool official = false, bool? ties = null, params CompetitionNextStep.GroupInput[] groups) =>
            CompetitionNextStep.Resolve(new CompetitionNextStep.Input
            {
                HasStartList = true, StartListPublished = true, FinalSeries = finalSeries,
                ResultsOfficial = official, UnresolvedTies = ties, Groups = groups.ToList()
            });

        [Fact] public void Ingen_startlista_ger_skapa() =>
            Assert.Equal(P.CreateStartList, CompetitionNextStep.Resolve(new CompetitionNextStep.Input()));

        [Fact] public void Opublicerad_startlista_ger_publicera() =>
            Assert.Equal(P.PublishStartList, CompetitionNextStep.Resolve(new CompetitionNextStep.Input { HasStartList = true }));

        [Fact] public void Grundomgang_pagar() => Assert.Equal(P.EnterQualification, R(3, groups: G("B", 56, 32)));

        [Fact] public void Grundomgang_klar_utan_finallista_ger_skapa_finalen() =>
            Assert.Equal(P.CreateFinals, R(3, groups: G("B", 56, 56)));

        [Fact] public void Final_med_en_klass_utan_finalister_star_kvar_pa_skapa_finalen()
        {
            var g = G("C", 70, 70, true, 15, 0);
            g.MissingFinalCategories = 4;
            Assert.Equal(P.CreateFinals, R(3, groups: g));
        }

        [Fact] public void Final_pagar() => Assert.Equal(P.EnterFinals, R(3, groups: G("B", 56, 56, true, 24, 10)));

        [Fact] public void Utan_final_gar_grundomgangen_direkt_till_publicera() =>
            Assert.Equal(P.PublishResults, R(0, groups: G("B", 56, 56)));

        [Fact] public void Allt_inmatat_och_publicerat_ger_klar() =>
            Assert.Equal(P.Done, R(3, official: true, ties: false, G("B", 56, 56, true, 24, 24)));

        [Fact] public void Oavgjord_medaljplats_ger_sarskjutning_aven_om_listan_ar_publicerad() =>
            Assert.Equal(P.ShootOff, R(3, official: true, ties: true, G("B", 56, 56, true, 24, 24)));

        [Fact] public void Okant_sarskjutningslage_ger_aldrig_sarskjutning() =>
            Assert.Equal(P.PublishResults, R(3, ties: null, groups: G("B", 56, 56, true, 24, 24)));

        // ⚠️ Fasen följer den grupp som ligger LÄNGST BAK. C har final klar, A har inte börjat finalen.
        [Fact] public void Flera_vapengrupper_foljer_den_som_ligger_langst_bak() =>
            Assert.Equal(P.CreateFinals, R(3, groups: new[] { G("C", 70, 70, true, 30, 30), G("A", 60, 60) }));

        [Fact] public void Grupp_utan_starter_raknas_inte() =>
            Assert.Equal(P.PublishResults, R(0, groups: new[] { G("B", 56, 56), G("A", 0, 0) }));

        [Fact] public void Publicerad_lista_utan_starter_ger_inmatning() =>
            Assert.Equal(P.EnterQualification, R(3));

        [Fact] public void Steglinjen_har_fem_steg() =>
            Assert.Equal(new[] { 0, 1, 2, 3, 3, 4, 4, 4 },
                Enum.GetValues<P>().Select(CompetitionNextStep.StepIndex).ToArray());
    }
}
