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
        bool PatrolPlanAvailable, int StartClashes, int Clashes);

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
        public static List<PlanStep> Derive(PlanFacts f)
        {
            var steps = new List<PlanStep>();

            // 1. Roller och behov — alltid.
            if (!f.HasRoles)
                steps.Add(new("behov", "Vem behövs, och hur många?", PlanStepState.Todo, "Välj vilka roller ni behöver — Sekretariat, Servering, Stationsmarkör …", new[] { "roller" }));
            else if (!f.HasNeeds)
                steps.Add(new("behov", "Vem behövs, och hur många?", PlanStepState.Todo, "Rollerna är valda. Sätt hur många som behövs av varje.", new[] { "behov" }));
            else
                steps.Add(new("behov", "Vem behövs, och hur många?", PlanStepState.Done, $"{f.PlacesNeeded} {(f.PlacesNeeded == 1 ? "plats" : "platser")} behövs", new[] { "behov" }));

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
                    : new("fyll", "Fyll platserna", PlanStepState.Todo, $"{f.PlacesMissing} av {f.PlacesNeeded} {(f.PlacesNeeded == 1 ? "plats" : "platser")} saknar folk", actions));
            }

            // 4. Startlistan — bara fält, och bara för den som får bygga om den.
            if (f.PatrolPlanAvailable)
            {
                steps.Add(f.StartClashes > 0
                    ? new("startlista", "Startlistan", PlanStepState.Todo, $"{f.StartClashes} {(f.StartClashes == 1 ? "start krockar" : "starter krockar")} med ett uppdrag — bygg om startlistan runt funktionärerna", new[] { "startlista" })
                    : new("startlista", "Startlistan", PlanStepState.Done, "Ingen start krockar med ett uppdrag", new[] { "startlista" }));
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
