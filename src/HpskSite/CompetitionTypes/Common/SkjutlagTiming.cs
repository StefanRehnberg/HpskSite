using System.Globalization;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Hur länge ett skjutlag står på banan, och när finalen kan börja. ENDA stället regeln bor —
    /// startlistans förslag på intervall och finalens förslag på starttid läser samma tal.
    ///
    /// Byggd efter Sune-genomgången 2026-10-03: startlistan föreslog en fast 105 minuter som ingen
    /// kunde förklara, och finalguiden föreslog 10:00 och 1:45 för en final vars grundomgång gick
    /// till 10:30. "Fortsätt i samma ordning" kopierade dessutom grundomgångens starttider rakt av,
    /// så finalen fick samma tid som serie 1.
    ///
    /// Tumregeln är ett FÖRSLAG som arrangören alltid kan ändra: ca 10 minuter per serie och 15
    /// minuters byte, avrundat uppåt till kvart.
    /// </summary>
    public static class SkjutlagTiming
    {
        public const int MinutesPerSeries = 10;
        public const int ChangeoverMinutes = 15;

        /// <summary>Hur länge ett skjutlag tar för <paramref name="series"/> serier, i minuter.</summary>
        public static int DurationMinutes(int series) =>
            RoundUpToQuarter(Math.Max(1, series) * MinutesPerSeries + ChangeoverMinutes);

        public static int RoundUpToQuarter(int minutes) => (int)Math.Ceiling(minutes / 15.0) * 15;

        /// <summary>"HH:mm" → minuter efter midnatt, eller null.</summary>
        public static int? ParseClock(string? hhmm)
        {
            if (string.IsNullOrWhiteSpace(hhmm)) return null;
            return TimeSpan.TryParseExact(hhmm.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t)
                ? (int)t.TotalMinutes : null;
        }

        public static string FormatClock(int minutes)
        {
            minutes = Math.Clamp(minutes, 0, 23 * 60 + 59);
            return $"{minutes / 60:00}:{minutes % 60:00}";
        }

        /// <summary>Ett intervall i finalguidens form "h:mm".</summary>
        public static string FormatInterval(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

        /// <summary>
        /// Finalens första start: när grundomgångens SISTA skjutlag är klart. Null när ingen
        /// starttid går att läsa — då får anroparen falla tillbaka på sitt eget förval.
        /// </summary>
        public static string? FinalsStartAfter(IEnumerable<string?> qualifyingStartTimes, int qualifyingSeries)
        {
            var last = qualifyingStartTimes.Select(ParseClock).Where(m => m.HasValue).Select(m => m!.Value)
                .DefaultIfEmpty(-1).Max();
            return last < 0 ? null : FormatClock(last + DurationMinutes(qualifyingSeries));
        }

        /// <summary>
        /// "Fortsätt i samma ordning": skjutlaget skjuter sin final direkt efter sin egen
        /// grundomgång. Oläsbar tid lämnas orörd.
        /// </summary>
        public static string ShiftAfterOwnQualification(string? qualifyingStart, int qualifyingSeries)
        {
            var m = ParseClock(qualifyingStart);
            return m.HasValue ? FormatClock(m.Value + DurationMinutes(qualifyingSeries)) : (qualifyingStart ?? "");
        }
    }
}
