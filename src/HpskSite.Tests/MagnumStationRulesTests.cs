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
            Notes(s).Should().ContainSingle(n => n.Contains("1 station med giltigt poängmål"));
            var none = Good().Select(x => x with { PoangmalFigures = 0 }).ToList();
            Notes(none).Should().Contain(n => n.Contains("0 stationer med giltigt poängmål"));
        }

        [Fact]
        public void Station_ett_raknas_aldrig_som_poangmalsstation()
        {
            // Station 1 + station 3 har poängmål: bara station 3 är giltig, så en till krävs, och raderna säger inte emot varandra.
            var s = Good().Select(x => x with { PoangmalFigures = x.Number == 1 || x.Number == 3 ? 1 : 0 }).ToList();
            var notes = Notes(s);
            notes.Should().Contain(n => n.Contains("Första stationen ska ge inskjutning") && n.Contains("räknas inte med"));
            notes.Should().ContainSingle(n => n.Contains("1 station med giltigt poängmål") && n.Contains("utan station 1"));
            // Station 1 + 3 + 6: station 3 och 6 räcker, så kravet är uppfyllt trots station 1.
            var ok = Good().Select(x => x with { PoangmalFigures = x.Number is 1 or 3 or 6 ? 1 : 0 }).ToList();
            Notes(ok).Should().NotContain(n => n.Contains("Minst två stationer"));
            // Bara station 1 har poängmål: noll giltiga.
            var onlyFirst = Good().Select(x => x with { PoangmalFigures = x.Number == 1 ? 1 : 0 }).ToList();
            Notes(onlyFirst).Should().Contain(n => n.Contains("0 stationer med giltigt poängmål") && n.Contains("utan station 1"));
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
            var ok = Good(); ok.Add(new(9, true, false, 6, 6, 1));
            Notes(ok).Should().BeEmpty();
        }

        [Fact]
        public void Lurifaxen_far_inte_vara_poangmal()
        {
            var s = Good(); s[2] = s[2] with { ForbiddenPoangmal = 1 };
            Notes(s).Should().ContainSingle(n => n.Contains("Station 3: Lurifaxen får inte vara poängmål"));
            Notes(Good()).Should().NotContain(n => n.Contains("Lurifaxen"));
            ShbFieldRules.IsNeverPoangmal("Lurifaxen 18x27cm").Should().BeTrue();
            ShbFieldRules.IsNeverPoangmal("Ovalen nr 3").Should().BeFalse();
        }

        [Fact]
        public void En_station_som_inte_kan_ge_sex_traff_varnar()
        {
            // En figur med högst 2 träff: kapaciteten 2 < 6 (E.6.4 sex skott).
            var s = Good(); s[2] = s[2] with { Figures = 1, MaxShotsPerFigure = 2 };
            Notes(s).Should().ContainSingle(n => n.Contains("Station 3 kan högst ge 2 träff"));
            // Två figurer med max 3 = 6: precis nog. Hands-up med två figurer och max 5 = 10: ok.
            var enough = Good(); enough[2] = enough[2] with { Figures = 2, MaxShotsPerFigure = 3 };
            Notes(enough).Should().NotContain(n => n.Contains("kan högst ge"));
            Notes(Good()).Should().NotContain(n => n.Contains("kan högst ge"));
            // Mål per figur räknas: en figur med två mål och max 3 = 6.
            var targets = Good(); targets[2] = targets[2] with { Figures = 1, MaxShotsPerFigure = 3, Slots = 2 };
            Notes(targets).Should().NotContain(n => n.Contains("kan högst ge"));
            // Särskjutningsstation med fyra figurer och max 1 träff ger bara 4.
            var shootOff = Good(); shootOff.Add(new(9, true, false, 4, 4, 1));
            Notes(shootOff).Should().ContainSingle(n => n.Contains("Särskjutningsstation 9 kan högst ge 4 träff"));
        }

        [Fact]
        public void Sarskjutningsstationen_raknas_inte_i_stationsantalet()
        {
            var s = Good().Take(6).ToList(); s.Add(new(9, true, false, 4, 4, 1)); // 6 poängstationer + särskjutning
            Notes(s).Should().NotContain(n => n.Contains("ingen standardmedalj"));
        }
    }
}
