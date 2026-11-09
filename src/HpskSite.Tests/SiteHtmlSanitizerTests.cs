using HpskSite.Extensions;
using Xunit;

namespace HpskSite.Tests;

/// <summary>Klubbsidorna 2a: sanerarprofilen för Text-block (striktare än HpskHtmlSanitizer).</summary>
public class SiteHtmlSanitizerTests
{
    [Theory]
    [InlineData("<script>alert(1)</script><p>hej</p>", "script")]
    [InlineData("<p onclick=\"x()\">hej</p>", "onclick")]
    [InlineData("<p class=\"btn\" id=\"a\" style=\"color:red\">hej</p>", "class")]
    [InlineData("<p style=\"color:red\">hej</p>", "style")]
    [InlineData("<p id=\"a\">hej</p>", "id=")]
    [InlineData("<div><span>hej</span></div>", "<div")]
    [InlineData("<span>hej</span>", "<span")]
    [InlineData("<iframe src=\"https://x.se\"></iframe>", "iframe")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript")]
    [InlineData("<a href=\"data:text/html,x\">x</a>", "data:")]
    [InlineData("<a href=\"https://x.se\" target=\"_blank\">x</a>", "target")]
    [InlineData("<p data-x=\"1\">hej</p>", "data-x")]
    public void Förbjudet_innehåll_tas_bort(string input, string forbidden)
        => Assert.DoesNotContain(forbidden, SiteHtmlSanitizer.Sanitize(input), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("<img src=\"https://evil.com/a.png\" alt=\"x\">")]
    [InlineData("<img src=\"//evil.com/a.png\" alt=\"x\">")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\" alt=\"x\">")]
    [InlineData("<img src=\"javascript:alert(1)\" alt=\"x\">")]
    [InlineData("<img src=\"/media/a.jpg\" alt=\"x\">")]
    [InlineData("<img src=\"/umbraco/surface/SiteFile/Image?id=5&x=1\" alt=\"x\">")]
    [InlineData("<img alt=\"x\">")]
    public void En_bild_i_texten_utan_sajtens_egen_adress_tas_bort(string input)
        => Assert.DoesNotContain("<img", SiteHtmlSanitizer.Sanitize(input), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void En_bild_med_sajtens_egen_adress_behålls_med_beskrivningen_men_utan_andra_attribut()
    {
        var html = SiteHtmlSanitizer.Sanitize("<figure class=\"image\"><img src=\"/umbraco/surface/SiteFile/Image?id=12\" alt=\"Skyttar\" onerror=\"x()\" style=\"width:9px\"></figure>");
        Assert.Contains("src=\"/umbraco/surface/SiteFile/Image?id=12\"", html);
        Assert.Contains("alt=\"Skyttar\"", html);
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("style", html);
        Assert.DoesNotContain("class", html);
    }

    [Fact]
    public void Bilderna_en_text_bäddar_in_räknas_så_att_de_hålls_kvar_och_serveras_rätt()
    {
        var ids = SiteHtmlSanitizer.InlineFileIds("<p><img src=\"/umbraco/surface/SiteFile/Image?id=7\" alt=\"a\"></p><img src=\"/umbraco/surface/SiteFile/Image?id=9\" alt=\"b\"><img src=\"/umbraco/surface/SiteFile/Image?id=7\" alt=\"c\">");
        Assert.Equal(new[] { 7, 9 }, ids.OrderBy(i => i));
        Assert.Empty(SiteHtmlSanitizer.InlineFileIds("<p>ingen bild</p>"));
    }

    [Theory]
    [InlineData("<a href=\"https://pistol.nu@evil.com/x\">x</a>")]
    [InlineData("<a href=\"https://user:pw@evil.com/\">x</a>")]
    [InlineData("<a href=\"//evil.com\">x</a>")]
    [InlineData("<a href=\"/\\evil.com\">x</a>")]
    [InlineData("<a href=\"/\t/evil.com\">x</a>")]
    [InlineData("<a href=\"\\\\evil.com\">x</a>")]
    [InlineData("<a href=\"http://evil.com\">x</a>")]
    public void Länkar_till_annan_värd_eller_med_användarinfo_tas_bort(string input)
        => Assert.DoesNotContain("evil.com", SiteHtmlSanitizer.Sanitize(input), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Varje_länk_får_rel_med_nofollow_och_ugc()
    {
        var html = SiteHtmlSanitizer.Sanitize("<a href=\"https://svenskskyttesport.se\" rel=\"opener\">x</a><a href=\"/halland/\">y</a><a href=\"mailto:a@b.se\">m</a>");
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(html, "rel=\"noopener noreferrer nofollow ugc\"").Count);
        Assert.DoesNotContain("opener\"", html.Replace("noopener", ""));
    }

    [Fact]
    public void Tillåtet_innehåll_överlever()
    {
        var html = SiteHtmlSanitizer.Sanitize(
            "<h2>Regler</h2><p><strong>Fet</strong> <em>kursiv</em> <a href=\"https://pistol.nu/x\">länk</a></p><ul><li>a</li></ul>" +
            "<table><thead><tr><th colspan=\"2\">H</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>");
        foreach (var keep in new[] { "<h2>", "<strong>", "<em>", "href=\"https://pistol.nu/x\"", "<ul>", "<li>", "<table>", "colspan=\"2\"", "<td>" })
            Assert.Contains(keep, html);
    }

    [Theory]
    [InlineData("https://pistol.nu/x", true)]
    [InlineData("/halland/ankeborg-pistolklubb/", true)]
    [InlineData("mailto:a@b.se", true)]
    [InlineData("tel:+46701234567", true)]
    [InlineData("#kontakt", true)]
    [InlineData("http://x.se", false)]
    [InlineData("//x.se", false)]
    [InlineData("/\\x.se", false)]
    [InlineData("https://a@x.se", false)]
    [InlineData("java\tscript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedLink(string? url, bool expected) => Assert.Equal(expected, SiteHtmlSanitizer.IsAllowedLink(url));

    [Fact]
    public void Tom_indata_ger_tom_sträng()
    {
        Assert.Equal("", SiteHtmlSanitizer.Sanitize(null));
        Assert.Equal("", SiteHtmlSanitizer.Sanitize("   "));
    }
}
