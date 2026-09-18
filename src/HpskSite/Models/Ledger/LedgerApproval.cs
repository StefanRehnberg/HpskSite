using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Attest på en utbetalning: vem godkände, utöver den som verkställde.
    ///
    /// <para>Föreningens enklaste internkontroll, och det revisorn frågar efter i
    /// förvaltningsrevisionen. Systemet ska åtminstone REGISTRERA vem som godkände; om det ska
    /// blockera eller bara varna är en UX-fråga för utgiftssidan, inte en lagerfråga.</para>
    ///
    /// <para><b>⚠️ EGEN TABELL — och det är hela poängen.</b> Attesten sätts EFTER att verifikationen
    /// skrivits, medan verifikationen inte får ändras. Alternativet vore att låta triggern släppa
    /// igenom uppdatering av just två kolumner, och det är precis den sortens hål som växer: nästa
    /// gång någon behöver "bara ett litet fält till" finns undantaget redan där att luta sig mot.
    /// En egen rad kostar en join och håller spärren hel.</para>
    /// </summary>
    [TableName("LedgerApproval")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerApproval
    {
        public int Id { get; set; }

        public int JournalEntryId { get; set; }

        /// <summary>
        /// Den som godkände. <b>Bör inte vara samma person som</b>
        /// <see cref="LedgerJournalEntry.CreatedByMemberId"/> — den som godkänner ska inte vara den
        /// som verkställer.
        /// </summary>
        public int ApprovedByMemberId { get; set; }

        public DateTime ApprovedUtc { get; set; }

        public string? Note { get; set; }
    }
}
