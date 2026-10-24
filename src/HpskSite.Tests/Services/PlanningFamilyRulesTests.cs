using System.Collections.Generic;
using FluentAssertions;
using HpskSite.Models.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Omgång 3: familjen är veckan — vilken tävling en planerings bemanning gäller.</summary>
    public class PlanningFamilyRulesTests
    {
        private static PlanningRow P(int id, int? parent = null, int? staffsFor = null) =>
            new() { Id = id, ParentPlanningId = parent, StaffsForCompetitionId = staffsFor };

        // SM-veckan (1, okopplad) med fält (2 → tävling 100) och precision (3 → tävling 200), och station 3 (4).
        private static readonly Dictionary<int, int> Week = new() { [2] = 100, [3] = 200 };

        [Fact]
        public void En_egen_koppling_vinner()
        {
            PlanningFamilyRules.StaffingCompetition(P(2, 1, staffsFor: 200), Week).Should().Be(100);
        }

        [Fact]
        public void En_delplanering_under_en_okopplad_huvudplanering_bemannar_ingen_tavling_tills_den_valt()
        {
            PlanningFamilyRules.StaffingCompetition(P(4, 1), Week).Should().BeNull();
            PlanningFamilyRules.StaffingCompetition(P(4, 1, staffsFor: 100), Week).Should().Be(100);
        }

        [Fact]
        public void Utan_val_galler_huvudplaneringens_tavling()
        {
            var links = new Dictionary<int, int> { [1] = 100 };
            PlanningFamilyRules.StaffingCompetition(P(4, 1), links).Should().Be(100);
        }

        [Fact]
        public void Ett_val_som_inte_langre_ar_familjens_raknas_inte()
        {
            var links = new Dictionary<int, int> { [1] = 100 };
            PlanningFamilyRules.StaffingCompetition(P(4, 1, staffsFor: 999), links).Should().Be(100);
            PlanningFamilyRules.StaffingCompetition(P(4, 1, staffsFor: 999), new Dictionary<int, int>()).Should().BeNull();
        }

        [Fact]
        public void Huvudplaneringen_kan_ocksa_valja_bland_familjens_tavlingar()
        {
            PlanningFamilyRules.StaffingCompetition(P(1, staffsFor: 200), Week).Should().Be(200);
        }
    }
}
