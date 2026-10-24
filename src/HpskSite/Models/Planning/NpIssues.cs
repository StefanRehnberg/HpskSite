using System;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Förbundets tidning Nationellt Pistolskytte: fyra nummer om året. P4 (Stefan 2026-10-24): SM-annonsen ska med i det
    /// nummer som kommer ut före tävlingen — den stora SM-veckan alltid i nr 2, ett SM senare på året (t.ex. springskytte)
    /// i nr 3.
    ///
    /// <para>Förbundet publicerar utgivningsplanen (pistolskytteforbundet.se, Om oss → Medlemstidningen). 2026:
    /// nr 1 manusstopp 23 feb (utgivning 7 apr), nr 2 4 maj (15 jun), nr 3 10 aug (21 sep), nr 4 2 nov (14 dec) — alltså
    /// fjärde måndagen i februari, första i maj, andra i augusti och första i november, och utgivning ungefär sex veckor
    /// senare. Det är en UPPSKATTNING för kommande år: milstolpen säger det och går att ändra.</para>
    /// </summary>
    public static class NpIssues
    {
        /// <summary>Hur långt före tävlingen numret ska ha kommit ut för att räknas (anmälan ska hinna göras).</summary>
        public const int LeadDays = 14;

        public const string PlanUrl = "https://www.pistolskytteforbundet.se/om-oss/medlemstidningen/";

        /// <summary>Uppskattat manusstopp för ett nummer (1–4) ett år.</summary>
        public static DateTime ManuscriptDeadline(int year, int issue) => issue switch
        {
            1 => NthMonday(year, 2, 4),
            2 => NthMonday(year, 5, 1),
            3 => NthMonday(year, 8, 2),
            4 => NthMonday(year, 11, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(issue)),
        };

        /// <summary>Uppskattad utgivning: sex veckor efter manusstoppet.</summary>
        public static DateTime EstimatedRelease(int year, int issue) => ManuscriptDeadline(year, issue).AddDays(42);

        /// <summary>
        /// Numret som annonsen ska in i: det senaste som kommer ut minst <see cref="LeadDays"/> dagar före tävlingen
        /// (förra årets nr 4 för en tävling tidigt på året).
        /// </summary>
        public static (int Year, int Issue, DateTime Deadline) ForCompetition(DateTime competitionDate)
        {
            var last = competitionDate.Date.AddDays(-LeadDays);
            for (var y = competitionDate.Year; y >= competitionDate.Year - 1; y--)
                for (var i = 4; i >= 1; i--)
                    if (EstimatedRelease(y, i) <= last) return (y, i, ManuscriptDeadline(y, i));
            var py = competitionDate.Year - 2;
            return (py, 4, ManuscriptDeadline(py, 4));
        }

        private static DateTime NthMonday(int year, int month, int n)
        {
            var d = new DateTime(year, month, 1);
            while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }
    }
}
