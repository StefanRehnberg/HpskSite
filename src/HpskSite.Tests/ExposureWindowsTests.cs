using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests;

/// <summary>Fas 4 (2026-11-21): upprepad exponering. Visning 1 = de gamla fälten, extra visningar = Exposures.</summary>
public class ExposureWindowsTests
{
    static string W(FaltskytteFigure f, int t) => string.Join(" ", ExposureWindows.For(f, t).Select(b => $"{b[0]}-{b[1]}"));

    [Fact]
    public void Mikaels_exempel_A_B_A_B_med_fyra_sekunder_och_skjuttid_16()
    {
        var a = new FaltskytteFigure { Behavior = "Framsvängande", DelayBeforeShowSec = 0, ShowTimeSec = 4, Exposures = { new() { FromSec = 8, DurSec = 4 } } };
        var b = new FaltskytteFigure { Behavior = "Framsvängande", DelayBeforeShowSec = 4, ShowTimeSec = 4, Exposures = { new() { FromSec = 12, DurSec = 4 } } };
        Assert.Equal("0-4 8-12", W(a, 16));
        Assert.Equal("4-8 12-16", W(b, 16));
    }

    [Theory]
    [InlineData("Fast", null, null, null, null, "0-30")]
    [InlineData("Framsvängande", 3, 4, null, null, "3-7")]
    [InlineData("Framsvängande", null, null, null, null, "0-8")]
    [InlineData("Bortsvängande", null, null, 5, 10, "0-5 10-30")]
    [InlineData("Bortsvängande", null, null, null, null, "0-8")]
    [InlineData("Rörligt", null, null, null, null, "0-30")]
    [InlineData("Rörligt", 3, 4, null, null, "3-7")]
    public void Gamla_stationer_utan_extra_visningar_ger_samma_fonster_som_forut(string behavior, int? delay, int? show, int? hide, int? again, string expected)
        => Assert.Equal(expected, W(new FaltskytteFigure { Behavior = behavior, DelayBeforeShowSec = delay, ShowTimeSec = show, HideAfterSec = hide, ReappearSec = again }, 30));

    [Fact]
    public void Fonster_klamms_mot_skjuttiden_och_tomma_visningar_hoppas_over()
    {
        var f = new FaltskytteFigure { Behavior = "Framsvängande", DelayBeforeShowSec = 0, ShowTimeSec = 4, Exposures = { new() { FromSec = 14, DurSec = 10 }, new() { FromSec = 6, DurSec = 0 }, new() { FromSec = 40, DurSec = 3 } } };
        Assert.Equal("0-4 14-16", W(f, 16));
    }

    [Fact]
    public void Extra_visningar_sorteras_pa_start()
    {
        var f = new FaltskytteFigure { Behavior = "Framsvängande", ShowTimeSec = 2, Exposures = { new() { FromSec = 10, DurSec = 2 }, new() { FromSec = 5, DurSec = 2 } } };
        Assert.Equal("0-2 5-7 10-12", W(f, 30));
    }

    [Fact]
    public void Rorligt_med_bara_en_extra_visning_blir_ett_band_inte_hela_tiden()
    {
        var f = new FaltskytteFigure { Behavior = "Rörligt", Exposures = { new() { FromSec = 10, DurSec = 2 } } };
        Assert.Equal("0-8 10-12", W(f, 30));
    }

    [Fact]
    public void Sista_visningens_slut_ar_oklamd_for_varningen()
    {
        var f = new FaltskytteFigure { Behavior = "Framsvängande", DelayBeforeShowSec = 0, ShowTimeSec = 4, Exposures = { new() { FromSec = 14, DurSec = 6 } } };
        Assert.Equal(20, ExposureWindows.LastEndUnclamped(f));
        Assert.Equal(0, ExposureWindows.LastEndUnclamped(new FaltskytteFigure { Behavior = "Fast" }));
    }

    [Fact]
    public void Beteendetexten_namner_de_extra_visningarna()
    {
        var f = new FaltskytteFigure { Behavior = "Framsvängande", DelayBeforeShowSec = 0, ShowTimeSec = 4, Exposures = { new() { FromSec = 8, DurSec = 4 } } };
        Assert.Equal("Framsvängande, visas i 4 sek, visas igen 8 sek in, i 4 sek", StationCardRenderer.BehaviorText(f));
    }
}
