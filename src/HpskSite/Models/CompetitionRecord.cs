using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Klubb- och kretsrekord. The cert-pattern (one row per record entry, IsCurrent
    /// flag, ReplacedByRecordId chain) is reused so we get cheap "current state" reads
    /// AND full history for free.
    /// </summary>
    public static class RecordLevels
    {
        public const string Club = "Club";
        public const string Region = "Region";
    }

    public static class RecordDisciplines
    {
        public const string Precision = "Precision";
        public const string MagnumPrecision = "MagnumPrecision";
        public const string Milsnabb = "Milsnabb";

        public static string DisplayName(string discipline) => discipline switch
        {
            Precision => "Precisionsskjutning",
            MagnumPrecision => "Magnumprecision",
            Milsnabb => "Militär snabbmatch",
            _ => discipline
        };
    }

    public static class RecordTypes
    {
        public const string Individual = "Individual";
        public const string Team = "Team";

        public static string DisplayName(string recordType) => recordType switch
        {
            Individual => "Individuell",
            Team => "Lag",
            _ => recordType
        };
    }

    [TableName("CompetitionRecords")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionRecord
    {
        public int Id { get; set; }

        /// <summary>'Club' or 'Region'.</summary>
        public string Level { get; set; } = "";

        /// <summary>clubId as string for Club records; regionCode for Region records.</summary>
        public string ScopeId { get; set; } = "";

        /// <summary>'Precision', 'MagnumPrecision', 'Milsnabb'.</summary>
        public string Discipline { get; set; } = "";

        /// <summary>'Individual' or 'Team'.</summary>
        public string RecordType { get; set; } = "";

        /// <summary>Discipline+RecordType-specific class code; see RecordClassRegistry.</summary>
        public string ClassCode { get; set; } = "";

        public int TotalScore { get; set; }

        /// <summary>Series count audit field — derived from Discipline+RecordType at write time.</summary>
        public int SeriesCount { get; set; }

        public DateTime RecordDate { get; set; }

        /// <summary>Free-text competition name. Not an FK to internal competition nodes.</summary>
        public string? CompetitionName { get; set; }

        /// <summary>Optional link to a member in our system. Nullable for external/historical names.</summary>
        public int? HolderMemberId { get; set; }

        /// <summary>Always populated for display; for team records this is the team name.</summary>
        public string HolderName { get; set; } = "";

        /// <summary>Team records only — the team name. (Same as HolderName for teams; kept separate for clarity.)</summary>
        public string? TeamName { get; set; }

        /// <summary>JSON list [{ memberId?: int, name: string }] for team members.</summary>
        public string? TeamMembersJson { get; set; }

        public string? Notes { get; set; }

        public bool IsCurrent { get; set; }

        /// <summary>When this record was beaten, points to the new current record.</summary>
        public int? ReplacedByRecordId { get; set; }

        public int EnteredByMemberId { get; set; }

        public DateTime EnteredAt { get; set; }
    }
}
