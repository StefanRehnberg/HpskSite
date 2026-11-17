using HpskSite.Extensions;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Signaturen i texten (avatar, namn, roll): vad som får stå i den lagrade HTML:en, vem som får skriva in en, och hur den ritas.
    /// Det som kräver databas (vem som hör till föreningen, rollerna) prövas i hpsk-verify/signatur-verify.mjs.
    /// </summary>
    public class SiteSignaturesTests
    {
        private const string Mine = "<hpsk-signatur member=\"5513\"></hpsk-signatur>";
        private const string Other = "<hpsk-signatur member=\"5514\"></hpsk-signatur>";

        // ── Saneraren: bara ett medlemsnummer, inget annat ──────────────────────────────────────────────────────────────

        [Fact]
        public void Sanitizer_keeps_a_signature_with_only_a_member_number()
        {
            Assert.Equal("<p>Hej</p>" + Mine, SiteHtmlSanitizer.Sanitize("<p>Hej</p>" + Mine));
        }

        [Theory]
        [InlineData("<hpsk-signatur member=\"abc\"></hpsk-signatur>")]
        [InlineData("<hpsk-signatur member=\"0\"></hpsk-signatur>")]
        [InlineData("<hpsk-signatur member=\"-5\"></hpsk-signatur>")]
        [InlineData("<hpsk-signatur member=\"1234567890\"></hpsk-signatur>")]   // tio siffror: ryms inte i ett medlemsnummer
        [InlineData("<hpsk-signatur></hpsk-signatur>")]
        public void Sanitizer_removes_a_signature_without_a_valid_number(string html) =>
            Assert.DoesNotContain("hpsk-signatur", SiteHtmlSanitizer.Sanitize("<p>x</p>" + html));

        [Fact]
        public void Sanitizer_strips_everything_but_the_number_from_a_signature()
        {
            var html = SiteHtmlSanitizer.Sanitize("<hpsk-signatur member=\"5513\" onclick=\"x()\" class=\"a\" style=\"width:1px\" title=\"y\">Ordförande <b>text</b></hpsk-signatur>");
            Assert.Equal(Mine, html);
        }

        [Fact]
        public void The_member_attribute_exists_only_on_a_signature()
        {
            var html = SiteHtmlSanitizer.Sanitize("<p member=\"5513\">x</p>");
            Assert.DoesNotContain("member", html);
        }

        // ── Vem som får skriva in en ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Restrict_keeps_the_writers_own_signature_and_drops_other_peoples()
        {
            var r = SiteSignatures.Restrict("<p>x</p>" + Mine + Other, writer: 5513, new HashSet<int>());
            Assert.Contains(Mine, r);
            Assert.DoesNotContain("5514", r);
        }

        [Fact]
        public void Restrict_keeps_a_signature_that_was_already_there()
        {
            var r = SiteSignatures.Restrict(Mine + Other, writer: 5513, new HashSet<int> { 5514 });
            Assert.Contains(Mine, r);
            Assert.Contains(Other, r);
        }

        [Fact]
        public void Restrict_without_a_writer_keeps_only_what_was_already_there()
        {
            var r = SiteSignatures.Restrict(Mine + Other, writer: null, new HashSet<int> { 5514 });
            Assert.DoesNotContain("5513", r);
            Assert.Contains("5514", r);
        }

        [Fact]
        public void RestrictBlocksJson_works_through_every_text_block_and_leaves_other_blocks_alone()
        {
            var json = "{\"schema\":1,\"blocks\":[{\"id\":\"a\",\"type\":\"text\",\"visibility\":0,\"data\":{\"html\":\"<p>x</p>" + Other.Replace("\"", "\\\"") +
                       "\"}},{\"id\":\"b\",\"type\":\"heading\",\"visibility\":0,\"data\":{\"text\":\"Rubrik\"}}]}";
            var r = SiteSignatures.RestrictBlocksJson(json, 5513, new HashSet<int>());
            Assert.DoesNotContain("5514", r);
            Assert.Contains("Rubrik", r);
            Assert.Contains("<p>x</p>", r);
        }

        [Fact]
        public void RestrictBlocksJson_returns_the_same_text_when_nothing_changes()
        {
            var json = "{\"schema\":1,\"blocks\":[{\"id\":\"a\",\"type\":\"text\",\"visibility\":0,\"data\":{\"html\":\"<p>x</p>\"}}]}";
            Assert.Same(json, SiteSignatures.RestrictBlocksJson(json, 5513, new HashSet<int>()));
        }

        [Fact]
        public void IdsInBlocksJson_finds_the_numbers_in_text_blocks_only()
        {
            var json = "{\"schema\":1,\"blocks\":[{\"id\":\"a\",\"type\":\"text\",\"visibility\":0,\"data\":{\"html\":\"" + (Mine + Other).Replace("\"", "\\\"") + "\"}}]}";
            Assert.Equal(new HashSet<int> { 5513, 5514 }, SiteSignatures.IdsInBlocksJson(json));
            Assert.Empty(SiteSignatures.IdsInBlocksJson("inte json"));
            Assert.Empty(SiteSignatures.IdsInBlocksJson(null));
        }

        [Theory]
        [InlineData("12,45", new[] { 12, 45 })]
        [InlineData(" 7 , x, -3, 0, 7", new[] { 7 })]
        [InlineData("", new int[0])]
        [InlineData(null, new int[0])]
        public void ParseIds_keeps_only_positive_numbers(string? csv, int[] expected) =>
            Assert.Equal(expected, SiteSignatures.ParseIds(csv));

        // ── Utritningen ─────────────────────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("Anna Lindqvist", "AL")]
        [InlineData("Karin", "K")]
        [InlineData("Anna Maria Lindqvist", "AL")]   // mellannamn hoppas över, som i medlemsregistret
        [InlineData("", "?")]
        [InlineData(null, "?")]
        [InlineData("åsa öberg", "ÅÖ")]
        public void Initials_follow_the_member_directory_rule(string? name, string expected) =>
            Assert.Equal(expected, SiteSignatures.Initials(name));

        [Fact]
        public void Render_puts_the_role_on_its_own_row_only_when_there_is_one()
        {
            var withRole = SiteSignatures.Render(new SiteSignature(1, "Anna Lindqvist", null, "Ordförande"));
            Assert.Contains("hs-sig-name\">Anna Lindqvist<", withRole);
            Assert.Contains("hs-sig-role\">Ordförande<", withRole);
            var without = SiteSignatures.Render(new SiteSignature(1, "Anna Lindqvist", null, null));
            Assert.DoesNotContain("hs-sig-role", without);
            Assert.DoesNotContain("hs-sig-role", SiteSignatures.Render(new SiteSignature(1, "Anna Lindqvist", null, "  ")));
        }

        [Fact]
        public void Render_encodes_everything_a_member_can_type()
        {
            var html = SiteSignatures.Render(new SiteSignature(1, "<script>alert(1)</script>", null, "\"><img src=x onerror=y>"));
            Assert.DoesNotContain("<script>", html);
            Assert.DoesNotContain("<img src=x", html);
            Assert.Contains("&lt;script&gt;", html);
        }

        [Theory]
        [InlineData("/media/abc/anna.jpg", true)]
        [InlineData("https://pistol.nu/media/a.jpg", true)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("data:image/svg+xml;base64,AAAA", false)]
        [InlineData("//evil.example/x.jpg", false)]
        [InlineData("/a\" onerror=\"x", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_own_or_https_pictures_are_used(string? url, bool expected) =>
            Assert.Equal(expected, SiteSignatures.IsOwnPicture(url));

        [Fact]
        public void Render_adds_the_picture_on_top_of_the_initials_and_drops_a_bad_one()
        {
            Assert.Contains("<img src=\"/media/a.jpg\"", SiteSignatures.Render(new SiteSignature(1, "Anna Lindqvist", "/media/a.jpg", null)));
            Assert.DoesNotContain("<img", SiteSignatures.Render(new SiteSignature(1, "Anna Lindqvist", "javascript:x", null)));
        }

        [Fact]
        public void Resolve_replaces_each_signature_and_removes_the_ones_that_may_not_be_shown()
        {
            var html = SiteSignatures.Resolve("<p>Hälsning</p>" + Mine + Other, id => id == 5513 ? new SiteSignature(5513, "Anna Lindqvist", null, "Ordförande") : null);
            Assert.Contains("Anna Lindqvist", html);
            Assert.DoesNotContain("hpsk-signatur", html);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "class=\"hs-sig\""));
            Assert.Contains("<p>Hälsning</p>", html);
        }

        [Fact]
        public void Resolve_looks_each_member_up_once()
        {
            var calls = 0;
            SiteSignatures.Resolve(Mine + Mine + Mine, id => { calls++; return new SiteSignature(id, "Anna", null, null); });
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Text_without_a_signature_is_returned_untouched()
        {
            const string s = "<p>Ingen signatur</p>";
            Assert.Same(s, SiteSignatures.Resolve(s, _ => throw new InvalidOperationException("ska inte slås upp")));
        }
    }
}
