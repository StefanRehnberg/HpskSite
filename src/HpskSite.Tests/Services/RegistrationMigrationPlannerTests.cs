using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models;
using HpskSite.Models.Registrations;
using HpskSite.Services.Registrations;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Reglerna för flytten av anmälningarna till tabeller (sajtstrukturen fas 1d).
    /// Det avgörande fallet är de äldre tävlingarna med EN nod per klass för samma skytt.
    /// </summary>
    public class RegistrationMigrationPlannerTests
    {
        private static RegistrationRecord Node(int id, int comp, int member, DateTime date, bool active = true, params string[] classes) => new()
        {
            Id = id, CompetitionId = comp, MemberId = member, MemberName = $"Skytt {member}",
            RegistrationDate = date, IsActive = active,
            Classes = classes.Select(c => new ShootingClassEntry { Class = c, StartPreference = "Inget" }).ToList()
        };

        private static readonly DateTime D = new(2026, 5, 1, 10, 0, 0);

        [Fact]
        public void En_nod_flyttas_med_sitt_id()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[] { Node(501, 10, 7, D, true, "C1") });
            var row = Assert.Single(plan.Rows);
            Assert.Equal(501, row.Row.Id);
            Assert.Empty(row.MergedNodeIds);
            Assert.False(row.Deactivated);
        }

        [Fact]
        public void En_nod_per_klass_slas_ihop_till_en_rad_med_alla_klasser()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[]
            {
                Node(601, 10, 7, D, true, "C1"),
                Node(602, 10, 7, D.AddMinutes(1), true, "A1"),
                Node(603, 10, 7, D.AddMinutes(2), true, "B1"),
            });
            var row = Assert.Single(plan.Rows);
            Assert.Equal(603, row.Row.Id);                       // nyaste vinner
            Assert.Equal(new[] { 601, 602 }, row.MergedNodeIds); // de andra pekar hit
            Assert.Equal(new[] { "B1", "C1", "A1" }, row.Row.ClassIds);
            Assert.Equal(2, plan.MergedNodes);
        }

        [Fact]
        public void Aktiv_dubblett_med_samma_klass_blir_inaktiv_rad_och_raderas_inte()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[]
            {
                Node(701, 10, 7, D, true, "C1"),
                Node(702, 10, 7, D.AddDays(1), true, "C1"),
            });
            Assert.Equal(2, plan.Rows.Count);
            var winner = plan.Rows.Single(r => r.Row.IsActive);
            var loser = plan.Rows.Single(r => !r.Row.IsActive);
            Assert.Equal(702, winner.Row.Id);
            Assert.Equal(701, loser.Row.Id);
            Assert.True(loser.Deactivated);
            Assert.Single(plan.Notes);
        }

        [Fact]
        public void Hogst_en_aktiv_rad_per_tavling_och_medlem()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[]
            {
                Node(801, 10, 7, D, true, "C1"),
                Node(802, 10, 7, D.AddDays(1), true, "C1", "A1"),
                Node(803, 10, 7, D.AddDays(2), true, "B1"),
                Node(804, 10, 7, D.AddDays(3), false, "C2"),
                Node(805, 11, 7, D, true, "C1"),
            });
            foreach (var g in plan.Rows.GroupBy(r => (r.Row.CompetitionId, r.Row.MemberId)))
                Assert.True(g.Count(r => r.Row.IsActive) <= 1);
            // Varje nod finns kvar: antingen som en egen rad eller sammanslagen.
            var accounted = plan.Rows.Select(r => r.Row.Id).Concat(plan.Rows.SelectMany(r => r.MergedNodeIds)).OrderBy(i => i);
            Assert.Equal(new[] { 801, 802, 803, 804, 805 }, accounted);
        }

        [Fact]
        public void Inaktiva_noder_flyttas_som_de_ar()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[]
            {
                Node(901, 10, 7, D, false, "C1"),
                Node(902, 10, 7, D, false, "A1"),
            });
            Assert.Equal(2, plan.Rows.Count);
            Assert.All(plan.Rows, r => Assert.False(r.Row.IsActive));
            Assert.Equal(0, plan.MergedNodes);
        }

        [Fact]
        public void Visningsnamn_normaliseras_till_klassens_id()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[] { Node(1001, 10, 7, D, true, "C Vet Y", "C1") });
            Assert.Equal(new[] { "C_Vet_Y", "C1" }, plan.Rows.Single().Row.ClassIds);
        }

        [Fact]
        public void Samma_klass_i_tva_former_blir_en_klass()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[] { Node(1101, 10, 7, D, true, "C_Vet_Y", "C Vet Y") });
            Assert.Equal(new[] { "C_Vet_Y" }, plan.Rows.Single().Row.ClassIds);
        }

        [Fact]
        public void Okand_klass_behalls_och_redovisas()
        {
            var plan = RegistrationMigrationPlanner.Plan(new[] { Node(1201, 10, 7, D, true, "C-H 50") });
            Assert.Equal(new[] { "C-H 50" }, plan.Rows.Single().Row.ClassIds);
            Assert.Contains("C-H 50", plan.UnknownClasses);
        }

        [Fact]
        public void Numerisk_klubbtext_blir_klubb_id()
        {
            var n = Node(1301, 10, 7, D, true, "C1");
            n.LegacyClubText = "2604";
            var plan = RegistrationMigrationPlanner.Plan(new[] { n });
            Assert.Equal(2604, plan.Rows.Single().Row.ClubId);
            Assert.Equal(1, plan.ClubFromLegacyText);
        }

        [Fact]
        public void Satt_klubb_vinner_over_klubbtext()
        {
            var n = Node(1401, 10, 7, D, true, "C1");
            n.ClubId = 2607;
            n.LegacyClubText = "2604";
            var plan = RegistrationMigrationPlanner.Plan(new[] { n });
            Assert.Equal(2607, plan.Rows.Single().Row.ClubId);
            Assert.Equal(0, plan.ClubFromLegacyText);
        }

        [Fact]
        public void Aldre_skalar_form_skrivs_som_klasslista()
        {
            var n = Node(1501, 10, 7, D, true, "C1");
            n.ClassesFromLegacyScalar = true;
            var plan = RegistrationMigrationPlanner.Plan(new[] { n });
            Assert.False(plan.Rows.Single().Row.ClassesFromLegacyScalar);
        }

        [Fact]
        public void Sammanslagning_behaller_fakturalank_och_klubb_fran_en_sammanslagen_nod()
        {
            var a = Node(1601, 10, 7, D, true, "C1"); a.InvoiceId = 9001; a.ClubId = 2604;
            var b = Node(1602, 10, 7, D.AddMinutes(1), true, "A1");
            var plan = RegistrationMigrationPlanner.Plan(new[] { a, b });
            var row = plan.Rows.Single();
            Assert.Equal(1602, row.Row.Id);
            Assert.Equal(9001, row.Row.InvoiceId);
            Assert.Equal(2604, row.Row.ClubId);
        }

        [Fact]
        public void Originalposterna_andras_inte()
        {
            var n = Node(1701, 10, 7, D, true, "C Vet Y");
            RegistrationMigrationPlanner.Plan(new[] { n });
            Assert.Equal("C Vet Y", n.Classes[0].Class);
        }
    }
}
