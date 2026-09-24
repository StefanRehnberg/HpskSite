using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En utfärdad faktura — eller kreditnota — från en förening till en klubb.
    ///
    /// <para><b>⚠️⚠️ FAKTURAN BUNTAR, DEN SKAPAR INGEN INTÄKT.</b> Arrangören väljer en klubbs öppna
    /// avgifter (begärda <see cref="LedgerPayment"/>-rader) och ställer ut EN faktura över dem.
    /// Avgiftsraderna makuleras i samma handling och pekar hit med
    /// <see cref="LedgerPayment.CoveredByChargeId"/>. Föreningen bokför enligt kontantmetoden, så
    /// intäkten uppstår först när fakturan <b>betalas</b> — en rad med
    /// <see cref="LedgerPayment.ChargeId"/>. Det finns alltså aldrig två saker som räknas som samma
    /// pengar, och ingen regel behöver undanta något ur summor. Det var fel 2, 4, 5 och 6 i den
    /// gamla samlingsfakturan.</para>
    ///
    /// <para><b>⚠️⚠️ EN HANDLING.</b> Utfärdad = oföränderlig (<c>TR_LedgerCharge_Immutable</c>):
    /// bara utskicket och makuleringen får skrivas efteråt. En felaktig faktura krediteras.</para>
    ///
    /// <para><b>⚠️ Saldo lagras aldrig.</b> Det är fakturans summa, minus dess kreditnotor, minus
    /// mottagna betalningar — räknat varje gång (<see cref="LedgerChargeBalance"/>).</para>
    ///
    /// <para><b>Fakturanummer ≠ referens</b> (Stefans krav 2026-08-07). Numret är lagens luckfria
    /// serie; referensen är det alla söker på och skriver på inbetalningen.</para>
    /// </summary>
    [TableName("LedgerCharge")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerCharge
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }

        /// <summary>Ur <see cref="LedgerChargeKind"/>.</summary>
        public string Kind { get; set; } = LedgerChargeKind.Invoice;

        /// <summary>Fakturan en kreditnota krediterar. Exakt en.</summary>
        public int? CreditsChargeId { get; set; }

        /// <summary>Mottagarens typ (<see cref="DocumentOwnerType"/>). I dag alltid en klubb.</summary>
        public int RecipientType { get; set; }
        public int RecipientId { get; set; }

        /// <summary><b>Snapshot.</b></summary>
        public string RecipientName { get; set; } = "";
        public string? RecipientEmail { get; set; }

        /// <summary>Ur <see cref="LedgerSourceType"/>; för tävlingsavgifter alltid tävlingen.</summary>
        public string SourceType { get; set; } = LedgerSourceType.CompetitionInvoice;
        public int SourceId { get; set; }

        /// <summary><b>Snapshot</b> av tävlingens namn.</summary>
        public string SourceName { get; set; } = "";

        public int SeriesId { get; set; }
        public int Number { get; set; }

        /// <summary>Numret som det skrivs, med föreningens prefix ("F-12").</summary>
        public string NumberText { get; set; } = "";

        public string Reference { get; set; } = "";

        public DateTime IssueDate { get; set; }
        public DateTime? DueDate { get; set; }

        /// <summary>Dokumentets summa. <b>Negativ för en kreditnota.</b></summary>
        public decimal Amount { get; set; }

        // ⚠️ SNAPSHOT — ändras klubbens bankgiro i morgon ska fakturan visa det som gällde i dag.
        public string IssuerName { get; set; } = "";
        public string? IssuerOrgNumber { get; set; }
        public string? IssuerAddress { get; set; }
        public string? IssuerEmail { get; set; }
        public string? IssuerBankgiro { get; set; }
        public string? IssuerSwish { get; set; }
        public bool IssuerIsVatRegistered { get; set; }

        public string? Note { get; set; }

        public DateTime CreatedUtc { get; set; }
        public int CreatedByMemberId { get; set; }

        public DateTime? SentUtc { get; set; }
        public string? SentToEmail { get; set; }

        public DateTime? VoidedUtc { get; set; }
        public int? VoidedByMemberId { get; set; }
        public string? VoidReason { get; set; }

        [Ignore]
        public bool IsCredit => Kind == LedgerChargeKind.Credit;

        [Ignore]
        public bool IsVoided => VoidedUtc is not null;

        [Ignore]
        public List<LedgerChargeLine> Lines { get; set; } = new();
    }

    /// <summary>En rad på en faktura. Pekar på den avgift den buntade.</summary>
    [TableName("LedgerChargeLine")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerChargeLine
    {
        public int Id { get; set; }
        public int ChargeId { get; set; }

        /// <summary>Den begärda avgiftsraden som buntades in. Null på en fri rad.</summary>
        public int? PaymentId { get; set; }

        /// <summary>Kopia av avgiftens <see cref="LedgerSourceType"/> — anmälan eller lag.</summary>
        public string? ItemType { get; set; }

        /// <summary>Anmälans eller lagets id.</summary>
        public int? ItemId { get; set; }

        public string Description { get; set; } = "";

        /// <summary>Negativ på en kreditnota.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>Nycklarna ligger i databasen — döp aldrig om.</summary>
    public static class LedgerChargeKind
    {
        public const string Invoice = "invoice";
        public const string Credit = "credit";
    }

    /// <summary>
    /// En fakturas saldo — <b>härlett, aldrig lagrat.</b>
    /// <para>Ren funktion så att reglerna kan prövas utan databas.</para>
    /// </summary>
    public readonly record struct LedgerChargeBalance(
        decimal Invoiced, decimal Credited, decimal Paid, decimal Claimed)
    {
        /// <summary>Kvar att betala. Aldrig negativt — en överbetalning är en egen fråga.</summary>
        public decimal Outstanding => Math.Max(0m, Invoiced + Credited - Paid);

        /// <summary>Betalt mer än fakturans nettobelopp.</summary>
        public decimal Overpaid => Math.Max(0m, Paid - (Invoiced + Credited));

        public decimal Net => Invoiced + Credited;

        public bool IsSettled => Net <= Paid;

        /// <summary>
        /// Räknar saldot. <paramref name="credits"/> är kreditnotornas (negativa) summor;
        /// <paramref name="payments"/> är fakturans betalningsrader.
        /// </summary>
        public static LedgerChargeBalance For(
            LedgerCharge invoice, IEnumerable<LedgerCharge> credits, IEnumerable<LedgerPayment> payments)
        {
            if (invoice.IsVoided) return new LedgerChargeBalance(0m, 0m, 0m, 0m);

            var credited = credits.Where(c => !c.IsVoided && c.CreditsChargeId == invoice.Id).Sum(c => c.Amount);
            var rows = payments.Where(p => p.ChargeId == invoice.Id && p.VoidedUtc is null).ToList();
            var paid = rows.Where(p => p.IsMoney).Sum(p => p.SettledAmount);
            var claimed = rows.Where(p => p.IsClaimedOnly).Sum(p => p.Amount);

            return new LedgerChargeBalance(invoice.Amount, credited, paid, claimed);
        }
    }
}
