using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests;

/// <summary>Fas 3b (2026-11-21): Full eller Kompakt (1 sida) per station.</summary>
public class StationCardCompactTests
{
    static StationPrintItem Item(bool compact, bool withQr = true) => new()
    {
        Station = 3,
        Compact = compact,
        Qr1Url = withQr ? "/qr1" : "",
        Qr2Url = withQr ? "/qr2" : "",
        StationsByWeaponClass =
        {
            ["C"] = new FaltskytteStationConfig
            {
                Station = 3, ShootingTimeSec = 30, MinShotsPerFigure = 1,
                TargetGroups = { new FaltskytteTargetGroup { Group = 1, ImageUrl = "/g.jpg", Description = "Tre figurer på rad",
                    Figures = { new FaltskytteFigure { FigureNumber = 1, IsPoangmal = true, ImageUrl = "/f.jpg" } } } },
            }
        }
    };

    static string Render(StationPrintItem item, StationCardToolbar? toolbar = null)
        => StationCardRenderer.RenderDocument("t", null, StationCardRenderer.BuildBlocks(item, true, null), false, false, toolbar);

    [Fact]
    public void Full_visar_bilder_och_QR_pa_skilda_sidor()
    {
        var html = Render(Item(false));
        Assert.Contains("src=\"/g.jpg\"", html);
        Assert.Contains("src=\"/f.jpg\"", html);
        Assert.DoesNotContain("qr-row", html.Split("</style>")[1]);
        Assert.DoesNotContain("station-block compact", html);
    }

    [Fact]
    public void Kompakt_utelamnar_bilder_men_behaller_karnan_och_QR_bredvid_varandra()
    {
        var html = Render(Item(true));
        var body = html.Split("</style>")[1];
        Assert.DoesNotContain("src=\"/g.jpg\"", body);
        Assert.DoesNotContain("src=\"/f.jpg\"", body);
        Assert.Contains("station-block compact", body);
        // Kärnan (SHB D.9.14): skjuttid, utgångsläge, målgrupp med beskrivning, figur med poängmarkering, båda QR-koderna.
        Assert.Contains("Skjuttid:", body);
        Assert.Contains("Utgångsläge:", body);
        Assert.Contains("Min träff/fig:", body);
        Assert.Contains("Målgrupp A", body);
        Assert.Contains("Tre figurer på rad", body);
        Assert.Contains("Poängräkning", body);
        Assert.Contains("alt=\"QR Förutsättningar\"", body);
        Assert.Contains("alt=\"QR Resultat\"", body);
        Assert.Contains("<div class=\"qr-row\">", body);
    }

    [Fact]
    public void Kompakt_utan_QR_ger_ingen_QR_rad_och_balanserad_html()
    {
        var body = Render(Item(true, withQr: false)).Split("</style>")[1];
        Assert.DoesNotContain("qr-row", body);
        Assert.Equal(Count(body, "<div"), Count(body, "</div>"));
    }

    [Fact]
    public void QR_raden_ar_balanserad_i_kompakt_lage()
    {
        var body = Render(Item(true)).Split("</style>")[1];
        Assert.Equal(Count(body, "<div"), Count(body, "</div>"));
    }

    [Fact]
    public void Verktygsfaltet_finns_bara_med_toolbar_och_far_aldrig_skrivas_ut()
    {
        Assert.DoesNotContain("card-toolbar\"", Render(Item(false)).Split("</style>")[1]);
        var html = Render(Item(false), new StationCardToolbar { Station = 3, WeaponClass = "C", Mode = StationPrintMode.Full, CanSetMode = true });
        Assert.Contains("Kompakt (1 sida)", html);
        Assert.Contains("setMode('Kompakt')", html);
        Assert.Contains(".card-toolbar { display: none !important; }", html); // i @media print
    }

    [Fact]
    public void Verktygsfaltets_knappar_ar_avstangda_nar_laget_inte_kan_sparas()
    {
        var html = Render(Item(false), new StationCardToolbar { Station = 3, WeaponClass = "C", CanSetMode = false });
        Assert.Contains(" disabled ", html);
    }

    [Fact]
    public void Klassnamnet_i_verktygsfaltet_kan_inte_bryta_ut_ur_skriptet()
    {
        var html = Render(Item(false), new StationCardToolbar { Station = 3, WeaponClass = "C');alert(1);//", CanSetMode = true });
        Assert.DoesNotContain("alert(1)", html);
    }

    [Theory]
    [InlineData("{\"_attachedConfigId\":42}", 42)]
    [InlineData("{\"_attachedConfigId\":\"7\"}", 7)]
    [InlineData("{\"C\":{}}", 0)]
    [InlineData("", 0)]
    [InlineData("inte json", 0)]
    public void Ansluten_konfigurations_id_lases_ur_stationskonfigurationen(string json, int expected)
        => Assert.Equal(expected, StationCardRenderer.ReadAttachedConfigId(json));

    static int Count(string s, string needle) { var n = 0; var i = 0; while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; } return n; }
}
