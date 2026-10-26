using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Planera dagen: pappersraden blir en person. Bevisen är att rätt person hittas trots stavningsvarianter, att flera med samma namn aldrig
    /// väljs på gissning, och att en nästan-träff bara föreslås.
    /// </summary>
    public class NameListMatcherTests
    {
        private static MatchPerson P(int id, string name, string? club = "Haaplinge GoAss", string? cls = "B1", params string[] starts) => new(id, name, club, cls, starts);

        private static readonly List<MatchPerson> Pool = new()
        {
            P(1, "Erik Pettersson", "Halmstads Sportskytteklubb", "A2", "09:00"),
            P(2, "Erik Pettersson", "Varbergs PK", "C2", "11:30"),
            P(3, "Björn Johansson", "Haaplinge GoAss", "A2", "09:00"),
            P(4, "Gunilla Nyström", "Varbergs PK", "B1", "10:20"),
            P(5, "Maria Eriksson Lund", "Varbergs PK", "A2", "09:10"),
            P(6, "Sofia Lindström"),
            P(7, "Sofia Lindqvist"),
        };

        private static NameMatch One(string line) => NameListMatcher.Match(new[] { line }, Pool).Single();

        [Theory]
        [InlineData("Björn Johansson")]
        [InlineData("  björn   johansson ")]
        [InlineData("Bjorn Johansson")]
        [InlineData("Johansson, Björn")]
        [InlineData("Johansson Björn")]
        [InlineData("1. Björn Johansson")]
        [InlineData("- Björn Johansson | A2 | patrull 1 09:00")]
        [InlineData("Björn Johansson\tA2")]
        public void Stavningsvarianter_ger_samma_person(string line)
        {
            var m = One(line);
            m.Status.Should().Be(NameMatchStatus.Matched);
            m.Chosen!.MemberId.Should().Be(3);
        }

        [Fact]
        public void Mellannamn_i_registret_matchar_pappret_med_for_och_efternamn()
        {
            One("Maria Karlsson").Status.Should().Be(NameMatchStatus.NotFound);   // fel efternamn
            foreach (var line in new[] { "Maria Eriksson Lund", "Maria Lund" })
            {
                var m = One(line);
                m.Status.Should().Be(NameMatchStatus.Matched, line);
                m.Chosen!.MemberId.Should().Be(5, line);
            }
        }

        [Fact]
        public void Flera_med_samma_namn_utan_tips_väljs_aldrig_på_gissning()
        {
            var m = One("Erik Pettersson");
            m.Status.Should().Be(NameMatchStatus.Ambiguous);
            m.Chosen.Should().BeNull();
            m.Options.Select(o => o.MemberId).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Theory]
        [InlineData("Erik Pettersson | C2", 2)]
        [InlineData("Erik Pettersson | A2", 1)]
        [InlineData("Erik Pettersson | patrull 16 11:30", 2)]
        [InlineData("Erik Pettersson | patrull 1 09:00", 1)]
        [InlineData("Erik Pettersson (Varbergs)", 2)]
        [InlineData("Erik Pettersson (Halmstad)", 1)]
        public void Tips_på_raden_pekar_ut_en_ensam(string line, int expected)
        {
            var m = One(line);
            m.Status.Should().Be(NameMatchStatus.Matched);
            m.Chosen!.MemberId.Should().Be(expected);
        }

        [Fact]
        public void Motstridiga_tips_ger_ingen_gissning()
        {
            // A2 pekar på 1, 11:30 pekar på 2 — lika många: människan väljer.
            One("Erik Pettersson | A2 | 11:30").Status.Should().Be(NameMatchStatus.Ambiguous);
        }

        [Fact]
        public void Nästan_stavning_föreslås_men_markeras_inte_och_två_lika_nära_ger_ingen_träff()
        {
            var m = One("Gunilla Nystrom x");   // ett tecken fel efter vikning
            m.Status.Should().Be(NameMatchStatus.Suggested);
            m.Chosen!.MemberId.Should().Be(4);
            One("Sofia Lindstrim").Chosen!.MemberId.Should().Be(6);   // Lindström är närmast, Lindqvist ligger för långt bort

            var tie = new List<MatchPerson> { P(1, "Nils Berg"), P(2, "Nils Bery") };
            var t = NameListMatcher.Match(new[] { "Nils Berk" }, tie).Single();
            t.Status.Should().Be(NameMatchStatus.NotFound);   // lika nära båda: ingen gissning
            t.Chosen.Should().BeNull();
        }

        [Fact]
        public void Okänt_namn_hittas_inte()
        {
            var m = One("Kalle Anka");
            m.Status.Should().Be(NameMatchStatus.NotFound);
            m.Chosen.Should().BeNull();
        }

        [Fact]
        public void Samma_person_två_gånger_är_en_dubblett()
        {
            var r = NameListMatcher.Match(new[] { "Björn Johansson", "Gunilla Nyström", "bjorn johansson" }, Pool);
            r.Select(x => x.Status).Should().Equal(NameMatchStatus.Matched, NameMatchStatus.Matched, NameMatchStatus.Duplicate);
        }

        [Fact]
        public void Tomma_rader_hoppas_över_och_antalet_är_begränsat()
        {
            NameListMatcher.Match(new[] { "", "  ", "Björn Johansson" }, Pool).Should().HaveCount(1);
            var many = Enumerable.Range(0, 500).Select(i => $"Person {i}").ToList();
            NameListMatcher.Match(many, Pool).Should().HaveCount(NameListMatcher.MaxLines);
        }

        [Fact]
        public void Egenskap_trettio_slumpade_papper_hittar_rätt_person_aldrig_fel()
        {
            var r = new Random(7);
            var first = new[] { "Anna", "Björn", "Åsa", "Örjan", "Sofia", "Nils", "Ida", "Märta" };
            var last = new[] { "Nyström", "Lindström", "Jönsson", "Åkesson", "Persson", "Hansson", "Öberg", "Svensson" };
            for (var round = 0; round < 40; round++)
            {
                var pool = new List<MatchPerson>(); var names = new HashSet<string>();
                while (pool.Count < 60)
                {
                    var n = $"{first[r.Next(first.Length)]} {last[r.Next(last.Length)]}";
                    if (names.Add(n)) pool.Add(P(1000 + pool.Count, n));
                }
                var chosen = pool.OrderBy(_ => r.Next()).Take(30).ToList();
                var lines = chosen.Select(p =>
                {
                    var parts = p.Name.Split(' ');
                    return r.Next(5) switch
                    {
                        0 => p.Name.ToUpperInvariant(),
                        1 => $"{parts[1]}, {parts[0]}",
                        2 => NameListMatcher.Fold(p.Name),
                        3 => $"{p.Name} | B1 | patrull {r.Next(1, 20)} 10:{r.Next(10, 60)}",
                        _ => "  " + p.Name + "  ",
                    };
                }).ToList();
                var res = NameListMatcher.Match(lines, pool);
                res.Should().HaveCount(30);
                for (var i = 0; i < 30; i++)
                {
                    // Vikt stavning kan ge fler kandidater (Åsa/Asa ...): då får det vara osäkert, men aldrig fel person.
                    if (res[i].Status == NameMatchStatus.Matched) res[i].Chosen!.MemberId.Should().Be(chosen[i].MemberId, $"round {round} rad {i}: {lines[i]}");
                }
                res.Count(x => x.Status == NameMatchStatus.Matched).Should().BeGreaterThan(25, $"round {round}: nästan alla ska hittas");
            }
        }
    }
}
