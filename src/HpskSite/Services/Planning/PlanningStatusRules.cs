using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HpskSite.Services.Planning
{
    /// <summary>En del (huvudplaneringen eller en delplanering) i lägesrapporten. <c>PlacesMissing</c> är null när delen saknar bemanning att räkna på.</summary>
    public sealed record StatusPart(string Name, bool Readable, int Open, int Total, int Overdue, int NeedsHelp, int? PlacesNeeded, int? PlacesMissing, int Unlinked = 0);

    /// <summary>En öppen milstolpe med datum, för tidslinjen per månad. Titeln visas bara för delar läsaren får läsa.</summary>
    public sealed record StatusMilestone(string Part, string Title, DateTime Due, bool Overdue);

    /// <summary>
    /// Lägesrapporten (Anders, persontest 11): <b>en</b> yta som svarar på "hur ligger vi till?" — meningar med siffror, ingen färgkod och inget riskregister. Ren funktion, ingenting lagras:
    /// "försenat" och "behöver hjälp" är det uppgifterna redan säger, och bemanningen räknas av rutnätet. Anders: "skriv det som '3 av 12 uppgifter är försenade'".
    /// </summary>
    public static class PlanningStatusRules
    {
        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        /// <summary>"Läget 27 oktober: 31 uppgifter öppna av 58, 4 försenade, 22 platser saknas."</summary>
        public static string Headline(DateTime today, IReadOnlyList<StatusPart> parts)
        {
            var open = parts.Sum(p => p.Open);
            var total = parts.Sum(p => p.Total);
            var overdue = parts.Sum(p => p.Overdue);
            var pieces = new List<string> { $"{open} {(open == 1 ? "uppgift" : "uppgifter")} öppna av {total}" };
            pieces.Add(overdue > 0 ? $"{overdue} {(overdue == 1 ? "försenad" : "försenade")}" : "ingen försenad");
            var needed = parts.Sum(p => p.PlacesNeeded ?? 0);
            var missing = parts.Sum(p => p.PlacesMissing ?? 0);
            if (needed > 0) pieces.Add(missing > 0 ? $"{missing} {(missing == 1 ? "plats saknas" : "platser saknas")}" : "alla platser är fyllda");
            // Anders (persontest 12): rapporten ska säga rakt ut hur många som inte kunnat kontrolleras mot startlistan.
            var unlinked = parts.Sum(p => p.Unlinked);
            if (unlinked > 0) pieces.Add($"{unlinked} {(unlinked == 1 ? "namn är inte kopplat" : "namn är inte kopplade")} till en medlem och kan inte kontrolleras mot startlistan");
            return $"Läget {today.ToString("d MMMM", Sv)}: {string.Join(", ", pieces)}.";
        }

        /// <summary>En rad per del: "12 öppna av 14 · 3 försenade · 22 av 40 platser saknas".</summary>
        public static string PartLine(StatusPart p)
        {
            var pieces = new List<string> { $"{p.Open} öppna av {p.Total}" };
            if (p.Overdue > 0) pieces.Add($"{p.Overdue} {(p.Overdue == 1 ? "försenad" : "försenade")}");
            if (p.NeedsHelp > 0) pieces.Add($"{p.NeedsHelp} markerade Behöver hjälp");
            if (p.PlacesNeeded is > 0) pieces.Add(p.PlacesMissing > 0 ? $"{p.PlacesMissing} av {p.PlacesNeeded} platser saknas" : "alla platser fyllda");
            if (p.Unlinked > 0) pieces.Add($"{p.Unlinked} namn ej kopplade till medlem");
            return string.Join(" · ", pieces);
        }

        /// <summary>Milstolparna per kalendermånad, tidigast först, och inom månaden i datumordning. Tom månad finns inte.</summary>
        public static List<(DateTime Month, List<StatusMilestone> Items)> ByMonth(IEnumerable<StatusMilestone> milestones) =>
            milestones.GroupBy(m => new DateTime(m.Due.Year, m.Due.Month, 1))
                .OrderBy(g => g.Key)
                .Select(g => (g.Key, g.OrderBy(m => m.Due).ThenBy(m => m.Title, StringComparer.Create(Sv, true)).ToList()))
                .ToList();

        public static string MonthLabel(DateTime month) => Sv.TextInfo.ToTitleCase(month.ToString("MMMM yyyy", Sv));
    }
}
