using FluentAssertions;
using HpskSite.Models;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>
    /// Sidmetadatan i Master (C3-planen F4). En routad sida bär en annan nod än den visar — då får
    /// nodens adress och beskrivning inte användas.
    /// </summary>
    public class PageMetaTests
    {
        private static HttpRequest Request(string path, string query = "")
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString("pistol.nu");
            ctx.Request.Path = path;
            ctx.Request.QueryString = new QueryString(query);
            return ctx.Request;
        }

        [Theory]
        [InlineData("/halland/klubbar/varberg/", "/halland/klubbar/varberg/")]
        [InlineData("/halland/klubbar/varberg", "/halland/klubbar/varberg/")]
        [InlineData("/Halland/Klubbar/Varberg/", "/halland/klubbar/varberg/")]
        [InlineData("/", "/")]
        [InlineData("/halland/klubbar/varberg/", "https://pistol.nu/halland/klubbar/varberg/")]
        public void Samma_sida_kanns_igen(string path, string nodeUrl) =>
            PageMeta.IsNodesOwnPage(path, nodeUrl).Should().BeTrue();

        [Theory]
        [InlineData("/halland/klubbar/varberg/2026/klubbmote/", "/halland/klubbar/varberg/")]
        [InlineData("/mitt-schema", "/")]
        [InlineData("/x/", null)]
        [InlineData("/x/", "#")]
        public void Barare_ar_inte_sidan(string path, string? nodeUrl) =>
            PageMeta.IsNodesOwnPage(path, nodeUrl).Should().BeFalse();

        [Fact]
        public void Relativ_bild_blir_absolut() =>
            PageMeta.Absolute(Request("/"), "/media/abc/logo.png").Should().Be("https://pistol.nu/media/abc/logo.png");

        [Fact]
        public void Absolut_adress_lamnas() =>
            PageMeta.Absolute(Request("/"), "https://cdn.example.se/a.png").Should().Be("https://cdn.example.se/a.png");

        [Fact]
        public void Tom_adress_ger_null() => PageMeta.Absolute(Request("/"), " ").Should().BeNull();

        [Fact]
        public void Nodens_egen_sida_tappar_fragestrangen() =>
            PageMeta.DefaultCanonical(Request("/halland/klubbar/varberg/", "?tab=members"), "/halland/klubbar/varberg/")
                .Should().Be("https://pistol.nu/halland/klubbar/varberg/");

        [Fact]
        public void Routad_sida_behaller_fragestrangen() =>
            PageMeta.DefaultCanonical(Request("/mitt-schema", "?c=5574"), "/")
                .Should().Be("https://pistol.nu/mitt-schema?c=5574");

        [Fact]
        public void Samma_objekt_hela_begaran()
        {
            var ctx = new DefaultHttpContext();
            PageMeta.For(ctx).Description = "x";
            PageMeta.For(ctx).Description.Should().Be("x");
        }
    }
}
