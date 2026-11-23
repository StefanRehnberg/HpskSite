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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bilder_tidslinje_och_bada_QR_finns_i_bada_lagen(bool compact)
    {
        var body = Render(Item(compact)).Split("</style>")[1];
        Assert.Contains("src=\"/g.jpg\"", body);
        Assert.Contains("src=\"/f.jpg\"", body);
        Assert.Contains("class=\"tl-bar\"", body);
        Assert.Contains("left:0%;width:100%", body); // Fast: synlig hela skjuttiden (30 s)
        Assert.Contains("alt=\"QR Förutsättningar\"", body);
        Assert.Contains("alt=\"QR Resultat\"", body);
        Assert.Contains("<div class=\"qr-cut\">", body); // Resultat-QR på egen sida
        Assert.True(body.IndexOf("alt=\"QR Förutsättningar\"") < body.IndexOf("<div class=\"qr-cut\">"));
        Assert.Equal(compact, body.Contains("station-block compact"));
    }

    [Fact]
    public void Resultat_QR_utelamnas_nar_den_inte_ar_satt_men_Forutsattnings_QR_star_kvar()
    {
        var item = Item(false);
        item.Qr2Url = "";
        var body = Render(item).Split("</style>")[1];
        Assert.Contains("alt=\"QR Förutsättningar\"", body);
        Assert.DoesNotContain("QR Resultat", body);
    }

    [Fact]
    public void Framsvangande_figur_far_gront_falt_for_sitt_fonster()
    {
        var item = Item(false);
        var fig = item.StationsByWeaponClass["C"].TargetGroups[0].Figures[0];
        fig.Behavior = "Framsvängande"; fig.DelayBeforeShowSec = 3; fig.ShowTimeSec = 15;
        var body = Render(item).Split("</style>")[1];
        Assert.Contains("left:10%;width:50%", body); // 3–18 s av 30 s
    }

    [Fact]
    public void Kompakt_behaller_karnan_och_QR_bredvid_varandra()
    {
        var html = Render(Item(true));
        var body = html.Split("</style>")[1];
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
        Assert.Contains(">Kompakt</button>", html);
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
