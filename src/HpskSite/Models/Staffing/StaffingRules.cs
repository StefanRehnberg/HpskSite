using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>Vad som gör en person upptagen: en egen start, ett annat uppdrag, eller att hen bara anmält sig till vissa tider.</summary>
    public enum BusyKind { Start = 0, Assignment = 1, Availability = 2 }

    /// <summary>En upptagen tid. <see cref="Interval"/> för en start är redan fönstret med förberedelse och vila (<see cref="ShootTime.Window"/>).</summary>
    public sealed record BusyItem(BusyKind Kind, StaffInterval Interval, string Label, string? Place = null);

    /// <summary>Det första som hindrar en person från en plats. <see cref="IsHard"/> = samtidigt (ett stopp som planeraren får trotsa), annars en varning.</summary>
    public sealed record SlotConflict(BusyKind Kind, string Label, ClashKind Clash, int? GapMinutes = null)
    {
        public bool IsHard => Clash == ClashKind.Overlap;
    }

    /// <summary>
    /// Planera dagen steg 2: <b>reglerna för "kan den här personen stå här?"</b> som EN ren funktion — samma krockregel som veckan
    /// (<see cref="StaffInterval.Between"/>), så att kandidatlistan, "lägg in flera", förslaget och kontrollen aldrig tycker olika.
    /// Ingen databas, ingen klocka.
    /// </summary>
    public static class StaffingRules
    {
        /// <summary>
        /// Det första hindret för <paramref name="slot"/>: ett samtidigt (hårt) före en trång förflyttning (varning); bland lika
        /// vinner en egen start före ett annat uppdrag — det är starten som inte går att flytta. Null = fri.
        /// </summary>
        public static SlotConflict? FirstConflict(StaffInterval slot, string? place, IEnumerable<BusyItem> busy, TimeSpan? travelMargin = null)
        {
            SlotConflict? soft = null;
            SlotConflict? hard = null;
            foreach (var b in busy.OrderBy(x => x.Kind == BusyKind.Start ? 0 : 1).ThenBy(x => x.Interval.Start))
            {
                // En Availability-post ÄR tiden utanför det personen anmält sig till — samtidigt med den är ett hinder, men ingen resa.
                var kind = b.Kind == BusyKind.Availability
                    ? (b.Interval.Overlaps(slot) ? ClashKind.Overlap : ClashKind.None)
                    : StaffInterval.Between(slot, place, b.Interval, b.Place, travelMargin);
                if (kind == ClashKind.None) continue;
                var gap = kind == ClashKind.TightTravel ? (int)Math.Round(slot.GapTo(b.Interval).TotalMinutes) : (int?)null;
                var c = new SlotConflict(b.Kind, b.Label, kind, gap);
                if (kind == ClashKind.Overlap) { hard ??= c; }
                else soft ??= c;
            }
            return hard ?? soft;
        }

        /// <summary>
        /// Anmälans tider som "upptagen utanför dem": en person som bara kan 12–15 är upptagen före och efter. Inga fönster = hela dagen
        /// (en dagsanmälan utan tider). Tom lista ger alltså inga hinder.
        /// </summary>
        public static IEnumerable<BusyItem> OutsideAvailability(DateTime day, IReadOnlyList<(TimeSpan From, TimeSpan To)> windows)
        {
            var valid = windows.Where(w => w.To > w.From).OrderBy(w => w.From).ToList();
            if (valid.Count == 0) yield break;
            var d = day.Date;
            var cursor = TimeSpan.Zero;
            foreach (var w in valid)
            {
                if (w.From > cursor) yield return new BusyItem(BusyKind.Availability, new StaffInterval(d + cursor, d + w.From, false), AvailabilityText(valid));
                if (w.To > cursor) cursor = w.To;
            }
            if (cursor < TimeSpan.FromDays(1)) yield return new BusyItem(BusyKind.Availability, new StaffInterval(d + cursor, d.AddDays(1), false), AvailabilityText(valid));
        }

        private static string AvailabilityText(List<(TimeSpan From, TimeSpan To)> w) =>
            "har bara anmält sig " + string.Join(", ", w.Select(x => $"{x.From:hh\\:mm}–{x.To:hh\\:mm}"));

        /// <summary>"HH:mm" eller "H:mm" → klockslag; allt annat = null.</summary>
        public static TimeSpan? ParseTime(string? s) =>
            !string.IsNullOrWhiteSpace(s) && TimeSpan.TryParseExact(s.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var t)
                && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;
    }

    /// <summary>Vad en persons funktionärsanmälan säger om en dag: har hen anmält sig, till rätt roll, och till vilka tider (tom = hela dagen).</summary>
    public sealed record VolunteerFit(bool Volunteers, bool RoleMatch, IReadOnlyList<(TimeSpan From, TimeSpan To)> Windows);

    public static class VolunteerRules
    {
        /// <summary>
        /// Läser en anmälans val för <paramref name="date"/>: ett dagsval med tom roll ("var det behövs") eller samma roll, eller ett eget
        /// tillfälle den dagen (<paramref name="slotIdsOnDate"/>). Ett val av en ANNAN roll den dagen räknas inte (jfr
        /// <c>ReplacementRules.Matches</c>). <paramref name="roleKey"/> null = vilken roll som helst räknas som anmäld.
        /// Tiderna samlas från de val som räknas; saknar något av dem tider gäller hela dagen (inga fönster).
        /// </summary>
        public static VolunteerFit Fit(IEnumerable<HelpSlotChoice> choices, DateTime date, string? roleKey, ISet<int> slotIdsOnDate)
        {
            bool any = false, role = false, unrestricted = false;
            var windows = new List<(TimeSpan, TimeSpan)>();
            void Take(HelpSlotChoice c)
            {
                any = true;
                var times = (c.Times ?? new List<HelpTimeWindow>())
                    .Select(t => (From: StaffingRules.ParseTime(t.From), To: StaffingRules.ParseTime(t.To)))
                    .Where(t => t.From != null && t.To != null && t.To > t.From).Select(t => (t.From!.Value, t.To!.Value)).ToList();
                if (times.Count == 0) unrestricted = true; else windows.AddRange(times);
            }
            foreach (var c in choices)
            {
                if (c.SlotId > 0) { if (slotIdsOnDate.Contains(c.SlotId)) Take(c); continue; }
                if (!c.IsDayChoice || !DateTime.TryParseExact(c.Date!.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Date != date.Date) continue;
                var r = (c.RoleKey ?? "").Trim();
                if (r.Length == 0) Take(c);
                else if (roleKey == null) Take(c);
                else if (string.Equals(r, roleKey, StringComparison.OrdinalIgnoreCase)) { Take(c); role = true; }
            }
            return new VolunteerFit(any, role, unrestricted ? Array.Empty<(TimeSpan, TimeSpan)>() : windows);
        }
    }

    /// <summary>
    /// Hur högt en kandidat står i listan. Lägre = högre upp. De som anmält sig som funktionärer och är lediga först (rätt roll före
    /// "var det behövs"); sedan de som står i startlistan men inte anmält sig ("kan frågas"); sist de som är upptagna.
    /// </summary>
    public enum CandidateTier
    {
        VolunteerRoleFree = 0,
        VolunteerFree = 1,
        CanAsk = 2,
        Unknown = 3,
        Busy = 4,
    }

    public static class CandidateRules
    {
        /// <param name="volunteer">Har anmält sig som funktionär den dagen.</param>
        /// <param name="roleMatch">Har anmält sig just till den här rollen (inte bara "var det behövs").</param>
        /// <param name="free">Fri på den angivna tiden. Null = ingen tid angiven än.</param>
        public static CandidateTier Tier(bool volunteer, bool roleMatch, bool? free)
        {
            if (free == false) return CandidateTier.Busy;
            if (volunteer) return roleMatch ? CandidateTier.VolunteerRoleFree : CandidateTier.VolunteerFree;
            return free == true ? CandidateTier.CanAsk : CandidateTier.Unknown;
        }

        private static readonly StringComparer Sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true);

        /// <summary>Sorterar efter nivå, sedan namn (svensk ordning), sedan id — fast ordning så att samma underlag ger samma lista.</summary>
        public static List<T> Rank<T>(IEnumerable<T> list, Func<T, CandidateTier> tier, Func<T, string> name, Func<T, int> id) =>
            list.OrderBy(c => (int)tier(c)).ThenBy(c => name(c), Sv).ThenBy(id).ToList();
    }
}
