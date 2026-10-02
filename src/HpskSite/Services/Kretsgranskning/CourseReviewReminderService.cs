using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Mail;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Bangranskningens påminnelser (fas 3), körda i SAMMA svep och med SAMMA spärrtabell som
    /// tävlingsansökans och resultatgranskningens (<see cref="CompetitionApplicationReminderHostedService"/>):
    /// <list type="bullet">
    /// <item>arrangören två veckor före och på sista dagen — <b>bara när granskningen KRÄVS</b>
    /// (nationell, SM/LDM, eller kretsen kräver den). En frivillig granskning påminns aldrig;</item>
    /// <item>kretsens granskare när en inskickad bana legat 5 dagar (inte Förbundets väg).</item>
    /// </list>
    /// Reglerna är <see cref="CourseReviewReminders.Compute"/>. Bara kommande tävlingar efter
    /// funktionens startdag räknas — banan granskas FÖRE tävlingen.
    /// </summary>
    public class CourseReviewReminderService
    {
        private readonly CourseReviewService _reviews;
        private readonly CourseReviewAccess _access;
        private readonly CompetitionApplicationService _apps;
        private readonly KretsCalendarService _calendar;
        private readonly IContentService _contentService;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _replyTo;
        private readonly ClubService _clubs;
        private readonly ILogger<CourseReviewReminderService> _logger;

        public CourseReviewReminderService(CourseReviewService reviews, CourseReviewAccess access, CompetitionApplicationService apps,
            KretsCalendarService calendar, IContentService contentService, EmailService email, ReplyContactResolver replyTo,
            ClubService clubs, ILogger<CourseReviewReminderService> logger)
        {
            _reviews = reviews;
            _access = access;
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
            var featureStart = _reviews.FeatureStart();
            var candidates = new List<CourseReviewReminders.Candidate>();
            foreach (var info in _calendar.AllCompetitions())
            {
                var date = info.Date;
                if (date == null || date.Value.Date < today.Date || submitted.Contains(info.CompetitionId)) continue;
                if (featureStart == null) continue;
                // Sista dagen ligger högst ett år + 12 veckor före tävlingen — längre fram finns inget att påminna om.
                if ((date.Value.Date - today.Date).TotalDays > 365 + 7 * CourseReviewRules.ForbundetLeadWeeks) continue;
                var c = _access.Resolve(info.CompetitionId);
                if (c == null || !c.Required || c.Deadline == null || c.RegionId <= 0) continue;
                // En sista dag som passerade innan funktionen fanns kunde ingen hålla — den påminns inte.
                if (c.Deadline.Value.Date < featureStart.Value) continue;
                candidates.Add(new CourseReviewReminders.Candidate(c.Id, c.RegionId, c.Deadline.Value));
            }

            var due = CourseReviewReminders.Compute(candidates, _reviews.Pending(), today);
            var sent = 0;
            foreach (var d in due)
            {
                if (!_apps.TryClaimReminder(d.Key)) continue;
                int n;
                try { n = d.Kind == CourseReviewReminders.KindArranger ? await ArrangerAsync(d) : await KretsAsync(d); }
                catch (Exception ex) { _logger.LogError(ex, "Påminnelse {Key} kunde inte skickas.", d.Key); n = 0; }
                _apps.SetReminderRecipients(d.Key, n);
                if (n > 0) sent++;
            }
            return sent;
        }

        private async Task<int> ArrangerAsync(CourseReviewReminders.Due d)
        {
            var c = _access.Resolve(d.CompetitionId);
            var node = _contentService.GetById(d.CompetitionId);
            if (c == null || node == null) return 0;
            var toForbundet = c.Route == CourseReviewRoute.Forbundet;
            var deadline = c.Deadline?.ToString("yyyy-MM-dd") ?? "";

            var to = new List<(string Email, string Name)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? e, string nm) { if (!string.IsNullOrWhiteSpace(e) && seen.Add(e.Trim())) to.Add((e.Trim(), nm)); }
            Add(node.GetValue<string>("contactEmail"), node.GetValue<string>("contactPerson") ?? "");
            if (c.ClubId > 0) Add(_clubs.GetClubById(c.ClubId)?.ContactEmail, _clubs.GetClubNameById(c.ClubId) ?? "");

            var url = $"{_access.SiteUrl}/competitionmanagement?competitionId={d.CompetitionId}";
            var subject = toForbundet ? $"Skicka banans underlag till Förbundet: {c.Name}" : $"Skicka banan till {c.RegionName}: {c.Name}";
            var count = 0;
            foreach (var (email, nm) in to)
                if (await _email.SendKretsgranskningAsync(email, nm, subject, new[]
                    {
                        toForbundet
                            ? $"Vid SM och landsdelsmästerskap granskar Förbundet målförutsättningarna och stationsbeskrivningarna för {c.Name}, minst 12 veckor före tävlingen. Underlaget är inte registrerat som skickat ännu."
                            : $"Banan för {c.Name} har inte skickats in till {c.RegionName} för granskning ännu.",
                        toForbundet
                            ? "Skriv ut stationsbeskrivningarna från Stationer-fliken, skicka dem till Förbundet och registrera att det är gjort."
                            : "Tryck \"Skicka till kretsen\" i kortet Bangranskning på Stationer-fliken. Det tar en minut.",
                        deadline.Length > 0 ? $"Sista dag: {deadline}." : ""
                    }, "Öppna Stationer-fliken", url, "Påminnelsen kommer för att granskningen krävs för tävlingen.",
                    toForbundet ? MailReplyTo.SiteAdmin : _replyTo.ForRegion(d.RegionId)))
                    count++;
            return count;
        }

        private async Task<int> KretsAsync(CourseReviewReminders.Due d)
        {
            var name = _calendar.CompetitionRegion(d.CompetitionId)?.Competition.Name ?? $"tävling {d.CompetitionId}";
            var (sent, _, _) = await _access.NotifyReviewersAsync(d.Review!, $"Påminnelse: bana att granska — {name}", new[]
            {
                $"Banan för {name} har väntat på kretsens granskning sedan {d.Review!.UpdatedAt:yyyy-MM-dd}.",
                d.Review.Required ? "Granskningen krävs för tävlingen." : ""
            }, _access.SiteUrl, _replyTo.ForCompetitionOrganiser(d.CompetitionId));
            return sent;
        }
    }
}
