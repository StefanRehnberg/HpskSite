using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests;

/// <summary>Magnum-omtaget (2026-11-22): stationskortet för vapengrupp M visar det SHB Del E kräver (E.9.14, E.6.x, E.10.5.1). Vanlig fält oförändrat.</summary>
public class StationCardMagnumTests
{
    static FaltskytteStationConfig Station(int n, string weapon = "Riktning tillåten", int max = 6, bool poangmal = false) => new()
    {
        Station = n, ShootingTimeSec = 40, ShooterStartPosition = "Stående", WeaponStartPosition = weapon, SupportHand = "Ej stödhand",
        MaxShotsPerFigure = max,
        TargetGroups = { new FaltskytteTargetGroup { Group = 1, Figures = { new FaltskytteFigure { FigureNumber = 1, Behavior = "Fast", IsPoangmal = poangmal } } } }
    };

    static string Html(string wc, FaltskytteStationConfig st)
    {
        var it = new StationPrintItem { Station = st.Station, Qr1Url = "/qr1", Qr2Url = "/qr2" };
        it.StationsByWeaponClass[wc] = st;
        return StationCardRenderer.RenderDocument("t", null, StationCardRenderer.BuildBlocks(it, false, null, null), false, false);
    }

    [Fact]
    public void Magnumkort_har_sex_skott_och_traffytan()
    {
        var html = Html("M", Station(3));
        Assert.Contains("sex skott på stationen", html);
        Assert.Contains("rikoschett är bom", html);
    }

    [Fact]
    public void Max_traff_per_figur_star_alltid_pa_Magnumkortet_aven_nar_det_ar_6()
    {
        Assert.Contains("Max träff/fig:</strong> 6", Html("M", Station(3, max: 6)));
        Assert.Contains("Max träff/fig:</strong> 2", Html("M", Station(3, max: 2)));
    }

    [Fact]
    public void Vanlig_falt_visar_fortfarande_inte_max_traff_nar_det_ar_6_och_inget_Magnumstycke()
    {
        var html = Html("C", Station(3, max: 6));
        Assert.DoesNotContain("Max träff/fig", html);
        Assert.DoesNotContain("magnum-note\">", html);   // CSS-regeln finns, men ingen ruta
        Assert.DoesNotContain("sex skott på stationen", html);
    }

    [Fact]
    public void Poangmal_ger_raden_om_haltpalen_bara_nar_stationen_har_poangmal()
    {
        Assert.Contains("vid haltpålen", Html("M", Station(3, poangmal: true)));
        Assert.DoesNotContain("vid haltpålen", Html("M", Station(3, poangmal: false)));
    }

    [Fact]
    public void HandsUp_ger_proceduren_och_underlagskravet()
    {
        var html = Html("M", Station(7, weapon: "Hands-up"));
        Assert.Contains("Hands-up:</strong>", html);
        Assert.Contains("10 sekunder kvar", html);
        Assert.Contains("skydda vapnet mot smuts", html);
        Assert.DoesNotContain("10 sekunder kvar", Html("M", Station(7, weapon: "Riktning tillåten")));
    }

    [Fact]
    public void HandsUp_pa_en_vanlig_vapengrupp_ger_inget_Magnumstycke()
        => Assert.DoesNotContain("10 sekunder kvar", Html("A", Station(7, weapon: "Hands-up")));
}
