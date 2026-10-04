namespace HpskSite.Services.Training
{
    /// <summary>
    /// Turns a training schedule into concrete occasions (fas B1). Pure: no database, no "now".
    ///
    /// <list type="bullet">
    /// <item>Every date in [from, to] whose ISO weekday is in the schedule, except dates inside a break.</item>
    /// <item>Skjutledare rotate in the given order over the occasions, starting with the first; with no
    /// rotation everyone gets the default (which may be null — "ingen utsedd än").</item>
    /// <item>⚠️ IDEMPOTENT: dates that already have an occasion are skipped, so running the schedule again
    /// after extending the period only adds the new dates. The rotation still counts the skipped dates —
    /// otherwise re-running would shift every later skjutledare by one.</item>
    /// </list>
    /// </summary>
    public static class TrainingSchedulePlanner
    {
        public record DateRange(DateTime From, DateTime To);
        public record Occasion(DateTime Date, int? SkjutledareMemberId);

        /// <summary>Hard cap so a typo in the period (2026 → 2062) cannot create thousands of rows.</summary>
        public const int MaxOccasions = 400;

        public static List<Occasion> Expand(
            DateTime from, DateTime to, IEnumerable<int> isoWeekdays,
            IEnumerable<DateRange>? breaks = null,
            int? defaultSkjutledare = null, IReadOnlyList<int>? rotation = null,
            IEnumerable<DateTime>? existingDates = null)
        {
            var days = isoWeekdays.Where(d => d is >= 1 and <= 7).ToHashSet();
            var result = new List<Occasion>();
            if (days.Count == 0 || to.Date < from.Date) return result;

            var breakList = (breaks ?? Enumerable.Empty<DateRange>()).ToList();
            var existing = (existingDates ?? Enumerable.Empty<DateTime>()).Select(d => d.Date).ToHashSet();

            int index = 0;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                if (!days.Contains(IsoWeekday(d))) continue;
                if (breakList.Any(b => d >= b.From.Date && d <= b.To.Date)) continue;

                int? leader = rotation is { Count: > 0 } ? rotation[index % rotation.Count] : defaultSkjutledare;
                index++;

                if (existing.Contains(d)) continue;
                result.Add(new Occasion(d, leader));
                if (result.Count >= MaxOccasions) break;
            }
            return result;
        }

        /// <summary>1 = Monday … 7 = Sunday (ISO 8601), unlike DayOfWeek where Sunday is 0.</summary>
        public static int IsoWeekday(DateTime d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

        /// <summary>Parses "2,4" (or "2, 4") into weekdays; unreadable parts are ignored.</summary>
        public static List<int> ParseWeekdays(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n is >= 1 and <= 7).Distinct().ToList();
    }
}
