namespace HpskSite.CompetitionTypes.Common.Utilities
{
    using System.Globalization;

    /// <summary>
    /// Utility functions for date and time formatting.
    /// Provides consistent date/time handling across all competition types.
    /// </summary>
    public static class DateTimeUtilities
    {
        private static readonly CultureInfo SwedishCulture = CultureInfo.GetCultureInfo("sv-SE");
        private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

        /// <summary>
        /// Format competition date in Swedish locale (yyyy-MM-dd).
        /// </summary>
        /// <param name="date">Date to format</param>
        /// <returns>Formatted date string (e.g., "2025-10-25")</returns>
        public static string FormatCompetitionDate(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", SwedishCulture);
        }

        /// <summary>
        /// Format date with long format in Swedish (e.g., "25 oktober 2025").
        /// </summary>
        /// <param name="date">Date to format</param>
        /// <returns>Formatted date string with month name</returns>
        public static string FormatDateLongSwedish(DateTime date)
        {
            return date.ToString("d MMMM yyyy", SwedishCulture);
        }

        /// <summary>
        /// Format time in HH:MM format.
        /// </summary>
        /// <param name="time">TimeSpan to format</param>
        /// <returns>Formatted time (e.g., "14:30")</returns>
        public static string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}";
        }

        /// <summary>
        /// Format time from DateTime.
        /// </summary>
        /// <param name="dateTime">DateTime to extract time from</param>
        /// <returns>Formatted time (e.g., "14:30")</returns>
        public static string FormatTime(DateTime dateTime)
        {
            return dateTime.ToString("HH:mm", InvariantCulture);
        }

        /// <summary>
        /// Format time with seconds in HH:MM:SS format.
        /// </summary>
        /// <param name="time">TimeSpan to format</param>
        /// <returns>Formatted time with seconds (e.g., "14:30:45")</returns>
        public static string FormatTimeWithSeconds(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        /// <summary>
        /// Format time with seconds from DateTime.
        /// </summary>
        /// <param name="dateTime">DateTime to extract time from</param>
        /// <returns>Formatted time with seconds (e.g., "14:30:45")</returns>
        public static string FormatTimeWithSeconds(DateTime dateTime)
        {
            return dateTime.ToString("HH:mm:ss", InvariantCulture);
        }

        /// <summary>
        /// Format competition schedule (date or date range).
        /// </summary>
        /// <param name="start">Start date</param>
        /// <param name="end">Optional end date</param>
        /// <returns>Formatted schedule (e.g., "2025-10-25" or "2025-10-25 - 2025-10-26")</returns>
        public static string FormatSchedule(DateTime start, DateTime? end)
        {
            var startStr = FormatCompetitionDate(start);

            if (end.HasValue && end.Value.Date != start.Date)
                return $"{startStr} - {FormatCompetitionDate(end.Value)}";

            return startStr;
        }

        /// <summary>
        /// Format full datetime with date and time.
        /// </summary>
        /// <param name="dateTime">DateTime to format</param>
        /// <returns>Formatted datetime (e.g., "2025-10-25 14:30")</returns>
        public static string FormatDateTime(DateTime dateTime)
        {
            return $"{FormatCompetitionDate(dateTime)} {FormatTime(dateTime)}";
        }

        /// <summary>
        /// Parse date from Swedish format (yyyy-MM-dd).
        /// </summary>
        /// <param name="dateStr">Date string to parse</param>
        /// <param name="date">Parsed date if successful</param>
        /// <returns>True if parsing successful, false otherwise</returns>
        public static bool TryParseCompetitionDate(string dateStr, out DateTime date)
        {
            return DateTime.TryParseExact(dateStr, "yyyy-MM-dd", SwedishCulture, DateTimeStyles.None, out date);
        }

        /// <summary>
        /// Parse time from HH:MM format.
        /// </summary>
        /// <param name="timeStr">Time string to parse (e.g., "14:30")</param>
        /// <param name="time">Parsed TimeSpan if successful</param>
        /// <returns>True if parsing successful, false otherwise</returns>
        public static bool TryParseTime(string timeStr, out TimeSpan time)
        {
            return TimeSpan.TryParseExact(timeStr, "hh\\:mm", InvariantCulture, out time);
        }

        /// <summary>
        /// Parse time with seconds from HH:MM:SS format.
        /// </summary>
        /// <param name="timeStr">Time string to parse (e.g., "14:30:45")</param>
        /// <param name="time">Parsed TimeSpan if successful</param>
        /// <returns>True if parsing successful, false otherwise</returns>
        public static bool TryParseTimeWithSeconds(string timeStr, out TimeSpan time)
        {
            return TimeSpan.TryParseExact(timeStr, "hh\\:mm\\:ss", InvariantCulture, out time);
        }

        /// <summary>
        /// Get human-readable relative time description (e.g., "2 days ago", "in 3 hours").
        /// </summary>
        /// <param name="dateTime">DateTime to describe</param>
        /// <returns>Relative time description in Swedish</returns>
        public static string GetRelativeTimeDescription(DateTime dateTime)
        {
            var now = DateTime.Now;
            var span = now - dateTime;

            if (span.TotalSeconds < 60)
                return "just now";

            if (span.TotalMinutes < 60)
                return $"{(int)span.TotalMinutes} minute{(span.TotalMinutes >= 2 ? "s" : "")} ago";

            if (span.TotalHours < 24)
                return $"{(int)span.TotalHours} hour{(span.TotalHours >= 2 ? "s" : "")} ago";

            if (span.TotalDays < 7)
                return $"{(int)span.TotalDays} day{(span.TotalDays >= 2 ? "s" : "")} ago";

            if (span.TotalDays < 30)
            {
                var weeks = (int)(span.TotalDays / 7);
                return $"{weeks} week{(weeks > 1 ? "s" : "")} ago";
            }

            if (span.TotalDays < 365)
            {
                var months = (int)(span.TotalDays / 30);
                return $"{months} month{(months > 1 ? "s" : "")} ago";
            }

            var years = (int)(span.TotalDays / 365);
            return $"{years} year{(years > 1 ? "s" : "")} ago";
        }

        /// <summary>
        /// Check if a date is within a range.
        /// </summary>
        /// <param name="date">Date to check</param>
        /// <param name="startDate">Range start (inclusive)</param>
        /// <param name="endDate">Range end (inclusive)</param>
        /// <returns>True if date is within range, false otherwise</returns>
        public static bool IsDateInRange(DateTime date, DateTime startDate, DateTime endDate)
        {
            return date.Date >= startDate.Date && date.Date <= endDate.Date;
        }

        /// <summary>
        /// Get the number of days between two dates.
        /// </summary>
        /// <param name="startDate">Start date</param>
        /// <param name="endDate">End date</param>
        /// <returns>Number of days (absolute value)</returns>
        public static int DaysBetween(DateTime startDate, DateTime endDate)
        {
            return Math.Abs((endDate.Date - startDate.Date).Days);
        }

        /// <summary>
        /// Add competition duration and return end time.
        /// </summary>
        /// <param name="startTime">Start time</param>
        /// <param name="durationMinutes">Duration in minutes</param>
        /// <returns>End time as TimeSpan</returns>
        public static TimeSpan AddDuration(TimeSpan startTime, int durationMinutes)
        {
            return startTime.Add(TimeSpan.FromMinutes(durationMinutes));
        }

        /// <summary>
        /// Format time interval (duration between two times).
        /// </summary>
        /// <param name="startTime">Start time</param>
        /// <param name="endTime">End time</param>
        /// <returns>Formatted duration (e.g., "1h 30m")</returns>
        public static string FormatDuration(TimeSpan startTime, TimeSpan endTime)
        {
            var duration = endTime - startTime;
            if (duration.TotalMinutes < 0)
                duration = duration.Add(TimeSpan.FromHours(24)); // Handle overnight durations

            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;

            if (hours == 0)
                return $"{minutes}m";

            if (minutes == 0)
                return $"{hours}h";

            return $"{hours}h {minutes}m";
        }

        /// <summary>
        /// Get start of day (midnight).
        /// </summary>
        /// <param name="date">Date to get start of day for</param>
        /// <returns>DateTime at 00:00:00</returns>
        public static DateTime GetStartOfDay(DateTime date)
        {
            return date.Date;
        }

        /// <summary>
        /// Get end of day (23:59:59).
        /// </summary>
        /// <param name="date">Date to get end of day for</param>
        /// <returns>DateTime at 23:59:59</returns>
        public static DateTime GetEndOfDay(DateTime date)
        {
            return date.Date.AddDays(1).AddSeconds(-1);
        }

        /// <summary>
        /// Check if date is today.
        /// </summary>
        /// <param name="date">Date to check</param>
        /// <returns>True if date is today, false otherwise</returns>
        public static bool IsToday(DateTime date)
        {
            return date.Date == DateTime.Now.Date;
        }

        /// <summary>
        /// Check if date is tomorrow.
        /// </summary>
        /// <param name="date">Date to check</param>
        /// <returns>True if date is tomorrow, false otherwise</returns>
        public static bool IsTomorrow(DateTime date)
        {
            return date.Date == DateTime.Now.Date.AddDays(1);
        }

        /// <summary>
        /// Check if date is in the past.
        /// </summary>
        /// <param name="date">Date to check</param>
        /// <returns>True if date is in the past, false otherwise</returns>
        public static bool IsInPast(DateTime date)
        {
            return date < DateTime.Now;
        }

        /// <summary>
        /// Check if date is in the future.
        /// </summary>
        /// <param name="date">Date to check</param>
        /// <returns>True if date is in the future, false otherwise</returns>
        public static bool IsInFuture(DateTime date)
        {
            return date > DateTime.Now;
        }

        /// <summary>
        /// Get the day of week name in Swedish.
        /// </summary>
        /// <param name="date">Date to get day name for</param>
        /// <returns>Day name in Swedish (e.g., "Fredag")</returns>
        public static string GetDayNameSwedish(DateTime date)
        {
            return date.ToString("dddd", SwedishCulture);
        }

        /// <summary>
        /// Get the month name in Swedish.
        /// </summary>
        /// <param name="date">Date to get month name for</param>
        /// <returns>Month name in Swedish (e.g., "Oktober")</returns>
        public static string GetMonthNameSwedish(DateTime date)
        {
            return date.ToString("MMMM", SwedishCulture);
        }
    }
}
