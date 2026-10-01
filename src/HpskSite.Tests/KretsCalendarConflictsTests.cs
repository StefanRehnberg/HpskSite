using FluentAssertions;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    public class KretsCalendarConflictsTests
    {
        private static KretsCalendarEntry E(string kind, int id, string disc, DateTime date, bool neighbour = false, bool sm = false, string region = "Halland")
            => new() { Kind = kind, Id = id, Name = $"{kind}{id}", Discipline = disc, DisciplineLabel = disc, Date = date,
                       IsNeighbour = neighbour, IsSm = sm, RegionName = region };

        private static readonly DateTime D = new(2027, 5, 1);

        [Fact]
        public void SammaGrenSammaDag_IKretsen_Varnas()
        {
            var a = E("competition", 1, "Precision", D);
            var b = E("application", 2, "Precision", D);
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().ContainSingle().Which.Should().Contain("i kretsen");
            b.Conflicts.Should().ContainSingle();
        }

        [Fact]
        public void OlikaGrenar_KrockarInte()
        {
            var a = E("competition", 1, "Precision", D);
            var b = E("competition", 2, "Faltskytte", D);
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().BeEmpty();
        }

        [Fact]
        public void OlikaDagar_KrockarInte()
        {
            var a = E("competition", 1, "Precision", D);
            var b = E("competition", 2, "Precision", D.AddDays(1));
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().BeEmpty();
        }

        [Fact]
        public void FlerdagarsTavling_KrockarMedDagInuti()
        {
            var a = E("competition", 1, "Precision", D); a.EndDate = D.AddDays(2);
            var b = E("application", 2, "Precision", D.AddDays(1));
            KretsCalendarConflicts.Mark(new[] { a, b });
            b.Conflicts.Should().ContainSingle();
        }

        [Fact]
        public void Grannkrets_NamngesISkalet_OchGrannenFarIngenVarning()
        {
            var own = E("competition", 1, "Precision", D);
            var nb = E("competition", 2, "Precision", D, neighbour: true, region: "Kronoberg");
            KretsCalendarConflicts.Mark(new[] { own, nb });
            own.Conflicts.Should().ContainSingle().Which.Should().Contain("Kronoberg");
            nb.Conflicts.Should().BeEmpty("en grannkrets krock är inte vår kalenders fråga");
        }

        [Fact]
        public void TvaGrannar_SomKrockar_VarnasInte()
        {
            var a = E("competition", 1, "Precision", D, neighbour: true);
            var b = E("competition", 2, "Precision", D, neighbour: true);
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().BeEmpty();
            b.Conflicts.Should().BeEmpty();
        }

        [Fact]
        public void SM_IGrenen_Varnas()
        {
            var own = E("application", 1, "Precision", D);
            var sm = E("competition", 9, "Precision", D, neighbour: true, sm: true, region: "Stockholm");
            KretsCalendarConflicts.Mark(new[] { own, sm });
            own.Conflicts.Should().ContainSingle().Which.Should().StartWith("SM i");
        }

        [Fact]
        public void Handelser_KrockarAldrig()
        {
            var a = E("event", 1, "", D);
            var b = E("event", 2, "", D);
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().BeEmpty();
        }

        [Fact]
        public void SammaPostTvaGanger_ArIngenKrock()
        {
            var a = E("competition", 1, "Precision", D);
            var b = E("competition", 1, "Precision", D);
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().BeEmpty();
        }
    }
}
