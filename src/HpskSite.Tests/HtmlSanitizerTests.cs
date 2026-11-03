using HpskSite.Extensions;
using Xunit;

namespace HpskSite.Tests;

public class HtmlSanitizerTests
{
    [Theory]
    [InlineData("<p>a<img src=x onerror=alert(1)></p>", "onerror")]
    [InlineData("<p onclick=\"x()\">a</p>", "onclick")]
    [InlineData("<script>alert(1)</script><p>a</p>", "script")]
    [InlineData("<a href=\"javascript:alert(1)\">a</a>", "javascript")]
    [InlineData("<iframe src=\"https://x.se\"></iframe>", "iframe")]
    [InlineData("<svg onload=alert(1)></svg>", "svg")]
    [InlineData("<img src=\"https://evil.example/t.gif\">", "evil.example")]
    [InlineData("<img src=\"//evil.example/t.gif\">", "evil.example")]
    public void Removes_active_content(string input, string forbidden)
        => Assert.DoesNotContain(forbidden, HpskHtmlSanitizer.Sanitize(input), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("<p>Hej <strong>du</strong></p>")]
    [InlineData("<h2>Rubrik</h2><ul><li>ett</li></ul>")]
    [InlineData("<a href=\"https://pistol.nu/x\">länk</a>")]
    [InlineData("<a href=\"mailto:a@b.se\">post</a>")]
    [InlineData("<figure><img src=\"/media/a.jpg\" alt=\"\"><figcaption>t</figcaption></figure>")]
    [InlineData("<table><tbody><tr><td colspan=\"2\">x</td></tr></tbody></table>")]
    public void Keeps_normal_content(string input)
        => Assert.Equal(input.Replace(" alt=\"\"", " alt=\"\""), HpskHtmlSanitizer.Sanitize(input));

    [Fact]
    public void SafeHtml_extracts_umbraco_json_markup()
        => Assert.Equal("<p>a</p>", RteHelper.SafeHtml("{\"markup\":\"<p onclick=x>a</p>\",\"blocks\":{}}").ToString());
}
