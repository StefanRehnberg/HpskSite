using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 6c (B12): lediga fönster att skjuta i.</summary>
    public class FreeWindowRulesTests
    {
        private static readonly DateTime Lor = new(2028, 8, 12);
        private static StaffInterval Iv(int from, int to) => StaffInterval.From(Lor, TimeSpan.FromHours(from), TimeSpan.FromHours(to))!.Value;
        private static string[] W(params StaffInterval[] busy) =>
            FreeWindowRules.Windows(busy, Lor).Select(w => $"{w.Start:HH:mm}-{w.End:HH:mm}").ToArray();

        [Fact]
        public void En_dag_utan_uppdrag_ar_hela_tavlingsdagen()
            => W().Should().Equal("07:00-20:00");

        [Fact]
        public void Ett_pass_i_mitten_lamnar_fonster_fore_och_efter()
            => W(Iv(10, 13)).Should().Equal("07:00-10:00", "13:00-20:00");

        [Fact]
        public void Tva_pass_ger_tre_fonster()
            => W(Iv(8, 10), Iv(13, 15)).Should().Equal("07:00-08:00", "10:00-13:00", "15:00-20:00");

        [Fact]
        public void Overlappande_pass_slas_ihop()
            => W(Iv(8, 11), Iv(10, 14)).Should().Equal("07:00-08:00", "14:00-20:00");

        [Fact]
        public void Kant_i_kant_lamnar_ingen_lucka()
            => W(Iv(8, 12), Iv(12, 16)).Should().Equal("07:00-08:00", "16:00-20:00");

        [Fact]
        public void En_lucka_kortare_an_en_timme_ar_inget_fonster()
            => W(Iv(8, 12), Iv(12, 13), Iv(13, 20)).Should().Equal("07:00-08:00");

        [Fact]
        public void Precis_en_timme_raknas()
            => FreeWindowRules.Windows(new[] { Iv(8, 12), Iv(13, 20) }, Lor).Select(w => w.Length).Should().Contain(TimeSpan.FromHours(1));

        [Fact]
        public void Heldag_tar_funktionarsdagen_08_17_men_inte_morgon_och_kvall()
        {
            var heldag = StaffInterval.From(Lor, null, null)!.Value;
            W(heldag).Should().Equal("07:00-08:00", "17:00-20:00");
        }

        [Fact]
        public void Ett_nattpass_fran_kvallen_fore_klipps_mot_dagen()
        {
            var natt = StaffInterval.From(Lor.AddDays(-1), TimeSpan.FromHours(22), TimeSpan.FromHours(9))!.Value;
            W(natt).Should().Equal("09:00-20:00");
        }

        [Fact]
        public void Ett_pass_utanfor_dagens_program_paverkar_inte()
            => W(Iv(2, 5), Iv(21, 23)).Should().Equal("07:00-20:00");

        [Fact]
        public void Ett_helt_upptaget_program_ger_inga_fonster()
            => FreeWindowRules.Windows(new[] { Iv(6, 21) }, Lor).Should().BeEmpty();

        [Fact]
        public void Ett_eget_program_och_en_egen_minimilangd_galler()
            => FreeWindowRules.Windows(new[] { Iv(10, 11) }, Lor, new DayProgram(TimeSpan.FromHours(9), TimeSpan.FromHours(12)), TimeSpan.FromMinutes(30))
                .Select(w => $"{w.Start:HH:mm}-{w.End:HH:mm}").Should().Equal("09:00-10:00", "11:00-12:00");
    }
}
