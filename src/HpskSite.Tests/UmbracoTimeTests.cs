using FluentAssertions;
using HpskSite.Services.Hosting;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Fas E: Umbraco 17 lämnar ut systemdatum i UTC, 16 gav dem som lokal tid (Kind = Local).
    /// <see cref="UmbracoTime"/> ger tillbaka 16:s värde.
    /// </summary>
    public class UmbracoTimeTests
    {
        private static readonly DateTime Utc = new(2026, 8, 25, 13, 0, 51, DateTimeKind.Utc);

        [Fact]
        public void Utc_blir_lokal_tid_med_kind_local_som_i_16()
        {
            var local = UmbracoTime.Local(Utc);
            local.Kind.Should().Be(DateTimeKind.Local);
            local.Should().Be(Utc.ToLocalTime());
        }

        [Fact]
        public void Ett_varde_som_redan_ar_lokalt_rors_inte()
        {
            // Ett datum som går genom två lager (lagringen och sedan anroparen) får inte flyttas två gånger.
            var once = UmbracoTime.Local(Utc);
            UmbracoTime.Local(once).Should().Be(once);
            var unspecified = new DateTime(2026, 8, 25, 15, 0, 51, DateTimeKind.Unspecified);
            UmbracoTime.Local(unspecified).Should().Be(unspecified);
        }

        [Fact]
        public void Null_forblir_null() => UmbracoTime.Local((DateTime?)null).Should().BeNull();

        [Fact]
        public void Databasens_utc_utan_kind_blir_lokal_tid_utan_kind()
        {
            var fromDb = new DateTime(2026, 8, 25, 13, 0, 51, DateTimeKind.Unspecified);
            var local = UmbracoTime.FromDbUtc(fromDb);
            local.Kind.Should().Be(DateTimeKind.Unspecified);
            local.Ticks.Should().Be(Utc.ToLocalTime().Ticks);
        }

        [Fact]
        public void MinValue_ror_inte() => UmbracoTime.FromDbUtc(DateTime.MinValue).Should().Be(DateTime.MinValue);
    }
}
