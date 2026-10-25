using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services.Mail;
using HpskSite.Services.Schedule;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Steg 4–6 K3/C1/C7/C8: själva sändvägen — claim, släpp, försökstak, avbrott, ett mejl per person, fel som inte tolkas som "inget".
    /// Falska portar i stället för databas och SMTP; den här delen var den enda som var helt otestad före granskningen.
    /// </summary>
    public class WeekDigestRunnerTests
    {
        private static readonly DateTime Kvall = new(2028, 8, 11, 18, 0, 0);   // fredag kväll → gäller lördag
        private static readonly DateTime Lor = new(2028, 8, 12);

        private sealed class Fake : IWeekDigestPorts
        {
            public List<(int MemberId, int RootId)> Cands = new();
            public HashSet<(int, string)> Claims = new();
            public Dictionary<int, int> FailCount = new();
            public List<(string To, string Subject, string Html, MailReplyTo Reply)> Mails = new();
            public Func<int, int, MySchedule?> WeekFor = (m, r) => MakeWeek(r, "Fält");
            public Func<int, DigestRecipient?> RecipientFor = m => new DigestRecipient($"m{m}@example.invalid", $"Medlem {m}");
            public Func<int, DigestContact?> ContactFor = r => new DigestContact("Anna Ek", "070-1", "anna@example.invalid");
            public Func<bool> SendResult = () => true;
            public int Released, Recorded, SendCalls;

            public IReadOnlyList<(int MemberId, int RootId)> Candidates(DateTime day) => Cands;
            public bool TryClaim(int memberId, DateTime day) => Claims.Add((memberId, StaffWeekDigest.Key(day)));
            public void Release(int memberId, DateTime day) { Claims.Remove((memberId, StaffWeekDigest.Key(day))); Released++; }
            public int Failures(int memberId, DateTime day) => FailCount.GetValueOrDefault(memberId);
            public void RecordFailure(int memberId, DateTime day) { FailCount[memberId] = FailCount.GetValueOrDefault(memberId) + 1; Recorded++; }
            public MySchedule? Week(int memberId, int rootId, DateTime day) => WeekFor(memberId, rootId);
            public DigestRecipient? Recipient(int memberId) => RecipientFor(memberId);
            public DigestContact? Contact(int rootId) => ContactFor(rootId);
            public Task<bool> SendAsync(string toEmail, string subject, string html, MailReplyTo replyTo)
            {
                SendCalls++;
                var ok = SendResult();
                if (ok) Mails.Add((toEmail, subject, html, replyTo));
                return Task.FromResult(ok);
            }
        }

        private static MySchedule MakeWeek(int root, string part)
        {
            var e = new PersonWeekEntry("a" + root, PersonWeekKind.Assignment, Lor, StaffInterval.From(Lor, TimeSpan.FromHours(8), TimeSpan.FromHours(12)), null,
                $"Markör ({part})", root, null, "Markör", part, "Accepted");
            return MyWeekService.ToSchedule(root, "Veckan " + root, PersonWeek.Build(new[] { e }, Lor, Lor), null, null, Kvall);
        }

        private static Task<WeekDigestResult> Run(Fake f, DateTime? now = null) =>
            WeekDigestRunner.RunAsync(now ?? Kvall, f, "https://pistol.nu", CancellationToken.None);

        [Fact]
        public async Task Ett_mejl_gar_iväg_med_ratt_mottagare_ratt_amne_och_kontaktperson()
        {
            var f = new Fake { Cands = { (1, 10) } };
            var r = await Run(f);
            r.Sent.Should().Be(1);
            f.Mails.Should().ContainSingle();
            f.Mails[0].To.Should().Be("m1@example.invalid");
            f.Mails[0].Subject.Should().Contain("lördag 12 augusti").And.Contain("Veckan 10");
            f.Mails[0].Html.Should().Contain("Anna Ek").And.Contain("tel:070-1").And.Contain("https://pistol.nu/mitt-schema?p=10");
            f.Mails[0].Reply.Should().NotBeSameAs(MailReplyTo.NoReply);
        }

        [Fact]
        public async Task Utan_kontaktpersonens_adress_ar_svara_inte_ett_uttryckligt_val()
        {
            var f = new Fake { Cands = { (1, 10) }, ContactFor = _ => null };
            await Run(f);
            f.Mails.Single().Reply.Should().BeSameAs(MailReplyTo.NoReply);
        }

        [Theory]
        [InlineData(16)]
        [InlineData(22)]
        [InlineData(3)]
        public async Task Utanfor_kvallsfonstret_skickas_ingenting_och_inget_tas(int hour)
        {
            var f = new Fake { Cands = { (1, 10) } };
            var r = await Run(f, new DateTime(2028, 8, 11, hour, 30, 0));
            (r.Sent, f.SendCalls, f.Claims.Count).Should().Be((0, 0, 0));
        }

        [Fact]
        public async Task Samma_person_i_tva_familjer_far_ETT_mejl_med_bada()
        {
            var f = new Fake { Cands = { (1, 10), (1, 20), (2, 10) } };
            var r = await Run(f);
            r.Sent.Should().Be(2);
            var mailToOne = f.Mails.Single(m => m.To == "m1@example.invalid");
            mailToOne.Html.Should().Contain("Veckan 10").And.Contain("Veckan 20");
            mailToOne.Subject.Should().NotContain("Veckan");   // flera familjer: inget enskilt namn i ämnet
            f.Mails.Count(m => m.To == "m1@example.invalid").Should().Be(1);
        }

        [Fact]
        public async Task Ett_andra_svep_samma_kvall_skickar_inget_nytt()
        {
            var f = new Fake { Cands = { (1, 10) } };
            await Run(f);
            var again = await Run(f);
            again.Sent.Should().Be(0);
            f.Mails.Should().ContainSingle();
        }

        [Fact]
        public async Task Ett_misslyckande_slapper_sparren_raknar_ett_forsok_och_nasta_svep_provar_igen()
        {
            var calls = 0;
            var f = new Fake { Cands = { (1, 10) }, SendResult = () => ++calls > 1 };
            var first = await Run(f);
            (first.Sent, first.Failed, f.Released, f.Recorded).Should().Be((0, 1, 1, 1));
            f.Claims.Should().BeEmpty();
            var second = await Run(f);
            second.Sent.Should().Be(1);
        }

        [Fact]
        public async Task Efter_tre_misslyckade_forsok_ges_det_upp_for_dagen()
        {
            var f = new Fake { Cands = { (1, 10) }, SendResult = () => false };
            for (var i = 0; i < 6; i++) await Run(f);
            f.SendCalls.Should().Be(StaffWeekDigest.MaxAttempts);
            (await Run(f)).Skipped.Should().Be(1);
        }

        [Fact]
        public async Task Tre_misslyckanden_i_rad_avbryter_svepet_sa_att_inte_alla_andra_provas()
        {
            var f = new Fake { SendResult = () => false };
            for (var m = 1; m <= 10; m++) f.Cands.Add((m, 10));
            var r = await Run(f);
            r.Aborted.Should().BeTrue();
            f.SendCalls.Should().Be(StaffWeekDigest.MaxConsecutiveFailures);
        }

        [Fact]
        public async Task En_lyckad_sandning_nollstaller_radraknaren()
        {
            var results = new Queue<bool>(new[] { false, false, true, false, false, true });
            var f = new Fake { SendResult = () => results.Dequeue() };
            for (var m = 1; m <= 6; m++) f.Cands.Add((m, 10));
            var r = await Run(f);
            r.Aborted.Should().BeFalse();
            r.Sent.Should().Be(2);
        }

        [Fact]
        public async Task Ett_fel_nar_veckan_byggs_ar_ett_misslyckande_inte_inget_att_skicka()
        {
            var f = new Fake { Cands = { (1, 10) }, WeekFor = (_, _) => throw new InvalidOperationException("databasen") };
            var r = await Run(f);
            (r.Sent, r.Failed, f.Released, f.Recorded).Should().Be((0, 1, 1, 1));
            f.Claims.Should().BeEmpty();   // spärren står inte kvar och förlorar mejlet
        }

        [Fact]
        public async Task Ingenting_den_dagen_ar_klart_utan_mejl_och_spararen_far_stå()
        {
            var f = new Fake { Cands = { (1, 10) }, WeekFor = (_, _) => null };
            var r = await Run(f);
            (r.Sent, r.Failed, f.SendCalls).Should().Be((0, 0, 0));
            f.Claims.Should().ContainSingle();
        }

        [Fact]
        public async Task En_medlem_utan_adress_hoppas_over_utan_att_raknas_som_fel()
        {
            var f = new Fake { Cands = { (1, 10), (2, 10) }, RecipientFor = m => m == 1 ? null : new DigestRecipient("x@example.invalid", "X") };
            var r = await Run(f);
            (r.Sent, r.Failed).Should().Be((1, 0));
        }

        [Fact]
        public async Task Ett_undantag_vid_sandningen_ar_ett_misslyckande()
        {
            var f = new Fake { Cands = { (1, 10) } };
            f.SendResult = () => throw new TimeoutException();
            var r = await Run(f);
            (r.Failed, f.Released).Should().Be((1, 1));
        }

        [Fact]
        public async Task Avbruten_forfragan_stoppar_svepet()
        {
            var f = new Fake { Cands = { (1, 10), (2, 10) } };
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var r = await WeekDigestRunner.RunAsync(Kvall, f, "u", cts.Token);
            f.SendCalls.Should().Be(0);
            r.Sent.Should().Be(0);
        }
    }
}
