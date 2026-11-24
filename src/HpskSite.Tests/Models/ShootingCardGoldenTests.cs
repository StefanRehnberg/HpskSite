using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>
    /// Facit för skjutkortens utskrift (Expertgruppen 2026-11-24): HTML-sidan för dagens layouter (<c>fig</c>, <c>tot</c>) i alla
    /// pappersformat får inte ändras när layoutregistret byggs in. Testet hashar hela dokumentet för en fast modell.
    /// Ändras utskriften medvetet: kör testet, läs det nya värdet i felmeddelandet och uppdatera <see cref="Expected"/>.
    /// </summary>
    public class ShootingCardGoldenTests
    {
        static List<ShootingCardModel> Model() => new()
        {
            new ShootingCardModel
            {
                PatrolNumber = 3, Label = "Patrull 3", StartTime = "09:42", WeaponGroupText = "A", Part = 1, Parts = 1,
                Stations = new List<ShootingCardStation>
                {
                    new(1, null, 3, 0), new(2, null, 1, 1), new(3, null, 6, 2), new(4, null, 2, 0), new(5, null, 4, 1)
                },
                Shooters = new List<ShootingCardShooter>
                {
                    new() { Name = "Kalle Anka", Club = "Ankeborgs PK", ShootingClass = "A2" },
                    new() { Name = "Kajsa Anka", Club = "Ankeborgs PK", ShootingClass = "B1" },
                    new() { Name = "Joakim von Anka", Club = "Gåsa SK", ShootingClass = "A3" }
                }
            },
            new ShootingCardModel
            {
                IsBlank = true,
                Stations = new List<ShootingCardStation> { new(1, null, 3, 0), new(2, null, 2, 1) },
                Shooters = Enumerable.Range(0, 4).Select(_ => new ShootingCardShooter()).ToList()
            }
        };

        // Radslut normaliseras: CSS-texten i renderaren är en verbatim-sträng och bär filens radslut (LF eller CRLF beroende på utcheckning).
        static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n"))))[..16];

        // layout/paper/per/sum/notes → hash av hela dokumentet
        // Värdena är tagna från koden som den såg ut före layoutregistret (commit 40fb35dc).
        static readonly Dictionary<string, string> Expected = new()
        {
            ["fig/portrait/1/sum0/notes1"] = "B043D03F525A8671",
            ["fig/portrait/2/sum0/notes1"] = "7609308989403C95",
            ["fig/landscape/1/sum0/notes1"] = "7CCF25E0DA2D5987",
            ["fig/landscape/2/sum0/notes1"] = "A698E5A989777DD7",
            ["tot/portrait/1/sum0/notes1"] = "97CD2002293D20BA",
            ["tot/portrait/2/sum0/notes1"] = "55273438D7EE2902",
            ["tot/landscape/1/sum0/notes1"] = "02D5B650E056385B",
            ["tot/landscape/2/sum0/notes1"] = "63752E2A8D1F8552",
            ["fig/landscape/1/sum1/notes0"] = "412678EE5BB669F2",
            ["tot/portrait/2/sum1/notes0"] = "C9B04A1A23D32F5A",
        };

        static IEnumerable<(string key, ShootingCardOptions o)> Cases()
        {
            foreach (var layout in new[] { "fig", "tot" })
                foreach (var paper in new[] { "portrait", "landscape" })
                    foreach (var per in new[] { 1, 2 })
                        yield return ($"{layout}/{paper}/{per}/sum0/notes1",
                            new ShootingCardOptions { CompetitionName = "Testtävling", Layout = layout, Paper = paper, PerSheet = per, SumColumn = false, Notes = true });
            yield return ("fig/landscape/1/sum1/notes0",
                new ShootingCardOptions { CompetitionName = "Testtävling", Layout = "fig", Paper = "landscape", PerSheet = 1, SumColumn = true, Notes = false });
            yield return ("tot/portrait/2/sum1/notes0",
                new ShootingCardOptions { CompetitionName = "Testtävling", Layout = "tot", Paper = "portrait", PerSheet = 2, SumColumn = true, Notes = false });
        }

        [Fact]
        public void Todays_layouts_render_exactly_as_before()
        {
            var actual = Cases().ToDictionary(c => c.key, c => Sha(ShootingCardRenderer.RenderDocument(Model(), c.o)));
            var report = string.Join("\n", actual.Select(a => $"            [\"{a.Key}\"] = \"{a.Value}\","));
            actual.Should().BeEquivalentTo(Expected, "dagens utskrift ska vara oförändrad. Nya värden:\n" + report);
        }

        [Fact]
        public void Rendering_is_deterministic()
        {
            var o = Cases().First().o;
            ShootingCardRenderer.RenderDocument(Model(), o).Should().Be(ShootingCardRenderer.RenderDocument(Model(), o));
        }
    }
}
