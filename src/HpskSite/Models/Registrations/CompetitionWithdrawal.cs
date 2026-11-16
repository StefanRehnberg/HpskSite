using NPoco;

namespace HpskSite.Models.Registrations
{
    /// <summary>
    /// En avanmälan från en tävling och vad den betydde för pengarna (<c>dbo.CompetitionWithdrawal</c>,
    /// <c>Migrations/create-competition-withdrawal-table.sql</c>). Skrivs av
    /// <c>RegistrationWithdrawalService</c> — aldrig någon annanstans.
    ///
    /// <para><see cref="RegistrationId"/> är den BORTTAGNA anmälans id: anmälan finns inte längre, så raden bär
    /// själv namn, klubb och klasser. Tiderna är svensk tid (som tävlingstabellerna i övrigt).</para>
    /// </summary>
    [TableName("CompetitionWithdrawal")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionWithdrawal
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int RegistrationId { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";
        public string? ClubName { get; set; }
        public string? Classes { get; set; }
        public DateTime WithdrawnAt { get; set; }
        public int? WithdrawnByMemberId { get; set; }
        public string WithdrawnBy { get; set; } = WithdrawnByKinds.Self;
        public decimal PaidAmount { get; set; }
        public decimal ClaimedAmount { get; set; }
        public string PaymentModel { get; set; } = "legacy";
        public string? Note { get; set; }
        public string RefundStatus { get; set; } = RefundStatuses.None;
        public DateTime? RefundedAt { get; set; }
        public int? RefundedByMemberId { get; set; }
        public string? RefundNote { get; set; }
        public DateTime? OrganiserMailedAt { get; set; }

        /// <summary>Pengar att ta ställning till: bekräftat betalt plus det skytten angett som betalt.</summary>
        [Ignore] public decimal MoneyInvolved => PaidAmount + ClaimedAmount;
    }

    /// <summary>Lagrade strängar byts aldrig (CHECK-villkoret i tabellen).</summary>
    public static class RefundStatuses
    {
        public const string None = "None";
        public const string Pending = "Pending";
        public const string Done = "Done";
    }

    public static class WithdrawnByKinds
    {
        public const string Self = "self";
        public const string Staff = "staff";
    }
}
