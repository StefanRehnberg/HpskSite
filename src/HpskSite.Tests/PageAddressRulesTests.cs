using FluentAssertions;
using HpskSite.Services.Addresses;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>C3-planen S6: adressernas regler — slug-krockar, årtalet och hur en sökväg delas.</summary>
    public class PageAddressRulesTests
    {
        [Fact]
        public void En_ledig_slug_far_sitt_namn()
            => PageAddressRules.UniqueSlug("boraskappen", new[] { ("annat", false) }).Should().Be("boraskappen");

        [Fact]
        public void En_slug_som_en_annan_sida_har_far_minus_tva()
            => PageAddressRules.UniqueSlug("boraskappen", new[] { ("boraskappen", false) }).Should().Be("boraskappen-2");

        [Fact]
        public void Nasta_lediga_nummer_valjs()
            => PageAddressRules.UniqueSlug("x", new[] { ("x", false), ("x-2", false), ("x-3", false) }).Should().Be("x-4");

        [Fact]
        public void Sidans_egen_gamla_adress_ateranvands()
            => PageAddressRules.UniqueSlug("x", new[] { ("x", true) }).Should().Be("x");

        [Fact]
        public void Krocken_ar_skiftlagesokanslig()
            => PageAddressRules.UniqueSlug("Boraskappen", new[] { ("boraskappen", false) }).Should().Be("Boraskappen-2");

        [Theory]
        [InlineData("gasaskjutning-2", "gasaskjutning")]
        [InlineData("gasaskjutning-12", "gasaskjutning")]
        [InlineData("omgang-1", "omgang-1")]
        [InlineData("ssm-2026", "ssm")]
        [InlineData("gasaskjutning", "gasaskjutning")]
        public void Grundsluggen(string slug, string expected) => PageAddressRules.BaseOf(slug).Should().Be(expected);

        [Theory]
        [InlineData("2026", true)]
        [InlineData("1990", false)]
        [InlineData("2200", false)]
        [InlineData("202", false)]
        [InlineData("02026", false)]
        [InlineData("klubbar", false)]
        public void Artalet(string segment, bool expected) => PageAddressRules.IsYearSegment(segment, out _).Should().Be(expected);

        [Fact]
        public void Klubbens_tavling_delas_vid_artalet()
        {
            var p = PageAddressRules.Parse(new[] { "halland", "klubbar", "haaplinge-goass", "2026", "boraskappen", "resultat" });
            p.Should().NotBeNull();
            p!.Value.OwnerSegments.Should().Equal("halland", "klubbar", "haaplinge-goass");
            p.Value.Year.Should().Be(2026);
            p.Value.Slug.Should().Be("boraskappen");
            p.Value.Remaining.Should().Equal("resultat");
        }

        [Fact]
        public void Arssidan_har_ingen_slug()
        {
            var p = PageAddressRules.Parse(new[] { "halland", "2026" });
            p!.Value.Slug.Should().BeNull();
            p.Value.Remaining.Should().BeEmpty();
        }

        [Fact]
        public void Forsta_segmentet_ar_aldrig_artalet()
            => PageAddressRules.Parse(new[] { "2026", "x" }).Should().BeNull();

        [Fact]
        public void En_adress_utan_artal_ar_inte_var()
            => PageAddressRules.Parse(new[] { "halland", "klubbar", "haaplinge-goass" }).Should().BeNull();

        [Fact]
        public void Forbundets_agare_kanns_igen()
        {
            PageAddressOwner.IsNationalType("SSM").Should().BeTrue();
            PageAddressOwner.IsNationalType("halland").Should().BeFalse();
            PageAddressOwner.ForNational("SM").Should().Be(new PageAddressOwner("sm", 0));
        }
    }
}
