using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 6b (B11): vem kan ta ett uppdrag som blivit ledigt.</summary>
    public class ReplacementRulesTests
    {
        private static readonly DateTime Lor = new(2028, 8, 12);
        private static readonly HashSet<int> NoSlots = new();
        private static HelpSlotChoice Day(string date, string? role = "") => new() { Date = date, RoleKey = role };
        private static StaffInterval Iv(int from, int to) => StaffInterval.From(Lor, TimeSpan.FromHours(from), TimeSpan.FromHours(to))!.Value;

        [Fact]
        public void Ett_dagsval_utan_roll_raknas_som_valfri_roll()
        {
            var (v, role) = ReplacementRules.Matches(new[] { Day("2028-08-12") }, Lor, "skjutledare", NoSlots);
            v.Should().BeTrue(); role.Should().BeFalse();
        }

        [Fact]
        public void Samma_roll_ar_en_traff_pa_rollen_oavsett_versaler()
        {
            var (v, role) = ReplacementRules.Matches(new[] { Day("2028-08-12", "Skjutledare") }, Lor, "skjutledare", NoSlots);
            v.Should().BeTrue(); role.Should().BeTrue();
        }

        [Fact]
        public void En_annan_roll_samma_dag_raknas_inte()
        {
            // Den som bara erbjudit sig som markör ska inte föreslås som skjutledare.
            ReplacementRules.Matches(new[] { Day("2028-08-12", "markor") }, Lor, "skjutledare", NoSlots).Volunteers.Should().BeFalse();
        }

        [Fact]
        public void Fel_dag_raknas_inte()
            => ReplacementRules.Matches(new[] { Day("2028-08-13") }, Lor, "skjutledare", NoSlots).Volunteers.Should().BeFalse();

        [Fact]
        public void Ett_eget_tillfalle_den_dagen_raknas_men_aldrig_som_roll()
        {
            var (v, role) = ReplacementRules.Matches(new[] { new HelpSlotChoice { SlotId = 7 } }, Lor, "skjutledare", new HashSet<int> { 7 });
            v.Should().BeTrue(); role.Should().BeFalse();
            ReplacementRules.Matches(new[] { new HelpSlotChoice { SlotId = 8 } }, Lor, "skjutledare", new HashSet<int> { 7 }).Volunteers.Should().BeFalse();
        }

        [Fact]
        public void Ett_trasigt_datum_kraschar_inte()
            => ReplacementRules.Matches(new[] { Day("inte ett datum") }, Lor, "x", NoSlots).Volunteers.Should().BeFalse();

        [Fact]
        public void Ledig_betyder_att_inget_overlappar_och_kant_i_kant_ar_ledigt()
        {
            ReplacementRules.FreeFor(Iv(8, 12), new[] { Iv(13, 15) }).Should().BeTrue();
            ReplacementRules.FreeFor(Iv(8, 12), new[] { Iv(12, 14) }).Should().BeTrue();
            ReplacementRules.FreeFor(Iv(8, 12), new[] { Iv(11, 14) }).Should().BeFalse();
            ReplacementRules.FreeFor(Iv(8, 12), Array.Empty<StaffInterval>()).Should().BeTrue();
        }

        [Fact]
        public void Okand_tid_pa_uppdraget_ger_okant_inte_ledig()
            => ReplacementRules.FreeFor(null, new[] { Iv(8, 12) }).Should().BeNull();

        [Fact]
        public void Heldag_hos_kandidaten_gor_hen_upptagen()
        {
            var heldag = StaffInterval.From(Lor, null, null)!.Value;
            ReplacementRules.FreeFor(Iv(13, 15), new[] { heldag }).Should().BeFalse();
        }

        [Fact]
        public void Rangordningen_ar_lediga_med_roll_lediga_okanda_upptagna_och_sedan_namn()
        {
            var list = new[]
            {
                new ReplacementCandidate(1, "Åsa", false, false, "Markör 8–12", null),
                new ReplacementCandidate(2, "Bo", false, null, null, null),
                new ReplacementCandidate(3, "Cecilia", false, true, null, null),
                new ReplacementCandidate(4, "Dan", true, true, null, null),
                new ReplacementCandidate(5, "Ada", false, true, null, null),
            };
            ReplacementRules.Rank(list).Select(c => c.MemberId).Should().Equal(4, 5, 3, 2, 1);
        }

        [Fact]
        public void En_upptagen_med_ratt_roll_star_efter_en_ledig_utan()
        {
            var list = new[]
            {
                new ReplacementCandidate(1, "Ada", true, false, "x", null),
                new ReplacementCandidate(2, "Bo", false, true, null, null),
            };
            ReplacementRules.Rank(list).Select(c => c.MemberId).Should().Equal(2, 1);
        }
    }
}
