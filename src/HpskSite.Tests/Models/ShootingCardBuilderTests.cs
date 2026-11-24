using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>Vilka kort som skrivs ut, och med vilka stationer (Stefan 2026-11-23).</summary>
    public class ShootingCardBuilderTests
    {
        static FaltskytteStationConfig Station(int n, int figures, int poangmal = 0) => new()
        {
            Station = n,
            TargetGroups = new List<FaltskytteTargetGroup>
            {
                new() { Group = 1, Figures = Enumerable.Range(1, figures).Select(i => new FaltskytteFigure { FigureNumber = i, IsPoangmal = i <= poangmal }).ToList() }
            }
        };
        static FaltskytteWeaponClassConfig Cfg(params (int n, int fig)[] st) => new() { Stations = st.Select(s => Station(s.n, s.fig)).ToList() };

        static FaltskytteCompetitionConfig Config(FaltskytteWeaponClassConfig a, FaltskytteWeaponClassConfig? c = null)
        {
            var cfg = new FaltskytteCompetitionConfig();
            cfg.WeaponConfigs["A"] = a;
            cfg.WeaponConfigs["A_Opt"] = new FaltskytteWeaponClassConfig { Stations = a.Stations.Select(s => Station(s.Station, s.TotalTargets)).ToList() }; // samma stationer som A
            if (c != null) cfg.WeaponConfigs["C"] = c;
            return cfg;
        }

        static FaltskyttePatrol Patrol(int id, int number, string? wg = "A") => new() { Id = id, CompetitionId = 1, PatrolNumber = number, WeaponGroup = wg, StartTime = new DateTime(2027, 5, 8, 9, 42, 0) };
        static FaltskyttePatrolMember Member(int patrolId, int pos, string name, string cls, string? status = null) => new()
        {
            PatrolId = patrolId, MemberId = patrolId * 1000 + pos, Position = pos, MemberName = name, ClubName = "Ankeborgs PK", ShootingClass = cls, Status = status
        };
        static ShootingCardSelection All => new(null, null);

        [Fact]
        public void Shooters_with_different_conditions_in_one_patrol_get_separate_cards()
        {
            // A skjuter 3 stationer, C skjuter 2 andra: olika förutsättningar = olika kort, båda med patrullens nummer.
            var cfg = Config(Cfg((1, 3), (2, 4), (3, 3)), Cfg((1, 6), (2, 2)));
            var members = new[] { Member(1, 1, "Anna", "A2"), Member(1, 2, "Calle", "C2"), Member(1, 3, "Berit", "A3") };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 7, "A+C") }, members, All, 0);
            r.Cards.Should().HaveCount(2);
            r.Cards.Select(c => c.PatrolNumber).Should().AllBeEquivalentTo(7);
            r.Cards[0].Shooters.Select(s => s.Name).Should().Equal("Anna", "Berit");
            r.Cards[0].Stations.Select(s => s.Figures).Should().Equal(3, 4, 3);
            r.Cards[1].Shooters.Select(s => s.Name).Should().Equal("Calle");
            r.Cards[1].Stations.Select(s => s.Figures).Should().Equal(6, 2);
        }

        [Fact]
        public void Different_weapon_groups_with_identical_stations_share_one_card()
        {
            var cfg = Config(Cfg((1, 3), (2, 4)));
            var members = new[] { Member(1, 1, "Anna", "A2"), Member(1, 2, "Olle", "A_opt_1") };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1, "A+A_Opt") }, members, All, 0);
            r.Cards.Should().HaveCount(1);
            r.Cards[0].Shooters.Should().HaveCount(2);
            r.Cards[0].WeaponGroupText.Should().Be("A+A Opt");   // som patrullistan visar den
        }

        [Fact]
        public void Two_cards_of_one_patrol_name_their_own_weapon_group()
        {
            var cfg = Config(Cfg((1, 3)), Cfg((1, 6)));
            var members = new[] { Member(1, 1, "Anna", "A2"), Member(1, 2, "Calle", "C2") };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1, "A+C") }, members, All, 0);
            r.Cards.Select(c => c.WeaponGroupText).Should().Equal("A", "C");
        }

        [Fact]
        public void Shooters_marked_DNS_are_not_printed()
        {
            var cfg = Config(Cfg((1, 3)));
            var members = new[] { Member(1, 1, "Anna", "A2"), Member(1, 2, "Borta", "A2", "DNS"), Member(1, 3, "Berit", "A3") };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1) }, members, All, 0);
            r.Cards.Single().Shooters.Select(s => s.Name).Should().Equal("Anna", "Berit");
        }

        [Fact]
        public void A_patrol_where_everyone_is_DNS_gets_no_card()
        {
            var cfg = Config(Cfg((1, 3)));
            ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1) }, new[] { Member(1, 1, "Borta", "A2", "DNS") }, All, 0).Cards.Should().BeEmpty();
        }

        [Fact]
        public void More_than_twelve_shooters_are_split_on_several_cards_marked_with_their_part()
        {
            var cfg = Config(Cfg((1, 3)));
            var members = Enumerable.Range(1, 13).Select(i => Member(1, i, "Skytt " + i, "A2")).ToList();
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 4) }, members, All, 0);
            r.Cards.Should().HaveCount(2);
            r.Cards.Select(c => c.Shooters.Count).Should().Equal(7, 6);
            r.Cards.Select(c => (c.Part, c.Parts)).Should().Equal((1, 2), (2, 2));
            r.Cards[1].Shooters[0].Name.Should().Be("Skytt 8");   // ordningen i patrullen behålls över korten
        }

        [Fact]
        public void A_shooter_whose_class_has_no_configuration_is_left_off_and_named_in_a_notice()
        {
            var cfg = Config(Cfg((1, 3)));                               // ingen C-konfiguration
            var members = new[] { Member(1, 1, "Anna", "A2"), Member(1, 2, "Calle Kula", "C2") };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 3, "ZZ") }, members, All, 0);
            r.Cards.Single().Shooters.Select(s => s.Name).Should().Equal("Anna");
            r.Notices.Should().ContainSingle().Which.Should().Contain("Calle Kula").And.Contain("patrull 3");
        }

        [Fact]
        public void Selection_by_patrol_numbers_weapon_group_or_all()
        {
            var cfg = Config(Cfg((1, 3)), Cfg((1, 6)));
            var patrols = new[] { Patrol(1, 1, "A"), Patrol(2, 2, "C"), Patrol(3, 3, "A") };
            var members = new[] { Member(1, 1, "A1", "A2"), Member(2, 1, "C1", "C2"), Member(3, 1, "A3", "A2") };
            ShootingCardBuilder.Build(cfg, patrols, members, All, 0).Cards.Select(c => c.PatrolNumber).Should().Equal(1, 2, 3);
            ShootingCardBuilder.Build(cfg, patrols, members, new(ShootingCardBuilder.ParsePatrolNumbers("2-3"), null), 0).Cards.Select(c => c.PatrolNumber).Should().Equal(2, 3);
            ShootingCardBuilder.Build(cfg, patrols, members, new(null, "a"), 0).Cards.Select(c => c.PatrolNumber).Should().Equal(1, 3);
            // Patrullnummer går före vapengrupp.
            ShootingCardBuilder.Build(cfg, patrols, members, new(ShootingCardBuilder.ParsePatrolNumbers("2"), "A"), 0).Cards.Select(c => c.PatrolNumber).Should().Equal(2);
        }

        [Fact]
        public void A_selection_that_matches_nothing_says_so()
        {
            var cfg = Config(Cfg((1, 3)));
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1) }, new[] { Member(1, 1, "Anna", "A2") }, new(ShootingCardBuilder.ParsePatrolNumbers("9"), null), 0);
            r.Cards.Should().BeEmpty();
            r.Notices.Should().ContainSingle().Which.Should().Contain("inga patruller");
        }

        [Fact]
        public void Blank_cards_copy_the_most_demanding_stations_and_have_twelve_empty_rows()
        {
            var cfg = Config(Cfg((1, 3), (2, 4)), Cfg((1, 6), (2, 6), (3, 6)));
            var members = new[] { Member(1, 1, "Anna", "A2") };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1, "A") }, members, All, 2);
            r.Cards.Should().HaveCount(3);
            var blank = r.Cards.Where(c => c.IsBlank).ToList();
            blank.Should().HaveCount(2);
            blank[0].Shooters.Should().HaveCount(12).And.OnlyContain(s => s.Name == "");
            blank[0].PatrolNumber.Should().BeNull();
            blank[0].Stations.Select(s => s.Figures).Should().Equal(3, 4);   // kortens mest krävande uppsättning (bara A skrivs ut här)
        }

        [Fact]
        public void Blank_cards_without_any_patrol_use_the_competition_configuration_and_are_capped_at_fifty()
        {
            var cfg = Config(Cfg((1, 3), (2, 4)), Cfg((1, 6), (2, 6), (3, 6)));
            var r = ShootingCardBuilder.Build(cfg, Array.Empty<FaltskyttePatrol>(), Array.Empty<FaltskyttePatrolMember>(), All, 80);
            r.Cards.Should().HaveCount(50);
            r.Cards[0].Stations.Select(s => s.Figures).Should().Equal(6, 6, 6);
        }

        [Fact]
        public void Poangmal_counts_travel_with_the_stations()
        {
            var cfg = new FaltskytteCompetitionConfig();
            cfg.WeaponConfigs["A"] = new FaltskytteWeaponClassConfig { Stations = new List<FaltskytteStationConfig> { Station(1, 3), Station(2, 4, poangmal: 2) } };
            var r = ShootingCardBuilder.Build(cfg, new[] { Patrol(1, 1) }, new[] { Member(1, 1, "Anna", "A2") }, All, 0);
            r.Cards.Single().Stations.Select(s => s.PoangmalCount).Should().Equal(0, 2);
        }

        // ── Renderingen ──

        static ShootingCardModel Card(int patrol, int shooters, int part = 1, int parts = 1) => new()
        {
            PatrolNumber = patrol, StartTime = "09:42", WeaponGroupText = "A", Part = part, Parts = parts,
            Stations = new List<ShootingCardStation> { new(1, null, 3, 0), new(2, null, 4, 1) },
            Shooters = Enumerable.Range(1, shooters).Select(i => new ShootingCardShooter { Name = "Skytt " + i, Club = "Ankeborgs PK", ShootingClass = "A2" }).ToList()
        };

        [Fact]
        public void The_page_declares_the_paper_size_and_orientation()
        {
            var land = ShootingCardRenderer.RenderDocument(new[] { Card(1, 6) }, new ShootingCardOptions { Paper = "landscape" });
            var port = ShootingCardRenderer.RenderDocument(new[] { Card(1, 6) }, new ShootingCardOptions { Paper = "portrait" });
            land.Should().Contain("size:A4 landscape").And.Contain("width:297mm;height:210mm");
            port.Should().Contain("size:A4 portrait").And.Contain("width:210mm;height:297mm");
        }

        [Fact]
        public void Cards_per_sheet_decide_the_sheet_count_and_the_cut_line()
        {
            var cards = new[] { Card(1, 6), Card(2, 6), Card(3, 6) };
            var one = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { PerSheet = 1 });
            var twoP = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { PerSheet = 2, Paper = "portrait" });
            var twoL = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { PerSheet = 2, Paper = "landscape" });
            Count(one, "class=\"sheet\"").Should().Be(3);
            Count(twoP, "class=\"sheet\"").Should().Be(2);                 // 3 kort, två per sida
            Count(twoP, "class=\"card\"").Should().Be(3);
            twoP.Should().Contain("cut h");
            twoL.Should().Contain("cut v");
            one.Should().NotContain("class=\"cut");
        }

        [Fact]
        public void A_split_patrol_says_which_sheet_of_how_many_each_card_is()
        {
            var html = ShootingCardRenderer.RenderDocument(new[] { Card(4, 7, 1, 2), Card(4, 6, 2, 2) }, new ShootingCardOptions());
            html.Should().Contain("blad 1 av 2").And.Contain("blad 2 av 2");
        }

        [Fact]
        public void The_sum_column_and_the_notes_box_are_options()
        {
            var cards = new[] { Card(1, 6) };
            var plain = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { Paper = "portrait", PerSheet = 2, SumColumn = false, Notes = false });
            var full = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { Paper = "portrait", PerSheet = 2, SumColumn = true, Notes = true });
            plain.Should().NotContain(">Σ<").And.NotContain("class=\"notes\"");
            full.Should().Contain(">Σ<").And.Contain("class=\"notes\"");
        }

        [Fact]
        public void The_only_hits_layout_has_no_figure_boxes_and_the_figure_layout_has_one_per_figure()
        {
            var cards = new[] { Card(1, 1) };
            var fig = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { Layout = "fig" });
            var tot = ShootingCardRenderer.RenderDocument(cards, new ShootingCardOptions { Layout = "tot" });
            // Rad: 3 + 4 figurrutor, 2 rutor för träff/figurer, 1 poängruta. Skjutkortet har en rad (en skytt).
            Count(RowOf(fig), "<i class=\"c\"").Should().Be(7);
            Count(RowOf(fig), "<i class=\"c d\"").Should().Be(2);
            Count(RowOf(fig), "<i class=\"c p\"").Should().Be(1);
            Count(RowOf(tot), "<i class=\"c\"").Should().Be(0);
            Count(RowOf(tot), "<i class=\"c d\"").Should().Be(2);
        }

        [Fact]
        public void Names_are_html_encoded()
        {
            var card = Card(1, 1);
            card.Shooters[0].Name = "<script>alert(1)</script>";
            var html = ShootingCardRenderer.RenderDocument(new[] { card }, new ShootingCardOptions { CompetitionName = "A & B <i>" });
            html.Should().NotContain("<script>alert(1)</script>").And.Contain("&lt;script&gt;");
            html.Should().Contain("A &amp; B &lt;i&gt;");
        }

        [Fact]
        public void A_blank_card_leaves_patrol_number_and_start_time_to_write_by_hand()
        {
            var blank = new ShootingCardModel { IsBlank = true, Stations = new() { new(1, null, 3, 0) }, Shooters = Enumerable.Range(0, 12).Select(_ => new ShootingCardShooter()).ToList() };
            var html = ShootingCardRenderer.RenderDocument(new[] { blank }, new ShootingCardOptions());
            html.Should().Contain("class=\"blank\"");
            Count(html, "class=\"row").Should().Be(12);
        }

        // ── Poängmålens skjutplatser (Stefan 2026-11-24: P1, P2 …) ──────────────────────────────

        static FaltskytteStationConfig TwoGroupStation() => new()
        {
            Station = 4,
            TargetGroups = new List<FaltskytteTargetGroup>
            {
                // Målgrupp A: figur 1 (1 mål), figur 2 (3 mål, poängmål). Målgrupp B: figur 1 (poängmål), figur 2.
                new() { Group = 1, Figures = new List<FaltskytteFigure> {
                    new() { FigureNumber = 1 }, new() { FigureNumber = 2, TargetsPerFigure = 3, IsPoangmal = true } } },
                new() { Group = 2, Figures = new List<FaltskytteFigure> {
                    new() { FigureNumber = 1, IsPoangmal = true }, new() { FigureNumber = 2 } } }
            }
        };

        [Fact]
        public void Poangmal_sit_on_the_last_slot_of_their_figure_counted_flat_across_target_groups()
        {
            var s = ShootingCardStation.From(TwoGroupStation());
            s.Figures.Should().Be(1 + 3 + 1 + 1);       // skjutplatser: 6
            s.PoangmalCount.Should().Be(2);
            s.PoangSlots.Should().Equal(4, 5);           // figur A2 slutar på plats 4, figur B1 är plats 5
        }

        [Fact]
        public void Two_classes_with_poangmal_on_different_figures_share_a_card_only_when_the_layout_ignores_the_position()
        {
            var cfg = new FaltskytteCompetitionConfig();
            cfg.WeaponConfigs["A"] = new FaltskytteWeaponClassConfig { Stations = new() { Station(1, 3, 1) } };            // poängmål på figur 1
            cfg.WeaponConfigs["C"] = new FaltskytteWeaponClassConfig { Stations = new() { new FaltskytteStationConfig
            {
                Station = 1,
                TargetGroups = new List<FaltskytteTargetGroup> { new() { Group = 1, Figures = new List<FaltskytteFigure> {
                    new() { FigureNumber = 1 }, new() { FigureNumber = 2 }, new() { FigureNumber = 3, IsPoangmal = true } } } }   // poängmål på figur 3
            } } };
            var members = new[] { Member(1, 1, "Anna", "A2"), Member(1, 2, "Calle", "C2") };
            var patrol = new[] { Patrol(1, 1, "A+C") };

            ShootingCardBuilder.Build(cfg, patrol, members, All, 0, "fig").Cards.Should().HaveCount(1);   // dagens layouter
            var split = ShootingCardBuilder.Build(cfg, patrol, members, All, 0, "card").Cards;              // Skyttblock/Skyttkort
            split.Should().HaveCount(2);
            split[0].Stations[0].PoangSlots.Should().Equal(1);
            split[1].Stations[0].PoangSlots.Should().Equal(3);
        }

        static int Count(string s, string needle) => (s.Length - s.Replace(needle, "").Length) / needle.Length;
        static string RowOf(string html)
        {
            var i = html.IndexOf("<div class=\"row", StringComparison.Ordinal);
            return html.Substring(i, html.IndexOf("<div class=\"notes", i, StringComparison.Ordinal) is var n and > 0 ? n - i : html.Length - i);
        }
    }
}
