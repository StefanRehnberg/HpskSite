using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Årshjulets återkommande uppgifter (2026-10-21): regeln och tillfällena som rena funktioner.
    /// </summary>
    public class BoardWheelPlannerTests
    {
        private static BoardWheelRule Rule(int id, int first, int interval = 1, int? last = null, int? month = 3, int? day = 15,
            string title = "Uppgift", string? key = null, bool active = true) =>
            new()
            {
                Id = id, OwnerType = 0, OwnerId = 1, Title = title, Month = month, Day = day,
                FirstYear = first, IntervalYears = interval, LastYear = last, TemplateKey = key, IsActive = active
            };

        private static BoardYearWheelItem Saved(int id, int year, int? ruleId = null, string title = "Sparad",
            DateTime? date = null, bool done = false, bool active = true) =>
            new() { Id = id, OwnerType = 0, OwnerId = 1, Year = year, RuleId = ruleId, Title = title, TargetDate = date, Done = done, IsActive = active };

        // ---- Occurs / Next ------------------------------------------------------------------

        [Fact]
        public void Varje_ar_slar_till_fran_forsta_aret()
        {
            var r = Rule(1, 2026);
            BoardWheelPlanner.Occurs(r, 2025).Should().BeFalse();
            BoardWheelPlanner.Occurs(r, 2026).Should().BeTrue();
            BoardWheelPlanner.Occurs(r, 2027).Should().BeTrue();
        }

        [Fact]
        public void Vart_femte_ar_fran_2030_ger_2030_och_2035_men_inte_2031()
        {
            var r = Rule(1, 2030, interval: 5);
            BoardWheelPlanner.Occurs(r, 2030).Should().BeTrue();
            BoardWheelPlanner.Occurs(r, 2031).Should().BeFalse();
            BoardWheelPlanner.Occurs(r, 2034).Should().BeFalse();
            BoardWheelPlanner.Occurs(r, 2035).Should().BeTrue();
            BoardWheelPlanner.Occurs(r, 2027).Should().BeFalse("regeln börjar 2030");
        }

        [Fact]
        public void Sista_aret_avslutar_regeln()
        {
            var r = Rule(1, 2026, last: 2028);
            BoardWheelPlanner.Occurs(r, 2028).Should().BeTrue();
            BoardWheelPlanner.Occurs(r, 2029).Should().BeFalse();
            BoardWheelPlanner.Next(r, 2028).Should().BeNull();
        }

        [Fact]
        public void Next_ger_nasta_tillfalle_efter_ett_ar()
        {
            var r = Rule(1, 2030, interval: 5);
            BoardWheelPlanner.Next(r, 2026).Should().Be(2030, "före första året är första året nästa");
            BoardWheelPlanner.Next(r, 2030).Should().Be(2035);
            BoardWheelPlanner.Next(r, 2031).Should().Be(2035);
            BoardWheelPlanner.Next(r, 2035).Should().Be(2040);
        }

        [Fact]
        public void Intervall_noll_behandlas_som_varje_ar()
        {
            var r = Rule(1, 2026, interval: 0);
            BoardWheelPlanner.Occurs(r, 2027).Should().BeTrue();
            BoardWheelPlanner.Next(r, 2026).Should().Be(2027);
        }

        // ---- Datum --------------------------------------------------------------------------

        [Fact]
        public void Skottdag_blir_28_februari_ett_vanligt_ar()
        {
            BoardWheelPlanner.DateFor(2, 29, 2028).Should().Be(new DateTime(2028, 2, 29));
            BoardWheelPlanner.DateFor(2, 29, 2027).Should().Be(new DateTime(2027, 2, 28));
        }

        [Fact]
        public void Utan_datum_ger_inget_datum()
        {
            BoardWheelPlanner.DateFor(null, null, 2027).Should().BeNull();
            BoardWheelPlanner.DateFor(13, 1, 2027).Should().BeNull();
        }

        // ---- ForYear ------------------------------------------------------------------------

        [Fact]
        public void Framraknat_tillfalle_bar_regelns_nyckel_titel_och_datum()
        {
            var o = BoardWheelPlanner.ForYear(new[] { Rule(7, 2030, interval: 5, title: "Besiktning", month: 6, day: 1) },
                Array.Empty<BoardYearWheelItem>(), 2035).Single();
            o.Key.Should().Be("rule:7:2035");
            o.ItemId.Should().BeNull();
            o.Title.Should().Be("Besiktning");
            o.TargetDate.Should().Be(new DateTime(2035, 6, 1));
            o.IntervalYears.Should().Be(5);
            o.NextYear.Should().Be(2040);
        }

        [Fact]
        public void Regel_som_inte_slar_till_ger_inget_tillfalle()
        {
            BoardWheelPlanner.ForYear(new[] { Rule(7, 2030, interval: 5) }, Array.Empty<BoardYearWheelItem>(), 2031)
                .Should().BeEmpty();
        }

        [Fact]
        public void Sparat_tillfalle_ersatter_det_framraknade()
        {
            var rules = new[] { Rule(7, 2026, title: "Regelns titel") };
            var saved = new[] { Saved(50, 2027, ruleId: 7, title: "Ändrad för 2027", done: true) };
            var o = BoardWheelPlanner.ForYear(rules, saved, 2027).Single();
            o.Key.Should().Be("item:50");
            o.Title.Should().Be("Ändrad för 2027");
            o.Done.Should().BeTrue();
            o.IntervalYears.Should().Be(1, "tillfället hör fortfarande till regeln");

            // Andra år påverkas inte.
            BoardWheelPlanner.ForYear(rules, saved, 2028).Single().Title.Should().Be("Regelns titel");
        }

        [Fact]
        public void Gravsten_tar_bort_aret_men_inte_ovriga_ar()
        {
            var rules = new[] { Rule(7, 2026) };
            var saved = new[] { Saved(50, 2027, ruleId: 7, active: false) };
            BoardWheelPlanner.ForYear(rules, saved, 2027).Should().BeEmpty();
            BoardWheelPlanner.ForYear(rules, saved, 2028).Should().ContainSingle();
        }

        [Fact]
        public void Engangspunkter_visas_bara_sitt_ar_och_inte_nar_de_ar_borttagna()
        {
            var saved = new[] { Saved(1, 2030, title: "Besiktning"), Saved(2, 2030, title: "Borta", active: false) };
            BoardWheelPlanner.ForYear(Array.Empty<BoardWheelRule>(), saved, 2030).Select(o => o.Title).Should().Equal("Besiktning");
            BoardWheelPlanner.ForYear(Array.Empty<BoardWheelRule>(), saved, 2027).Should().BeEmpty();
        }

        [Fact]
        public void Inaktiv_regel_raknas_inte_fram_men_sparat_ar_star_kvar()
        {
            var rules = new[] { Rule(7, 2026, active: false) };
            var saved = new[] { Saved(50, 2026, ruleId: 7, done: true) };
            BoardWheelPlanner.ForYear(rules, saved, 2026).Select(o => o.Key).Should().Equal("item:50");
            BoardWheelPlanner.ForYear(rules, saved, 2027).Should().BeEmpty();
        }

        [Fact]
        public void Sparat_tillfalle_star_kvar_aven_nar_regeln_inte_langre_slar_till()
        {
            // Regeln avslutades 2027 efter att 2028 bockats av — historiken får inte försvinna.
            var rules = new[] { Rule(7, 2026, last: 2027) };
            var saved = new[] { Saved(50, 2028, ruleId: 7, done: true) };
            BoardWheelPlanner.ForYear(rules, saved, 2028).Select(o => o.Key).Should().Equal("item:50");
        }

        [Fact]
        public void Ordningen_ar_utan_datum_forst_sedan_datum()
        {
            var rules = new[] { Rule(1, 2026, month: 12, day: 1, title: "Dec"), Rule(2, 2026, month: 1, day: 31, title: "Jan") };
            var saved = new[] { Saved(9, 2026, title: "Utan datum") };
            BoardWheelPlanner.ForYear(rules, saved, 2026).Select(o => o.Title).Should().Equal("Utan datum", "Jan", "Dec");
        }

        // ---- DueSoon ------------------------------------------------------------------------

        [Fact]
        public void DueSoon_ser_januari_i_december()
        {
            var rules = new[] { Rule(1, 2026, month: 1, day: 10, title: "Bokslut") };
            var none = Array.Empty<BoardYearWheelItem>();
            var all = BoardWheelPlanner.ForYear(rules, none, 2026).Concat(BoardWheelPlanner.ForYear(rules, none, 2027));
            var due = BoardWheelPlanner.DueSoon(all, new DateTime(2026, 12, 20), 45);
            due.Select(o => o.TargetDate).Should().Contain(new DateTime(2027, 1, 10));
            due.Select(o => o.TargetDate).Should().Contain(new DateTime(2026, 1, 10), "årets försenade räknas också");
        }

        [Fact]
        public void DueSoon_tar_inte_med_avklarade_eller_for_avlagsna()
        {
            var occ = new[]
            {
                new WheelOccurrence { Key = "a", TargetDate = new DateTime(2026, 11, 1), Done = true },
                new WheelOccurrence { Key = "b", TargetDate = new DateTime(2027, 6, 1) },
                new WheelOccurrence { Key = "c" },
                new WheelOccurrence { Key = "d", TargetDate = new DateTime(2026, 12, 30) }
            };
            BoardWheelPlanner.DueSoon(occ, new DateTime(2026, 12, 1), 45).Select(o => o.Key).Should().Equal("d");
        }

        // ---- Kommande längre fram -----------------------------------------------------------

        [Fact]
        public void Upcoming_visar_nasta_besiktning_men_inte_arliga_regler()
        {
            var rules = new[] { Rule(1, 2030, interval: 5, title: "Besiktning"), Rule(2, 2026, title: "Bokslut") };
            var none = Array.Empty<BoardYearWheelItem>();
            BoardWheelPlanner.Upcoming(rules, none, 2026).Select(o => (o.Title, o.Year)).Should().Equal(("Besiktning", 2030));
            BoardWheelPlanner.Upcoming(rules, none, 2030).Single().Year.Should().Be(2035, "efter det visade året");
        }

        [Fact]
        public void Upcoming_hoppar_over_ett_overhoppat_ar_och_ett_avbockat()
        {
            var rules = new[] { Rule(1, 2030, interval: 5) };
            BoardWheelPlanner.Upcoming(rules, new[] { Saved(9, 2030, ruleId: 1, active: false) }, 2026).Single().Year.Should().Be(2035);
            BoardWheelPlanner.Upcoming(rules, new[] { Saved(9, 2030, ruleId: 1, done: true) }, 2026).Should().BeEmpty(
                "redan avklarad — och regelns nästa efter det hör till nästa visning");
        }

        [Fact]
        public void Upcoming_tar_med_kommande_engangspunkter_som_inte_ar_klara()
        {
            var saved = new[]
            {
                Saved(1, 2031, title: "Senare"), Saved(2, 2031, title: "Klar", done: true),
                Saved(3, 2026, title: "I år"), Saved(4, 2032, title: "Borttagen", active: false)
            };
            BoardWheelPlanner.Upcoming(Array.Empty<BoardWheelRule>(), saved, 2026).Select(o => o.Title).Should().Equal("Senare");
        }

        // ---- Banans krav (B2) -----------------------------------------------------------------

        [Fact]
        public void Banans_krav_blir_skrivskyddade_tillfallen_med_sort_i_nyckeln()
        {
            var o = BoardWheelPlanner.FromRangeRequirements(new[]
            {
                new RangeRequirement(RangeRequirement.Inspection, 7, 13, "Änganäs", "Besiktning", new DateTime(2030, 6, 1)),
                new RangeRequirement(RangeRequirement.Permit, 3, 13, "Änganäs", "Polistillståndet", new DateTime(2027, 2, 1))
            });
            o.Select(x => x.Key).Should().Equal("range:inspection:7", "range:permit:3");
            o[0].Title.Should().Be("Besiktning av Änganäs");
            o[1].Title.Should().Be("Polistillståndet för Änganäs går ut");
            o.Should().OnlyContain(x => x.FromRange && x.Url == "/skjutbana/13#vy=tillstand");
            o.Select(x => x.Year).Should().Equal(2030, 2027);
            WheelKey.Parse(o[0].Key).Should().BeNull("ett krav från banan kan aldrig bockas av eller ändras i årshjulet");
        }

        [Fact]
        public void Bara_agare_hyresgast_och_huvudanvandare_ser_banans_krav()
        {
            RangeRequirement.Relations.Should().BeEquivalentTo(new[] { "Owner", "PrimaryUser", "Tenant" });
            RangeRequirement.Relations.Should().NotContain("User");
        }

        [Fact]
        public void Kallelsen_bockar_aldrig_ett_krav_fran_banan()
        {
            var occ = new[] { new WheelOccurrence { Key = "range:document:1", Title = "Kallelse till besiktning", FromRange = true } };
            BoardWheelPlanner.KallelseTarget(occ).Should().BeNull();
        }

        // ---- Påminnelse (del C) ---------------------------------------------------------------

        [Fact]
        public void Paminnelsen_foljer_regeln_och_egna_engangspunkter_men_inte_gamla_standardrader()
        {
            var rules = new[] { Rule(1, 2026, key: "budget"), Rule(2, 2026) };
            rules[0].Remind = false; rules[1].Remind = true;
            var saved = new[]
            {
                new BoardYearWheelItem { Id = 10, Year = 2026, Title = "Budget (gammal kopia)", SortOrder = 7, IsActive = true },
                new BoardYearWheelItem { Id = 11, Year = 2026, Title = "Egen", SortOrder = 100, IsActive = true }
            };
            var o = BoardWheelPlanner.ForYear(rules, saved, 2026).ToDictionary(x => x.Key, x => x.Remind);
            o["rule:1:2026"].Should().BeFalse();
            o["rule:2:2026"].Should().BeTrue();
            o["item:10"].Should().BeFalse("en standardpunkt kopierad av den gamla koden mejlar inte");
            o["item:11"].Should().BeTrue();
        }

        // ---- Kallelsen ---------------------------------------------------------------------

        [Fact]
        public void Kallelsen_bockar_standardpunkten_aven_nar_den_ar_omdopt()
        {
            var rules = new[]
            {
                Rule(1, 2026, title: "Skicka inbjudan till årsmötet", key: BoardYearWheelTemplate.KallelseKey),
                Rule(2, 2026, title: "Kallelse till extra möte (egen)")
            };
            var t = BoardWheelPlanner.KallelseTarget(BoardWheelPlanner.ForYear(rules, Array.Empty<BoardYearWheelItem>(), 2027));
            t!.Key.Should().Be("rule:1:2027", "nyckeln avgör, inte titeln");
        }

        [Fact]
        public void Kallelsen_bockar_inte_en_redan_avbockad_och_faller_tillbaka_pa_aldre_rader()
        {
            var rules = new[] { Rule(1, 2027, key: BoardYearWheelTemplate.KallelseKey) };
            var saved = new[]
            {
                Saved(10, 2026, title: "Kallelse till årsmöte utskickad"),           // äldre år, utan regel
                Saved(11, 2027, ruleId: 1, title: "Kallelse", done: true)            // redan avbockad
            };
            BoardWheelPlanner.KallelseTarget(BoardWheelPlanner.ForYear(rules, saved, 2026))!.Key.Should().Be("item:10");
            BoardWheelPlanner.KallelseTarget(BoardWheelPlanner.ForYear(rules, saved, 2027)).Should().BeNull();
        }

        // ---- Nycklar, standardpunkter --------------------------------------------------------

        [Theory]
        [InlineData("item:12", 12, null, 0)]
        [InlineData("rule:3:2030", null, 3, 2030)]
        public void Nyckeln_tolkas(string key, int? itemId, int? ruleId, int year)
        {
            var k = WheelKey.Parse(key)!;
            k.ItemId.Should().Be(itemId);
            k.RuleId.Should().Be(ruleId);
            k.Year.Should().Be(year);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("12")]
        [InlineData("item:0")]
        [InlineData("item:x")]
        [InlineData("rule:3")]
        [InlineData("rule:3:99")]
        [InlineData("event:3")]
        public void Ogiltig_nyckel_ger_null(string? key) => WheelKey.Parse(key).Should().BeNull();

        [Fact]
        public void Standardpunkterna_borjar_aret_efter_senaste_sparade_men_aldrig_fore_i_ar()
        {
            BoardWheelPlanner.SeedFirstYear(null, 2026).Should().Be(2026);
            BoardWheelPlanner.SeedFirstYear(2026, 2026).Should().Be(2027, "2026 står kvar som historik");
            BoardWheelPlanner.SeedFirstYear(2028, 2026).Should().Be(2029);
            BoardWheelPlanner.SeedFirstYear(2023, 2026).Should().Be(2026, "passerade år fylls inte med försenade punkter");
        }

        [Fact]
        public void Standardpunkterna_ar_arliga_regler_med_nyckel_och_utan_paminnelse()
        {
            var rules = BoardWheelPlanner.TemplateRules(0, 1, 2027, new DateTime(2026, 10, 21));
            rules.Should().HaveCount(BoardYearWheelTemplate.Items.Length);
            rules.Should().OnlyContain(r => r.IntervalYears == 1 && r.FirstYear == 2027 && !r.Remind && r.TemplateKey != null);
            rules.Select(r => r.TemplateKey).Should().Contain(BoardYearWheelTemplate.KallelseKey);
            rules.Select(r => r.TemplateKey).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void Paminnelsen_ar_pa_for_egna_punkter_och_langa_intervall()
        {
            BoardWheelRule.DefaultRemind(null, 1).Should().BeTrue("egen punkt");
            BoardWheelRule.DefaultRemind("kallelse", 1).Should().BeFalse("årlig standardpunkt");
            BoardWheelRule.DefaultRemind("kallelse", 2).Should().BeTrue("intervall ≥ 2 år");
        }
    }
}
