using FluentAssertions;
using HpskSite.Services.Hosting;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Fas E: Umbraco 17 lämnar ut systemdatum i UTC. <see cref="UmbracoTime"/> ger tillbaka det värde
    /// Umbraco 16 lämnade ut — lokal tid med Kind = Unspecified.
    /// </summary>
    public class UmbracoTimeTests
    {
        [Fact]
        public void Utc_blir_lokal_tid_utan_kind()
        {
            var utc = new DateTime(2026, 8, 25, 13, 0, 51, DateTimeKind.Utc);
            var local = UmbracoTime.Local(utc);
            local.Kind.Should().Be(DateTimeKind.Unspecified);
            local.Should().Be(DateTime.SpecifyKind(utc.ToLocalTime(), DateTimeKind.Unspecified));
        }

        [Fact]
        public void Ett_varde_som_redan_ar_lokalt_rors_inte()
        {
            // Ett datum som går genom två lager (lagringen och sedan anroparen) får inte flyttas två gånger.
            var unspecified = new DateTime(2026, 8, 25, 15, 0, 51, DateTimeKind.Unspecified);
            UmbracoTime.Local(unspecified).Should().Be(unspecified);
            UmbracoTime.Local(UmbracoTime.Local(new DateTime(2026, 8, 25, 13, 0, 51, DateTimeKind.Utc)))
                .Should().Be(UmbracoTime.Local(new DateTime(2026, 8, 25, 13, 0, 51, DateTimeKind.Utc)));
        }

        [Fact]
        public void Null_forblir_null() => UmbracoTime.Local((DateTime?)null).Should().BeNull();

        [Fact]
        public void Databasens_utc_utan_kind_rakas_om()
        {
            var fromDb = new DateTime(2026, 8, 25, 13, 0, 51, DateTimeKind.Unspecified);
            UmbracoTime.FromDbUtc(fromDb).Should()
                .Be(UmbracoTime.Local(new DateTime(2026, 8, 25, 13, 0, 51, DateTimeKind.Utc)));
        }

        [Fact]
        public void MinValue_ror_inte() => UmbracoTime.FromDbUtc(DateTime.MinValue).Should().Be(DateTime.MinValue);
    }
}
