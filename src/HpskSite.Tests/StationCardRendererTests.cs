using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests;

/// <summary>Fas 3a (2026-11-21): EN renderare för stationskortet, för tävlingens kort och konfiguratorns utskrift.</summary>
public class StationCardRendererTests
{
    static FaltskytteStationConfig Station(int n, int sec = 30, string support = "Ej stödhand", int max = 6, string? desc = null, string behavior = "Fast",
        int? delay = null, int? show = null, string? name = null) => new()
    {
        Station = n, Name = name, ShootingTimeSec = sec, ShooterStartPosition = "Stående", WeaponStartPosition = "45 grader",
        SupportHand = support, MaxShotsPerFigure = max,
        TargetGroups = { new FaltskytteTargetGroup { Group = 1, Description = desc, Figures = { new FaltskytteFigure { FigureNumber = 1, Behavior = behavior, DelayBeforeShowSec = delay, ShowTimeSec = show } } } },
    };

    static StationPrintItem Item(params (string wc, FaltskytteStationConfig st)[] classes)
    {
        var it = new StationPrintItem { Station = classes[0].st.Station, Qr1Url = "/qr1", Qr2Url = "/qr2" };
        foreach (var (wc, st) in classes) it.StationsByWeaponClass[wc] = st;
        return it;
    }

    static string Html(StationPrintItem item, bool strict = false, string? only = null, bool poang = false, IReadOnlyList<int>? course = null)
        => StationCardRenderer.RenderDocument("t", null, StationCardRenderer.BuildBlocks(item, strict, course, only), poang, false);

    [Theory]
    [InlineData("Framsvängande", null, null, "Framsvängande")]
    [InlineData("Framsvängande", 3, null, "Framsvängande, fram efter 3 sek")]
    [InlineData("Framsvängande", 3, 4, "Framsvängande, fram efter 3 sek, visas i 4 sek")]
    [InlineData("Framsvängande", 0, 0, "Framsvängande")]
    [InlineData("Rörligt", 3, 4, "Rörligt, fram efter 3 sek, visas i 4 sek")]
    [InlineData("Uppdykande", 3, 4, "Fast")]
    [InlineData("Fast", null, null, "Fast")]
    public void Beteendetexten_visar_bara_det_som_sparats(string behavior, int? delay, int? show, string expected)
        => Assert.Equal(expected, StationCardRenderer.BehaviorText(new FaltskytteFigure { Behavior = behavior, DelayBeforeShowSec = delay, ShowTimeSec = show }));

    [Fact]
    public void Rorligt_far_samma_tider_som_Framsvangande_och_text_utan_tider_ar_oforandrad()
    {
        Assert.Equal("Rörligt, fram efter 3 sek, visas i 8 sek",
            StationCardRenderer.BehaviorText(new FaltskytteFigure { Behavior = "Rörligt", DelayBeforeShowSec = 3, ShowTimeSec = 8 }));
        Assert.Equal("Rörligt", StationCardRenderer.BehaviorText(new FaltskytteFigure { Behavior = "Rörligt" }));
    }

    [Fact]
    public void Bortsvangande_text()
        => Assert.Equal("Bortsvängande, bort efter 5 sek, visar igen efter 10 sek",
            StationCardRenderer.BehaviorText(new FaltskytteFigure { Behavior = "Bortsvängande", HideAfterSec = 5, ReappearSec = 10 }));

    [Fact]
    public void Standardvardet_8_skrivs_aldrig_ut_for_en_tid_som_ingen_sparat()
    {
        var html = Html(Item(("C", Station(1, behavior: "Framsvängande"))));
        Assert.DoesNotContain(" 8 s", html);
        Assert.Contains("Figur 1</strong> — Framsvängande<", html.Replace("\n", ""));
    }

    [Fact]
    public void Text_fran_anvandaren_kodas()
    {
        var html = Html(Item(("C", Station(1, desc: "<script>alert(1)</script>\nrad två", name: "Skog <b>"))));
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;<br>rad två", html);
        Assert.Contains("Skog &lt;b&gt;", html);
    }

