using NPoco;

namespace HpskSite.Models
{
    /// <summary>Klubbens undre åldersgräns för en medlemstyp ("Senior från 21"). Inga rader för klubben = regeln är av.</summary>
    [TableName("ClubMembershipTypeAge")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubMembershipTypeAge
    {
        public int Id { get; set; }
        public int ClubId { get; set; }

        /// <summary>Samma nyckel som <see cref="ClubMembershipType.TypeKey"/>. Joina aldrig på namnet.</summary>
        public string TypeKey { get; set; } = string.Empty;

        public int FromAge { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>Kassören har valt "Behåll typen" för en medlem i en klubb. Hör i <c>MemberDataPurgeService.SubjectTables</c>.</summary>
    [TableName("ClubMemberAgeRuleOptOut")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubMemberAgeRuleOptOut
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public int MemberId { get; set; }

        /// <summary>Aktören (kassören) — inte ämnet, därför utanför gallringskartan.</summary>
        public int? ByMemberId { get; set; }

        public DateTime CreatedUtc { get; set; }
    }

    /// <summary>Lagrade strängar (CHECK-regel i databasen) — byt aldrig en befintlig.</summary>
    public static class MembershipTypeChangeAction
    {
        public const string Applied = "Applied";
        public const string Kept = "Kept";
        public const string Unlocked = "Unlocked";
        public const string Undone = "Undone";
    }

    /// <summary>Loggrad för ett typbyte/undantag. Hör i <c>MemberDataPurgeService.SubjectTables</c>.</summary>
    [TableName("ClubMembershipTypeChange")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubMembershipTypeChange
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public int MemberId { get; set; }
        public int Year { get; set; }
        public string Action { get; set; } = MembershipTypeChangeAction.Applied;
        public string? FromType { get; set; }
        public string? ToType { get; set; }
        public int? RuleFromAge { get; set; }

        /// <summary>Aktören (kassören), null = systemet. Utanför gallringskartan.</summary>
        public int? ByMemberId { get; set; }

        public DateTime ChangedUtc { get; set; }
    }
}
