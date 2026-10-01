using FluentAssertions;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    public class CompetitionApplicationRemindersTests
    {
        private static readonly DateTime Today = new(2027, 4, 1);

        private static CompetitionApplication A(int id, string status, DateTime date, string level = "Krets", DateTime? updated = null, int? compId = null, string? opinion = null)
            => new() { Id = id, RegionId = 3788, Name = "T" + id, Level = level, Discipline = "Precision", Status = status,
                       CompetitionDate = date, UpdatedAt = updated ?? Today, CompetitionId = compId, KretsOpinion = opinion };

        [Fact]
        public void GodkandUtanTavling_PaminnsInom8Veckor()
        {
            var due = CompetitionApplicationReminders.Compute(new[] { A(1, "Beviljad", Today.AddDays(50)) }, Today);
            due.Should().ContainSingle(d => d.Key == "app-8w-1" && d.Kind == CompetitionApplicationReminders.KindCreateCompetition);
        }

        [Fact]
        public void GodkandLangtBort_PaminnsInte() =>
            CompetitionApplicationReminders.Compute(new[] { A(1, "Beviljad", Today.AddDays(90)) }, Today).Should().BeEmpty();

        [Fact]
        public void GodkandMedTavling_PaminnsInte() =>
            CompetitionApplicationReminders.Compute(new[] { A(1, "Beviljad", Today.AddDays(30), compId: 55) }, Today).Should().BeEmpty();

        [Fact]
        public void TillstyrktNationell_PaminnsOcksa() =>
            CompetitionApplicationReminders.Compute(new[] { A(1, "HosForbundet", Today.AddDays(30), "Nationell", opinion: "Tillstyrker") }, Today)
                .Should().Contain(d => d.Kind == CompetitionApplicationReminders.KindCreateCompetition);

        [Fact]
        public void InskickadOrord14Dagar_PaminnerKretsen()
        {
            var due = CompetitionApplicationReminders.Compute(new[] { A(2, "Inskickad", Today.AddDays(120), updated: Today.AddDays(-15)) }, Today);
            due.Should().ContainSingle(d => d.Kind == CompetitionApplicationReminders.KindKretsStale);
            due[0].Key.Should().Be($"krets-14d-2-{Today.AddDays(-15):yyyyMMdd}", "nyckeln beväpnar om när klubben kompletterar");
        }

        [Fact]
        public void InskickadNyss_PaminnerInte() =>
            CompetitionApplicationReminders.Compute(new[] { A(2, "Inskickad", Today.AddDays(120), updated: Today.AddDays(-3)) }, Today).Should().BeEmpty();

        [Fact]
        public void Komplettering_VantarPaKlubben_KretsenPaminnsInte() =>
            CompetitionApplicationReminders.Compute(new[] { A(2, "Komplettering", Today.AddDays(120), updated: Today.AddDays(-30)) }, Today).Should().BeEmpty();

        [Fact]
        public void ForbundetsGrans_PaminnerEnGangPerKrets()
        {
            var today = new DateTime(2027, 9, 20);   // tio dagar före 30 september
            var apps = new[]
            {
                A(3, "Inskickad", new DateTime(2028, 6, 1), "Nationell", updated: today),
                A(4, "Komplettering", new DateTime(2028, 7, 1), "Landsdel", updated: today),
                A(5, "Inskickad", new DateTime(2028, 7, 1), "Krets", updated: today)
            };
            var due = CompetitionApplicationReminders.Compute(apps, today).Where(d => d.Kind == CompetitionApplicationReminders.KindForbundet).ToList();
            due.Should().ContainSingle();
            due[0].Key.Should().Be("forbundet-3788-2028");
            due[0].Apps!.Select(a => a.Id).Should().BeEquivalentTo(new[] { 3, 4 }, "kretstävlingar går inte till Förbundet");
        }

        [Fact]
        public void ForbundetsGrans_LangtFram_PaminnerInte()
        {
            var today = new DateTime(2027, 8, 1);
            CompetitionApplicationReminders.Compute(new[] { A(3, "Inskickad", new DateTime(2028, 6, 1), "Nationell", updated: today) }, today)
                .Should().NotContain(d => d.Kind == CompetitionApplicationReminders.KindForbundet);
        }
    }
}
