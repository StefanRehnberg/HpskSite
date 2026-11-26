using NPoco;

namespace HpskSite.Models
{
    /// <summary>Klubbens namn på en medlemstyp. Lat: ingen rad = standardnamnet, aktiv.</summary>
    [TableName("ClubMembershipType")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubMembershipType
    {
        public int Id { get; set; }
        public int ClubId { get; set; }

        /// <summary>Fast nyckel: en av <see cref="MembershipTypeKeys"/>, eller "x-…" för en egen typ.</summary>
        public string TypeKey { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    /// <summary>Ett tillval klubben erbjuder (Aktiv i SPSF, Sportskytte …).</summary>
    [TableName("ClubFeeOption")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubFeeOption
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>Förbundets officiella namn när tillvalet är ett förbund, annars null.</summary>
        public string? SystemKey { get; set; }

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>Ett tillval som kassören bockat i på en klubbmedlem.</summary>
    [TableName("ClubMembershipOption")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubMembershipOption
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public int OptionId { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>"Gäller när" för ett tillägg/avdrag. Lagrade strängar byts aldrig (CHECK-regel i databasen).</summary>
    public static class MembershipFeePartCondition
    {
        public const string Always = "Always";
        public const string PrimaryClub = "PrimaryClub";
        public const string NotPrimaryClub = "NotPrimaryClub";
        public const string Option = "Option";
    }

    /// <summary>Ett tillägg (belopp &gt; 0) eller avdrag (&lt; 0) per klubb och år. Typerna ligger i <see cref="MembershipFeePartType"/>; inga rader = alla.</summary>
    [TableName("MembershipFeePart")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MembershipFeePart
    {
        public int Id { get; set; }
        public int ClubId { get; set; }
        public int Year { get; set; }
        public string Label { get; set; } = string.Empty;

        /// <summary>Belopp med tecken.</summary>
        public decimal Amount { get; set; }

        public string Condition { get; set; } = MembershipFeePartCondition.Always;

        /// <summary>Bara när <see cref="Condition"/> är Option.</summary>
        public int? OptionId { get; set; }

        public int SortOrder { get; set; }
        public DateTime CreatedDate { get; set; }

        /// <summary>Tillvalets namn när villkoret är Option. Fylls av <c>MembershipFeeService.LoadParts</c>, lagras inte.</summary>
        [Ignore] public string? OptionName { get; set; }

        /// <summary>
        /// Texten på avgiftsraden och i mejlet. Ett tillägg som gäller ett tillval får tillvalets namn med
        /// ("Förbundsavgift – Sportskytte"), annars säger flera rader samma sak. Nämner etiketten redan förbundet ("Svartkrut" för "Svenska Svartkruts SkytteFederationen", eller tvärtom) lämnas den.
        /// </summary>
        [Ignore]
        public string DisplayLabel =>
            Condition == MembershipFeePartCondition.Option && !string.IsNullOrWhiteSpace(OptionName)
            && Label.IndexOf(OptionName, StringComparison.OrdinalIgnoreCase) < 0
            && OptionName.IndexOf(Label, StringComparison.OrdinalIgnoreCase) < 0
                ? $"{Label} – {OptionName}"
                : Label;
    }

    [TableName("MembershipFeePartType")]
    [PrimaryKey("PartId,TypeKey", AutoIncrement = false)]
    public class MembershipFeePartType
    {
        public int PartId { get; set; }
        public string TypeKey { get; set; } = string.Empty;
    }
}
