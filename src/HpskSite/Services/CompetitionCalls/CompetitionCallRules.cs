using System.Globalization;
using HpskSite.Models.CompetitionCalls;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Utlysningens regler som RENA funktioner (kretsutlysningen F2): vad som får ändras i vilket läge, vad som är
    /// giltigt, och vilka rader årsprogrammet innehåller. Ingen databas, ingen inloggning — tjänsten och kontrollern
    /// anropar dem, och <c>CompetitionCallRulesTests</c> prövar dem.
    /// </summary>
    public static class CompetitionCallRules
    {
        public const int MaxName = 200;
        public const int MaxDescription = 1000;
        public static readonly string[] Months =
            { "januari", "februari", "mars", "april", "maj", "juni", "juli", "augusti", "september", "oktober", "november", "december" };

        private static readonly CultureInfo Sv = new("sv-SE");

        /// <summary>Felmeddelandet (på svenska, till kretsens styrelse) eller null när innehållet är giltigt.</summary>
        public static string? ValidateContent(CompetitionCall c, DateTime today)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) return "Skriv ett namn på tävlingen.";
            if (c.Name.Trim().Length > MaxName) return $"Namnet får vara högst {MaxName} tecken.";
            if (c.Description != null && c.Description.Length > MaxDescription) return $"Beskrivningen får vara högst {MaxDescription} tecken.";
            if (c.Year < 2000 || c.Year > today.Year + 5) return "Året stämmer inte.";
            if (c.CompetitionDate.HasValue && c.CompetitionDate.Value.Year != c.Year)
                return $"Datumet ligger inte i {c.Year}. Ändra året eller datumet.";
            if (c.LockedByKrets && !c.CompetitionDate.HasValue)
                return "Datumet kan inte vara fastställt när inget datum är satt.";
            if (c.LockedByKrets && c.Level != "Krets") return "Bara en kretstävling kan ha fastställt datum.";
            if (c.WishDeadline.HasValue && c.CompetitionDate.HasValue && c.WishDeadline.Value.Date > c.CompetitionDate.Value.Date)
                return "Sista dag för önskemål ligger efter tävlingen.";
            return null;
        }

        /// <summary>Kretsen får ändra uppgifterna så länge tävlingen inte är skapad eller inställd.</summary>
        public static bool CanEdit(string status) =>
            status is CompetitionCallStatus.Planering or CompetitionCallStatus.Utlyst or CompetitionCallStatus.Tilldelad;

        /// <summary>Utlys = släpp den till klubbarna.</summary>
        public static bool CanPublish(string status) => status == CompetitionCallStatus.Planering;

        /// <summary>Dra tillbaka till planering: bara en utlysning ingen klubb har önskat eller fått.</summary>
        public static bool CanWithdraw(string status, int activeWishes) => status == CompetitionCallStatus.Utlyst && activeWishes == 0;

        /// <summary>Utse en klubb: direkt ur planering eller utlyst (kretsen får utse utan önskemål). Aldrig om redan utsedd.</summary>
        public static bool CanAward(string status) => status is CompetitionCallStatus.Planering or CompetitionCallStatus.Utlyst;

        /// <summary>
        /// Ta tillbaka en utnämning. Bara när klubben inte skapat tävlingen — en skapad tävling raderas aldrig tyst
        /// (F4 avgör när en skapad tävling går att byta klubb på).
        /// </summary>
        public static bool CanUnaward(string status, int? competitionId) =>
            status == CompetitionCallStatus.Tilldelad && !(competitionId > 0);

        public static bool CanCancel(string status, int? competitionId) =>
            status != CompetitionCallStatus.Installd && !(competitionId > 0);

        /// <summary>Förklaringen när en handling är avstängd — knappen står aldrig avstängd utan skäl.</summary>
        public static string? WhyNot(string action, string status, int? competitionId, int activeWishes = 0)
        {
            switch (action)
            {
                case "edit" when !CanEdit(status):
                    return status == CompetitionCallStatus.Installd ? "Utlysningen är inställd." : "Tävlingen är skapad – ändra den under klubbens tävlingar.";
                case "publish" when !CanPublish(status):
                    return "Utlysningen är redan utlyst.";
                case "withdraw" when !CanWithdraw(status, activeWishes):
                    return activeWishes > 0 ? "Klubbar har redan önskat – utlysningen går inte att dra tillbaka." : "Bara en utlyst utlysning kan dras tillbaka.";
                case "award" when !CanAward(status):
                    return status == CompetitionCallStatus.Tilldelad ? "En klubb är redan utsedd. Ta tillbaka utnämningen först."
                         : status == CompetitionCallStatus.Skapad ? "Klubben har redan skapat tävlingen." : "Utlysningen är inställd.";
                case "unaward" when !CanUnaward(status, competitionId):
                    return status == CompetitionCallStatus.Skapad || competitionId > 0
                        ? "Klubben har redan skapat tävlingen, så utnämningen går inte att ta tillbaka här."
                        : "Ingen klubb är utsedd.";
                case "cancel" when !CanCancel(status, competitionId):
                    return competitionId > 0 ? "Klubben har skapat tävlingen – den berörs inte av att utlysningen ställs in." : "Utlysningen är redan inställd.";
            }
            return null;
        }

        // ── Årsprogrammet (utskrift och, i F5, kalendern) ──────────────────────────────────────

        /// <summary>
        /// En rad i årsprogrammet. <paramref name="Organiser"/> är klubbens namn, eller null = "Ej utsedd".
        /// </summary>
        public sealed record ProgramRow(int CallId, DateTime? Date, string Weekday, string Name, string? Organiser, string Discipline, string Note, string Status, bool IsPlanning);

        /// <summary>
        /// Raderna kretsen skriver ut. Med <paramref name="includePlanning"/> falskt utelämnas det som bara är planering
        /// (klubbarna och skyttarna ser inte det). Inställda tas aldrig med. Datumlösa kommer sist, för sig.
        /// </summary>
        public static List<ProgramRow> ProgramRows(IEnumerable<CompetitionCall> calls, Func<int, string?> clubName,
            Func<string?, string> disciplineLabel, bool includePlanning)
        {
            var rows = new List<ProgramRow>();
            foreach (var c in calls.Where(c => c.Status != CompetitionCallStatus.Installd))
            {
                if (!includePlanning && c.Status == CompetitionCallStatus.Planering) continue;
                var date = c.CompetitionDate;
                rows.Add(new ProgramRow(c.Id, date, date.HasValue ? WeekdaySv(date.Value) : "", c.Name,
                    c.AwardedClubId > 0 ? clubName(c.AwardedClubId!.Value) : null,
                    disciplineLabel(c.Discipline), c.Description ?? "", c.Status, c.Status == CompetitionCallStatus.Planering));
            }
            return rows.OrderBy(r => r.Date.HasValue ? 0 : 1).ThenBy(r => r.Date).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Raderna i månadsgrupper (sidbrytning per månad); datumlösa i en egen grupp sist.</summary>
        public static List<(string Heading, List<ProgramRow> Rows)> ByMonth(IEnumerable<ProgramRow> rows)
        {
            var list = rows.ToList();
            var groups = list.Where(r => r.Date.HasValue).GroupBy(r => r.Date!.Value.Month)
                .OrderBy(g => g.Key).Select(g => (Capitalize(Months[g.Key - 1]), g.ToList())).ToList();
            var undated = list.Where(r => !r.Date.HasValue).ToList();
            if (undated.Count > 0) groups.Add(("Datum inte bestämt", undated));
            return groups;
        }

        public static string WeekdaySv(DateTime d) => Capitalize(d.ToString("dddd", Sv));

        private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Sv) + s[1..];
    }
}
