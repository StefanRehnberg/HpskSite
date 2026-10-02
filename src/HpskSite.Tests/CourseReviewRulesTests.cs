using FluentAssertions;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    public class CourseReviewRulesTests
    {
        private const string Config = "{\"_attachedConfigId\":12,\"_morker\":false,\"_scoringMode\":\"Normal\",\"stations\":[{\"station\":1,\"figures\":[{\"targetId\":3,\"distance\":120}]}]}";

        [Fact]
        public void Kontrollsumma_PaverkasInteAvVilkenKonfigurationKopianKomIfran()
        {
            var other = Config.Replace("\"_attachedConfigId\":12", "\"_attachedConfigId\":99");
            CourseReviewRules.Checksum(other).Should().Be(CourseReviewRules.Checksum(Config));
        }

        [Fact]
        public void Kontrollsumma_AndrasAvEttAndratAvstand() =>
            CourseReviewRules.Checksum(Config.Replace("120", "130")).Should().NotBe(CourseReviewRules.Checksum(Config));

        [Fact]
        public void Kontrollsumma_AndrasAvMorker() =>
            // Mörker ändrar skjuttiden ×1,30 — det är en ändring av banan, inte en meta-uppgift.
            CourseReviewRules.Checksum(Config.Replace("\"_morker\":false", "\"_morker\":true")).Should().NotBe(CourseReviewRules.Checksum(Config));

        [Fact]
        public void Kontrollsumma_IgnorerarLankadeStationersKalla()
        {
            var a = "{\"stations\":[{\"station\":1,\"_linkedFromConfigId\":4,\"_linkedFromChecksum\":\"x\",\"figures\":[]}]}";
            var b = "{\"stations\":[{\"station\":1,\"_linkedFromConfigId\":7,\"_linkedFromChecksum\":\"y\",\"figures\":[]}]}";
            CourseReviewRules.Checksum(a).Should().Be(CourseReviewRules.Checksum(b));
        }

        [Theory]
        [InlineData("Faltskytte", false, true, false, true)]
        [InlineData("MagnumFalt", false, true, false, true)]
        [InlineData("Precision", false, true, true, false)]   // bara fält
        [InlineData("Faltskytte", true, true, true, false)]   // klubbintern
        [InlineData("Faltskytte", false, false, false, false)] // föreningstävling utan standardmedaljer
        [InlineData("Faltskytte", false, false, true, true)]   // obekräftad kategori, men standardmedaljer
        public void Erbjuds(string type, bool clubOnly, bool kretsOrAbove, bool medals, bool expected) =>
            CourseReviewRules.Offered(type, clubOnly, kretsOrAbove, medals).Should().Be(expected);

        [Fact]
        public void Kravs_NationellAlltid() =>
            CourseReviewRules.Required(isNationalOrHigher: true, isSmOrLandsdel: false, kretsRequires: false, null, new DateTime(2027, 5, 1)).Should().BeTrue();

        [Fact]
        public void Kravs_KretstavlingBaraNarKretsenKraverDet()
        {
            CourseReviewRules.Required(false, false, kretsRequires: false, null, new DateTime(2027, 5, 1)).Should().BeFalse();
            CourseReviewRules.Required(false, false, kretsRequires: true, null, new DateTime(2027, 5, 1)).Should().BeTrue();
        }

        [Fact]
        public void Kravs_GallerBaraTavlingarFranDagenKravetSlogsPa()
        {
            var since = new DateTime(2027, 3, 1);
            CourseReviewRules.Required(false, false, true, since, new DateTime(2027, 2, 20)).Should().BeFalse();
            CourseReviewRules.Required(false, false, true, since, new DateTime(2027, 3, 1)).Should().BeTrue();
        }

        [Fact]
        public void SmOchLandsdel_GarTillForbundet_TolvVeckorFore()
        {
            CourseReviewRules.RouteFor(true).Should().Be(CourseReviewRoute.Forbundet);
            CourseReviewRules.RouteFor(false).Should().Be(CourseReviewRoute.Krets);
            CourseReviewRules.Deadline(new DateTime(2027, 8, 28), forbundet: true, kretsWeeks: null).Should().Be(new DateTime(2027, 6, 5));
        }

        [Fact]
        public void Kretsens_Framforhallning_UtanVeckorIngenSistaDag()
        {
            CourseReviewRules.Deadline(new DateTime(2027, 8, 28), false, null).Should().BeNull();
            CourseReviewRules.Deadline(new DateTime(2027, 8, 28), false, 4).Should().Be(new DateTime(2027, 7, 31));
        }

        [Fact]
        public void Kretsen_KanInteBeslutaOmEnForbundsgranskning() =>
            CourseReviewRules.KretsCanAct(new CompetitionCourseReview { Route = CourseReviewRoute.Forbundet, Status = CourseReviewStatus.Inskickad }).Should().BeFalse();

        [Fact]
        public void Granskare_Normaliseras()
        {
            CourseReviewerKind.Normalize(null).Should().Be(CourseReviewerKind.Bangranskare);
            CourseReviewerKind.Normalize("kretsinstruktor").Should().Be(CourseReviewerKind.Kretsinstruktor);
            CourseReviewerKind.Normalize("nonsens").Should().Be(CourseReviewerKind.Bangranskare);
        }

        // ── Påminnelserna ────────────────────────────────────────────────────────────────

        private static readonly DateTime Dl = new(2027, 6, 1);
        private static List<CourseReviewReminders.Due> Arr(int daysBeforeDeadline) =>
            CourseReviewReminders.Compute(new[] { new CourseReviewReminders.Candidate(5, 3788, Dl) }, Array.Empty<CompetitionCourseReview>(), Dl.AddDays(-daysBeforeDeadline));

        [Fact]
        public void Arrangoren_IngenPaminnelseLangtFore() => Arr(20).Should().BeEmpty();

        [Fact]
        public void Arrangoren_TvaVeckorFore() => Arr(14).Should().ContainSingle().Which.Key.Should().Be("cr-arr14-5");

        [Fact]
        public void Arrangoren_PaSistaDagen_BaraSenasteSteget() => Arr(0).Should().ContainSingle().Which.Key.Should().Be("cr-arr0-5");

        [Fact]
        public void Kretsen_EfterFemDagar_InteForForbundsvagen()
        {
            var r = new CompetitionCourseReview { Id = 3, Status = CourseReviewStatus.Inskickad, UpdatedAt = new DateTime(2027, 5, 1) };
            CourseReviewReminders.Compute(Array.Empty<CourseReviewReminders.Candidate>(), new[] { r }, new DateTime(2027, 5, 6))
                .Should().ContainSingle().Which.Key.Should().Be("cr-krets5-3-20270501");
            r.Route = CourseReviewRoute.Forbundet;
            CourseReviewReminders.Compute(Array.Empty<CourseReviewReminders.Candidate>(), new[] { r }, new DateTime(2027, 5, 6)).Should().BeEmpty();
        }
    }
}
