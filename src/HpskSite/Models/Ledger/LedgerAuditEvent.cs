using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ändringsloggen. Append-only: vem gjorde vad, när.
    ///
    /// <para><b>⚠️ Loggar också AVVISADE försök.</b> Ett avvisat försök att ändra en bokförd rad är
    /// mer intressant än ett lyckat, inte mindre — det är precis vad en revisor vill se att spärren
    /// höll för.</para>
    ///
    /// <para><b>Loggen ersätter inte <see cref="LedgerJournalEntry.CorrectsEntryId"/>.</b> Kopplingen
    /// mellan en rättelse och sitt original måste finnas i bokföringen, inte bara i en logg vid
    /// sidan om.</para>
    /// </summary>
    [TableName("LedgerAuditEvent")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerAuditEvent
    {
        public int Id { get; set; }

        public DateTime OccurredUtc { get; set; }

        /// <summary>0 när handlingen kom från ett bakgrundsjobb och inte från en människa.</summary>
        public int MemberId { get; set; }

        /// <summary>Nyckel ur <see cref="LedgerAuditAction"/>.</summary>
        public string Action { get; set; } = "";

        /// <summary>Tabellnamnet utan <c>Ledger</c>-prefix, t.ex. "JournalEntry".</summary>
        public string ObjectType { get; set; } = "";

        public int ObjectId { get; set; }

        /// <summary>Fritt JSON. Det som behövs för att förstå raden om fem år.</summary>
        public string? Detail { get; set; }
    }

    /// <summary>
    /// Vad som hände. Nycklarna ligger i databasen — lägg till, döp aldrig om.
    /// </summary>
    public static class LedgerAuditAction
    {
        public const string Posted = "posted";
        public const string Corrected = "corrected";
        public const string Approved = "approved";
        public const string YearEstablished = "year-established";
        public const string Imported = "imported";
        public const string AttachmentAdded = "attachment-added";

        /// <summary>
        /// Kassören intygade att föreningen INTE hade några ingående balanser (allt noll) —
        /// ObjectType "FiscalYear", ObjectId = året. Se <c>LedgerOpeningBalanceService.Save</c>.
        /// </summary>
        public const string OpeningBalancesNone = "opening-balances-none";

        /// <summary>Databasen vägrade en ändring eller radering. Se klassens varning.</summary>
        public const string Rejected = "rejected";
    }
}
