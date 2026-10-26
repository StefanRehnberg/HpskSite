using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Planera dagen, "Dagens plan": statusen är härledd ur data, så bevisen är att varje steg ändrar tillstånd när datan ändras, att små fall ser små ut
    /// och att inget steg någonsin är blockerat eller saknar förklaring i text.
    /// </summary>
    public class PlanStepRulesTests
    {
        private static PlanFacts Facts(bool roles = true, bool needs = true, int needed = 24, int missing = 0, bool linked = true, int vols = 0, string? mode = null,
            bool falt = false, int startClashes = 0, int clashes = 0) => new(roles, needs, needed, missing, linked, vols, mode, falt, startClashes, clashes);

        private static PlanStep Step(PlanFacts f, string key) => PlanStepRules.Derive(f).Single(s => s.Key == key);

        [Fact]
        public void Utan_roller_ar_forsta_steget_att_valja_roller_och_inget_annat_finns_att_gora()
        {
            var steps = PlanStepRules.Derive(Facts(roles: false, needs: false, needed: 0, linked: false));
            steps.Select(s => s.Key).Should().Equal("behov");
            steps[0].State.Should().Be(PlanStepState.Todo);
            steps[0].Actions.Should().Equal("roller");
        }

        [Fact]
        public void Roller_utan_behov_ber_om_antal_och_fyll_och_kontroll_finns_inte_an()
        {
            var steps = PlanStepRules.Derive(Facts(needs: false, needed: 0, linked: false));
            steps.Select(s => s.Key).Should().Equal("behov");
            steps[0].Actions.Should().Equal("behov");
            steps[0].Text.Should().Contain("hur många");
        }

        [Fact]
        public void Behov_satta_ger_fyll_och_kontroll_och_visar_antalen_i_text()
        {
            var steps = PlanStepRules.Derive(Facts(needed: 24, missing: 17, linked: false));
            steps.Select(s => s.Key).Should().Equal("behov", "fyll", "kontroll");
            Step(Facts(needed: 24, missing: 17, linked: false), "fyll").Text.Should().Contain("17 av 24");
            Step(Facts(needed: 24, missing: 17, linked: false), "kontroll").Text.Should().Contain("17 platser saknas");
        }

        [Fact]
        public void Allt_fyllt_utan_krockar_ar_klart_i_alla_steg_och_ingen_next()
        {
            var steps = PlanStepRules.Derive(Facts(missing: 0, vols: 5, mode: "Samordnat", falt: true));
            steps.Should().OnlyContain(s => s.State == PlanStepState.Done);
            PlanStepRules.Next(steps).Should().BeNull();
        }

        [Fact]
        public void Den_lilla_klubbtavlingen_ser_tre_steg_aldrig_fem()
        {
            // Ingen kopplad tävling, inga anmälda funktionärer, ingen fältstartlista.
            PlanStepRules.Derive(Facts(linked: false, vols: 0, falt: false)).Select(s => s.Key).Should().Equal("behov", "fyll", "kontroll");
            // Kopplad men utan funktionärer och utan fält: fortfarande tre (skjutläget har inget att passa ihop med).
            PlanStepRules.Derive(Facts(linked: true, vols: 0, falt: false)).Select(s => s.Key).Should().Equal("behov", "fyll", "kontroll");
        }

        [Fact]
        public void Skjutlaget_visas_bara_med_kopplad_tavling_och_funktionarer_eller_fältstartlista()
        {
            PlanStepRules.Derive(Facts(linked: true, vols: 3)).Select(s => s.Key).Should().Contain("lage");
            PlanStepRules.Derive(Facts(linked: true, falt: true)).Select(s => s.Key).Should().Contain("lage");
            PlanStepRules.Derive(Facts(linked: false, vols: 3)).Select(s => s.Key).Should().NotContain("lage");
        }

        [Fact]
        public void Skjutlaget_ar_Todo_tills_ett_val_ar_gjort_och_Done_med_valet_i_text()
        {
            var todo = Step(Facts(vols: 3), "lage"); todo.State.Should().Be(PlanStepState.Todo);
            var done = Step(Facts(vols: 3, mode: "Samordnat"), "lage"); done.State.Should().Be(PlanStepState.Done); done.Text.Should().Contain("Samordnat");
            todo.Actions.Should().Contain("skjuttid");
        }

        [Fact]
        public void Startlistan_ar_bara_med_for_falt_och_krockar_gor_den_till_Todo()
        {
            PlanStepRules.Derive(Facts(falt: false)).Select(s => s.Key).Should().NotContain("startlista");
            Step(Facts(falt: true, startClashes: 0, mode: "x"), "startlista").State.Should().Be(PlanStepState.Done);
            var s = Step(Facts(falt: true, startClashes: 3, mode: "x"), "startlista");
            s.State.Should().Be(PlanStepState.Todo);
            s.Text.Should().Contain("3 starter krockar");
        }

        [Fact]
        public void Krockar_gor_kontrollen_Todo_aven_nar_alla_platser_ar_fyllda()
        {
            var s = Step(Facts(missing: 0, clashes: 2), "kontroll");
            s.State.Should().Be(PlanStepState.Todo);
            s.Text.Should().Contain("2 krockar").And.NotContain("saknas");
        }

        [Fact]
        public void Forslag_erbjuds_bara_nar_det_finns_anmalda_funktionarer()
        {
            Step(Facts(vols: 0, missing: 5), "fyll").Actions.Should().Equal("laggin");
            Step(Facts(vols: 4, missing: 5), "fyll").Actions.Should().Equal("laggin", "forslag");
        }

        [Fact]
        public void Varje_steg_har_en_forklarande_mening_och_minst_en_handling_i_alla_kombinationer()
        {
            var bools = new[] { false, true };
            foreach (var roles in bools) foreach (var needs in bools) foreach (var linked in bools) foreach (var falt in bools)
            foreach (var missing in new[] { 0, 3 }) foreach (var vols in new[] { 0, 2 }) foreach (var clashes in new[] { 0, 2 }) foreach (var mode in new string?[] { null, "Samordnat" })
            {
                var f = Facts(roles, needs && roles, needs && roles ? 12 : 0, needs && roles ? missing : 0, linked, vols, mode, falt && linked, falt && linked ? clashes : 0, clashes);
                var steps = PlanStepRules.Derive(f);
                steps.Should().NotBeEmpty();
                steps.Select(s => s.Key).Should().OnlyHaveUniqueItems();
                steps.Should().OnlyContain(s => s.Text.Length > 8 && s.Actions.Count > 0 && s.Title.Length > 3, $"{f}");
                steps.Select(s => s.Key).First().Should().Be("behov");
            }
        }

        // Persontest 6b: "8 på varje av åtta stationer" blev 64 platser och steget visade grön bock på siffran. Nu syns varje rolls summa på stegets rad, så en orimlig rad syns.
        [Fact]
        public void Behovssteget_visar_platserna_per_roll_sa_att_en_orimlig_siffra_syns()
        {
            var by = new[] { new RolePlaces("Sekretariat", 2, 2), new RolePlaces("Servering", 3, 3), new RolePlaces("Stationsmarkör", 64, 64) };
            var f = Facts(needed: 69, missing: 69) with { ByRole = by };
            Step(f, "behov").Text.Should().Contain("69 platser behövs").And.Contain("Stationsmarkör 64").And.Contain("Servering 3");
        }

        [Fact]
        public void Fyllsteget_sager_vilka_roller_som_saknar_folk_storst_brist_forst()
        {
            var by = new[] { new RolePlaces("Sekretariat", 2, 2), new RolePlaces("Stationschef", 8, 8), new RolePlaces("Servering", 3, 0) };
            var f = Facts(needed: 13, missing: 10) with { ByRole = by };
            var text = Step(f, "fyll").Text;
            text.Should().Contain("10 av 13 platser saknar folk").And.Contain("Stationschef 8, Sekretariat 2");
            text.Should().NotContain("Servering", "en fylld roll saknar inget");
        }

        [Fact]
        public void Utan_uppdelning_per_roll_ar_texten_som_forut()
        {
            Step(Facts(needed: 12, missing: 5), "fyll").Text.Should().Be("5 av 12 platser saknar folk");
            Step(Facts(needed: 12), "behov").Text.Should().Be("12 platser behövs");
        }

        [Fact]
        public void Next_pekar_pa_forsta_Todo_i_ordning()
        {
            var steps = PlanStepRules.Derive(Facts(missing: 4, vols: 2, mode: null));
            PlanStepRules.Next(steps)!.Key.Should().Be("lage");
            PlanStepRules.Next(PlanStepRules.Derive(Facts(missing: 4, vols: 2, mode: "Samordnat")))!.Key.Should().Be("fyll");
        }
    }
}
