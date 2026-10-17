using System.Reflection;
using FluentAssertions;
using HpskSite.Services.Hosting;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Fas E: Umbraco 17 märker datum som UTC. <see cref="WallClockDateMapper"/> ger våra klasser och
    /// datumegenskaperna tillbaka Kind = Unspecified; Umbracos egna klasser lämnas åt Umbraco.
    /// </summary>
    public class WallClockDatesTests
    {
        private sealed class OurRow
        {
            public DateTime When { get; set; }
            public DateTime? Maybe { get; set; }
            public string Text { get; set; } = "";
        }

        private static MemberInfo Member<T>(string name) => typeof(T).GetProperty(name)!;

        [Fact]
        public void Vara_datumfalt_far_unspecified()
        {
            var mapper = new WallClockDateMapper();
            var convert = mapper.GetFromDbConverter(Member<OurRow>(nameof(OurRow.When)), typeof(DateTime));
            convert.Should().NotBeNull();
            var value = (DateTime)convert!(new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));
            value.Kind.Should().Be(DateTimeKind.Unspecified);
            value.Should().Be(new DateTime(2026, 10, 17, 9, 0, 0)); // värdet flyttas inte
        }

        [Fact]
        public void Nullbart_datumfalt_omfattas()
            => new WallClockDateMapper().GetFromDbConverter(Member<OurRow>(nameof(OurRow.Maybe)), typeof(DateTime))
                .Should().NotBeNull();

        [Fact]
        public void Andra_falt_lamnas_at_umbraco()
            => new WallClockDateMapper().GetFromDbConverter(Member<OurRow>(nameof(OurRow.Text)), typeof(string))
                .Should().BeNull();

        [Fact]
        public void Umbracos_egna_klasser_lamnas_at_umbraco()
        {
            // En Umbraco-klass med ett DateTime-fält: systemdatumen ska förbli UTC.
            var umbracoMember = typeof(Umbraco.Cms.Core.Models.Membership.IUser).GetProperty("CreateDate")
                ?? typeof(Umbraco.Cms.Core.Models.Entities.IEntity).GetProperty("CreateDate")!;
            WallClockDateMapper.AppliesTo(umbracoMember).Should().BeFalse();
        }

        [Fact]
        public void Normaliseringen_tar_bort_kind_utan_att_flytta_vardet()
        {
            var local = DateTime.SpecifyKind(new DateTime(2026, 10, 17, 9, 31, 23), DateTimeKind.Local);
            var stripped = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            stripped.Kind.Should().Be(DateTimeKind.Unspecified);
            stripped.Ticks.Should().Be(local.Ticks);
        }
    }
}
