using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// En rad i ändringsloggen för medlemsavgiftens inställningar (expertgruppen 2026-11-26, K4). "Vem ändrade avgiften från 300 till 350?"
    /// Aktören (<see cref="ByMemberId"/>) räknas inte i gallringskartan, som i ClubMembershipTypeChange. Tiden är UTC (StoredTimeBasis).
    /// </summary>
    [TableName("ClubFeeChangeLog")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubFeeChangeLogEntry
    {
        public int Id { get; set; }
        public int ClubId { get; set; }

        /// <summary>Avgiftsåret. Null för det som gäller alla år (typer och förbund).</summary>
        public int? Year { get; set; }

        public string Entity { get; set; } = string.Empty;
        public string? EntityKey { get; set; }
        public string Action { get; set; } = ClubFeeChangeAction.Saved;
        public string Detail { get; set; } = string.Empty;
        public int? ByMemberId { get; set; }
        public string? Basis { get; set; }
        public DateTime ChangedUtc { get; set; }
    }

    public static class ClubFeeChangeEntity
    {
        public const string Category = "Category";
        public const string Part = "Part";
        public const string Option = "Option";
        public const string Type = "Type";
        public const string MemberOption = "MemberOption";
        public const string MembersType = "MembersType";
    }

    public static class ClubFeeChangeAction
    {
        public const string Saved = "Saved";
        public const string Deleted = "Deleted";
    }
}
