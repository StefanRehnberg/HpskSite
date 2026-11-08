using System.Runtime.CompilerServices;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Mästerskapets inställningar (vilka deltar, vapengrupper) i Redigera tävling — flyttade hit från kortet i Anmälningar. Testerna rör aldrig
    /// den processgemensamma grenlistan (testprocessen står på förvalet: bara fält); precisionens vägar provas med <c>disciplineEnabled: true</c>.
    /// </summary>
    public class ChampionshipEditSettingsTests
    {
        private const string Krets = "Kretsmästerskap";
        private const string Sm = "Svenskt Mästerskap";
        private const string Klubb = "Klubbmästerskap";

        private static CompetitionInfo Comp(string type, string? scope, string? participants = null, string? groups = null) => new()
        {
            CompetitionType = type,
            CompetitionScope = scope,
            ChampionshipParticipants = participants,
            ChampionshipWeaponGroups = groups
        };

        private static Dictionary<string, object> F(params (string, object)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

        // ── Spärren ───────────────────────────────────────────────────────────

        [Fact]
        public void En_sparning_utan_installningarna_provar_ingenting_aven_om_listan_ar_last()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets), F(("venue", "Banan")), OutOfCompetitionLockReason.ResultsPublished);
            Assert.Null(r);
        }

        [Fact]
        public void Oforandrade_installningar_kan_sparas_trots_lasning()
        {
            // Dialogen skickar dem inte när de är låsta, men en annan klient kan: oförändrat värde ska aldrig stoppa sparningen.
            var saved = Comp("Faltskytte", Krets, participants: "own", groups: "A");
            var r = ChampionshipEditSettings.Refusal(saved,
                F(("championshipParticipants", "own"), ("championshipWeaponGroups", "A")), OutOfCompetitionLockReason.ResultsPublished);
            Assert.Null(r);
        }

        [Theory]
        [InlineData(OutOfCompetitionLockReason.ResultsPublished, "Avpublicera")]
        [InlineData(OutOfCompetitionLockReason.FinalsBuilt, "Finalstartlistan är redan byggd")]
        [InlineData(OutOfCompetitionLockReason.QualifyingFrozen, "kvalgrupp är låst")]
        public void En_andring_vagras_nar_listan_ar_last(OutOfCompetitionLockReason reason, string expected)
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets),
                F(("championshipParticipants", "own")), reason);
            Assert.NotNull(r);
            Assert.Contains(expected, r);
        }

        [Fact]
        public void Publicerad_precisionslista_hanvisar_till_Resultat_fliken_inte_Ratta()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Precision", Krets),
                F(("championshipParticipants", "own")), OutOfCompetitionLockReason.ResultsPublished, disciplineEnabled: true);
            Assert.Contains("Resultat-fliken", r);
            Assert.DoesNotContain("Rätta", r);
        }

        [Fact]
        public void Publicerad_faltlista_nampner_Ratta_masterskapet()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets),
                F(("championshipParticipants", "own")), OutOfCompetitionLockReason.ResultsPublished);
            Assert.Contains("Rätta mästerskapet", r);
        }

        // ── Reglerna per nivå ─────────────────────────────────────────────────

        [Fact]
        public void Svenskt_masterskap_har_ingen_begransning_av_vilka_som_deltar()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", Sm), F(("championshipParticipants", "own")), OutOfCompetitionLockReason.None);
            Assert.Contains("svenskt mästerskap", r);
        }

        [Fact]
        public void Grannkretsar_kan_bara_valjas_for_kretsmasterskap()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", Klubb),
                F(("championshipParticipants", "own+neighbours")), OutOfCompetitionLockReason.None);
            Assert.Contains("Angränsande kretsar", r);
            Assert.Null(ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets),
                F(("championshipParticipants", "own+neighbours")), OutOfCompetitionLockReason.None));
        }

        [Fact]
        public void Vilka_deltar_kraver_ett_masterskap()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", null), F(("championshipParticipants", "own")), OutOfCompetitionLockReason.None);
            Assert.Contains("bara anges för ett mästerskap", r);
        }

        [Fact]
        public void Omfattningen_i_samma_sparning_avgor_reglerna()
        {
            // Från "inget mästerskap" till kretsmästerskap i samma dialog: "bara egna" är tillåtet.
            Assert.Null(ChampionshipEditSettings.Refusal(Comp("Faltskytte", null),
                F(("competitionScope", Krets), ("championshipParticipants", "own")), OutOfCompetitionLockReason.None));
            // Från kretsmästerskap med "bara egna" till svenskt mästerskap: skickas "own" kvar vägras det.
            Assert.NotNull(ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets, participants: "own"),
                F(("competitionScope", Sm), ("championshipParticipants", "own")), OutOfCompetitionLockReason.None));
            // ...men "alla" (det dialogen skickar för SM) går igenom.
            Assert.Null(ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets, participants: "own"),
                F(("competitionScope", Sm), ("championshipParticipants", "all")), OutOfCompetitionLockReason.None));
        }

        [Fact]
        public void Precisionsfamiljen_har_inget_vapengruppsval()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Precision", Krets),
                F(("championshipWeaponGroups", "A")), OutOfCompetitionLockReason.None, disciplineEnabled: true);
            Assert.Contains("Vapengrupper kan inte väljas", r);
        }

        [Fact]
        public void Fält_far_valja_vapengrupper_men_bara_i_ett_masterskap()
        {
            Assert.Null(ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets), F(("championshipWeaponGroups", "A,C")), OutOfCompetitionLockReason.None));
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", null), F(("championshipWeaponGroups", "A")), OutOfCompetitionLockReason.None);
            Assert.Contains("bara anges för ett mästerskap", r);
        }

        [Fact]
        public void Att_rensa_tillbaka_till_alla_gar_alltid_nar_inget_ar_last()
        {
            var saved = Comp("Faltskytte", Krets, participants: "own", groups: "A");
            Assert.Null(ChampionshipEditSettings.Refusal(saved,
                F(("championshipParticipants", "all"), ("championshipWeaponGroups", "")), OutOfCompetitionLockReason.None));
        }

        [Fact]
        public void En_gren_som_inte_ar_pa_vagras()
        {
            // Förvalet i testprocessen: bara fält. Precision är inte med.
            var r = ChampionshipEditSettings.Refusal(Comp("Precision", Krets), F(("championshipParticipants", "own")), OutOfCompetitionLockReason.None);
            Assert.Contains("gäller inte den här grenen", r);
        }

        [Fact]
        public void Nycklarna_laser_oberoende_av_skiftlage()
        {
            var r = ChampionshipEditSettings.Refusal(Comp("Faltskytte", Krets),
                F(("ChampionshipParticipants", "own")), OutOfCompetitionLockReason.FinalsBuilt);
            Assert.NotNull(r);
        }

        // ── Vad dialogen får veta ─────────────────────────────────────────────

        private static object Get(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;

        [Fact]
        public void Dialogen_far_fältets_vapengrupper_och_det_sparade_valet()
        {
            var info = Comp("Faltskytte", Krets, participants: "own", groups: "A");
            var state = ChampionshipEditSettings.ForEditor(info, OutOfCompetitionLockReason.None);
            Assert.Equal(true, Get(state, "available"));
            Assert.Equal("own", Get(state, "participants"));
            Assert.Equal(new[] { "A" }, (string[])Get(state, "weaponGroups"));
            Assert.Contains("A Opt", (string[])Get(state, "offeredGroups"));
            Assert.Equal("None", Get(state, "lockReason"));
        }

        [Fact]
        public void Ett_saknat_val_ar_alla_deltar()
        {
            var state = ChampionshipEditSettings.ForEditor(Comp("Faltskytte", Krets), OutOfCompetitionLockReason.FinalsBuilt);
            Assert.Equal("all", Get(state, "participants"));
            Assert.Equal("FinalsBuilt", Get(state, "lockReason"));
        }

        [Fact]
        public void Precision_far_inget_vapengruppsval_och_ar_inte_pa_med_forvalet()
        {
            var state = ChampionshipEditSettings.ForEditor(Comp("Precision", Krets), OutOfCompetitionLockReason.None);
            Assert.Empty((string[])Get(state, "offeredGroups"));
            Assert.Equal(false, Get(state, "available"));
            Assert.Equal(true, Get(state, "isPrecisionFamily"));
        }

        // ── Kopplingen: dialogen, båda JSON-svaren och sparningen ─────────────

        private static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite"));
        private static string Code(string rel) =>
            string.Join("\n", File.ReadAllText(Path.Combine(Root(), rel)).Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        [Theory]
        [InlineData("Controllers/CompetitionEditController.cs")]
        [InlineData("Controllers/CompetitionAdminController.cs")]
        public void Bada_JSON_svaren_bar_masterskapets_installningar(string file) =>
            Assert.Contains("championship = HpskSite.CompetitionTypes.Common.ChampionshipEditSettings.ForEditor(", Code(file));

        [Fact]
        public void Sparningen_provar_spärren_innan_den_skriver() =>
            Assert.Contains("ChampionshipEditSettings.Refusal(", Code("Controllers/CompetitionEditController.cs"));

        [Fact]
        public void Dialogen_fylls_i_och_samlar_in_installningarna()
        {
            var modal = Code("Views/Partials/CompetitionEditModal.cshtml");
            Assert.Contains("id=\"edit_championshipSection\"", modal);
            Assert.Contains("hpskChampionshipPopulate(competition.championship", modal);
            Assert.Contains("fields.championshipParticipants = champ.participants", modal);
            Assert.Contains("fields.championshipWeaponGroups = champ.groups", modal);
        }

        [Fact]
        public void Kortet_i_Anmalningar_ar_en_sammanfattning_utan_eget_formular()
        {
            var card = Code("Views/Partials/CompetitionRegistrationManagement.cshtml");
            Assert.DoesNotContain("regChampForm", card);
            Assert.DoesNotContain("SetChampionshipSettings", card);
            Assert.Contains("champOpenEditor", card);
        }
    }
}
