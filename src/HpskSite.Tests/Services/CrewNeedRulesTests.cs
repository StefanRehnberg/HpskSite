using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Behovet per roll och dag: alla dagars antal, och dagar som avviker.</summary>
    public class CrewNeedRulesTests
    {
        private static readonly DateTime Fri = new(2028, 8, 18), Sat = new(2028, 8, 19), Sun = new(2028, 8, 20);

        private static StaffCrewNeed Row(string role, int count, DateTime? day = null) =>
            new() { ScopeKind = CrewNeedScope.All, RoleKey = role, Count = count, DayDate = day };

        [Fact]
        public void Dagen_utan_egen_rad_far_alla_dagars_antal()
        {
            var rows = new[] { Row("markor", 8), Row("markor", 0, Fri) };
            CrewNeedRules.ForDay(rows, CrewNeedScope.All, "markor", Sat).Should().Be(8);
            CrewNeedRules.ForDay(rows, CrewNeedScope.All, "markor", Fri).Should().Be(0);
            CrewNeedRules.Default(rows, CrewNeedScope.All, "markor").Should().Be(8);
        }

        [Fact]
        public void En_roll_utan_alla_dagars_antal_kan_behovas_en_dag()
        {
            var rows = new[] { Row("banbygge", 6, Fri) };
            CrewNeedRules.ForDay(rows, CrewNeedScope.All, "banbygge", Fri).Should().Be(6);
            CrewNeedRules.ForDay(rows, CrewNeedScope.All, "banbygge", Sat).Should().Be(0);
        }

        [Fact]
        public void Planen_lagrar_bara_dagar_som_avviker()
        {
            var plan = CrewNeedRules.Plan(8, new[] { (Fri, 0), (Sat, 8), (Sun, 8) });
            plan.Should().BeEquivalentTo(new (DateTime?, int)[] { (null, 8), (Fri, 0) });
        }

        [Fact]
        public void Noll_alla_dagar_lagras_inte_men_en_dag_med_behov_gor_det()
        {
            var plan = CrewNeedRules.Plan(0, new[] { (Fri, 6), (Sat, 0) });
            plan.Should().BeEquivalentTo(new (DateTime?, int)[] { (Fri, 6) });
        }

        [Fact]
        public void Effektivt_behov_per_dag_raknar_inte_nollor()
        {
            var rows = new[] { Row("markor", 8), Row("markor", 0, Fri), Row("banbygge", 6, Fri) };
            CrewNeedRules.EffectiveFor(rows, CrewNeedScope.All, Fri).Should().BeEquivalentTo(new[] { ("banbygge", 6) });
            CrewNeedRules.EffectiveFor(rows, CrewNeedScope.All, Sat).Should().BeEquivalentTo(new[] { ("markor", 8) });
        }

        [Fact]
        public void Rutnatets_rad_svarar_per_kolumn()
        {
            var row = new GridRow { Needed = 8 };
            row.NeededByDay["2028-08-18"] = 0;
            row.NeededByDay["2028-08-19"] = 8;
            row.NeedFor("2028-08-18").Should().Be(0);
            row.NeedFor("2028-08-19").Should().Be(8);
            row.NeedFor("2028-08-21").Should().Be(8); // en dag utan egen uppgift
            row.NeedFor("").Should().Be(0);           // den odaterade kolumnen
            row.HasNeed.Should().BeTrue();
        }
    }
}
