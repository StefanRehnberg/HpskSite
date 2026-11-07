using HpskSite.Extensions;
using Xunit;

namespace HpskSite.Tests;

/// <summary>
/// Klubbsidorna steg 2 (F1, säkerhetsgranskningen K2): webbläsaren normaliserar en adress före tolkning
/// (tabbar/radbrytningar bort, "\" läses som "/"), så "/\evil.com" och "/&lt;tab&gt;/evil.com" pekar på en annan värd.
/// </summary>
public class HtmlSanitizerOwnUrlTests
{
    [Theory]
    [InlineData("/media/a.jpg")]
    [InlineData("/halland/ankeborg-pistolklubb/banor/")]
    [InlineData("/x?y=1#z")]
    [InlineData("/media/min fil.pdf")]
    public void Own_urls_are_accepted(string url) => Assert.True(HpskHtmlSanitizer.IsOwnUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil.com")]
    [InlineData("/\\evil.com")]
    [InlineData("\\\\evil.com")]
    [InlineData("/\t/evil.com")]
    [InlineData("/\n/evil.com")]
    [InlineData("/\r/evil.com")]
    [InlineData("/a\\b")]
    [InlineData("evil.com")]
    [InlineData("https://evil.com")]
    public void Other_hosts_and_odd_forms_are_rejected(string? url) => Assert.False(HpskHtmlSanitizer.IsOwnUrl(url));

    [Theory]
    [InlineData("<a href=\"/\\evil.com\">x</a>")]
    [InlineData("<a href=\"/\t/evil.com\">x</a>")]
    [InlineData("<a href=\"\t//evil.com\">x</a>")]
    [InlineData("<a href=\"\\\\evil.com\">x</a>")]
    [InlineData("<a href=\"//evil.com\">x</a>")]
    [InlineData("<img src=\"/\\evil.com/t.gif\">")]
    [InlineData("<img src=\"/\t/evil.com/t.gif\">")]
    public void Links_and_images_to_another_host_are_removed(string input)
        => Assert.DoesNotContain("evil.com", HpskHtmlSanitizer.Sanitize(input), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Normal_relative_link_and_image_survive()
    {
        var html = HpskHtmlSanitizer.Sanitize("<p><a href=\"/halland/x/\">l</a><img src=\"/media/a.jpg\" alt=\"\"></p>");
        Assert.Contains("href=\"/halland/x/\"", html);
        Assert.Contains("src=\"/media/a.jpg\"", html);
    }
}
