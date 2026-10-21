using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Banans besiktning (2026-10-21): förslaget, den senaste, valideringen och "Att göra".</summary>
    public class RangeInspectionRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 21);

        [Fact]
        public void Nasta_foreslas_som_besiktningsdagen_plus_intervallet()
        {
            RangeInspectionRules.SuggestNextDue(new DateTime(2025, 6, 1), 5).Should().Be(new DateTime(2030, 6, 1));
            RangeInspectionRules.SuggestNextDue(new DateTime(2024, 2, 29), 1).Should().Be(new DateTime(2025, 2, 28));
            RangeInspectionRules.SuggestNextDue(new DateTime(2025, 6, 1), null).Should().BeNull("inget intervall = inget förslag");
        }

        [Fact]
        public void Senaste_aktiva_besiktningen_avgor()
        {
            var list = new[]
            {
                new RangeInspection { Id = 1, InspectedOn = new DateTime(2020, 5, 1), IsActive = true },
                new RangeInspection { Id = 2, InspectedOn = new DateTime(2025, 5, 1), IsActive = false },
                new RangeInspection { Id = 3, InspectedOn = new DateTime(2024, 5, 1), IsActive = true },
                new RangeInspection { Id = 4, InspectedOn = new DateTime(2024, 5, 1), IsActive = true }
            };
            RangeInspectionRules.Latest(list)!.Id.Should().Be(4, "den gömda räknas inte; samma dag → den senast registrerade");
            RangeInspectionRules.Latest(Array.Empty<RangeInspection>()).Should().BeNull();
        }

        [Fact]
        public void Validering()
        {
            RangeInspectionRules.Validate(Today.AddDays(1), null, Today).Should().Contain("framtiden");
            RangeInspectionRules.Validate(Today, Today, Today).Should().Contain("efter");
            RangeInspectionRules.Validate(Today, null, Today).Should().BeNull("nästa datum är frivilligt");
            RangeInspectionRules.Validate(Today.AddYears(-1), Today.AddYears(4), Today).Should().BeNull();
        }

        private static RangeTodoInput Input() => new()
        {
            Today = Today, HasSkjutbanechef = true, HasPosition = true, SectionCount = 1,
            Permits = { new RangeTodoDated("Polistillståndet", null, 1000) }
        };

        [Fact]
        public void Att_gora_varnar_nar_intervall_finns_men_ingen_besiktning()
        {
            var i = Input(); i.InspectionIntervalYears = 5;
            RangeTodoRules.Build(i).Should().ContainSingle(t => t.Text.Contains("vart 5:e år") && t.Text.Contains("ingen besiktning"));
            i.InspectionIntervalYears = null;
            RangeTodoRules.Build(i).Should().NotContain(t => t.Text.Contains("besikt"), "inget intervall = inget krav att påminna om");
        }

        [Fact]
        public void Att_gora_varnar_90_dagar_fore_och_nar_den_passerats()
        {
            var i = Input(); i.HasInspection = true;
            i.NextInspectionDue = Today.AddDays(60);
            var warn = RangeTodoRules.Build(i).Single(t => t.Text.Contains("Besiktning"));
            warn.Severity.Should().Be(RangeTodoItem.Warn);
            warn.Text.Should().Contain("om 60 dagar");

            i.NextInspectionDue = Today.AddDays(-3);
            RangeTodoRules.Build(i).First().Severity.Should().Be(RangeTodoItem.Danger, "det som brådskar står först");

            i.NextInspectionDue = Today.AddDays(200);
            RangeTodoRules.Build(i).Should().NotContain(t => t.Text.Contains("esiktning"));
        }
    }
}
