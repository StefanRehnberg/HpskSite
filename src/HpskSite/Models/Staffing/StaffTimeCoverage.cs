using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Omgång 3 (Expertgruppen 3.2): <b>tiden är sanningen</b>. Hur många som är på plats räknas som det största antalet
    /// som är där SAMTIDIGT, inte som antalet uppdrag — tio markörer i två skift om fem är fem på plats, inte tio
    /// (persona-testets "40/10"). Ett uppdrag utan tid gäller hela dagen och är därför med i varje ögonblick.
    /// </summary>
    public static class StaffTimeCoverage
    {
        /// <summary>"08:00–12:00" (tankstreck eller bindestreck) som ett tidsintervall; null när det inte går att läsa.</summary>
        public static (TimeSpan Start, TimeSpan End)? ParseRange(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var parts = label.Split('–', '-');
            if (parts.Length != 2) return null;
            // ClockTime, aldrig TimeSpan.TryParse: "8" är klockan 8, inte åtta dygn.
            if (HpskSite.Models.ClockTime.ToTimeSpan(parts[0].Trim()) is not TimeSpan s) return null;
            if (HpskSite.Models.ClockTime.ToTimeSpan(parts[1].Trim()) is not TimeSpan e) return null;
            return e <= s ? null : (s, e);
        }

        /// <summary>
        /// Flest samtidigt. <paramref name="spans"/>: ett intervall per uppdrag, null = hela dagen. Med
        /// <paramref name="window"/> räknas bara det som överlappar fönstret (kant i kant räknas inte), och ett uppdrag
        /// utan tid räknas med. Utan fönster: hela dagen.
        /// </summary>
        public static int Peak(IEnumerable<(TimeSpan Start, TimeSpan End)?> spans, (TimeSpan Start, TimeSpan End)? window = null)
        {
            var list = spans.ToList();
            var allDay = list.Count(s => s == null);
            var timed = list.Where(s => s != null).Select(s => s!.Value).ToList();
            if (window is { } w)
                timed = timed.Where(s => s.Start < w.End && w.Start < s.End)
                    .Select(s => (Start: s.Start < w.Start ? w.Start : s.Start, End: s.End > w.End ? w.End : s.End)).ToList();
            var events = timed.SelectMany(s => new[] { (T: s.Start, D: 1), (T: s.End, D: -1) })
                // Vid samma tidpunkt går den som slutar före den som börjar — en överlämning är inte två samtidigt.
                .OrderBy(e => e.T).ThenBy(e => e.D);
            int cur = 0, peak = 0;
            foreach (var e in events) { cur += e.D; if (cur > peak) peak = cur; }
            return allDay + peak;
        }
    }
}
