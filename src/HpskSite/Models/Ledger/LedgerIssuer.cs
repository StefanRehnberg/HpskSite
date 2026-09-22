using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En <b>utställare</b> — det som verifikationer, kvitton och betalningar faktiskt hänger på.
    ///
    /// <para><b>⚠️⚠️ UTSTÄLLAREN ÄR INTE FÖRENINGEN.</b> Fram till 2026-09-22 var
    /// <c>IssuerId</c> nodens id, och då fanns bara en väg att nollställa en testklubb: radera
    /// rader som liggaren är byggd för att aldrig släppa ifrån sig. Nu är utställaren ett eget
    /// objekt som <i>pekar</i> på föreningen, och "börja om" är att skapa en NY — en
    /// <c>INSERT</c>, inte en <c>DELETE</c>. Ingen trigger behöver röras, och ingen endpoint kan
    /// förstöra riktig bokföring.</para>
    ///
    /// <para><b>⚠️ De LEVANDE utställarna har <c>Id = OwnerId</c>, med flit.</b> Därmed stämmer
    /// varje rad som skrevs före ändringen redan, och migreringen behövde aldrig röra
    /// <c>LedgerJournalEntry</c> eller <c>LedgerReceipt</c>. Lita inte på likheten i kod — den är
    /// ett migreringsknep, inte en regel. <b>Slå alltid upp utställaren.</b></para>
    ///
    /// <para>Sandlådor numreras från 1 000 000 ur <c>SQ_LedgerIssuerSandbox</c>. Högsta nodnummer
    /// är femsiffrigt, så rymderna kan inte kollidera — och ett id över en miljon syns dessutom
    /// direkt i en logg.</para>
    /// </summary>
    [TableName("LedgerIssuer")]
    [PrimaryKey("Id", AutoIncrement = false)]
    public class LedgerIssuer
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int OwnerType { get; set; }

        /// <summary>Föreningens NOD-id.</summary>
        public int OwnerId { get; set; }

        /// <summary>Ur <see cref="LedgerIssuerKind"/>.</summary>
        public string Kind { get; set; } = LedgerIssuerKind.Sandbox;

        /// <summary>Vad kassören själv kallar den. Null för den levande.</summary>
        public string? Label { get; set; }

        public DateTime CreatedUtc { get; set; }

        public int? CreatedByMemberId { get; set; }

        /// <summary>
        /// Satt när sandlådan kastats. <b>Övergiven, inte raderad</b> — verifikationerna i den
        /// står kvar och går att förklara om någon undrar.
        /// </summary>
        public DateTime? AbandonedUtc { get; set; }

        [Ignore]
        public bool IsSandbox => Kind == LedgerIssuerKind.Sandbox;

        [Ignore]
        public bool IsActive => AbandonedUtc is null;
    }

    /// <summary>
    /// Utställarens slag. <b>Text, inte boolean</b> — en tredje form ska kunna tillkomma utan att
    /// levande pengadata konverteras.
    /// </summary>
    public static class LedgerIssuerKind
    {
        /// <summary>Föreningens riktiga bokföring. Exakt en per förening, garanterat av ett
        /// filtrerat unikt index i databasen och inte av koden.</summary>
        public const string Live = "live";

        /// <summary>
        /// Ett test. Får kastas när som helst genom att en ny skapas.
        /// <para>⚠️ Allt som produceras här — kvitton, verifikationer, rapporter — <b>måste</b>
        /// bära en synlig märkning. Ett sandlådekvitto som ser äkta ut är en handling som ljuger.</para>
        /// </summary>
        public const string Sandbox = "sandbox";

        public static bool IsValid(string? kind) => kind == Live || kind == Sandbox;
    }
}
