using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;
using static HpskSite.CompetitionTypes.Faltskytte.Services.MagnumStationRules;

namespace HpskSite.Tests
{
    public class MagnumStationRulesTests
    {
        // En bana som följer SHB: 8 stationer, station 1 utan poängmål, station 3 och 6 med poängmål, station 7 Hands-up (2 figurer).
        private static List<Station> Good() => new()
        {
            new(1, false, false, 3, 0, 6), new(2, false, false, 4, 0, 6), new(3, false, false, 4, 2, 6), new(4, false, false, 3, 0, 6),
            new(5, false, false, 3, 0, 6), new(6, false, false, 4, 3, 6), new(7, false, true, 2, 0, 5), new(8, false, false, 3, 0, 6),
        };

        [Fact]
        public void En_bana_enligt_SHB_ger_inga_anmarkningar() => Notes(Good()).Should().BeEmpty();

        [Fact]
        public void Tom_bana_ger_inget() => Notes(new List<Station>()).Should().BeEmpty();

        [Fact]
        public void Farre_an_sex_stationer_ger_ingen_standardmedalj()
        {
            var s = Good().Take(5).ToList();
            Notes(s).Should().Contain(n => n.Contains("ingen standardmedalj"));
        }

        [Fact]
        public void Sex_eller_sju_stationer_ar_under_SHB_rekommendation()
        {
            var s = Good().Where(x => x.Number != 8 && x.Number != 5).ToList(); // 6 kvar
            Notes(s).Should().Contain(n => n.Contains("minst 8"));
            Notes(s).Should().NotContain(n => n.Contains("ingen standardmedalj"));
        }

        [Fact]
        public void Station_ett_far_inte_ha_poangmal()
        {
            var s = Good(); s[0] = s[0] with { PoangmalFigures = 1 };
            Notes(s).Should().ContainSingle(n => n.Contains("Första stationen ska ge inskjutning"));
        }

        [Fact]
        public void Minst_tva_stationer_ska_ha_poangmal()
        {
            var s = Good().Select(x => x with { PoangmalFigures = x.Number == 3 ? 1 : 0 }).ToList();
            Notes(s).Should().ContainSingle(n => n.Contains("1 station med poängmål"));
            var none = Good().Select(x => x with { PoangmalFigures = 0 }).ToList();
            Notes(none).Should().Contain(n => n.Contains("0 stationer med poängmål"));
        }

        [Fact]
        public void Ingen_Hands_up_ger_anmarkning()
        {
            var s = Good().Select(x => x with { HandsUp = false }).ToList();
            Notes(s).Should().ContainSingle(n => n.Contains("Ingen station är Hands-up"));
        }

        [Fact]
        public void Tva_Hands_up_ar_for_manga_vid_atta_stationer_men_ok_vid_fler()
        {
            var eight = Good(); eight[3] = eight[3] with { HandsUp = true, Figures = 2 };
            Notes(eight).Should().ContainSingle(n => n.Contains("2 stationer är Hands-up"));
            var nine = Good(); nine.Add(new(9, false, false, 3, 0, 6)); nine[3] = nine[3] with { HandsUp = true, Figures = 2 };
            Notes(nine).Should().BeEmpty();
            var ten = nine.Concat(new[] { new Station(10, false, true, 2, 0, 6), new Station(11, false, true, 2, 0, 6) }).ToList();
            Notes(ten).Should().ContainSingle(n => n.Contains("4 stationer är Hands-up"));
        }

        [Fact]
        public void Hands_up_hogst_tva_figurer()
        {
            var s = Good(); s[6] = s[6] with { Figures = 3 };
            Notes(s).Should().ContainSingle(n => n.Contains("Station 7 (Hands-up) har 3 figurer"));
        }

        [Fact]
        public void Sarskjutningsstationen_ska_ha_poangmal_och_hogst_en_traff_per_figur()
        {
            var s = Good(); s.Add(new(9, true, false, 4, 2, 6));
            var notes = Notes(s);
            notes.Should().Contain(n => n.Contains("alla figurer ska vara poängmål"));
            notes.Should().Contain(n => n.Contains("högst en träff per figur"));
            var ok = Good(); ok.Add(new(9, true, false, 4, 4, 1));
            Notes(ok).Should().BeEmpty();
        }

        [Fact]
        public void Sarskjutningsstationen_raknas_inte_i_stationsantalet()
        {
            var s = Good().Take(6).ToList(); s.Add(new(9, true, false, 4, 4, 1)); // 6 poängstationer + särskjutning
            Notes(s).Should().NotContain(n => n.Contains("ingen standardmedalj"));
        }
    }
}
