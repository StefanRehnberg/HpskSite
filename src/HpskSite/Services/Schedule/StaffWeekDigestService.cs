using HpskSite.Services.Mail;
using HpskSite.Services.Planning;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Steg 4 (B9): skickar <b>ett mejl per person och dag</b> för veckan över en planeringsfamilj, kvällen före (se
    /// <see cref="StaffWeekDigest"/>). Anropas av <see cref="ScheduleReminderHostedService"/> i samma svep som pushpåminnelserna.
    ///
    /// <para><b>Claim-then-send</b> i <c>ScheduleReminderLog</c> (CompetitionId 0, nyckeln <c>week-{familj}-{dag}</c>) — samma
    /// unika spärr som pushpåminnelserna, så två svep eller två servrar aldrig skickar två mejl. Går sändningen inte iväg
    /// släpps spärren, och nästa svep (var 5:e minut, till 21:59) försöker igen.</para>
    ///
    /// <para><b>Bara familjer där veckan tillför något</b> (<see cref="MyWeekService.WeekFamilySql"/>): en ensam planering med en
    /// tävling får inget mejl — den har sina egna påminnelser.</para>
    /// </summary>
    public class StaffWeekDigestService
    {
        private readonly IScopeProvider _scopes;
        private readonly MyWeekService _week;
        private readonly PlanningService _planning;
        private readonly PlanningLinkService _links;
        private readonly HpskSite.Services.EmailService _email;
        private readonly IMemberService _members;
        private readonly ILogger<StaffWeekDigestService> _logger;

        public StaffWeekDigestService(IScopeProvider scopes, MyWeekService week, PlanningService planning, PlanningLinkService links,
            HpskSite.Services.EmailService email, IMemberService members, ILogger<StaffWeekDigestService> logger)
        {
            _scopes = scopes; _week = week; _planning = planning; _links = links; _email = email; _members = members; _logger = logger;
        }

        public sealed class Candidate
        {
            public int MemberId { get; set; }
            public int RootId { get; set; }
            public string? Status { get; set; }
        }

        /// <summary>Uppdragen som ligger på dagen <c>@0</c> i en familj där veckan tillför något. Datumet är uppdragets egen dag (starttid → dag → passets dag).</summary>
        public static readonly string CandidatesSql = @"
            SELECT sa.MemberId AS MemberId, p.FamilyRootId AS RootId, sa.Status AS Status
              FROM StaffAssignment sa
              JOIN Planning p ON p.Id = sa.PlanningId AND p.DeletedAt IS NULL
              LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
             WHERE sa.MemberId IS NOT NULL AND sa.PlanningId IS NOT NULL
               AND COALESCE(CAST(sa.StartsAt AS date), CAST(sa.DayDate AS date), CAST(sp.PassDate AS date)) = @0
               AND " + MyWeekService.WeekFamilySql;

        /// <summary>Personer och familjer som ska ha ett mejl för <paramref name="day"/> — en rad per person och familj.</summary>
        public List<(int MemberId, int RootId)> Candidates(DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                return s.Database.Fetch<Candidate>(CandidatesSql, day.Date)
                    .Where(c => StaffWeekDigest.Remindable(c.Status))
                    .Select(c => (c.MemberId, c.RootId))
                    .Distinct()
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Veckomejlet: kandidaterna för {Day:yyyy-MM-dd} gick inte att läsa.", day);
                return new();
            }
        }

        /// <summary>Ett svep. Returnerar antalet mejl som gick iväg. Gör ingenting utanför kvällsfönstret.</summary>
        public async Task<int> SendAsync(DateTime now, CancellationToken ct)
        {
            if (!StaffWeekDigest.InSendWindow(now)) return 0;
            var day = StaffWeekDigest.DayFor(now);
            var sent = 0;
            foreach (var (memberId, rootId) in Candidates(day))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (!TryClaim(memberId, rootId, day)) continue;
                    if (await SendOneAsync(memberId, rootId, day)) sent++;
                    else Release(memberId, rootId, day);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Veckomejlet till medlem {Member} i familj {Root} föll.", memberId, rootId);
                    Release(memberId, rootId, day);
                }
            }
            return sent;
        }

        private async Task<bool> SendOneAsync(int memberId, int rootId, DateTime day)
        {
            var week = _week.Get(memberId, rootId, day, day);
            if (week == null || !week.HasAny) return true;   // inget kvar den dagen (borttaget, avböjt) — inget att skicka, spärren får stå
            var member = _members.GetById(memberId);
            if (member == null || string.IsNullOrWhiteSpace(member.Email)) return true;
            var url = $"{_links.SiteUrl}/mitt-schema?p={rootId}";
            var html = PlanningNotifier.Page(member.Name ?? "", week.CompName, StaffWeekDigest.Inner(week, day, url), StaffWeekDigest.Footer());
            // Svara-inte-adressen är ett uttryckligt val (varje mejl väljer): frågor går till den som bemannar, inte till en postlåda.
            return await _email.SendPlanningMailAsync(member.Email, StaffWeekDigest.Subject(week.CompName, day), html, MailReplyTo.NoReply);
        }

        private bool TryClaim(int memberId, int rootId, DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope();
                var n = s.Database.Execute(@"
                    INSERT INTO ScheduleReminderLog (CompetitionId, MemberId, SourceKey, ItemStartsAt, SentAt)
                    SELECT 0, @0, @1, @2, GETDATE()
                     WHERE NOT EXISTS (SELECT 1 FROM ScheduleReminderLog WHERE CompetitionId = 0 AND MemberId = @0 AND SourceKey = @1)",
                    memberId, StaffWeekDigest.Key(rootId, day), day.Date);
                s.Complete();
                return n > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Veckomejlet: spärren för medlem {Member} gick inte att ta.", memberId);
                return false;
            }
        }

        private void Release(int memberId, int rootId, DateTime day)
        {
            try
            {
                using var s = _scopes.CreateScope();
                s.Database.Execute("DELETE FROM ScheduleReminderLog WHERE CompetitionId = 0 AND MemberId = @0 AND SourceKey = @1",
                    memberId, StaffWeekDigest.Key(rootId, day));
                s.Complete();
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Veckomejlet: spärren för medlem {Member} gick inte att släppa.", memberId); }
        }
    }
}
