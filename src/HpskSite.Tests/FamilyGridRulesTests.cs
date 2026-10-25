using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 2: familjens dagaxel är unionen av delarnas dagar.</summary>
    public class FamilyGridRulesTests
    {
        private static GridColumn Col(string key, bool planned = true, bool work = false, string? label = null, int dayId = 7)
            => new() { Key = key, Label = label ?? key, IsPlanned = planned, HasAssignments = work, DayId = dayId };

        [Fact]
        public void Unionen_har_varje_dag_en_gang_i_datumordning()
        {
            var falt = new[] { Col("2028-08-12"), Col("2028-08-11") };
            var precision = new[] { Col("2028-08-12"), Col("2028-08-13") };
            FamilyGridRules.UnionColumns(new[] { falt, precision }).Select(c => c.Key)
                .Should().Equal("2028-08-11", "2028-08-12", "2028-08-13");
        }

        [Fact]
        public void Utan_datum_ligger_sist()
        {
            var a = new[] { Col(""), Col("2028-08-12") };
            var b = new[] { Col("2028-08-10") };
            FamilyGridRules.UnionColumns(new[] { a, b }).Select(c => c.Key).Should().Equal("2028-08-10", "2028-08-12", "");
        }

        [Fact]
        public void Planerad_och_arbete_slas_ihop_dagens_id_nollas()
        {
            var a = new[] { Col("2028-08-12", planned: false, work: false) };
            var b = new[] { Col("2028-08-12", planned: true, work: true) };
            var u = FamilyGridRules.UnionColumns(new[] { a, b }).Single();
            u.IsPlanned.Should().BeTrue();
            u.HasAssignments.Should().BeTrue();
            u.DayId.Should().Be(0);
        }

        [Fact]
        public void Delens_dagar_ar_dess_egna_nycklar_utan_utan_datum()
            => FamilyGridRules.DayKeys(new[] { Col("2028-08-12"), Col(""), Col("2028-08-12") })
                .Should().Equal("2028-08-12");

        [Fact]
        public void Tom_familj_ger_tom_axel()
            => FamilyGridRules.UnionColumns(new GridColumn[][] { }).Should().BeEmpty();
    }
}
