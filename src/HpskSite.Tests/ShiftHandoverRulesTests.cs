using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Avlösningen sedd från den som står i skiftet (Stefan 2026-10-27): "Du avlöses kl 14:00 av Berit" och "Du tar över kl 11:00 efter Anna".</summary>
    public class ShiftHandoverRulesTests
    {
        private static readonly DateTime D = new(2027, 6, 12);
        private static DateTime T(int h, int m = 0) => D.AddHours(h).AddMinutes(m);
        private static int _id;

        private static StaffAssignment A(string name, int h1, int h2, int? member = null, string role = "stationschef", string? scope = "Station", string? key = "3",
            string status = "Planned", int planning = 7, int m1 = 0) => new()
        {
            Id = ++_id, PlanningId = planning, MemberId = member ?? _id + 1000, DisplayName = name, RoleKey = role, ScopeType = scope, ScopeKey = key,
            StartsAt = T(h1, m1), EndsAt = T(h2), Status = status,
        };

        [Fact]
        public void Mitt_i_kedjan_ser_både_före_och_efter()
        {
            var anna = A("Anna", 8, 11); var berit = A("Berit", 11, 14); var cecilia = A("Cecilia", 14, 17);
            var h = ShiftHandoverRules.For(berit, new[] { anna, berit, cecilia });
            h.Before.Should().Be("Du tar över kl 11:00 efter Anna.");
            h.After.Should().Be("Du avlöses kl 14:00 av Cecilia.");
        }

        [Fact]
        public void Första_skiftet_har_bara_efter_och_sista_bara_före()
        {
            var anna = A("Anna", 8, 11); var berit = A("Berit", 11, 14);
            ShiftHandoverRules.For(anna, new[] { anna, berit }).Should().Be(new ShiftHandoverRules.Handover(null, "Du avlöses kl 11:00 av Berit."));
            ShiftHandoverRules.For(berit, new[] { anna, berit }).Should().Be(new ShiftHandoverRules.Handover("Du tar över kl 11:00 efter Anna.", null));
        }

        [Fact]
        public void Glapp_inom_en_timme_nämns_med_grannens_egen_tid()
        {
            var anna = A("Anna", 8, 11); var berit = A("Berit", 11, 14, m1: 30);
            ShiftHandoverRules.For(anna, new[] { anna, berit }).After.Should().Be("Nästa på platsen är Berit från 11:30.");
            ShiftHandoverRules.For(berit, new[] { anna, berit }).Before.Should().Be("Före dig på platsen: Anna till 11:00.");
        }

        [Fact]
        public void Glapp_över_en_timme_är_ingen_avlösning()
        {
            var anna = A("Anna", 8, 10); var berit = A("Berit", 12, 15);
            ShiftHandoverRules.For(anna, new[] { anna, berit }).After.Should().BeNull();
        }

        [Fact]
        public void Samtidiga_skift_är_ingen_avlösning()
        {
            var anna = A("Anna", 8, 12); var berit = A("Berit", 8, 12);
            ShiftHandoverRules.For(anna, new[] { anna, berit }).Should().Be(new ShiftHandoverRules.Handover(null, null));
        }

        [Fact]
        public void Annan_roll_annan_station_annan_planering_eller_annan_dag_räknas_inte()
        {
            var anna = A("Anna", 8, 11);
            var annan = new[] { A("B", 11, 14, role: "sekretariat"), A("C", 11, 14, key: "4"), A("D", 11, 14, planning: 8), new StaffAssignment { Id = 99, PlanningId = 7, MemberId = 5, DisplayName = "E", RoleKey = "stationschef", ScopeType = "Station", ScopeKey = "3", StartsAt = T(35), EndsAt = T(38) } };
            ShiftHandoverRules.For(anna, annan).Should().Be(new ShiftHandoverRules.Handover(null, null));
        }

        [Fact]
        public void Samma_person_är_inte_sin_egen_avlösare()
        {
            var anna = A("Anna", 8, 11, member: 5); var igen = A("Anna", 11, 14, member: 5);
            ShiftHandoverRules.For(anna, new[] { anna, igen }).After.Should().BeNull();
        }

        [Fact]
        public void Ett_skift_utan_tider_har_ingen_avlösning_och_utan_scope_matchar_hela_tävlingen()
        {
            var heldag = new StaffAssignment { Id = 500, PlanningId = 7, RoleKey = "stationschef", DisplayName = "X" };
            ShiftHandoverRules.For(heldag, new[] { A("Anna", 8, 11) }).Should().Be(new ShiftHandoverRules.Handover(null, null));
            var a = A("Anna", 8, 11, scope: null, key: null); var b = A("Berit", 11, 14, scope: "All", key: null);
            ShiftHandoverRules.For(a, new[] { a, b }).After.Should().Be("Du avlöses kl 11:00 av Berit.");
        }

        [Fact]
        public void Närmast_i_tiden_väljs_när_flera_kommer_efter()
        {
            var anna = A("Anna", 8, 11); var sent = A("Sven", 11, 12, m1: 30); var rakt = A("Berit", 11, 14);
            ShiftHandoverRules.For(anna, new[] { anna, sent, rakt }).After.Should().Be("Du avlöses kl 11:00 av Berit.");
        }
    }
}
