using HpskSite.Models.Schedule;
using HpskSite.Services.Mail;
using HpskSite.Services.Planning;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Steg 4 (B9): skickar <b>ett mejl per person och dag</b> för veckan över planeringsfamiljerna, kvällen före. Anropas av
    /// <see cref="ScheduleReminderHostedService"/> efter pushpåminnelserna. Logiken ligger i <see cref="WeekDigestRunner"/>; den här
    /// klassen är databasen och sändningen bakom <see cref="IWeekDigestPorts"/>.
    ///
    /// <para><b>Claim-then-send</b> i <c>ScheduleReminderLog</c> (CompetitionId 0, nyckeln <c>week-{dag}</c>) — samma unika spärr som
    /// pushpåminnelserna. Går sändningen inte iväg släpps spärren och ett försök räknas (<c>weekfail-{dag}-…</c>); efter
    /// <see cref="StaffWeekDigest.MaxAttempts"/> ges det upp för dagen.</para>
    ///
    /// <para><b>Nödbroms:</b> <c>Schedule:WeekDigest:Enabled = false</c> i appsettings stoppar utskicken utan deploy (saknas nyckeln är
    /// mejlet på). Ett skickat mejl går inte att ta tillbaka — till skillnad från behörigheten, som inte har någon av/på-inställning (B16).</para>
    ///
    /// <para><b>Vilka familjer:</b> bara där veckan tillför något (<see cref="MyWeekService.WeekFamilySql"/>).</para>
    /// </summary>
    public class StaffWeekDigestService : IWeekDigestPorts
    {
        private readonly IScopeProvider _scopes;
        private readonly MyWeekService _week;
        private readonly PlanningService _planning;
        private readonly PlanningLinkService _links;
        private readonly IWeekDigestMailer _mailer;
        private readonly IMemberService _members;
        private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
        private readonly ILogger<StaffWeekDigestService> _logger;

        public StaffWeekDigestService(IScopeProvider scopes, MyWeekService week, PlanningService planning, PlanningLinkService links,
            IWeekDigestMailer mailer, IMemberService members, Microsoft.Extensions.Configuration.IConfiguration config, ILogger<StaffWeekDigestService> logger)
        {
            _scopes = scopes; _week = week; _planning = planning; _links = links; _mailer = mailer; _members = members; _config = config; _logger = logger;
        }

        /// <summary>Nödbromsen. Saknas inställningen är mejlet på.</summary>
        public bool Enabled => _config.GetValue<bool?>("Schedule:WeekDigest:Enabled") ?? true;

        public sealed class Candidate
        {
            public int MemberId { get; set; }
            public int RootId { get; set; }
            public string? Status { get; set; }
            public DateTime? NotifiedAt { get; set; }
        }

        /// <summary>Uppdragen som ligger på dagen <c>@0</c> i en familj där veckan tillför något. Datumet är uppdragets egen dag (starttid → dag → passets dag).</summary>
        public static readonly string CandidatesSql = @"
            SELECT sa.MemberId AS MemberId, p.FamilyRootId AS RootId, sa.Status AS Status, sa.NotifiedAt AS NotifiedAt
              FROM StaffAssignment sa
              JOIN Planning p ON p.Id = sa.PlanningId AND p.DeletedAt IS NULL
              LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
             WHERE sa.MemberId IS NOT NULL AND sa.PlanningId IS NOT NULL
               AND COALESCE(CAST(sa.StartsAt AS date), CAST(sa.DayDate AS date), CAST(sp.PassDate AS date)) = @0
               AND " + MyWeekService.WeekFamilySql;

        /// <summary>Ett svep. Returnerar antalet mejl som gick iväg. Gör ingenting utanför kvällsfönstret eller när nödbromsen är på.</summary>
        public async Task<int> SendAsync(DateTime now, CancellationToken ct)
        {
            if (!Enabled) return 0;
            var r = await WeekDigestRunner.RunAsync(now, this, _links.SiteUrl, ct);
            if (r.Failed > 0)
                _logger.LogWarning("Veckomejlet: {Sent} skickade, {Failed} misslyckades{Aborted}, {Skipped} uppgivna efter {Max} försök.",
                    r.Sent, r.Failed, r.Aborted ? " (svepet avbröts, SMTP verkar vara nere)" : "", r.Skipped, StaffWeekDigest.MaxAttempts);
            return r.Sent;
        }

        // ---------------------------------------------------------------- IWeekDigestPorts

        public IReadOnlyList<(int MemberId, int RootId)> Candidates(DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                return s.Database.Fetch<Candidate>(CandidatesSql, day.Date)
                    .Where(c => StaffWeekDigest.Remindable(c.Status, c.NotifiedAt))
                    .Select(c => (c.MemberId, c.RootId))
                    .Distinct()
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Veckomejlet: kandidaterna för {Day:yyyy-MM-dd} gick inte att läsa.", day);
                return new List<(int, int)>();
            }
        }

        public MySchedule? Week(int memberId, int rootId, DateTime day) => _week.GetOrThrow(memberId, rootId, day, day);

        public DigestRecipient? Recipient(int memberId)
        {
            var m = _members.GetById(memberId);
            return m == null || string.IsNullOrWhiteSpace(m.Email) ? null : new DigestRecipient(m.Email, m.Name ?? "");
        }

        /// <summary>Huvudplaneringens sammankallande (den som skapade den först) — den man ringer, och som svaret går till.</summary>
        public DigestContact? Contact(int rootId) => _week.ContactFor(rootId);

        public Task<bool> SendAsync(string toEmail, string subject, string html, MailReplyTo replyTo) => _mailer.SendAsync(toEmail, subject, html, replyTo);

        public bool TryClaim(int memberId, DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope();
                var n = s.Database.Execute(@"
                    INSERT INTO ScheduleReminderLog (CompetitionId, MemberId, SourceKey, ItemStartsAt, SentAt)
                    SELECT 0, @0, @1, @2, GETDATE()
                     WHERE NOT EXISTS (SELECT 1 FROM ScheduleReminderLog WHERE CompetitionId = 0 AND MemberId = @0 AND SourceKey = @1)",
                    memberId, StaffWeekDigest.Key(day), day.Date);
                s.Complete();
                return n > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Veckomejlet: spärren för medlem {Member} gick inte att ta.", memberId);
                return false;
            }
        }

        public void Release(int memberId, DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope();
                s.Database.Execute("DELETE FROM ScheduleReminderLog WHERE CompetitionId = 0 AND MemberId = @0 AND SourceKey = @1", memberId, StaffWeekDigest.Key(day));
                s.Complete();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Veckomejlet: spärren för medlem {Member} gick inte att släppa.", memberId); }
        }

        public int Failures(int memberId, DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                return s.Database.ExecuteScalar<int>("SELECT COUNT(*) FROM ScheduleReminderLog WHERE CompetitionId = 0 AND MemberId = @0 AND SourceKey LIKE @1",
                    memberId, StaffWeekDigest.FailPrefix(day) + "%");
            }
            catch { return int.MaxValue; }   // kan vi inte räkna försöken skickar vi hellre inget än fem timmars omförsök
        }

        public void RecordFailure(int memberId, DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope();
                s.Database.Execute(@"INSERT INTO ScheduleReminderLog (CompetitionId, MemberId, SourceKey, ItemStartsAt, SentAt) VALUES (0, @0, @1, @2, GETDATE())",
                    memberId, StaffWeekDigest.FailPrefix(day) + Guid.NewGuid().ToString("N"), day.Date);
                s.Complete();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Veckomejlet: misslyckandet för medlem {Member} gick inte att räkna.", memberId); }
        }
    }

    /// <summary>Själva sändningen av veckomejlet — ett eget gränssnitt så att den kan bytas mot en falsk i tester.</summary>
    public interface IWeekDigestMailer
    {
        Task<bool> SendAsync(string toEmail, string subject, string html, MailReplyTo replyTo);
    }

    public sealed class SmtpWeekDigestMailer : IWeekDigestMailer
    {
        private readonly HpskSite.Services.EmailService _email;
        public SmtpWeekDigestMailer(HpskSite.Services.EmailService email) { _email = email; }
        /// <summary>
        /// SmtpClient.Timeout gäller inte säkert den asynkrona sändningen, så en egen gräns: svarar SMTP inte inom 45 s räknas det som ett
        /// misslyckande (ett försök på taket) i stället för att hålla hela svepet — och pushpåminnelserna efter det.
        /// </summary>
        public async Task<bool> SendAsync(string toEmail, string subject, string html, MailReplyTo replyTo)
        {
            var send = _email.SendPlanningMailAsync(toEmail, subject, html, replyTo);
            var done = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(45)));
            return done == send && await send;
        }
    }
}
