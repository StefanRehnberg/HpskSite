using NPoco;

namespace HpskSite.Models.CompetitionCalls
{
    /// <summary>
    /// Läget för en medlems önskemål om att bli tävlingsledare (klubbprogrammet F7). Lagrade strängar — byt aldrig en befintlig.
    /// </summary>
    public static class CompetitionManagerWishStatus
    {
        /// <summary>Medlemmen har önskat och inget är avgjort.</summary>
        public const string Aktiv = "Aktiv";
        /// <summary>Medlemmen utsågs. Rätten bor i rosterraden, inte här.</summary>
        public const string Utsedd = "Utsedd";
        /// <summary>Adminen stängde rollen utan att just den här medlemmen utsågs.</summary>
        public const string Avslutad = "Avslutad";
    }

    /// <summary>En medlems önskemål. En rad per (tävling, medlem). Hör i <c>MemberDataPurgeService.SubjectTables</c>.</summary>
    [TableName("CompetitionManagerWish")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionManagerWish
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int MemberId { get; set; }
        public string Status { get; set; } = CompetitionManagerWishStatus.Aktiv;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Adminen har stängt rollen för tävlingen ("Klart, tävlingsledare tillsatt").</summary>
    [TableName("CompetitionManagerClosure")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionManagerClosure
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public DateTime ClosedAt { get; set; }
        public int? ClosedByMemberId { get; set; }
    }
}