    [Fact]
    public void Klasser_med_samma_figurer_delar_kort_och_gemensamma_varden_hamnar_pa_en_rad()
    {
        var html = Html(Item(("C", Station(1, 12)), ("B", Station(1, 16)), ("A", Station(1, 14)), ("R", Station(1, 14))));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"station-block\""));
        Assert.Contains("<div class=\"rules-shared\"><strong>Utgångsläge:</strong> Stående", html);
        Assert.Contains("<td>A, R</td><td>14 sek</td>", html);   // A och R har samma uppsättning: en rad
        Assert.DoesNotContain("Vapenklass C, B", html);          // alla klasser delar kortet: ingen klasslista i rubriken
    }

    [Fact]
    public void Klasser_med_olika_figurer_far_var_sitt_kort_med_klasserna_i_rubriken()
    {
        var html = Html(Item(("C", Station(1, desc: "a")), ("B", Station(1, desc: "a")), ("A", Station(1, desc: "annan"))));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "class=\"station-block\"").Count);
        Assert.Contains("— Vapenklass C, B</h1>", html);
        Assert.Contains("— Vapenklass A</h1>", html);
    }

    [Fact]
    public void En_klass_far_klassen_i_rubriken()
        => Assert.Contains("<h1>Station 1 — Vapenklass C</h1>", Html(Item(("C", Station(1)))));

    [Fact]
    public void Konfiguratorn_skriver_bara_gruppen_runt_den_valda_klassen()
    {
        var item = Item(("C", Station(1, desc: "a")), ("B", Station(1, desc: "a")), ("A", Station(1, desc: "annan")));
        var blocks = StationCardRenderer.BuildBlocks(item, false, null, "B");
        var b = Assert.Single(blocks);
        Assert.Equal(new[] { "C", "B" }, b.Classes);   // klasserna står i fast ordning, kortet läses ur B
        Assert.Equal("B", b.PrimaryClass);
        Assert.True(b.ShowClassesInHeader);
    }

    [Fact]
    public void Avancerat_lage_kraver_lika_forutsattningar()
    {
        var item = Item(("C", Station(1, 12)), ("B", Station(1, 16, max: 3)));
        Assert.Equal(2, StationCardRenderer.BuildBlocks(item, strict: true, null).Count);
        Assert.Single(StationCardRenderer.BuildBlocks(item, strict: false, null));
    }

    [Fact]
    public void Max_traff_per_figur_visas_inte_i_Poang_men_Min_gor()
    {
        var one = Item(("C", Station(1, max: 2)));
        Assert.Contains("Max träff/fig:", Html(one));
        Assert.DoesNotContain("Max träff/fig:", Html(one, poang: true));
        var two = Item(("C", Station(1, max: 2)), ("B", Station(1, max: 2)));
        Assert.DoesNotContain("Max träff/fig", Html(two, poang: true));
    }

    [Fact]
    public void QR_koderna_en_gang_per_station_pa_sista_kortet()
    {
        var item = Item(("C", Station(1, desc: "a")), ("A", Station(1, desc: "annan")));
        var html = Html(item);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "alt=\"QR Förutsättningar\""));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "alt=\"QR Resultat\""));
        Assert.DoesNotContain("QR Förutsättningar", Html(new StationPrintItem { Station = 1, StationsByWeaponClass = { ["C"] = Station(1) } }));
    }

    [Fact]
    public void Stationsnamn_och_banans_nummer_i_rubriken()
        => Assert.Contains("<h1>Station 2 – Skog · tävlingens station 7 — Vapenklass C</h1>",
            Html(Item(("C", Station(2, name: "Skog"))), course: new[] { 5, 7 }));

    [Fact]
    public void Olika_tider_per_klass_for_en_rorlig_figur_skrivs_per_klassgrupp()
    {
        var html = Html(Item(("C", Station(1, 12, behavior: "Framsvängande", delay: 6, show: 6)), ("B", Station(1, 16, behavior: "Framsvängande", delay: 8, show: 8))));
        Assert.Contains("C: fram efter 6 sek, visas i 6 sek", html);
        Assert.Contains("B: fram efter 8 sek, visas i 8 sek", html);
    }

    [Fact]
    public void ReadMeta_laser_lage_och_banans_stationer()
    {
        var (strict, course) = StationCardRenderer.ReadMeta("{\"_mode\":\"simple\",\"_courseStations\":[4,0,9],\"C\":{\"stations\":[]}}");
        Assert.False(strict);
        Assert.Equal(new[] { 4, 0, 9 }, course);
        Assert.True(StationCardRenderer.ReadMeta("{\"C\":{}}").Strict);
    }
}
