using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.Models.Schedule
{
    /// <summary>
    /// One line of a competition's day programme — the things that belong on everybody's itinerary but
    /// have nowhere else to live: "Anmälan öppnar 08:00", "Upprop 09:15", "Lunch", "Prisutdelning 17:00".
    ///
    /// Start lists answer "when do I shoot" and StaffAssignment answers "when do I work"; this fills the
    /// gap between them so /mitt-schema reads like a programme rather than a sparse list. Edited by the
    /// organiser on /tavlingsplanering → Dagsprogram.
    /// </summary>
    [TableName("CompetitionAgendaItem")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionAgendaItem
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }

        /// <summary>The day this belongs to. Null = the competition's own date (single-day comps).</summary>
        public DateTime? ItemDate { get; set; }

        /// <summary>"HH:mm". Null = no fixed time ("under dagen").</summary>
        [MaxLength(10)]
        public string? StartTime { get; set; }
        [MaxLength(10)]
        public string? EndTime { get; set; }

        [Required]
        [MaxLength(200)]
        public string Title { get; set; } = "";

        [MaxLength(200)]
        public string? Location { get; set; }

        [MaxLength(500)]
        public string? Note { get; set; }

        /// <summary>All | Shooters | Staff — see <see cref="AgendaAudience"/>.</summary>
        [MaxLength(20)]
        public string Audience { get; set; } = AgendaAudience.All;

        /// <summary>Bootstrap icon class, e.g. "bi-trophy". Optional.</summary>
        [MaxLength(50)]
        public string? Icon { get; set; }

        public int SortOrder { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

    public static class AgendaAudience
    {
        public const string All = "All";
        public const string Shooters = "Shooters";
        public const string Staff = "Staff";

        public static string Label(string? value) => value switch
        {
            Shooters => "Endast skyttar",
            Staff => "Endast funktionärer",
            _ => "Alla",
        };
    }

    /// <summary>
    /// The starter programme offered as "Lägg till standardpunkter" — a competition day looks broadly
    /// the same everywhere, so an empty editor is a worse default than a list to prune.
    /// </summary>
    public static class AgendaTemplate
    {
        public record Row(string Title, string? StartTime, string? EndTime, string Audience, string Icon);

        public static readonly Row[] Default =
        {
            new("Anmälan/incheckning öppnar", "08:00", "09:00", AgendaAudience.All,      "bi-clipboard-check"),
            new("Funktionärssamling",         "08:15", null,    AgendaAudience.Staff,    "bi-people"),
            new("Upprop",                     "08:45", null,    AgendaAudience.All,      "bi-megaphone"),
            new("Första start",               "09:00", null,    AgendaAudience.All,      "bi-play-circle"),
            new("Lunch",                      "12:00", "13:00", AgendaAudience.All,      "bi-cup-hot"),
            new("Prisutdelning",              "16:00", null,    AgendaAudience.All,      "bi-trophy"),
            new("Städning och avrigg",        "16:30", null,    AgendaAudience.Staff,    "bi-box-seam"),
        };
    }

    // --- Request DTOs ---

    public class SaveAgendaItemRequest
    {
        public int Id { get; set; }              // 0 = create
        public int CompetitionId { get; set; }
        public string? ItemDate { get; set; }     // "yyyy-MM-dd" or null
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Title { get; set; } = "";
        public string? Location { get; set; }
        public string? Note { get; set; }
        public string? Audience { get; set; }
        public string? Icon { get; set; }
    }

    public class DeleteAgendaItemRequest
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
    }

    public class SeedAgendaRequest
    {
        public int CompetitionId { get; set; }
    }
}
