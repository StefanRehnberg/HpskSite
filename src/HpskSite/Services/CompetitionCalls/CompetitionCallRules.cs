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
        /// Ta tillbaka en utnämning. Stefan 2026-11-20 (F4): alltid, även när klubben skapat tävlingen och även när den är publicerad
        /// med anmälningar. Tävlingen står kvar hos klubben (raderas/flyttas aldrig); <see cref="UnawardWarning"/> varnar.
        /// </summary>
        public static bool CanUnaward(string status, int? competitionId) =>
            status is CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad;

        /// <summary>
        /// Varningen kretsen får innan en utnämning tas tillbaka från en klubb som skapat tävlingen; null när ingen tävling finns.
        /// Sajten löser inga betalningar — det gör klubbarna och kretsen sinsemellan.
        /// </summary>
        public static string? UnawardWarning(int? competitionId, bool published, int registrations)
        {
            if (!(competitionId > 0)) return null;
            var s = "Klubben har redan skapat tävlingen. Den står kvar hos klubben, men är inte längre kopplad till kretsens utlysning.";
            if (published) s += " Tävlingen är publicerad och syns för skyttarna.";
            if (registrations > 0)
                s += $" Den har {registrations} anmälningar. Har någon betalat sin anmälan återbetalas inget automatiskt – "
                   + "klubbarna och kretsen löser betalningarna sinsemellan, utanför sajten.";
            return s;
        }

        /// <summary>
        /// Får klubben ändra datumet? Utlysningen äger datumet tills tävlingen skapats; är det fastställt av kretsen (<c>LockedByKrets</c>)
        /// får klubben aldrig ändra det, varken i utlysningen eller i tävlingen — bara föreslå. Efter skapandet äger tävlingen datumet
        /// (Stefan 2026-11-20): kretsen flyttar det, klubben föreslår. Kretsens egna handlingar prövas med <paramref name="isKrets"/>.
        /// </summary>
        public static string? DateChangeRefusal(bool lockedByKrets, bool isKrets) =>
            lockedByKrets && !isKrets ? "Kretsen har fastställt datumet. Föreslå ett annat datum till kretsen i stället." : null;

        public static bool CanCancel(string status, int? competitionId) =>
            status != CompetitionCallStatus.Installd && !(competitionId > 0);

        /// <summary>
        /// Får den utsedda klubben skapa tävlingen nu? Null = ja, annars förklaringen. Kräver läget Tilldelad, ingen tävling och en gren
        /// (tävlingen måste ha en tävlingstyp — kretsen anger grenen i utlysningen).
        /// </summary>
        public static string? WhyNotCreate(string status, int? competitionId, string? discipline)
        {
            if (status == CompetitionCallStatus.Skapad || competitionId > 0) return "Tävlingen är redan skapad.";
            if (status != CompetitionCallStatus.Tilldelad) return "Kretsen har inte utsett er till arrangör.";
            if (string.IsNullOrWhiteSpace(discipline)) return "Kretsen har inte angett vilken gren tävlingen är. Hör av dig till kretsen.";
            return null;
        }

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
                    return "Ingen klubb är utsedd.";
                case "cancel" when !CanCancel(status, competitionId):
                    return competitionId > 0 ? "Klubben har skapat tävlingen – den berörs inte av att utlysningen ställs in." : "Utlysningen är redan inställd.";
            }
            return null;
        }

        // ── Önskemål (F3) ──────────────────────────────────────────────────────────────────────

        public const int MaxWishNote = 500;

        /// <summary>
        /// Får en klubb önska nu? Bara en UTLYST utlysning, och inte efter sista dagen — utom när kretsen själv
        /// skriver in åt klubben (<paramref name="onBehalf"/>): kretsen får ta emot ett svar som kommit per mejl i efterhand.
        /// </summary>
        public static bool CanWish(string status, DateTime? deadline, DateTime today, bool onBehalf) =>
            status == CompetitionCallStatus.Utlyst && (onBehalf || !deadline.HasValue || today.Date <= deadline.Value.Date);

        public static string? WhyNotWish(string status, DateTime? deadline, DateTime today, bool onBehalf)
        {
            if (CanWish(status, deadline, today, onBehalf)) return null;
            if (status == CompetitionCallStatus.Utlyst)
                return $"Sista dag för önskemål var {deadline!.Value.ToString("d MMMM yyyy", Sv)}. Hör av dig till kretsen om ni ändå vill arrangera.";
            return status switch
            {
                CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad => "Kretsen har redan utsett en klubb.",
                CompetitionCallStatus.Installd => "Utlysningen är inställd.",
                _ => "Utlysningen är inte utlyst än."
            };
        }

        /// <summary>Felmeddelande om önskemålets uppgifter, annars null. Förslaget till dag ska ligga i utlysningens år.</summary>
        public static string? ValidateWish(CompetitionCall call, DateTime? proposedDate, string? note)
        {
            if (note != null && note.Trim().Length > MaxWishNote) return $"Meddelandet får vara högst {MaxWishNote} tecken.";
            if (proposedDate.HasValue && proposedDate.Value.Year != call.Year) return $"Förslaget till dag ligger inte i {call.Year}.";
            return null;
        }

        /// <summary>Klubbens läge mot en utlysning — styr vad kortet visar och vilka knappar som finns.</summary>
        public static string ClubState(string status, int? awardedClubId, int clubId, bool myActiveWish) => status switch
        {
            CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad =>
                awardedClubId == clubId ? "awarded-me" : "awarded-other",
            CompetitionCallStatus.Utlyst => myActiveWish ? "wished" : "open",
            _ => "hidden"
        };

        /// <summary>Utlysningar en klubb ser alls: aldrig planering och aldrig inställda.</summary>
        public static bool VisibleToClubs(string status) =>
            status is CompetitionCallStatus.Utlyst or CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad;

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
