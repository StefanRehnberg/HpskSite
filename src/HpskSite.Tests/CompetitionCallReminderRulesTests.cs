using FluentAssertions;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.CompetitionCalls;
using HpskSite.Services.Ops;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Kretsutlysningen F6: vem som påminns när, och att det aldrig blir ett FAIL i driftskollen.</summary>
    public class CompetitionCallReminderRulesTests
    {
        private static readonly DateTime Today = new(2027, 3, 15);

        private static CompetitionCall Awarded(int daysAgo, DateTime? date = null, int? competitionId = null) => new()
        {
            Id = 7, RegionId = 3788, Name = "KM Precision", Status = CompetitionCallStatus.Tilldelad, AwardedClubId = 9003,
            AwardedAt = Today.AddDays(-daysAgo), CompetitionDate = date ?? Today.AddDays(40), CompetitionId = competitionId
        };

        private static CompetitionCall Open(DateTime? deadline) => new()
        {
            Id = 8, RegionId = 3788, Name = "Vårcupen", Status = CompetitionCallStatus.Utlyst, WishDeadline = deadline
        };

        [Fact]
        public void Klubben_pamins_forst_efter_14_dagar()
        {
            CompetitionCallRules.NeedsCreate(Awarded(13), Today).Should().BeFalse();
            CompetitionCallRules.NeedsCreate(Awarded(14), Today).Should().BeTrue();
        }

        [Fact]
        public void Ingen_paminnelse_nar_tavlingen_ar_skapad_eller_dagen_har_passerat()
        {
            CompetitionCallRules.NeedsCreate(Awarded(30, competitionId: 555), Today).Should().BeFalse();
            CompetitionCallRules.NeedsCreate(Awarded(30, date: Today.AddDays(-1)), Today).Should().BeFalse("dagen har gått");
            CompetitionCallRules.NeedsCreate(Awarded(30, date: Today), Today).Should().BeTrue("tävlingen är i dag: fortfarande ingen tävling");
        }

        [Fact]
        public void Datumlos_utlysning_paminns_tills_den_far_ett_datum()
        {
            var c = Awarded(20); c.CompetitionDate = null;
            CompetitionCallRules.NeedsCreate(c, Today).Should().BeTrue();
        }

        [Fact]
        public void Bara_tilldelad_paminns_om_att_skapa()
        {
            var c = Awarded(30); c.Status = CompetitionCallStatus.Utlyst;
            CompetitionCallRules.NeedsCreate(c, Today).Should().BeFalse();
        }

        [Fact]
        public void Kretsen_paminns_nar_sista_dagen_gatt_och_klubbar_onskat()
        {
            CompetitionCallRules.NeedsAward(Open(Today.AddDays(-1)), 2, Today).Should().BeTrue();
            CompetitionCallRules.NeedsAward(Open(Today.AddDays(-1)), 0, Today).Should().BeFalse("ingen har önskat: inget att utse");
            CompetitionCallRules.NeedsAward(Open(Today), 2, Today).Should().BeFalse("sista dagen är i dag");
            CompetitionCallRules.NeedsAward(Open(null), 2, Today).Should().BeFalse("ingen sista dag satt");
        }

        [Fact]
        public void Nyckeln_ar_stabil_inom_en_period_och_ny_nasta()
        {
            var a = CompetitionCallRules.DueReminders(new[] { Awarded(14) }, _ => 0, Today).Single();
            var sameDay = CompetitionCallRules.DueReminders(new[] { Awarded(14) }, _ => 0, Today).Single();
            var later = CompetitionCallRules.DueReminders(new[] { Awarded(14) }, _ => 0, Today.AddDays(13)).Single();
            var nextPeriod = CompetitionCallRules.DueReminders(new[] { Awarded(14) }, _ => 0, Today.AddDays(14)).Single();
            a.Kind.Should().Be(CompetitionCallRules.ReminderClubCreate);
            sameDay.Key.Should().Be(a.Key, "ett omstartat svep ger samma nyckel och därmed ingen andra mejl");
            later.Key.Should().Be(a.Key, "fortfarande samma period");
            nextPeriod.Key.Should().NotBe(a.Key, "efter 14 dagar går en ny påminnelse");
        }

        [Fact]
        public void Kretsens_paminnelse_har_egen_art_och_kraver_onskemal()
        {
            var due = CompetitionCallRules.DueReminders(new[] { Open(Today.AddDays(-3)) }, _ => 1, Today);
            due.Should().ContainSingle().Which.Kind.Should().Be(CompetitionCallRules.ReminderKretsAward);
            CompetitionCallRules.DueReminders(new[] { Open(Today.AddDays(-3)) }, _ => 0, Today).Should().BeEmpty();
        }

        [Fact]
        public void Driftskollen_varnar_men_faller_aldrig_for_utlysningar()
        {
            var r = new CompetitionHealthService.Report { CallsAwardedWithoutNotice = 2, CallsAwardedNotCreated = 1 };
            CompetitionHealthService.Evaluate(r);
            r.Warnings.Should().Contain(w => w.Contains("2 utsedda klubbar fick inget mejl"));
            r.Warnings.Should().Contain(w => w.Contains("1 utsedda klubbar har inte skapat tävlingen"));
            r.Problems.Should().BeEmpty();
            r.Status.Should().Be("WARN");
        }

        [Fact]
        public void Driftskollen_ar_ren_utan_utlysningar_att_ta_tag_i()
        {
            var r = new CompetitionHealthService.Report();
            CompetitionHealthService.Evaluate(r);
            r.Warnings.Should().NotContain(w => w.Contains("utsedda klubbar"));
        }
    }
}
