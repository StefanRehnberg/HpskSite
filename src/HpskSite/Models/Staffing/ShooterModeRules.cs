using System;
using System.Collections.Generic;
using System.Linq;
using NPoco;

namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Planera dagen (Stefan 2026-10-26): <b>hur en funktionär som också skjuter förhåller sig till startlistan</b>. Arrangörerna gör olika; ett begrepp,
    /// ett förval per planering och undantag per person. Se <see cref="ShooterModeRules"/>.
    /// </summary>
    public static class ShooterMode
    {
        /// <summary>Hoppar in om det blir en lucka i ett lag; ingen fast starttid.</summary>
        public const string Reserve = "Reserve";
        /// <summary>Går en funktionärspatrull (tidigt eller sist) som också kontrollerar banan.</summary>
        public const string Patrol = "Patrol";
        /// <summary>Skjuter sin egen start först; avlöser sedan när hen har tid.</summary>
        public const string ShootFirst = "ShootFirst";
        /// <summary>Starttid och funktionärstid planeras ihop.</summary>
        public const string Coordinated = "Coordinated";

        public static readonly string[] All = { Coordinated, ShootFirst, Patrol, Reserve };
    }

    [TableName("PlanningShooterMode")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningShooterModeRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public int MemberId { get; set; }
        public string Mode { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public int UpdatedBy { get; set; }
    }

    public static class ShooterModeRules
    {
        public static bool IsValid(string? mode) => mode != null && ShooterMode.All.Contains(mode);

        /// <summary>Texterna arrangören väljer mellan: en rubrik och en mening var, i personens ord.</summary>
        public static (string Title, string Help) Describe(string mode) => mode switch
        {
            ShooterMode.Reserve => ("De hoppar in om det blir en lucka", "De som vill skjuta har ingen fast starttid. Blir det en lucka i ett skjutlag eller en patrull tar de den."),
            ShooterMode.Patrol => ("De går en egen patrull före tävlingen", "Funktionärerna skjuter en patrull tidigt (eller sist) och kontrollerar banan samtidigt. Sedan är de lediga att arbeta."),
            ShooterMode.ShootFirst => ("De skjuter sin runda först och avlöser sedan", "Starten ligger fast. När de är klara anmäler de att de har tid, och kan avlösa någon på en station."),
            _ => ("Vi planerar starttider och funktionärstid ihop", "Pistol.nu föreslår starttider och uppdrag som inte krockar — runt det ni låst. Det är det här verktyget är byggt för."),
        };

        /// <summary>Personens läge: det egna om det finns, annars planeringens förval, annars samordnat.</summary>
        public static string Effective(string? planningDefault, string? personOverride) =>
            IsValid(personOverride) ? personOverride! : IsValid(planningDefault) ? planningDefault! : ShooterMode.Coordinated;

        /// <summary>
        /// Ska personen få en starttid av generatorn (läge D)? Alla utom reserven — en reserv har ingen fast start och tar en lucka först när det
        /// blir en. Funktionärspatrullen får sin tid i patrullerna före (eller efter) de ordinarie.
        /// </summary>
        public static bool GetsFixedStart(string mode) => mode != ShooterMode.Reserve;

        /// <summary>
        /// Kan personen avlösa på en station efter sin start? Läget "skjuter först" är gjort för det; samordnat och patrull räknar redan
        /// funktionärstiden mot starten, så där finns inget separat avlösarläge.
        /// </summary>
        public static bool RelievesAfterStart(string mode) => mode == ShooterMode.ShootFirst;
    }
}
