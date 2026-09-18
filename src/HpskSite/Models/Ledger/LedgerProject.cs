using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Ett projekt — den andra etiketten på en konteringsrad, vid sidan av kontot.
    ///
    /// <para><b>Kontot svarar på VAD pengarna var</b> ("Priser och medaljer").
    /// <b>Projektet svarar på VAD DE HÖRDE TILL</b> ("SSM 2025"). Med båda blir "vad kostade SSM"
    /// en summering på projektet och "vad lade vi på priser i år" en summering på kontot — samma
    /// rader, två frågor.</para>
    ///
    /// <para><b>⚠️ Utan den här dimensionen finns bara en utväg, och den är fel:</b> att göra
    /// tävlingen till ett konto. Det är vad Hallands Pistolskyttekrets tvingades göra 2025, och det
    /// fungerar precis ett år. År två behövs SSM-26, år tre SSM-27, och då går
    /// "anmälningsavgifter 2025 jämfört med 2026" inte att få fram — de ligger i olika konton.
    /// Kontoplanen växer med verksamheten i stället för att beskriva den.</para>
    ///
    /// <para><b>⚠️ Heter PROJEKT, inte kostnadsställe</b> (beslut 2026-09-18). Skillnaden i
    /// redovisning går på tid: ett kostnadsställe är en bestående del av organisationen, ett
    /// projekt är tidsbegränsat och vill ha ett slutresultat för hela företaget — inte per år.
    /// SSM 2025 är per definition det andra, så det begripligaste ordet råkar också vara det mest
    /// korrekta. Det som verkligen ÄR kostnadsställen (Klubbstugan, Skjutbanan, Ungdomssektionen)
    /// läggs som projekt utan slutdatum. <b>En dimension, aldrig två</b> — en andra fördubblar
    /// fältet i varje bokföringsbild för ett värde nästan ingen förening behöver.</para>
    ///
    /// <para>Fältet är <b>aldrig obligatoriskt</b>. En förening som inte bryr sig ska kunna låta
    /// det stå tomt hela året utan att något klagar.</para>
    /// </summary>
    [TableName("LedgerProject")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerProject
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>
        /// Föreningens eget ord: "SSM 2025", "Klubbstugan", "Ungdomssektionen". Unikt per
        /// utställare — två projekt med samma namn är en dubblett som delar rapporten i två.
        /// </summary>
        public string Name { get; set; } = "";

        public string? Description { get; set; }

        /// <summary>Får vara null. Ett projekt utan datum är hur ett kostnadsställe uttrycks här.</summary>
        public DateTime? StartDate { get; set; }

        public DateTime? EndDate { get; set; }

        /// <summary>
        /// Stängt = visas inte i väljaren när någon bokför. Rapporterna läser det ändå.
        ///
        /// <para><b>⚠️ Två tillstånd räcker här, till skillnad från
        /// <see cref="LedgerFiscalYear.Status"/> som har tre.</b> Årets mellanläge finns för att
        /// bokslutsposterna måste kunna skrivas i ett år som är stängt för löpande bokföring. Ett
        /// projekt har inget motsvarande behov — det finns ingen post som bara får skrivas på ett
        /// avslutat projekt.</para>
        /// </summary>
        public bool IsClosed { get; set; }

        /// <summary>
        /// Vad projektet handlar om, löst kopplat med samma mönster som
        /// <see cref="LedgerJournalEntry.SourceType"/>. Satt för ett projekt som skapats ur en
        /// tävling.
        ///
        /// <para><b>Det är den här kopplingen som gör projektmärkningen gratis där den betyder
        /// mest:</b> de hundratals automatiska anmälnings- och kioskraderna vet redan vilken
        /// tävling de hör till, så ingen människa behöver välja något. Kvar för handpåläggning är
        /// bara kassörens egna rader — tavlorna, gravyren, smörgåstårtan.</para>
        /// </summary>
        public string? SourceType { get; set; }

        public int? SourceId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public int CreatedByMemberId { get; set; }
    }
}
