using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    public class CompetitionApplicationRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 1);

        private static CompetitionApplication App(string level = "Krets", string status = "Inskickad") => new()
        {
            Name = "Höstpokalen", Level = level, Discipline = "Precision",
            CompetitionDate = new DateTime(2027, 5, 1), Status = status
        };

        [Fact]
        public void Foreningstavling_SoksInte()
        {
            CompetitionApplicationRules.IsApplicableLevel(CompetitionLevel.Forening).Should().BeFalse();
            CompetitionApplicationRules.ValidateContent(App(CompetitionLevel.Forening), Today).Should().Contain("söks inte");
        }

        [Fact]
        public void Rikstavling_SoksInte() =>
            CompetitionApplicationRules.IsApplicableLevel(CompetitionLevel.Riks).Should().BeFalse();

        [Theory]
        [InlineData("Krets", false)]
        [InlineData("Landsdel", true)]
        [InlineData("Nationell", true)]
        public void ForbundetBeslutar_BaraOverKrets(string level, bool expected) =>
            CompetitionApplicationRules.NeedsForbundet(level).Should().Be(expected);

        [Fact]
        public void GiltigAnsokan_Passerar() =>
            CompetitionApplicationRules.ValidateContent(App(), Today).Should().BeNull();

        [Fact]
        public void PasseratDatum_Vagras()
        {
            var a = App(); a.CompetitionDate = Today.AddDays(-1);
            CompetitionApplicationRules.ValidateContent(a, Today).Should().Contain("passerat");
        }

        [Fact]
        public void SlutdatumForeStart_Vagras()
        {
            var a = App(); a.EndDate = a.CompetitionDate.AddDays(-1);
            CompetitionApplicationRules.ValidateContent(a, Today).Should().Contain("Slutdatumet");
        }

        [Fact]
        public void OkandGren_Vagras()
        {
            var a = App(); a.Discipline = "Bågskytte";
            CompetitionApplicationRules.ValidateContent(a, Today).Should().Contain("gren");
        }

        [Fact]
        public void TomtNamn_Vagras()
        {
            var a = App(); a.Name = "  ";
            CompetitionApplicationRules.ValidateContent(a, Today).Should().Contain("namn");
        }

        [Theory]
        [InlineData("Utkast", true)]
        [InlineData("Inskickad", true)]
        [InlineData("Komplettering", true)]
        [InlineData("HosForbundet", false)]
        [InlineData("Beviljad", false)]
        [InlineData("Avslagen", false)]
        public void KlubbenFarAndra_ForeBeslut(string status, bool expected) =>
            CompetitionApplicationRules.ClubCanEdit(status).Should().Be(expected);

        [Theory]
        [InlineData("Inskickad", true)]
        [InlineData("Komplettering", true)]
        [InlineData("Utkast", false)]
        [InlineData("HosForbundet", false)]
        [InlineData("Beviljad", false)]
        public void KretsenAgerar_BaraNarDenArPaTur(string status, bool expected) =>
            CompetitionApplicationRules.KretsCanAct(status).Should().Be(expected);

        [Fact]
        public void Aterkallad_GarInteAttAterkallaIgen()
        {
            CompetitionApplicationRules.ClubCanWithdraw(CompetitionApplicationStatus.Aterkallad).Should().BeFalse();
            CompetitionApplicationRules.ClubCanWithdraw(CompetitionApplicationStatus.Avslagen).Should().BeFalse();
            CompetitionApplicationRules.ClubCanWithdraw(CompetitionApplicationStatus.Beviljad).Should().BeTrue();
        }

        [Fact]
        public void TavlingKanSkapas_EfterKretsensJa()
        {
            CompetitionApplicationRules.CanCreateCompetition(App(status: "Beviljad")).Should().BeTrue();
            CompetitionApplicationRules.CanCreateCompetition(App(status: "Inskickad")).Should().BeFalse();
            CompetitionApplicationRules.CanCreateCompetition(App(status: "Avslagen")).Should().BeFalse();
        }

        [Fact]
        public void Nationell_KanSkapasEfterTillstyrkan_MenInteEfterAvstyrkan()
        {
            var a = App("Nationell", "HosForbundet");
            a.KretsOpinion = CompetitionApplicationOpinion.Tillstyrker;
            CompetitionApplicationRules.CanCreateCompetition(a).Should().BeTrue();
            a.KretsOpinion = CompetitionApplicationOpinion.Avstyrker;
            CompetitionApplicationRules.CanCreateCompetition(a).Should().BeFalse();
        }

        [Fact]
        public void RedanKopplad_KanInteSkapasIgen()
        {
            var a = App(status: "Beviljad"); a.CompetitionId = 123;
            CompetitionApplicationRules.CanCreateCompetition(a).Should().BeFalse();
        }

        [Fact]
        public void ForbundetsGrans_Ar30SeptemberAretFore() =>
            CompetitionApplicationRules.ForbundetDeadline(2028).Should().Be(new DateTime(2027, 9, 30));

        [Fact]
        public void SistaDagen_RaknasMed()
        {
            var last = new DateTime(2026, 9, 1);
            CompetitionApplicationRules.IsLate(last, last).Should().BeFalse();
            CompetitionApplicationRules.IsLate(last.AddDays(1), last).Should().BeTrue();
        }

        [Fact]
        public void EffektivtDatum_ArForbundetsOmDetFlyttats()
        {
            var a = App();
            a.EffectiveDate.Should().Be(a.CompetitionDate);
            a.GrantedDate = new DateTime(2027, 5, 8);
            a.EffectiveDate.Should().Be(new DateTime(2027, 5, 8));
        }
    }
}
