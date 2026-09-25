using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.CompetitionTypes.Precision.Controllers;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using HpskSite.Models.ViewModels.Competition;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Formatet "Mixade vapengrupper per skjutlag": samma skytt står i FLERA skjutlag, en start per
    /// vapengrupp. Två fel rapporterade av Michael Henriksson (Åmåls PK, tävling 5574, 2026-09-24):
    /// alla skjutlag fick samma starttid, och en flytt av skytten verkade på fel skjutlag.
    /// </summary>
    public class PrecisionMixedStartListTests
    {
        private static CompetitionRegistration Reg(int memberId, string name, string cls) =>
            new CompetitionRegistration { Id = memberId * 10 + cls.Length, MemberId = memberId, MemberName = name, MemberClass = cls };

        private static List<CompetitionRegistration> TwoClassesEach() => new()
        {
            Reg(1, "Anna Berg", "A1"), Reg(1, "Anna Berg", "R1"),
            Reg(2, "Bo Carlsson", "A2"), Reg(2, "Bo Carlsson", "R2"),
            Reg(3, "Cilla Dahl", "A1"), Reg(3, "Cilla Dahl", "R1"),
        };

        [Fact]
        public void Varje_skjutlag_far_sin_egen_tid_ett_intervall_efter_det_forra()
        {
            var teams = new PrecisionMixedTeamsGenerator(TwoClassesEach(), 10, new TimeSpan(9, 0, 0), 120, "FirstName")
                .GenerateStartLists();

            Assert.Equal(2, teams.Count);
            Assert.Equal("09:00", teams[0].StartTime);
            Assert.Equal("11:00", teams[0].EndTime);
            Assert.Equal("11:00", teams[1].StartTime);
            Assert.Equal("13:00", teams[1].EndTime);
        }

        [Fact]
        public void Samma_skytt_star_aldrig_tva_ganger_i_samma_skjutlag()
        {
            var teams = new PrecisionMixedTeamsGenerator(TwoClassesEach(), 10, new TimeSpan(9, 0, 0), 120, "FirstName")
                .GenerateStartLists();

            foreach (var t in teams)
                Assert.Equal(t.Shooters!.Count, t.Shooters.Select(s => s.MemberId).Distinct().Count());
            Assert.Equal(6, teams.Sum(t => t.Shooters!.Count));
        }

        [Fact]
        public void Sorteringsvalet_foljs_och_skrivs_inte_over_med_fornamn()
        {
            var regs = new List<CompetitionRegistration>
            {
                Reg(1, "Anna Östman", "A1"),
                Reg(2, "Bo Andersson", "A1"),
            };
            var team = new PrecisionMixedTeamsGenerator(regs, 10, new TimeSpan(9, 0, 0), 60, "LastName")
                .GenerateStartLists().Single();

            Assert.Equal("Bo Andersson", team.Shooters![0].Name);
        }

        private static StartListConfiguration TwoTeamsSameMember() => new()
        {
            Teams = new List<StartListTeam>
            {
                new() { TeamNumber = 1, Shooters = new List<StartListShooter> { new() { MemberId = 7, WeaponClass = "A1", Position = 1 } } },
                new() { TeamNumber = 2, Shooters = new List<StartListShooter> { new() { MemberId = 7, WeaponClass = "R1", Position = 1 } } },
            }
        };

        [Fact]
        public void FindShooter_med_skjutlag_hittar_raden_i_just_det_laget()
        {
            var (shooter, team, error) = PrecisionStartListController.FindShooter(TwoTeamsSameMember(), 7, 2);

            Assert.Null(error);
            Assert.Equal(2, team!.TeamNumber);
            Assert.Equal("R1", shooter!.WeaponClass);
        }

        [Fact]
        public void FindShooter_utan_skjutlag_vagrar_nar_medlemmen_star_i_flera()
        {
            var (shooter, _, error) = PrecisionStartListController.FindShooter(TwoTeamsSameMember(), 7, 0);

            Assert.Null(shooter);
            Assert.Contains("2 skjutlag", error);
        }

        [Fact]
        public void FindShooter_utan_skjutlag_godtar_det_otvetydiga_fallet()
        {
            var cfg = TwoTeamsSameMember();
            cfg.Teams![1].Shooters!.Clear();

            var (shooter, team, error) = PrecisionStartListController.FindShooter(cfg, 7, 0);

            Assert.Null(error);
            Assert.Equal(1, team!.TeamNumber);
            Assert.NotNull(shooter);
        }

        [Fact]
        public void FindShooter_med_fel_skjutlag_sager_det()
        {
            var (shooter, _, error) = PrecisionStartListController.FindShooter(TwoTeamsSameMember(), 7, 3);

            Assert.Null(shooter);
            Assert.Contains("skjutlag 3", error);
        }
    }
}
