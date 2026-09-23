using NPoco;

namespace HpskSite.Models
{
    /// <summary>Vem som ställer ut en avgift. Speglar kolumnen <c>IssuerType</c>.</summary>
    public static class MembershipFeeIssuer
    {
        /// <summary>Klubbens medlemsavgift till en medlem.</summary>
        public const int Club = 0;

        /// <summary>Kretsens avgift till en klubb.</summary>
        public const int Region = 1;
    }

    /// <summary>
    /// En rad på ett avgiftskrav — i dag bara kretsavgiftens: grundavgift, per medlem, ett handskrivet
    /// belopp eller ett tillägg (t.ex. en lagavgift).
    ///
    /// <para><b>⚠️ Kravets <c>Amount</c> är summan av raderna</b>, och räknas om av tjänsten vid varje
    /// ändring. Ett krav vars rader och belopp säger olika saker är ett krav ingen kan förklara — och
    /// det är beloppet klubben swishar.</para>
    /// </summary>
    [TableName("MembershipFeeChargeLine")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MembershipFeeChargeLine
    {
        public int Id { get; set; }
        public int ChargeId { get; set; }
        public int SortOrder { get; set; }

        /// <summary>Ur <see cref="MembershipFeeLineKind"/>.</summary>
        public string Kind { get; set; } = MembershipFeeLineKind.Extra;

        public string Description { get; set; } = "";

        /// <summary>Antalet — medlemmarna för en per-medlem-rad. Null för ett fast belopp.</summary>
        public decimal? Quantity { get; set; }

        public decimal? UnitPrice { get; set; }

        public decimal Amount { get; set; }

        public DateTime CreatedUtc { get; set; }

        public int CreatedByMemberId { get; set; }
    }

    public static class MembershipFeeLineKind
    {
        /// <summary>Kretsens grundavgift per klubb.</summary>
        public const string Base = "base";

        /// <summary>Pris × antal medlemmar.</summary>
        public const string PerMember = "per-member";

        /// <summary>Ett belopp kretsen skrivit för hand i stället för formeln.</summary>
        public const string Manual = "manual";

        /// <summary>Ett tillägg, t.ex. en lagavgift.</summary>
        public const string Extra = "extra";
    }
}
