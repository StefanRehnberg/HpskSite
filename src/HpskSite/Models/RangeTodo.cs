namespace HpskSite.Models
{
    /// <summary>
    /// Rutan "Att göra" på banans översikt (/skjutbana/{id}, 2026-10-11). Förvaltaren ska kunna se på
    /// EN plats vad banan väntar på — tillstånd som går ut, klubbar utan tider, tider utanför
    /// tillståndets fönster — i stället för att klicka igenom sex flikar.
    ///
    /// <para>Ren funktion: allt hämtas av anroparen, och "i dag" skickas in, så att varje regel går
    /// att pröva utan databas (<c>RangeTodoRulesTests</c>).</para>
    /// </summary>
    public static class RangeTodoRules
    {
        /// <summary>Hur långt i förväg ett tillstånd eller dokument som går ut ska synas.</summary>
        public const int WarningDays = 90;

        /// <summary>Andel av årets skottak där förvaltaren varnas.</summary>
        public const double CapWarnShare = 0.8;

        public static List<RangeTodoItem> Build(RangeTodoInput i)
        {
            var list = new List<RangeTodoItem>();
            var today = i.Today.Date;

            // ── Tillstånd ─────────────────────────────────────────────────────
            if (i.Permits.Count == 0)
                list.Add(new RangeTodoItem(RangeTodoItem.Info, "Inga tillstånd är inlagda. Lägg in polistillståndet så att banan påminner när det går ut.", "tillstand"));
            foreach (var p in i.Permits)
                Expiry(list, today, p.Label, p.ExpiryDate, "tillstand");
            foreach (var d in i.Documents)
                Expiry(list, today, d.Label, d.ValidUntil, "tillstand");

            // ── Besiktningen ──────────────────────────────────────────────────
            if (i.InspectionIntervalYears is >= 1 && !i.HasInspection)
                list.Add(new RangeTodoItem(RangeTodoItem.Warn,
                    $"Banan ska besiktas {(i.InspectionIntervalYears == 1 ? "varje år" : $"vart {i.InspectionIntervalYears}:e år")}, men ingen besiktning är registrerad.", "tillstand"));
            if (i.NextInspectionDue.HasValue)
            {
                var d = i.NextInspectionDue.Value.Date;
                var days = (d - today).Days;
                if (days < 0)
                    list.Add(new RangeTodoItem(RangeTodoItem.Danger, $"Besiktningen skulle ha gjorts senast {d:yyyy-MM-dd}.", "tillstand"));
                else if (days <= WarningDays)
                    list.Add(new RangeTodoItem(RangeTodoItem.Warn,
                        $"Besiktning: nästa senast {d:yyyy-MM-dd} (om {days} {(days == 1 ? "dag" : "dagar")}).", "tillstand"));
            }

            // ── Skottaket ─────────────────────────────────────────────────────
            var cap = i.Permits.Where(p => p.MaxShotsPerYear is > 0).Select(p => p.MaxShotsPerYear!.Value).DefaultIfEmpty(0).Min();
            if (i.Permits.Count > 0 && cap == 0)
                list.Add(new RangeTodoItem(RangeTodoItem.Info, "Ingen maxgräns för skott per år är angiven i tillstånden.", "tillstand"));
            else if (cap > 0 && i.ShotsThisYear >= cap)
                list.Add(new RangeTodoItem(RangeTodoItem.Danger, $"Årets skott ({Num(i.ShotsThisYear)}) har nått taket på {Num(cap)}.", "aktivitet"));
            else if (cap > 0 && i.ShotsThisYear >= cap * CapWarnShare)
                list.Add(new RangeTodoItem(RangeTodoItem.Warn, $"Årets skott är {Num(i.ShotsThisYear)} av {Num(cap)} ({(int)Math.Round(100.0 * i.ShotsThisYear / cap)} %).", "aktivitet"));

            // ── Klubbar och tider ─────────────────────────────────────────────
            foreach (var c in i.Clubs.Where(c => c.AllocationCount == 0))
                list.Add(new RangeTodoItem(RangeTodoItem.Info, $"{c.ClubName} har inga fasta tider på banan.", "klubbar"));
            if (i.AllocationsOutsideWindow > 0)
                list.Add(new RangeTodoItem(RangeTodoItem.Warn,
                    i.AllocationsOutsideWindow == 1
                        ? "En fast tid ligger utanför tillståndets skjuttider."
                        : $"{i.AllocationsOutsideWindow} fasta tider ligger utanför tillståndets skjuttider.", "klubbar"));

            // ── Banans uppgifter ──────────────────────────────────────────────
            if (!i.HasSkjutbanechef)
                list.Add(new RangeTodoItem(RangeTodoItem.Info, "Ingen skjutbanechef är angiven.", "redigera"));
            if (!i.HasPosition)
                list.Add(new RangeTodoItem(RangeTodoItem.Info, "Banan saknar position på kartan, så ingen kan få en vägbeskrivning.", "redigera"));
            if (i.SectionCount == 0)
                list.Add(new RangeTodoItem(RangeTodoItem.Info, "Inga banor eller vallar är inlagda.", "banor"));

            // Det som brådskar först, sedan i den ordning reglerna står.
            return list.Select((t, n) => (t, n)).OrderBy(x => x.t.Severity switch
            {
                RangeTodoItem.Danger => 0,
                RangeTodoItem.Warn => 1,
                _ => 2
            }).ThenBy(x => x.n).Select(x => x.t).ToList();
        }

        private static void Expiry(List<RangeTodoItem> list, DateTime today, string label, DateTime? date, string goTo)
        {
            if (date == null) return;
            var d = date.Value.Date;
            var days = (d - today).Days;
            if (days < 0)
                list.Add(new RangeTodoItem(RangeTodoItem.Danger, $"{label} gick ut {d:yyyy-MM-dd}.", goTo));
            else if (days <= WarningDays)
                list.Add(new RangeTodoItem(RangeTodoItem.Warn,
                    days == 0 ? $"{label} går ut i dag." : $"{label} går ut {d:yyyy-MM-dd} (om {days} {(days == 1 ? "dag" : "dagar")}).", goTo));
        }

        private static string Num(int n) => n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("sv-SE"));

        /// <summary>Ligger en fast tid helt inom något av tillståndens fönster samma veckodag? Utan
        /// fönster finns inget att jämföra mot, och då räknas tiden som innanför.</summary>
        public static bool IsOutsideWindows(byte dayOfWeek, TimeSpan start, TimeSpan end, IReadOnlyCollection<RangeTodoWindow> windows)
        {
            if (windows.Count == 0) return false;
            return !windows.Any(w => w.Day == dayOfWeek && w.Start <= start && end <= w.End);
        }
    }

    public class RangeTodoInput
    {
        public DateTime Today { get; set; } = DateTime.Today;
        public List<RangeTodoDated> Permits { get; set; } = new();
        public List<RangeTodoDatedDoc> Documents { get; set; } = new();
        public List<RangeTodoClub> Clubs { get; set; } = new();
        public int AllocationsOutsideWindow { get; set; }
        public int ShotsThisYear { get; set; }
        public bool HasSkjutbanechef { get; set; }
        public bool HasPosition { get; set; }
        public int SectionCount { get; set; }
        /// <summary>Banans besiktningsintervall (null = inget angivet).</summary>
        public int? InspectionIntervalYears { get; set; }
        /// <summary>Finns någon aktiv besiktning registrerad?</summary>
        public bool HasInspection { get; set; }
        /// <summary>Den senaste besiktningens "nästa senast".</summary>
        public DateTime? NextInspectionDue { get; set; }
    }

    public record RangeTodoDated(string Label, DateTime? ExpiryDate, int? MaxShotsPerYear);
    public record RangeTodoDatedDoc(string Label, DateTime? ValidUntil);
    public record RangeTodoClub(string ClubName, int AllocationCount);
    public record RangeTodoWindow(byte Day, TimeSpan Start, TimeSpan End);

    public record RangeTodoItem(string Severity, string Text, string GoTo)
    {
        public const string Danger = "danger";
        public const string Warn = "warn";
        public const string Info = "info";
    }
}
