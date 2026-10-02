using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Mail;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Resultatgranskningens påminnelser (fas 2), körda i SAMMA svep som tävlingsansökans
    /// (<see cref="CompetitionApplicationReminderHostedService"/>) och med samma spärrtabell:
    /// <list type="bullet">
    /// <item>arrangören 3 och 10 dagar efter tävlingen om listan inte skickats in;</item>
    /// <item>kretsens granskare när en inskickad lista legat 5 dagar.</item>
    /// </list>
    /// Reglerna är <see cref="ResultReviewReminders.Compute"/>; här hämtas underlaget och mejlen skickas.
    /// </summary>
    public class ResultReviewReminderService
    {
        private readonly ResultReviewService _reviews;
        private readonly ResultReviewHooks _hooks;
        private readonly CompetitionApplicationService _apps;
        private readonly KretsCalendarService _calendar;
        private readonly IContentService _contentService;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _replyTo;
        private readonly ClubService _clubs;
        private readonly ILogger<ResultReviewReminderService> _logger;

        public ResultReviewReminderService(ResultReviewService reviews, ResultReviewHooks hooks, CompetitionApplicationService apps,
            KretsCalendarService calendar, IContentService contentService, EmailService email, ReplyContactResolver replyTo,
            ClubService clubs, ILogger<ResultReviewReminderService> logger)
        {
            _reviews = reviews;
            _hooks = hooks;
            _apps = apps;
            _calendar = calendar;
            _contentService = contentService;
            _email = email;
            _replyTo = replyTo;
            _clubs = clubs;
            _logger = logger;
        }

        public async Task<int> RunOnceAsync(DateTime today)
        {
            if (!_reviews.TablesExist()) return 0;
            var submitted = _reviews.SubmittedCompetitionIds();
            var candidates = new List<ResultReviewReminders.Candidate>();
            foreach (var c in _calendar.AllCompetitions())
            {
                var end = c.EndDate ?? c.Date;
                if (end == null || submitted.Contains(c.CompetitionId)) continue;
                var days = (today.Date - end.Value.Date).TotalDays;
                if (days < ResultReviewReminders.ArrangerFirstDays || days > ResultReviewReminders.ArrangerWindowDays) continue;
                var node = _contentService.GetById(c.CompetitionId);
                if (node == null || !ResultReviewRules.Offered(node.GetValue<bool>("isAwardingStandardMedals"), node.GetValue<bool>("isClubOnly"))) continue;
                var region = _calendar.CompetitionRegion(c.CompetitionId);
                if (region == null) continue;
                candidates.Add(new ResultReviewReminders.Candidate(c.CompetitionId, region.Value.Region.Id, end.Value));
            }

            var due = ResultReviewReminders.Compute(candidates, _reviews.Pending(), today);
            var sent = 0;
            foreach (var d in due)
            {
                if (!_apps.TryClaimReminder(d.Key)) continue;
                int n;
                try { n = d.Kind == ResultReviewReminders.KindArranger ? await ArrangerAsync(d) : await KretsAsync(d); }
                catch (Exception ex) { _logger.LogError(ex, "Påminnelse {Key} kunde inte skickas.", d.Key); n = 0; }
                _apps.SetReminderRecipients(d.Key, n);
                if (n > 0) sent++;
            }
            return sent;
        }

        private async Task<int> ArrangerAsync(ResultReviewReminders.Due d)
        {
            var node = _contentService.GetById(d.CompetitionId);
            if (node == null) return 0;
            var name = node.GetValue<string>("competitionName") is { Length: > 0 } n ? n : node.Name ?? "";
            var region = _calendar.Region(d.RegionId)?.Name ?? "kretsen";
            var info = _calendar.CompetitionRegion(d.CompetitionId)?.Competition;
            var deadline = (info?.EndDate ?? info?.Date)?.AddDays(14).ToString("yyyy-MM-dd") ?? "";

            var to = new List<(string Email, string Name)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? e, string nm) { if (!string.IsNullOrWhiteSpace(e) && seen.Add(e.Trim())) to.Add((e.Trim(), nm)); }
            Add(node.GetValue<string>("contactEmail"), node.GetValue<string>("contactPerson") ?? "");
            var clubId = node.GetValue<int>("clubId");
            if (clubId > 0) Add(_clubs.GetClubById(clubId)?.ContactEmail, _clubs.GetClubNameById(clubId) ?? "");

            var url = $"{_hooks.SiteUrl}/competitionmanagement?competitionId={d.CompetitionId}";
            var count = 0;
            foreach (var (email, nm) in to)
                if (await _email.SendKretsgranskningAsync(email, nm, $"Skicka resultatlistan till {region}: {name}", new[]
                    {
                        $"Resultatlistan för {name} har inte skickats in till {region} för granskning ännu.",
                        "Publicera resultatlistan och tryck \"Skicka till kretsen\" på Resultat-fliken. Det tar en minut.",
                        deadline.Length > 0 ? $"Listan ska skickas in senast {deadline}." : ""
                    }, "Öppna Resultat-fliken", url, "Påminnelsen kommer för att tävlingen ger standardmedaljer.", _replyTo.ForRegion(d.RegionId)))
                    count++;
            return count;
        }

        private async Task<int> KretsAsync(ResultReviewReminders.Due d)
        {
            var name = _calendar.CompetitionRegion(d.CompetitionId)?.Competition.Name ?? $"tävling {d.CompetitionId}";
            var (sent, _, _) = await _hooks.NotifyReviewersAsync(d.Review!, $"Påminnelse: resultatlista att granska — {name}", new[]
            {
                $"Resultatlistan för {name} har väntat på kretsens granskning sedan {d.Review!.UpdatedAt:yyyy-MM-dd}.",
                d.Review.Gated ? "Kretsen kräver sitt godkännande, så skyttarnas standardmedaljer väntar på granskningen." : ""
            }, _hooks.SiteUrl, _replyTo.ForCompetitionOrganiser(d.CompetitionId));
            return sent;
        }
    }
}
