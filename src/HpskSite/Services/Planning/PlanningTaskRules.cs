using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Uppgifternas och rollernas regler som rena funktioner (ingen databas, inget "nu" utom som
    /// parameter), så att de går att pröva.
    /// </summary>
    public static class PlanningTaskRules
    {
        public const int MaxTitle = 300;
        public const int MaxReport = 2000;
        public const int MaxTopic = 500;
        public const int MaxRoleName = 100;
        public const int MaxRolesPerPerson = 10;

        public static string? ValidateTitle(string? title)
        {
            var t = (title ?? "").Trim();
            if (t.Length == 0) return "Skriv vad som ska göras.";
            if (t.Length > MaxTitle) return $"Uppgiften är för lång (högst {MaxTitle} tecken).";
            return null;
        }

        /// <summary>
        /// Pågår mötet — eller har det nyss varit? Då beslutar MÖTET om uppgifterna (alla som deltar får trycka
        /// Klar / Fortsätter). Fönstret: från tolv timmar före start (man förbereder samma dag) till fjorton dagar
        /// efter slut (anteckningarna skrivs ofta i efterhand). Annars är det den sammankallande som beslutar.
        /// </summary>
        public static bool MeetingIsCurrent(PlanningMeetingRow? m, DateTime now)
        {
            if (m == null || m.Status != PlanningMeetingStatus.Scheduled || !m.StartsAt.HasValue || m.DeletedAt != null) return false;
            var end = m.EndsAt ?? m.StartsAt.Value.AddHours(1);
            return m.StartsAt.Value <= now.AddHours(12) && end >= now.AddDays(-14);
        }

        /// <summary>
        /// Uppgifterna på ett möte: alla öppna, plus de som beslutades utförda PÅ just det mötet (så att mötet
        /// ser vad det beslutade, och kan ångra). Ordning: Klar att pröva först (de väntar på beslut), sedan
        /// Behöver hjälp, Pågår, Ej påbörjad — och sist de utförda.
        /// </summary>
        public static List<PlanningTaskRow> ForMeeting(IEnumerable<PlanningTaskRow> tasks, int meetingId) =>
            tasks.Where(t => t.DeletedAt == null && (PlanningTaskStatus.IsOpen(t.Status) || t.DoneAtMeetingId == meetingId))
                 .OrderBy(t => MeetingOrder(t.Status)).ThenBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Id).ToList();

        public static int MeetingOrder(string? status) => status switch
        {
            PlanningTaskStatus.ReadyForReview => 0,
            PlanningTaskStatus.NeedsHelp => 1,
            PlanningTaskStatus.InProgress => 2,
            PlanningTaskStatus.NotStarted => 3,
            _ => 9,
        };

        /// <summary>
        /// Läget efter "Fortsätter": en uppgift som var Klar att pröva var inte klar — den pågår. Andra lägen
        /// står kvar (mötet ändrar inte vad den ansvarige sagt, bara att den inte är utförd).
        /// </summary>
        public static string AfterContinue(string? status) =>
            status == PlanningTaskStatus.ReadyForReview ? PlanningTaskStatus.InProgress : (PlanningTaskStatus.Normalize(status) ?? PlanningTaskStatus.NotStarted);

        /// <summary>Läget efter "Ångra Utförd": det uppgiften hade innan, annars Klar att pröva.</summary>
        public static string AfterUndo(string? statusBeforeDone)
        {
            var s = PlanningTaskStatus.Normalize(statusBeforeDone);
            return s == null || s == PlanningTaskStatus.Done ? PlanningTaskStatus.ReadyForReview : s;
        }

        /// <summary>
        /// Rollförslagen: bemanningens katalog (så att "Banansvarig" betyder samma sak i planeringen och på
        /// tävlingen) plus det en planeringsgrupp brukar fördela. Egen text går alltid.
        /// </summary>
        public static IReadOnlyList<(string? Key, string Name)> RoleSuggestions()
        {
            var list = new List<(string? Key, string Name)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? key, string name) { if (seen.Add(name)) list.Add((key, name)); }
            foreach (var n in PlanningExtras) Add(null, n);
            foreach (var type in new[] { "Precision", "Faltskytte", "Springskytte" })
                foreach (var r in FunctionaryRoles.ForDiscipline(type))
                    Add(r.Key, r.DisplayName);
            return list;
        }

        private static readonly string[] PlanningExtras =
        {
            "Tävlingsledare", "Biträdande tävlingsledare", "Säkerhetschef", "Sekreterare", "Ekonomi",
            "Inbjudan och information", "Anmälan och startlistor", "Resultat", "Priser och medaljer",
            "Servering", "Parkering och tillstånd", "Kontakt med markägaren", "Kontakt med kommunen",
            "Boende och resor", "Funktionärer",
        };

        /// <summary>Katalogens nyckel för ett rollnamn, eller null när namnet är egen text.</summary>
        public static string? RoleKeyFor(string name)
        {
            foreach (var (key, n) in RoleSuggestions())
                if (key != null && string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) return key;
            return null;
        }

        /// <summary>Rensar en rollista: trimmar, tar bort tomma och dubbletter, kapar längd och antal.</summary>
        public static List<string> CleanRoleNames(IEnumerable<string>? names)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outList = new List<string>();
            foreach (var raw in names ?? Enumerable.Empty<string>())
            {
                var n = (raw ?? "").Trim();
                if (n.Length == 0) continue;
                if (n.Length > MaxRoleName) n = n.Substring(0, MaxRoleName);
                if (seen.Add(n)) outList.Add(n);
                if (outList.Count >= MaxRolesPerPerson) break;
            }
            return outList;
        }
    }
}
