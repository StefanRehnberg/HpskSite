using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for DateTimeUtilities
    /// Tests date/time formatting and parsing
    /// </summary>
    public class DateTimeUtilitiesTests
    {
        // ============ FormatCompetitionDate Tests ============

        [Fact]
        public void FormatCompetitionDate_WithDate_ReturnsYyyyMmDdFormat()
        {
            var date = new DateTime(2025, 10, 25);
            var result = DateTimeUtilities.FormatCompetitionDate(date);
            Assert.Equal("2025-10-25", result);
        }

        [Fact]
        public void FormatCompetitionDate_WithPaddedDayMonth_FormatsCorrectly()
        {
            var date = new DateTime(2025, 1, 5);
            var result = DateTimeUtilities.FormatCompetitionDate(date);
            Assert.Equal("2025-01-05", result);
        }

        // ============ FormatTime Tests ============

        [Fact]
        public void FormatTime_WithTimeSpan_ReturnsHhMmFormat()
        {
            var time = new TimeSpan(14, 30, 0);
            var result = DateTimeUtilities.FormatTime(time);
            Assert.Equal("14:30", result);
        }

        [Fact]
        public void FormatTime_WithDateTime_ReturnsHhMmFormat()
        {
            var dateTime = new DateTime(2025, 10, 25, 14, 30, 45);
            var result = DateTimeUtilities.FormatTime(dateTime);
            Assert.Equal("14:30", result);
        }

        [Fact]
        public void FormatTime_WithMidnight_Returns0000()
        {
            var time = new TimeSpan(0, 0, 0);
            var result = DateTimeUtilities.FormatTime(time);
            Assert.Equal("00:00", result);
        }

        // ============ FormatTimeWithSeconds Tests ============

        [Fact]
        public void FormatTimeWithSeconds_WithTimeSpan_ReturnsHhMmSsFormat()
        {
            var time = new TimeSpan(14, 30, 45);
            var result = DateTimeUtilities.FormatTimeWithSeconds(time);
            Assert.Equal("14:30:45", result);
        }

        [Fact]
        public void FormatTimeWithSeconds_WithDateTime_ReturnsHhMmSsFormat()
        {
            var dateTime = new DateTime(2025, 10, 25, 14, 30, 45);
            var result = DateTimeUtilities.FormatTimeWithSeconds(dateTime);
            Assert.Equal("14:30:45", result);
        }

        // ============ FormatSchedule Tests ============

        [Fact]
        public void FormatSchedule_WithSameDateStart_ReturnsOnlyDate()
        {
            var start = new DateTime(2025, 10, 25);
            var end = new DateTime(2025, 10, 25);
            var result = DateTimeUtilities.FormatSchedule(start, end);
            Assert.Equal("2025-10-25", result);
        }

        [Fact]
        public void FormatSchedule_WithDifferentDates_ReturnsDateRange()
        {
            var start = new DateTime(2025, 10, 25);
            var end = new DateTime(2025, 10, 26);
            var result = DateTimeUtilities.FormatSchedule(start, end);
            Assert.Equal("2025-10-25 - 2025-10-26", result);
        }

        [Fact]
        public void FormatSchedule_WithNullEnd_ReturnsStartDateOnly()
        {
            var start = new DateTime(2025, 10, 25);
            var result = DateTimeUtilities.FormatSchedule(start, null);
            Assert.Equal("2025-10-25", result);
        }

        // ============ FormatDateTime Tests ============

        [Fact]
        public void FormatDateTime_WithDateTime_ReturnsDateAndTime()
        {
            var dateTime = new DateTime(2025, 10, 25, 14, 30, 0);
            var result = DateTimeUtilities.FormatDateTime(dateTime);
            Assert.Equal("2025-10-25 14:30", result);
        }

        // ============ TryParseCompetitionDate Tests ============

        [Fact]
        public void TryParseCompetitionDate_WithValidDate_ReturnsTrueAndParsedDate()
        {
            var dateStr = "2025-10-25";
            var result = DateTimeUtilities.TryParseCompetitionDate(dateStr, out var parsedDate);

            Assert.True(result);
            Assert.Equal(2025, parsedDate.Year);
            Assert.Equal(10, parsedDate.Month);
            Assert.Equal(25, parsedDate.Day);
        }

        [Fact]
        public void TryParseCompetitionDate_WithInvalidDate_ReturnsFalse()
        {
            var dateStr = "invalid";
            var result = DateTimeUtilities.TryParseCompetitionDate(dateStr, out var parsedDate);
            Assert.False(result);
        }

        // ============ TryParseTime Tests ============

        [Fact]
        public void TryParseTime_WithValidTime_ReturnsTrueAndParsedTime()
        {
            var timeStr = "14:30";
            var result = DateTimeUtilities.TryParseTime(timeStr, out var parsedTime);

            Assert.True(result);
            Assert.Equal(14, parsedTime.Hours);
            Assert.Equal(30, parsedTime.Minutes);
        }

        [Fact]
        public void TryParseTime_WithInvalidTime_ReturnsFalse()
        {
            var timeStr = "invalid";
            var result = DateTimeUtilities.TryParseTime(timeStr, out var parsedTime);
            Assert.False(result);
        }

        // ============ IsDateInRange Tests ============

        [Fact]
        public void IsDateInRange_WithDateInRange_ReturnsTrue()
        {
            var date = new DateTime(2025, 10, 25);
            var start = new DateTime(2025, 10, 20);
            var end = new DateTime(2025, 10, 30);

            var result = DateTimeUtilities.IsDateInRange(date, start, end);
            Assert.True(result);
        }

        [Fact]
        public void IsDateInRange_WithDateOutOfRange_ReturnsFalse()
        {
            var date = new DateTime(2025, 11, 5);
            var start = new DateTime(2025, 10, 20);
            var end = new DateTime(2025, 10, 30);

            var result = DateTimeUtilities.IsDateInRange(date, start, end);
            Assert.False(result);
        }

        // ============ DaysBetween Tests ============

        [Fact]
        public void DaysBetween_WithTwoDates_ReturnsCorrectCount()
        {
            var start = new DateTime(2025, 10, 25);
            var end = new DateTime(2025, 10, 30);

            var result = DateTimeUtilities.DaysBetween(start, end);
            Assert.Equal(5, result);
        }

        [Fact]
        public void DaysBetween_WithReverseOrder_ReturnsAbsoluteValue()
        {
            var start = new DateTime(2025, 10, 30);
            var end = new DateTime(2025, 10, 25);

            var result = DateTimeUtilities.DaysBetween(start, end);
            Assert.Equal(5, result);
        }

        // ============ AddDuration Tests ============

        [Fact]
        public void AddDuration_WithValidDuration_ReturnsCorrectEndTime()
        {
            var startTime = new TimeSpan(10, 0, 0);
            var result = DateTimeUtilities.AddDuration(startTime, 90);

            Assert.Equal(11, result.Hours);
            Assert.Equal(30, result.Minutes);
        }

        // ============ FormatDuration Tests ============

        [Fact]
        public void FormatDuration_WithHoursAndMinutes_ReturnsFormattedString()
        {
            var start = new TimeSpan(10, 0, 0);
            var end = new TimeSpan(11, 30, 0);

            var result = DateTimeUtilities.FormatDuration(start, end);
            Assert.Equal("1h 30m", result);
        }

        [Fact]
        public void FormatDuration_WithOnlyMinutes_ReturnsMinutesOnly()
        {
            var start = new TimeSpan(10, 0, 0);
            var end = new TimeSpan(10, 45, 0);

            var result = DateTimeUtilities.FormatDuration(start, end);
            Assert.Equal("45m", result);
        }

        // ============ GetStartOfDay Tests ============

        [Fact]
        public void GetStartOfDay_WithDateTime_ReturnsMidnight()
        {
            var dateTime = new DateTime(2025, 10, 25, 14, 30, 45);
            var result = DateTimeUtilities.GetStartOfDay(dateTime);

            Assert.Equal(0, result.Hour);
            Assert.Equal(0, result.Minute);
            Assert.Equal(0, result.Second);
        }

        // ============ GetEndOfDay Tests ============

        [Fact]
        public void GetEndOfDay_WithDateTime_Returns235959()
        {
            var dateTime = new DateTime(2025, 10, 25, 14, 30, 45);
            var result = DateTimeUtilities.GetEndOfDay(dateTime);

            Assert.Equal(23, result.Hour);
            Assert.Equal(59, result.Minute);
            Assert.Equal(59, result.Second);
        }

        // ============ IsToday Tests ============

        [Fact]
        public void IsToday_WithTodayDate_ReturnsTrue()
        {
            var today = DateTime.Now.Date;
            var result = DateTimeUtilities.IsToday(today);
            Assert.True(result);
        }

        [Fact]
        public void IsToday_WithYesterdayDate_ReturnsFalse()
        {
            var yesterday = DateTime.Now.Date.AddDays(-1);
            var result = DateTimeUtilities.IsToday(yesterday);
            Assert.False(result);
        }

        // ============ IsTomorrow Tests ============

        [Fact]
        public void IsTomorrow_WithTomorrowDate_ReturnsTrue()
        {
            var tomorrow = DateTime.Now.Date.AddDays(1);
            var result = DateTimeUtilities.IsTomorrow(tomorrow);
            Assert.True(result);
        }

        [Fact]
        public void IsTomorrow_WithTodayDate_ReturnsFalse()
        {
            var today = DateTime.Now.Date;
            var result = DateTimeUtilities.IsTomorrow(today);
            Assert.False(result);
        }

        // ============ IsInPast Tests ============

        [Fact]
        public void IsInPast_WithPastDate_ReturnsTrue()
        {
            var pastDate = DateTime.Now.AddDays(-1);
            var result = DateTimeUtilities.IsInPast(pastDate);
            Assert.True(result);
        }

        [Fact]
        public void IsInPast_WithFutureDate_ReturnsFalse()
        {
            var futureDate = DateTime.Now.AddDays(1);
            var result = DateTimeUtilities.IsInPast(futureDate);
            Assert.False(result);
        }

        // ============ IsInFuture Tests ============

        [Fact]
        public void IsInFuture_WithFutureDate_ReturnsTrue()
        {
            var futureDate = DateTime.Now.AddDays(1);
            var result = DateTimeUtilities.IsInFuture(futureDate);
            Assert.True(result);
        }

        [Fact]
        public void IsInFuture_WithPastDate_ReturnsFalse()
        {
            var pastDate = DateTime.Now.AddDays(-1);
            var result = DateTimeUtilities.IsInFuture(pastDate);
            Assert.False(result);
        }

        // ============ GetDayNameSwedish Tests ============

        [Fact]
        public void GetDayNameSwedish_WithDate_ReturnsSwedishDayName()
        {
            var friday = new DateTime(2025, 10, 24);  // This is a Friday
            var result = DateTimeUtilities.GetDayNameSwedish(friday);
            Assert.Contains("dag", result.ToLower());  // Swedish days end with "dag"
        }

        // ============ GetMonthNameSwedish Tests ============

        [Fact]
        public void GetMonthNameSwedish_WithOctober_ReturnsOktober()
        {
            var octDate = new DateTime(2025, 10, 25);
            var result = DateTimeUtilities.GetMonthNameSwedish(octDate);
            Assert.Equal("oktober", result.ToLower());
        }
    }
}
