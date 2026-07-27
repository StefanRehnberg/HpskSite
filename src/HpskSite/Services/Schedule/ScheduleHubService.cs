using System.Globalization;
using HpskSite.Models.Schedule;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Home-page summary for the "Ditt schema" card — mirrors BoardHubService / StaffHubService.
    ///
    /// Deliberately time-boxed to the next few days. That's what resolves the overlap with the existing
    /// "Dina funktionärsuppdrag" card: far from a competition, that card's job is "answer this / sign up
    /// for this"; close to it, this card's job is "here's your day, in order". A competition shown here
    /// is suppressed there (see <see cref="ScheduleHubSummary.ShownCompetitionIds"/>) — except when the
    /// member still owes a response, because an unanswered invitation is a to-do that must never be
    /// hidden behind a timeline.
    /// </summary>
    public class ScheduleHubService
    {
        /// <summary>How far ahead the card looks. A week is enough to plan around without turning the
        /// home page into a calendar.</summary>
        public const int WindowDays = 7;

        /// <summary>Rows shown per competition before "Öppna hela schemat".</summary>
        private const int PreviewRows = 4;

        private readonly MyScheduleService _schedule;
        private readonly ILogger<ScheduleHubService> _logger;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        public ScheduleHubService(MyScheduleService schedule, ILogger<ScheduleHubService> logger)
        {
            _schedule = schedule;
            _logger = logger;
        }

        public ScheduleHubSummary GetSummary(int memberId)
        {
            var s = new ScheduleHubSummary();
            if (memberId <= 0) return s;

            try
            {
                var today = DateTime.Today;
                var compIds = _schedule.GetCompetitionIdsForMember(memberId, today, today.AddDays(WindowDays));

                foreach (var compId in compIds)
                {
                    var sched = _schedule.GetSchedule(memberId, compId);
                    // Nothing to show and nothing pending → not worth a card row.
                    if (!sched.HasAny && !sched.StartListPending) continue;

                    var isToday = sched.CompDate?.Date == today || sched.IsToday;
                    var isTomorrow = sched.CompDate?.Date == today.AddDays(1);

                    var preview = BuildPreview(sched);
                    // Does anything in the preview sit on a day other than the competition's own? A build
                    // day or a pre-comp pass does, and the card header only carries the competition date.
                    var spansOtherDays = sched.CompDate != null
                        && preview.Any(i => i.StartsAt != null && i.StartsAt.Value.Date != sched.CompDate.Value.Date);

                    s.Items.Add(new ScheduleHubItem
                    {
                        CompetitionId = compId,
                        CompName = sched.CompName,
                        CompDate = sched.CompDate,
                        DateLabel = BuildDateLabel(sched.CompDate, sched.CompEndDate),
                        IsToday = isToday,
                        IsTomorrow = isTomorrow,
                        ItemCount = sched.ItemCount,
                        ConflictCount = sched.ConflictCount,
                        StartListPending = sched.StartListPending,
                        NextItem = sched.NextItem,
                        Preview = preview,
                        PreviewSpansOtherDays = spansOtherDays,
                    });
                    s.ShownCompetitionIds.Add(compId);
                }

                // Soonest first; undated last.
                s.Items = s.Items
                    .OrderBy(i => i.CompDate ?? DateTime.MaxValue)
                    .ThenBy(i => i.CompName, StringComparer.Create(Sv, false))
                    .ToList();
            }
            catch (Exception ex)
            {
                // Best-effort, exactly like the sibling hub services: never break the home page.
                _logger.LogWarning(ex, "ScheduleHubService: summary failed for member {Member}", memberId);
            }

            s.HasAny = s.Items.Count > 0;
            return s;
        }

        /// <summary>
        /// The rows worth showing on a card. Prefers what's still ahead today; falls back to the first
        /// day's rows so a card for a competition two days out isn't empty.
        /// </summary>
        private static List<ScheduleItem> BuildPreview(MySchedule sched)
        {
            var now = DateTime.Now;

            var upcoming = sched.Days
                .SelectMany(d => d.Items)
                .Where(i => i.StartsAt == null || i.StartsAt >= now)
                .Take(PreviewRows)
                .ToList();
            if (upcoming.Count > 0) return upcoming;

            return sched.Days.FirstOrDefault()?.Items.Take(PreviewRows).ToList() ?? new List<ScheduleItem>();
        }

        private static string BuildDateLabel(DateTime? start, DateTime? end)
        {
            if (start == null) return "Datum ej satt";
            var today = DateTime.Today;
            var d = start.Value.Date;

            var multi = end != null && end.Value.Date > d;
            var span = multi ? $"–{end!.Value.ToString("d MMM", Sv)}" : "";

            if (d == today) return multi ? $"Idag{span}" : "Idag";
            if (d == today.AddDays(1)) return multi ? $"Imorgon{span}" : "Imorgon";
            // Inside a multi-day comp that already started.
            if (multi && d < today && end!.Value.Date >= today) return $"Pågår nu (t.o.m. {end.Value.ToString("d MMM", Sv)})";
            return d.ToString("ddd d MMM", Sv) + span;
        }
    }
}
