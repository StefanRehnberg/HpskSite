using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Testerna kontrollerar gränsschemats FORM, inte dess geografi. Att Halland gränsar till
    /// Kronoberg kan bara en människa med en karta avgöra; att varje kod finns i enumet, att varje
    /// gräns går båda vägarna och att ingen krets är en oavsiktlig ö kan koden avgöra — och det är
    /// de felen som annars smyger in när någon lägger till ett par.
    /// </summary>
    public class RegionAdjacencyTests
    {
        private static HashSet<string> AllRegionCodes() =>
            Enum.GetNames(typeof(Federations.RegionalFederations)).ToHashSet(StringComparer.Ordinal);

        [Fact]
        public void Every_declared_code_is_a_real_krets()
        {
            var valid = AllRegionCodes();
            var unknown = RegionAdjacency.DeclaredBorders
                .SelectMany(b => new[] { b.A, b.B })
                .Distinct()
                .Where(c => !valid.Contains(c))
                .ToList();

            Assert.True(unknown.Count == 0,
                "Okända kretskoder i gränsschemat (stavfel eller borttagen krets): " + string.Join(", ", unknown));
        }

        [Fact]
        public void No_krets_borders_itself()
        {
            var selfLoops = RegionAdjacency.DeclaredBorders
                .Where(b => string.Equals(b.A, b.B, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.A)
                .ToList();

            Assert.True(selfLoops.Count == 0, "Krets som gränsar till sig själv: " + string.Join(", ", selfLoops));
        }

        [Fact]
        public void No_border_is_declared_twice()
        {
            // Ett dubblerat par är inte farligt (mängden avdubblar) men det är alltid ett skrivfel,
            // och det gör listan svårare att läsa mot en karta.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dupes = new List<string>();
            foreach (var (a, b) in RegionAdjacency.DeclaredBorders)
            {
                var key = string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";
                if (!seen.Add(key)) dupes.Add(key);
            }
            Assert.True(dupes.Count == 0, "Dubblerade gränser: " + string.Join(", ", dupes));
        }

        [Fact]
        public void Adjacency_is_mutual()
        {
            // Spegling sker i Build(), så det här skulle bara kunna falla om någon slutade spegla —
            // vilket är precis den ändring som ger "A är nära B men inte B nära A".
            var asymmetric = new List<string>();
            foreach (var region in RegionAdjacency.RegionsWithNeighbours)
            {
                foreach (var neighbour in RegionAdjacency.NeighboursOf(region))
                {
                    if (!RegionAdjacency.NeighboursOf(neighbour).Contains(region))
                        asymmetric.Add($"{region} -> {neighbour}");
                }
            }
            Assert.True(asymmetric.Count == 0, "Ensidiga gränser: " + string.Join(", ", asymmetric));
        }

        [Fact]
        public void Only_deliberate_islands_have_no_neighbours()
        {
            // Ankeland är påhittad demodata och ska inte dra in riktiga kretsars tävlingar.
            // Alla ANDRA kretsar måste ha minst en granne, annars får deras medlemmar en tom
            // "Nära dig"-sektion utan att någon märker det.
            var deliberate = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ankeland" };

            var islands = AllRegionCodes()
                .Where(c => !deliberate.Contains(c))
                .Where(c => RegionAdjacency.NeighboursOf(c).Count == 0)
                .ToList();

            Assert.True(islands.Count == 0,
                "Kretsar utan grannar (får en tom Nära dig-sektion): " + string.Join(", ", islands));
        }

        [Fact]
        public void Ankeland_has_no_neighbours()
        {
            Assert.Empty(RegionAdjacency.NeighboursOf("Ankeland"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Finnmark")]
        public void Unknown_or_blank_gives_empty_not_null(string? code)
        {
            // Anropssidan ska aldrig behöva null-kolla; en okänd krets betyder "inga grannar".
            Assert.Empty(RegionAdjacency.NeighboursOf(code));
        }

        [Fact]
        public void Lookup_is_case_insensitive()
        {
            // NormalizeRegionCode gemenar koden på en del kodvägar, så en skiftlägeskänslig
            // uppslagning hade tystnat just där.
            Assert.Equal(
                RegionAdjacency.NeighboursOf("Halland").OrderBy(x => x),
                RegionAdjacency.NeighboursOf("halland").OrderBy(x => x));
            Assert.NotEmpty(RegionAdjacency.NeighboursOf("HALLAND"));
        }

        [Fact]
        public void NeighboursOfAny_excludes_the_members_own_kretsar()
        {
            // En granne som samtidigt är min egen krets hör i "Din krets", aldrig i "Nära dig" —
            // annars hamnar samma tävling i två sektioner.
            var own = new[] { "Halland", "Kronoberg" };
            var neighbours = RegionAdjacency.NeighboursOfAny(own);

            Assert.DoesNotContain("Halland", neighbours);
            Assert.DoesNotContain("Kronoberg", neighbours);
            // Kronoberg gränsar till Halland, och båda är egna — men grannarna utanför ska finnas.
            Assert.Contains("Jonkoping", neighbours);
            Assert.Contains("Kristianstad", neighbours);
        }

        [Fact]
        public void NeighboursOfAny_handles_no_kretsar()
        {
            // Medlem utan klubb: inga egna kretsar, alltså inga grannar och ingen sektion.
            Assert.Empty(RegionAdjacency.NeighboursOfAny(Array.Empty<string>()));
            Assert.Empty(RegionAdjacency.NeighboursOfAny(new[] { "", "  " }));
        }

        [Fact]
        public void A_spot_check_against_the_map()
        {
            // Några gränser som är svåra att få fel och lätta att råka radera vid en ombrytning.
            Assert.Contains("Kristianstad", RegionAdjacency.NeighboursOf("Malmohus"));
            Assert.Contains("Norrbotten", RegionAdjacency.NeighboursOf("Vasterbotten"));
            Assert.Contains("Uppsala", RegionAdjacency.NeighboursOf("Stockholm"));

            // Och några som INTE gränsar — hela Sverige emellan.
            Assert.DoesNotContain("Norrbotten", RegionAdjacency.NeighboursOf("Malmohus"));
            Assert.DoesNotContain("Stockholm", RegionAdjacency.NeighboursOf("Goteborg"));
        }
    }
}
