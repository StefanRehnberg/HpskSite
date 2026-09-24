using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En nummerserie per utställare, år och slag.
    ///
    /// <para><b>En verifikationsserie per utställare och år, delad av alla slag</b> — inte en serie
    /// per transaktionstyp. Kvitton har däremot en <b>egen</b> serie: ett kvitto är en handling till
    /// betalaren, en verifikation är bokföringens post. De har olika mottagare och olika krav, och
    /// att slå ihop dem gör båda fel. Kvittonummer ≠ verifikationsnummer.</para>
    ///
    /// <para><b><see cref="Prefix"/> väljer föreningen själv</b> (Michaels svar på F1, 2026-09-16):
    /// en klubb som samtidigt kör ett annat bokföringsprogram kan låta alla verifikationer från
    /// pistol.nu börja med en egen bokstav, så att de två serierna kan samexistera utan att krocka.
    /// Det löser både samexistensen och uppdelningen, som en inställning i stället för en regel.</para>
    ///
    /// <para><b>⚠️ <see cref="NextNumber"/> rörs bara av <c>LedgerNumberAllocator</c>.</b> Den
    /// räknar upp med UPDLOCK i samma transaktion som verifikationen skrivs, eftersom luckfrihet är
    /// ett legalt krav och IDENTITY lämnar hål vid rollback. Följden att leva med: serien
    /// serialiserar bokföringen per utställare, så inget långsamt (mejl, filskrivning,
    /// Umbraco-publicering) får hållas öppet inuti den transaktionen.</para>
    /// </summary>
    [TableName("LedgerNumberSeries")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerNumberSeries
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        public int Year { get; set; }

        /// <summary>Ur <see cref="LedgerSeriesKind"/>.</summary>
        public string Kind { get; set; } = LedgerSeriesKind.JournalEntry;

        /// <summary>Föreningens egen prefixbokstav, t.ex. "V". Får vara tom.</summary>
        public string Prefix { get; set; } = "";

        /// <summary>Nästa nummer att dela ut. Räknas upp av allokatorn, aldrig för hand.</summary>
        public int NextNumber { get; set; } = 1;
    }

    /// <summary>
    /// Serieslagen. <b>Två serier, två syften</b> — se <see cref="LedgerNumberSeries"/>.
    /// Nycklarna skrivs till databasen; döp aldrig om en och återanvänd den aldrig till något annat.
    /// </summary>
    public static class LedgerSeriesKind
    {
        /// <summary>Verifikationer. Luckfri, lagkrav.</summary>
        public const string JournalEntry = "journal";

        /// <summary>
        /// Kvitton till betalaren. Egen obruten serie — beslutat 2026-09-17 med common sense:
        /// den är billig att ha och omöjlig att lägga till i efterhand. (Halva F1 är fortfarande
        /// obesvarad: om kvitton FORMELLT kräver en obruten serie eller om det räcker att varje
        /// kvitto pekar på sin verifikation. Vi bygger det dyrare alternativet.)
        /// </summary>
        public const string Receipt = "receipt";

        /// <summary>
        /// Fakturor (<see cref="LedgerCharge"/>) — luckfri serie per utställare och år, lagkrav.
        /// <para>⚠️ Egen serie, aldrig kvittots eller verifikationens. Ett nummer tilldelas vid
        /// UTFÄRDANDET: fakturan skapas och skickas i samma handling, så inget utkast kan lämna en
        /// lucka.</para>
        /// </summary>
        public const string Invoice = "invoice";
    }

    /// <summary>
    /// Var posten kommer ifrån. <b>Löst kopplat</b> — ingen foreign key, ingen kaskad: källan får
    /// försvinna, verifikationen står kvar.
    /// <para>⚠️ Nycklarna ligger i databasen. Lägg till nya, men döp aldrig om en befintlig.</para>
    ///
    /// <para><b>⚠️⚠️ FÖR <see cref="CompetitionRegistration"/>, <see cref="TeamFee"/> OCH
    /// <see cref="Event"/> ÄR <c>SourceId</c> TÄVLINGENS RESPEKTIVE HÄNDELSENS NOD-ID</b> — aldrig
    /// anmälans eller lagets. Tre saker vilar på det: översiktens panel 3 grupperar per källa,
    /// avprickningslistan (<c>ForSource</c>) läser per källa, och det automatiska projektet
    /// (<see cref="LedgerProjectSource.For"/>) nycklas på den. Ett anmälnings-id här hade gett ett
    /// projekt och en panelrad per skytt. Vem som betalade bär betalningsraden i sina egna fält.</para>
    /// </summary>
    public static class LedgerSourceType
    {
        public const string CompetitionRegistration = "competition-registration";
        public const string TeamFee = "team-fee";
        public const string Event = "event";
        public const string MembershipFee = "membership-fee";
        public const string RegionFee = "region-fee";

        /// <summary>Kassören skrev in den för hand — järnhandelskvittot.</summary>
        public const string Manual = "manual";

        /// <summary>Skapad ur en importerad kontoutdragsrad vid avstämning.</summary>
        public const string BankImport = "bank-import";

        /// <summary>Ingående balanser vid anslutning, eller årsskiftets överföring.</summary>
        public const string OpeningBalance = "opening-balance";

        /// <summary>
        /// Årets avskrivning på en tillgång i anläggningsregistret.
        /// <para>⚠️ <c>SourceId</c> är tillgångens id — det är den nyckeln "bokfört i år" härleds
        /// ur, så strängen får inte ändras.</para>
        /// </summary>
        public const string AssetDepreciation = "asset-depreciation";

        /// <summary>
        /// Utrangeringen av en tillgång: anskaffningsvärdet och det ackumulerade bort, resten
        /// som förlust. <c>SourceId</c> är tillgångens id.
        /// <para>⚠️ EGEN källtyp, aldrig <see cref="AssetDepreciation"/>. "Bokfört i år" summerar
        /// debet i avskrivningsposterna — med utrangeringen där hade minuskontots debet räknats
        /// som årets avskrivning.</para>
        /// </summary>
        public const string AssetDisposal = "asset-disposal";

        /// <summary>
        /// En utbetald utgift: ett utlägg eller en leverantörsfaktura.
        /// <para>⚠️ Verifikationen skrivs vid BETALNINGEN, inte vid registreringen — föreningen
        /// bokför enligt kontantmetoden. <c>SourceId</c> är utgiftens id.</para>
        /// </summary>
        public const string Expense = "expense";

        /// <summary>
        /// Betalningen av en faktura som arrangören ställt ut till en klubb över dess anmälningar.
        /// <para>⚠️ <c>SourceId</c> är TÄVLINGENS id, precis som för anmälningsavgiften — samma
        /// projekt, samma intäktsroll. Fakturan bär betalningsraden i <c>ChargeId</c>.</para>
        /// </summary>
        public const string CompetitionInvoice = "competition-invoice";

        /// <summary>
        /// En verifikation inläst ur föreningens tidigare program (SIE-import). Numret är
        /// originalets, i en egen serie (prefix I + serien).
        /// <para>Rättas som en handbokförd post — den har ingen källa hos oss som kan säga emot.</para>
        /// </summary>
        public const string SieImport = "sie-import";
    }
}
