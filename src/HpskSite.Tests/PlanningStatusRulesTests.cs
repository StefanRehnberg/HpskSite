using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Lägesrapporten (Anders, persontest 11) är ren text ur siffror som redan finns: bevisen är att meningen säger exakt det siffrorna säger, att ingenting påstås om det som saknas
    /// och att milstolparna hamnar i rätt månad. Plus kolumnen "Sammankallande" som visade "—" fast den som frågade var ledare i delen.
    /// </summary>
    public class PlanningStatusRulesTests
    {
        private static StatusPart Part(string name = "Fält", int open = 12, int total = 14, int overdue = 0, int help = 0, int? needed = null, int? missing = null)
            => new(name, true, open, total, overdue, help, needed, missing);

        [Fact]
        public void Rubriken_summerar_delarna_och_namnger_datumet()
        {
            var h = PlanningStatusRules.Headline(new DateTime(2026, 10, 27), new[] { Part(open: 12, total: 14, overdue: 3, needed: 40, missing: 22), Part("Precision", open: 19, total: 44, overdue: 1) });
            h.Should().Be("Läget 27 oktober: 31 uppgifter öppna av 58, 4 försenade, 22 platser saknas.");
        }

        [Fact]
        public void Inget_forsenat_sager_det_och_utan_bemanning_namns_inga_platser()
        {
            var h = PlanningStatusRules.Headline(new DateTime(2026, 10, 27), new[] { Part(open: 1, total: 3) });
            h.Should().Be("Läget 27 oktober: 1 uppgift öppna av 3, ingen försenad.");
            h.Should().NotContain("plats");
        }

        [Fact]
        public void Fyllda_platser_sager_att_alla_ar_fyllda_och_ett_behov_utan_brist_ar_inte_en_brist()
        {
            PlanningStatusRules.Headline(new DateTime(2026, 10, 27), new[] { Part(needed: 40, missing: 0) }).Should().Contain("alla platser är fyllda");
        }

        [Fact]
        public void Delraden_namner_bara_det_som_galler()
        {
            PlanningStatusRules.PartLine(Part(open: 12, total: 14)).Should().Be("12 öppna av 14");
            PlanningStatusRules.PartLine(Part(open: 12, total: 14, overdue: 3, help: 1, needed: 40, missing: 22))
                .Should().Be("12 öppna av 14 · 3 försenade · 1 markerade Behöver hjälp · 22 av 40 platser saknas");
            PlanningStatusRules.PartLine(Part(needed: 40, missing: 0)).Should().Contain("alla platser fyllda");
        }

        [Fact]
        public void Milstolparna_grupperas_per_manad_i_datumordning()
        {
            var m = new[]
            {
                new StatusMilestone("Fält", "Underlag till förbundet", new DateTime(2028, 4, 10), false),
                new StatusMilestone("Fält", "Inbjudan godkänd", new DateTime(2028, 2, 5), true),
                new StatusMilestone("Precision", "Stomprogram", new DateTime(2028, 2, 1), false),
            };
            var g = PlanningStatusRules.ByMonth(m);
            g.Select(x => x.Month).Should().Equal(new DateTime(2028, 2, 1), new DateTime(2028, 4, 1));
            g[0].Items.Select(i => i.Title).Should().Equal("Stomprogram", "Inbjudan godkänd");
            PlanningStatusRules.MonthLabel(new DateTime(2028, 2, 1)).Should().Be("Februari 2028");
        }

        [Fact]
        public void Tom_lista_ger_inga_manader()
            => PlanningStatusRules.ByMonth(Array.Empty<StatusMilestone>()).Should().BeEmpty();

        // Anders: kolumnen "Sammankallande" visade "—" när huvudplaneringens ägare också ägde delen.
        [Fact]
        public void Sammankallande_visar_delens_egna_ledare_annars_som_huvudplaneringen_och_bara_tomt_utan_ledare()
        {
            PlanningService.ConvenersText(new[] { "Kalle", "Lisa" }, true).Should().Be("Kalle, Lisa");
            PlanningService.ConvenersText(Array.Empty<string>(), true).Should().Be("Som huvudplaneringen");
            PlanningService.ConvenersText(Array.Empty<string>(), false).Should().BeEmpty();
        }

        // En räkning av platser för både bandet och rapporten: tomt rutnät ger noll, inga undantag.
        [Fact]
        public void Platsrakningen_av_ett_tomt_rutnat_ar_noll()
        {
            var (needed, missing, perRole) = StaffingPlaces.Count(new GridResponse());
            (needed, missing).Should().Be((0, 0));
            perRole.Should().BeEmpty();
        }

        // Anders (persontest 12): rapporten ska säga hur många som inte kunnat kontrolleras mot startlistan.
        [Fact]
        public void Rubrik_och_delrad_sager_hur_manga_namn_som_inte_ar_kopplade()
        {
            var h = PlanningStatusRules.Headline(new DateTime(2026, 10, 27), new[] { new StatusPart("Fält", true, 2, 2, 0, 0, null, null, 2), new StatusPart("Servering", true, 1, 1, 0, 0, null, null, 1) });
            h.Should().Contain("3 namn är inte kopplade till en medlem och kan inte kontrolleras mot startlistan");
            PlanningStatusRules.Headline(new DateTime(2026, 10, 27), new[] { new StatusPart("Fält", true, 2, 2, 0, 0, null, null, 1) }).Should().Contain("1 namn är inte kopplat");
            PlanningStatusRules.Headline(new DateTime(2026, 10, 27), new[] { Part() }).Should().NotContain("kopplade");
            PlanningStatusRules.PartLine(new StatusPart("Fält", true, 2, 2, 0, 0, null, null, 2)).Should().Contain("2 namn ej kopplade till medlem");
        }

        [Fact]
        public void Fritextnamn_raknas_en_gang_per_namn_och_inte_for_Tavlingsledningen_eller_avbojda()
        {
            GridEntry E(string name, int? member = null, string status = "Planned") => new() { DisplayName = name, MemberId = member, Status = status };
            var resp = new GridResponse();
            resp.Rows.Add(new GridRow { RoleKey = "stationsmarkor", Cells = new() { ["2027-06-05"] = new() { E("ZZZ Hjälpare 1"), E("zzz hjälpare 1 "), E("Medlem", 55), E("Avböjd", null, "Declined") } } });
            resp.Rows.Add(new GridRow { RoleKey = "sekretariat", Cells = new() { ["2027-06-05"] = new() { E("ZZZ Hjälpare 2") } } });
            resp.Rows.Add(new GridRow { RoleKey = "tavlingsledning", Cells = new() { ["2027-06-05"] = new() { E("Lisa", null) } } });
            StaffingPlaces.Unlinked(resp).Should().Be(2);
        }
    }
}
