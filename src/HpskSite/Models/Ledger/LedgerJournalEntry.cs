using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En verifikation — bokföringens grundpost. Ett huvud med sina konteringsrader
    /// (<see cref="LedgerJournalEntryLine"/>), balanserad, numrerad och oföränderlig.
    ///
    /// <para><b>⚠️⚠️ RADEN KAN INTE ÄNDRAS ELLER RADERAS.</b> En rättelse är en NY verifikation med
    /// motsatt tecken och <see cref="CorrectsEntryId"/> satt — originalet rörs aldrig. Detta är
    /// egenskapen hela granskningsbarheten vilar på: kan siffrorna ändras efter att revisorn läst
    /// dem är revisionsberättelsen värdelös. Spärren ligger som <b>trigger i databasen</b>
    /// (<c>TR_LedgerJournalEntry_NoUpdateDelete</c>), inte som en konvention i koden — det var
    /// exakt felet i den gamla fakturamodellen, där fakturorna var Umbraco-noder och därmed
    /// redigerbara i backoffice när som helst.</para>
    ///
    /// <para><b>⚠️ Utställaren är ett (typ, id)-par</b> — <see cref="IssuerType"/> ur
    /// <see cref="DocumentOwnerType"/> (Club/Region) plus <see cref="IssuerId"/>. Aldrig ett ensamt
    /// <c>ClubId</c>: det felet har gjorts fyra gånger i det här repot och låser varje gång ute
    /// kretsen.</para>
    ///
    /// <para><b>Ursprunget är LÖST kopplat</b> (<see cref="SourceType"/> + <see cref="SourceId"/>):
    /// ingen foreign key, ingen kaskad. En tävling kan raderas; verifikationen står kvar. Bokföring
    /// överlever det den beskriver.</para>
    ///
    /// <para><b>Tre datum, med flit.</b> <see cref="AccountingDate"/> styr vilken period posten
    /// hamnar i, <see cref="EventDate"/> är när det faktiskt hände, och
    /// <see cref="RegisteredUtc"/> är systemtiden. <b>Numret följer registreringsordningen</b>, inte
    /// datumen — därför kan ingenting någonsin infogas mellan två befintliga nummer.</para>
    /// </summary>
    [TableName("LedgerJournalEntry")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerJournalEntry
    {
        /// <summary>Teknisk nyckel. <b>Inte</b> verifikationsnumret — det är <see cref="Number"/>.</summary>
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        /// <summary>Klubbens eller kretsens Umbraco-nod-id.</summary>
        public int IssuerId { get; set; }

        public int SeriesId { get; set; }

        /// <summary>
        /// Löpnumret inom serien. <b>Luckfritt</b> — tilldelas av räknaren i
        /// <c>LedgerNumberAllocator</c> i samma transaktion som insert:en, aldrig av IDENTITY
        /// (som lämnar hål vid rollback).
        /// </summary>
        public int Number { get; set; }

        public int FiscalYearId { get; set; }

        /// <summary>Bokföringsdatum — avgör period, och därmed om året är låst.</summary>
        public DateTime AccountingDate { get; set; }

        /// <summary>Händelsedatum. Får avvika från bokföringsdatumet.</summary>
        public DateTime EventDate { get; set; }

        /// <summary>Systemtid. Numret följer den här ordningen.</summary>
        public DateTime RegisteredUtc { get; set; }

        public string Description { get; set; } = "";

        /// <summary>Ur <see cref="DocumentOwnerType"/>, eller null när motparten är en medlem eller saknas.</summary>
        public int? CounterpartyType { get; set; }

        public int? CounterpartyId { get; set; }

        /// <summary>
        /// <b>Snapshot.</b> En medlem kan byta namn, gå ur eller raderas — verifikationen ska ändå
        /// kunna läsas om sju år.
        /// </summary>
        public string? CounterpartyName { get; set; }

        /// <summary>Ur <see cref="LedgerSourceType"/>. Löst kopplat, ingen FK.</summary>
        public string SourceType { get; set; } = LedgerSourceType.Manual;

        public int? SourceId { get; set; }

        /// <summary>Betalningen posten bokför, när den har en.</summary>
        public int? PaymentId { get; set; }

        /// <summary>
        /// Satt på en RÄTTELSE och pekar på verifikationen som rättas.
        /// <para>Kopplingen ligger här och inte bara i <see cref="LedgerAuditEvent"/>: den måste
        /// finnas i BOKFÖRINGEN, annars går en rättelse inte att skilja från originalet i
        /// efterhand.</para>
        /// </summary>
        public int? CorrectsEntryId { get; set; }

        public int CreatedByMemberId { get; set; }
    }

    /// <summary>
    /// En konteringsrad. Exakt en av <see cref="Debit"/> och <see cref="Credit"/> är större än noll —
    /// <b>aldrig negativa belopp</b>. Ett negativt belopp är samma sak som motsatt sida, och två
    /// sätt att uttrycka samma sak ger två sätt att summera fel.
    ///
    /// <para><b>⚠️⚠️ KONTOT LAGRAS SOM NUMMER OCH NAMN, INTE SOM EN REFERENS.</b> Det är det som gör
    /// kontoplanen ofarlig att välja fel: en förening kan döpa om, numrera om eller bygga om sin
    /// kontoplan utan att skriva om sin egen historik. Samma princip som betalningsuppgifterna
    /// skulle ha haft på den gamla fakturan, där bankgirot lästes live ur klubbnoden så att en
    /// ändring ändrade vad en gammal faktura visade.</para>
    /// </summary>
    [TableName("LedgerJournalEntryLine")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerJournalEntryLine
    {
        public int Id { get; set; }

        public int JournalEntryId { get; set; }

        public int LineNumber { get; set; }

        /// <summary>Fyrsiffrigt BAS-konto. <b>Snapshot</b>, inte en FK till <see cref="LedgerAccount"/>.</summary>
        public int AccountNumber { get; set; }

        /// <summary>Vad kontot hette när posten bokfördes. <b>Snapshot.</b></summary>
        public string AccountName { get; set; } = "";

        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        public string? Text { get; set; }

        /// <summary>
        /// Momssatsen raden avser, i procent (25, 12, 6, 0). Null = momsen berör inte raden.
        ///
        /// <para><b>MOMSEN BALANSERAR INTE HÄR — den har en egen rad.</b> En momspliktig försäljning
        /// bokförs som tre rader: bankkontot i debet, intäkten i kredit och utgående moms (rollen
        /// <c>vat-outgoing</c>) i kredit. Verifikationen går ihop på Debet/Kredit ensamma, precis
        /// som förut.</para>
        ///
        /// <para>Fälten här är alltså <b>metadata på källraden</b>: de behövs för att kvittot ska
        /// kunna skriva ut momssats och momsbelopp, och för att en momsrapport ska vara möjlig att
        /// bygga senare utan att räkna om någon bokföring. Att lagra dem binder oss inte till att
        /// bygga rapporten.</para>
        /// </summary>
        public decimal? VatRate { get; set; }

        /// <summary>Momsbeloppet som hör till raden. Se <see cref="VatRate"/>.</summary>
        public decimal? VatAmount { get; set; }

        /// <summary>
        /// Projektet raden hör till, eller null. Se <see cref="LedgerProject"/> för varför
        /// dimensionen finns.
        ///
        /// <para><b>⚠️ DIMENSIONEN SITTER PÅ RADEN, INTE PÅ VERIFIKATIONEN.</b> En och samma
        /// betalning kan höra till två projekt — en bankdragning som täcker både tävlingen och
        /// kansliet — och SIE lägger dimensioner på <c>#TRANS</c>, alltså på raden. På
        /// verifikationen hade det varit billigare och fel.</para>
        ///
        /// <para><b>Det här är vad rapporterna grupperar på</b>, inte namnet. Döper föreningen om
        /// "SSM 2025" till "SSM Fältskytte 2025" följer hela historiken med namnbytet, i stället
        /// för att projektet delas i två poster i sin egen resultatrapport.</para>
        /// </summary>
        public int? ProjectId { get; set; }

        /// <summary>
        /// Vad projektet hette när posten bokfördes. <b>Snapshot</b> — men av ett annat skäl än
        /// <see cref="AccountName"/>.
        ///
        /// <para>Kontonamnet är snapshot för att historiken inte ska skrivas om. Projektnamnet är
        /// det bara för <b>handlingar som redan lämnat systemet</b>: en utskriven
        /// verifikationslista, en SIE-fil, en bilaga till årsmötet. Den som läser om ett papper
        /// från 2025 ska se det som stod där då. Levande rapporter läser namnet ur
        /// <see cref="ProjectId"/>.</para>
        ///
        /// <para>Tom sträng när raden saknar projekt.</para>
        /// </summary>
        public string ProjectName { get; set; } = "";
    }
}
