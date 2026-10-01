namespace HpskSite.Models.Kretsgranskning
{
    /// <summary>
    /// En post i kretskalendern (fas 4). Kalendern läser fyra källor — ansökningar som ännu saknar
    /// tävling, tävlingar, kretsens händelser och (senare) Förbundets stomprogram — genom EN tjänst,
    /// och den här formen är vad varje källa översätts till.
    /// </summary>
    public class KretsCalendarEntry
    {
        /// <summary>competition, application, event.</summary>
        public string Kind { get; set; } = "";
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime Date { get; set; }
        public DateTime? EndDate { get; set; }
        /// <summary>Gren (CompetitionTypes-id) — tom för en händelse.</summary>
        public string Discipline { get; set; } = "";
        public string DisciplineLabel { get; set; } = "";
        /// <summary>Kategorin (CompetitionLevel), om känd.</summary>
        public string Level { get; set; } = "";
        public string Organiser { get; set; } = "";
        public string Place { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>Kretsens ord: preliminar, godkand, installd, tavling, handelse.</summary>
        public string Status { get; set; } = "";
        public string StatusLabel { get; set; } = "";
        /// <summary>Kretsen posten hör till (regionCode).</summary>
        public string RegionCode { get; set; } = "";
        public string RegionName { get; set; } = "";
        /// <summary>Posten kommer från en grannkrets (visas dovare).</summary>
        public bool IsNeighbour { get; set; }
        /// <summary>Är posten ett svenskt mästerskap (krockar med kretsens tävlingar i samma gren).</summary>
        public bool IsSm { get; set; }
        /// <summary>Krockvarningar med skäl — aldrig stopp.</summary>
        public List<string> Conflicts { get; set; } = new();
    }

    public static class KretsCalendarStatus
    {
        public const string Preliminar = "preliminar";
        public const string Godkand = "godkand";
        public const string Installd = "installd";
        public const string Tavling = "tavling";
        public const string Handelse = "handelse";

        public static string Label(string s) => s switch
        {
            Preliminar => "Preliminär",
            Godkand => "Godkänd",
            Installd => "Inställd",
            Tavling => "Tävling",
            Handelse => "Händelse",
            _ => s
        };
    }

    /// <summary>
    /// Krockvarningar som ren funktion. Två saker varnas för, med skäl och aldrig som stopp:
    /// samma gren samma dag (i kretsen eller i en grannkrets), och ett SM i grenen samma dag
    /// (SHB C.3.5.1.1 — ingen annan tävling i grenen bör ligga då).
    /// </summary>
    public static class KretsCalendarConflicts
    {
        public static void Mark(IReadOnlyList<KretsCalendarEntry> entries)
        {
            var dated = entries.Where(e => e.Kind != "event" && !string.IsNullOrEmpty(e.Discipline)).ToList();
            foreach (var e in dated)
            {
                foreach (var o in dated)
                {
                    if (ReferenceEquals(e, o) || o.Discipline != e.Discipline || !Overlaps(e, o)) continue;
                    // Samma post via två källor (tävlingen och dess ansökan) är ingen krock.
                    if (e.Kind == o.Kind && e.Id == o.Id) continue;

                    string reason;
                    if (o.IsSm && !e.IsSm)
                        reason = $"SM i {e.DisciplineLabel.ToLowerInvariant()} samma dag: {o.Name}";
                    else if (o.IsNeighbour && !e.IsNeighbour)
                        reason = $"Samma gren samma dag i {o.RegionName}: {o.Name}";
                    else if (!o.IsNeighbour && !e.IsNeighbour)
                        reason = $"Samma gren samma dag i kretsen: {o.Name}";
                    else continue;   // två grannar som krockar med varandra är inte kretsens fråga

                    if (!e.Conflicts.Contains(reason)) e.Conflicts.Add(reason);
                }
            }
        }

        public static bool Overlaps(KretsCalendarEntry a, KretsCalendarEntry b)
        {
            var aEnd = (a.EndDate ?? a.Date).Date;
            var bEnd = (b.EndDate ?? b.Date).Date;
            return a.Date.Date <= bEnd && b.Date.Date <= aEnd;
        }
    }
}
