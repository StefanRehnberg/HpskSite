using System;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    /// <summary>Serialiseras som text ("Done"/"Todo") — sidan jämför med strängen, aldrig med ett tal (en omsorterad enum ska inte kunna vända betydelsen).</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    [Newtonsoft.Json.JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum PlanStepState { Done, Todo }

    /// <summary>
    /// Ett steg i "Dagens plan". <see cref="Actions"/> är nycklar som sidan kopplar till de dialoger som redan finns (roller, behov, lage, skjuttid, laggin, forslag, startlista, skrivut) —
    /// stegen äger inga egna handlingar och spärrar ingenting. <see cref="Text"/> är alltid en mening med tal eller val, aldrig bara en bock.
    /// </summary>
    public sealed record PlanStep(string Key, string Title, PlanStepState State, string Text, IReadOnlyList<string> Actions);

    /// <summary>Fakta om planeringen som stegen härleds ur — alla läses ur samma rutnätssvar, ingenting lagras.</summary>
    public sealed record PlanFacts(bool HasRoles, bool HasNeeds, int PlacesNeeded, int PlacesMissing, bool Linked, int VolunteersTotal, string? ShooterModeTitle,
        bool PatrolPlanAvailable, int StartClashes, int Clashes, IReadOnlyList<RolePlaces>? ByRole = null,
        int AssignedPeople = 0, int PeopleWithStart = 0, int AssignedTotal = 0, int PlannedUnsent = 0, int PlannedUnreachable = 0,
        int Unlinked = 0);

    /// <summary>Platser per roll (summerat över dagar och stationer) — så att stegen kan säga VILKA roller som saknar folk, och så att ett orimligt antal (8 på varje av åtta stationer) syns som en siffra på sin rad.</summary>
    public sealed record RolePlaces(string Name, int Needed, int Missing);

    /// <summary>
    /// Planera dagen: <b>"Dagens plan"</b> — ett band överst i Bemanning som visar läget i text. Ren funktion; ingen databas, ingen klocka. Reglerna (Expertgruppen 2026-10-26):
    /// <list type="bullet">
    /// <item><b>Härlett, aldrig lagrat:</b> "klart" betyder att datan säger det (behoven är satta, platserna är fyllda) — inte att något klickats.</item>
    /// <item><b>Ingenting låser:</b> alla steg går att göra i valfri ordning; ett villkor är en mening, aldrig ett avaktiverat steg.</item>
    /// <item><b>Små fall ser små ut:</b> steg som inte är aktuella finns inte med — en klubbtävling utan funktionärsanmälan eller fältstartlista ser tre steg (behov, fyll, kontrollera).
    /// Det stora fallet (SM, flera delar) får aldrig bli utgångsläget för det lilla.</item>
    /// <item><b>Startlistan och fyllningen är en loop</b>, inte en följd: startlistan går att bygga om när uppdragen ändras, och tvärtom.</item>
    /// </list>
    /// </summary>
    public static class PlanStepRules
    {
        /// <summary>"Sekretariat 2, Servering 3, Stationschef 8" — högst sex roller, resten som "m.fl.". Tom när uppdelningen saknas.</summary>
        private static string RoleList(IEnumerable<(string Name, int N)> items)
        {
            var l = items.Where(i => i.N > 0).ToList();
            return string.Join(", ", l.Take(6).Select(i => $"{i.Name} {i.N}")) + (l.Count > 6 ? " m.fl." : "");
        }

        public static List<PlanStep> Derive(PlanFacts f)
        {
            var byRole = f.ByRole ?? Array.Empty<RolePlaces>();
            var steps = new List<PlanStep>();

            // 1. Roller och behov — alltid.
            if (!f.HasRoles)
                steps.Add(new("behov", "Vem behövs, och hur många?", PlanStepState.Todo, "Välj vilka roller ni behöver — Sekretariat, Servering, Stationsmarkör …", new[] { "roller" }));
            else if (!f.HasNeeds)
                steps.Add(new("behov", "Vem behövs, och hur många?", PlanStepState.Todo, "Rollerna är valda. Sätt hur många som behövs av varje.", new[] { "behov" }));
            else
                steps.Add(new("behov", "Vem behövs, och hur många?", PlanStepState.Done,
                    $"{f.PlacesNeeded} {(f.PlacesNeeded == 1 ? "plats" : "platser")} behövs" + (byRole.Count > 1 ? ": " + RoleList(byRole.Select(r => (r.Name, r.Needed))) : ""), new[] { "behov" }));

            // 2. Funktionärer som skjuter — bara när tävlingen är kopplad och det finns funktionärer eller en fältstartlista att passa ihop med.
            if (f.Linked && (f.VolunteersTotal > 0 || f.PatrolPlanAvailable))
            {
                steps.Add(string.IsNullOrEmpty(f.ShooterModeTitle)
                    ? new("lage", "Funktionärer som skjuter", PlanStepState.Todo, "Välj hur ni gör med funktionärer som också skjuter: reserv, egen patrull, skjuter först eller samordnat.", new[] { "lage", "skjuttid" })
                    : new("lage", "Funktionärer som skjuter", PlanStepState.Done, $"Valt: {f.ShooterModeTitle}", new[] { "lage", "skjuttid" }));
            }

            // 3. Fyll platserna — när något behövs.
            if (f.HasNeeds)
            {
                var actions = f.VolunteersTotal > 0 ? new[] { "laggin", "forslag" } : new[] { "laggin" };
                steps.Add(f.PlacesMissing == 0
                    ? new("fyll", "Fyll platserna", PlanStepState.Done, "Alla platser är fyllda", actions)
                    : new("fyll", "Fyll platserna", PlanStepState.Todo, $"{f.PlacesMissing} av {f.PlacesNeeded} {(f.PlacesNeeded == 1 ? "plats" : "platser")} saknar folk"
                        + (byRole.Any(r => r.Missing > 0) ? ": " + RoleList(byRole.OrderByDescending(r => r.Missing).Select(r => (r.Name, r.Missing))) : ""), actions));
            }

            // 3b. Skicka förfrågan — det som lagts in ser ingen förrän det skickats (persontest 8: hon hittade aldrig var man gör det).
            if (f.HasNeeds && f.AssignedTotal > 0)
            {
                steps.Add(f.PlannedUnsent > 0
                    ? new("skicka", "Skicka förfrågan till de inlagda", PlanStepState.Todo,
                        $"{f.PlannedUnsent} {(f.PlannedUnsent == 1 ? "inlagd har" : "inlagda har")} ännu ingen förfrågan — de ser inget och får inget mejl förrän du skickar", new[] { "skicka" })
                    : f.PlannedUnreachable > 0
                        // Anders (persontest 11): "klart" stod där fast två inlagda inte gick att nå alls.
                        ? new("skicka", "Skicka förfrågan till de inlagda", PlanStepState.Todo,
                            $"{f.PlannedUnreachable} {(f.PlannedUnreachable == 1 ? "inlagd kan" : "inlagda kan")} inte nås — varken konto eller e-post. Lägg till e-post eller koppla till en medlem, så går förfrågan att skicka", new[] { "skicka" })
                        : new("skicka", "Skicka förfrågan till de inlagda", PlanStepState.Done, "Alla inlagda har fått sin förfrågan", new[] { "skicka" }));
            }

            // 4. Startlistan — bara fält, och bara för den som får bygga om den. Grönt först när någon faktiskt är kontrollerad och inga platser står tomma
            // (persontest 8: det gröna stod där före allt var gjort, och ingen skärm sa att alla var kontrollerade).
            if (f.PatrolPlanAvailable)
            {
                // Bara medlemmar går att kontrollera mot startlistan: en fritextperson har ingen identitet att matcha en start mot (Anders, persontest 11).
                // Texten säger därför vad som faktiskt kontrollerats och hur många som inte kunnat kontrolleras — den gissar aldrig på namn.
                var members = Math.Max(0, f.AssignedPeople - f.Unlinked);
                var who = f.PeopleWithStart > 0
                    ? $"Ingen av de {f.PeopleWithStart} placerade som också skjuter krockar med sin egen start"
                    : members > 0 ? $"Ingen av de {members} placerade medlemmarna skjuter i tävlingen, så ingen start krockar" : "";
                var unlinkedText = f.Unlinked > 0
                    ? $"{f.Unlinked} {(f.Unlinked == 1 ? "namn är inte kopplat till en medlem och går" : "namn är inte kopplade till en medlem och går")} inte att kontrollera"
                    : "";
                if (f.StartClashes > 0)
                    steps.Add(new("startlista", "Startlistan", PlanStepState.Todo, $"{f.StartClashes} {(f.StartClashes == 1 ? "start krockar" : "starter krockar")} med en placering — bygg om startlistan runt funktionärerna", new[] { "startlista" }));
                else if (f.AssignedPeople == 0)
                    steps.Add(new("startlista", "Startlistan", PlanStepState.Todo, "Ingen är inlagd än, så det finns inget att kontrollera mot startlistan", new[] { "startlista" }));
                else if (f.Unlinked > 0)
                    steps.Add(new("startlista", "Startlistan", PlanStepState.Todo,
                        (who.Length > 0 ? who + ". " : "") + char.ToUpper(unlinkedText[0]) + unlinkedText[1..],
                        new[] { "koppla", "startlista" }));
                else if (f.PlacesMissing > 0)
                    steps.Add(new("startlista", "Startlistan", PlanStepState.Todo, $"{who} — men {f.PlacesMissing} {(f.PlacesMissing == 1 ? "plats" : "platser")} är inte fyllda, så kontrollen är inte klar", new[] { "startlista" }));
                else
                    steps.Add(new("startlista", "Startlistan", PlanStepState.Done, who, new[] { "startlista" }));
            }

            // 5. Kontrollera och skriv ut — när något behövs.
            if (f.HasNeeds)
            {
                var open = new List<string>();
                if (f.PlacesMissing > 0) open.Add($"{f.PlacesMissing} {(f.PlacesMissing == 1 ? "plats saknas" : "platser saknas")}");
                if (f.Clashes > 0) open.Add($"{f.Clashes} {(f.Clashes == 1 ? "krock" : "krockar")}");
                steps.Add(open.Count == 0
                    ? new("kontroll", "Kontrollera och skriv ut", PlanStepState.Done, "Allt är bemannat och inget krockar — klart att skriva ut", new[] { "skrivut" })
                    : new("kontroll", "Kontrollera och skriv ut", PlanStepState.Todo, string.Join(" · ", open), new[] { "skrivut" }));
            }
            return steps;
        }

        /// <summary>Det första steget som behöver göras, eller null när allt är klart. Bandets hopfällda rad pekar dit.</summary>
        public static PlanStep? Next(IReadOnlyList<PlanStep> steps) => steps.FirstOrDefault(s => s.State == PlanStepState.Todo);
    }
}
