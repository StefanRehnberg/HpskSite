using FluentAssertions;
using HpskSite.Models;
using HpskSite.Models.Training;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Incheckning på banan → närvaro (Michael, Luleå 2026-10-11): rätt bana, och vad som pågår just nu.
    /// </summary>
    public class RangeCheckInAttendanceTests
    {
        private static readonly int[] Club = { 10, 11 };

        [Theory]
        [InlineData(10, 10, true)]   // tillfällets egen bana
        [InlineData(11, 10, false)]  // klubbens ANDRA bana — Michaels fråga
        [InlineData(99, 99, true)]   // en annan klubbs bana som tillfället ligger på
        [InlineData(11, null, true)] // utan bana: klubbens alla banor, som förut
        [InlineData(99, null, false)]
        public void Checkin_counts_only_at_the_occasions_own_range(int checkInRange, int? occasionRange, bool expected)
            => TrainingCourseRules.CheckInAtRightRange(checkInRange, occasionRange, Club).Should().Be(expected);

        [Fact]
        public void Checkin_window_reads_clock_times_without_colon()
        {
            var d = new DateTime(2026, 10, 11);
            // "18" är 18 DYGN för TimeSpan.TryParse — tillfället hade matchat hela dagen.
            TrainingCourseRules.CheckInCounts(d.AddHours(10), d, "18", "20").Should().BeFalse();
            TrainingCourseRules.CheckInCounts(d.AddHours(17.5), d, "18", "20").Should().BeTrue();
        }

        private static RangeCalendarItem I(string kind, string start, string? end = null, bool cancelled = false, string date = "2026-10-11")
            => new() { Kind = kind, SourceId = 1, Title = kind, Date = date, Start = start, End = end, IsCancelled = cancelled };

        [Fact]
        public void Ongoing_is_trainings_and_events_inside_the_window()
        {
            var now = new DateTime(2026, 10, 11, 17, 30, 0);
            var items = new[]
            {
                I(RangeCalendarKind.Training, "18:00", "20:00"),          // börjar om 30 min → med
                I(RangeCalendarKind.Event, null!),                         // händelse utan klockslag → hela dagen
                I(RangeCalendarKind.Training, "20:00", "21:00"),          // 2,5 h bort → inte
                I(RangeCalendarKind.Training, "18:00", "20:00", cancelled: true),
                I(RangeCalendarKind.Training, "18:00", "20:00", date: "2026-10-12"),
                I(RangeCalendarKind.Allocation, "17:00", "21:00"),        // fast tid har ingen närvaro
                I(RangeCalendarKind.External, "17:00", "21:00"),
                I(RangeCalendarKind.Competition, null!),
            };
            var got = RangeCalendarRules.OngoingForCheckIn(items, now);
            got.Select(i => (i.Kind, i.Start)).Should().BeEquivalentTo(new[]
            {
                (RangeCalendarKind.Training, (string?)"18:00"),
                (RangeCalendarKind.Event, (string?)null),
            });
        }

        [Fact]
        public void Ongoing_ends_an_hour_after_the_end()
        {
            var items = new[] { I(RangeCalendarKind.Training, "18:00", "20:00") };
            RangeCalendarRules.OngoingForCheckIn(items, new DateTime(2026, 10, 11, 20, 59, 0)).Should().HaveCount(1);
            RangeCalendarRules.OngoingForCheckIn(items, new DateTime(2026, 10, 11, 21, 1, 0)).Should().BeEmpty();
        }
    }
}
