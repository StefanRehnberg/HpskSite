using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En betalning — <b>källdokumentet</b>. Verifikationen ligger under den.
    ///
    /// <para><b>⚠️⚠️ TVÅ STEG SOM ALDRIG FÅR SLÅS IHOP.</b> <see cref="ClaimedUtc"/> är BETALARENS
    /// påstående ("jag har swishat"); <see cref="ConfirmedUtc"/> är ARRANGÖRENS mottagande. Bara det
    /// andra är pengar. Vi har ingen Swish-API och ingen callback, så betalningen är <b>antagen</b>,
    /// aldrig verifierad — och en betalare får därför aldrig kunna sätta "mottagen".</para>
    ///
    /// <para>Det var fel 3 och 9 i den gamla fakturamodellen: ett enda strängfält bar
    /// dokumenttillstånd, betaltillstånd och betalarens påstående samtidigt, och kaskaden skrev
    /// "Paid" på barnfakturor för pengar som aldrig kom från de skyttarna.</para>
    ///
    /// <para><b>Saldo lagras aldrig.</b> Det räknas fram som dokument minus betalningar, varje gång.
    /// Finns det ett lagrat tillstånd att glömma uppdatera, blir det glömt.</para>
    /// </summary>
    [TableName("LedgerPayment")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerPayment
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Ur <see cref="LedgerSourceType"/>. Löst kopplat — källan får försvinna.</summary>
        public string SourceType { get; set; } = LedgerSourceType.Manual;

        public int? SourceId { get; set; }

        public int? PayerMemberId { get; set; }

        /// <summary><b>Snapshot.</b> En medlem kan byta namn eller raderas.</summary>
        public string PayerName { get; set; } = "";

        public decimal Amount { get; set; }

        /// <summary>Ur <see cref="LedgerPaymentMethod"/>.</summary>
        public string Method { get; set; } = LedgerPaymentMethod.Swish;

        /// <summary><b>Betalarens påstående.</b> Aldrig pengar.</summary>
        public DateTime? ClaimedUtc { get; set; }

        public int? ClaimedByMemberId { get; set; }

        /// <summary><b>Arrangörens mottagande.</b> Det här ÄR pengar.</summary>
        public DateTime? ConfirmedUtc { get; set; }

        public int? ConfirmedByMemberId { get; set; }

        /// <summary>
        /// Det belopp arrangören faktiskt tog emot, när det skiljer sig från <see cref="Amount"/>.
        /// <para>⚠️ <c>null</c> betyder <b>ingen avvikelse</b> — inte noll.</para>
        /// </summary>
        public decimal? ActualAmount { get; set; }

        public int? JournalEntryId { get; set; }

        public int? ReceiptId { get; set; }

        public DateTime? VoidedUtc { get; set; }
        public int? VoidedByMemberId { get; set; }
        public string? VoidReason { get; set; }

        public DateTime CreatedUtc { get; set; }

        // ── Tävlingsavgifterna (P3/P4, 2026-09-24) ───────────────────────────────────────────
        //
        // ⚠️⚠️ SourceId FÖRBLIR TÄVLINGENS ID. Anmälan eller laget bär SourceItemId — tre saker
        //    vilar på att SourceId är tävlingen (projektet, översiktens panel 3, avprickningen).

        /// <summary>
        /// Anmälans id (<see cref="LedgerSourceType.CompetitionRegistration"/>) eller lagets id
        /// (<see cref="LedgerSourceType.TeamFee"/>). Null för allt annat.
        /// </summary>
        public int? SourceItemId { get; set; }

        /// <summary>Ur <see cref="HpskSite.Models.CompetitionFees.CompetitionFeePart"/>: vilken del av anmälans avgift raden gäller.</summary>
        public string? FeePart { get; set; }

        /// <summary>
        /// Anmälans klubb, <b>snapshot</b> — nyckeln när arrangören fakturerar en klubb i efterhand.
        /// <para>⚠️ Anmälans klubb, aldrig medlemmens <c>primaryClubId</c>: en skytt kan tävla för en
        /// annan av sina klubbar, och då ska avgiften inte hamna på fel klubbs räkning.</para>
        /// </summary>
        public int? PayerClubId { get; set; }

        /// <summary>
        /// Satt på en <b>begärd</b> avgift som arrangören buntat in i en faktura. Raden makuleras i
        /// samma handling — så varje befintlig läsare ser den som ersatt utan att känna till
        /// fakturorna. Det är fakturan som nu ska betalas.
        /// </summary>
        public int? CoveredByChargeId { get; set; }

        /// <summary>Satt på <b>betalningen av en faktura</b> (<see cref="LedgerSourceType.CompetitionInvoice"/>).</summary>
        public int? ChargeId { get; set; }

        /// <summary>Mottagen av arrangören och inte makulerad. Det enda som räknas som pengar.</summary>
        [Ignore]
        public bool IsMoney => ConfirmedUtc is not null && VoidedUtc is null;

        /// <summary>Betalaren har sagt att hen betalat, men arrangören har inte bekräftat.</summary>
        [Ignore]
        public bool IsClaimedOnly => ClaimedUtc is not null && ConfirmedUtc is null && VoidedUtc is null;

        /// <summary>Beloppet som ska bokföras: det mottagna om det avviker, annars det begärda.</summary>
        [Ignore]
        public decimal SettledAmount => ActualAmount ?? Amount;
    }

    /// <summary>
    /// Betalsätt. Nycklarna ligger i databasen — lägg till, döp aldrig om.
    /// <para>⚠️ Varje betalsätt måste kunna mappas till en kontoroll, annars går betalningen inte
    /// att bokföra. <see cref="LedgerPaymentMethod.RoleFor"/> är den enda uppslagningen.</para>
    /// </summary>
    public static class LedgerPaymentMethod
    {
        public const string Swish = "swish";
        public const string BankGiro = "bankgiro";
        public const string Cash = "kontant";
        public const string Card = "kort";
        public const string Other = "annat";

        public static readonly string[] All = { Swish, BankGiro, Cash, Card, Other };

        /// <summary>
        /// Kontorollen pengarna landar på.
        /// <para>Kort och "annat" går till bankkontot: pengarna hamnar där oavsett, och ett eget
        /// konto för dem är något föreningen får lägga upp själv om den vill skilja dem ut.</para>
        /// </summary>
        public static string RoleFor(string method) => method switch
        {
            Swish => LedgerAccountRoles.Swish,
            Cash => LedgerAccountRoles.CashBox,
            _ => LedgerAccountRoles.BankAccount
        };

        public static bool IsValid(string method) => All.Contains(method);

        /// <summary>
        /// Betalsättet i klartext, för en handling en människa läser.
        /// <para>⚠️ Nycklarna ligger i databasen och får aldrig döpas om — därför översätts de här
        /// i stället för att lagras läsbara.</para>
        /// </summary>
        public static string LabelFor(string method) => method switch
        {
            Swish => "Swish",
            BankGiro => "Bankgiro",
            Cash => "Kontant",
            Card => "Kort",
            _ => "Annat"
        };
    }

    /// <summary>
    /// Ett kvitto — en <b>utfärdad handling</b> till betalaren.
    ///
    /// <para><b>⚠️⚠️ UTFÄRDAS VID ARRANGÖRENS BEKRÄFTELSE, ALDRIG VID QR-VISNING.</b> Ett kvitto på
    /// en betalning som aldrig kom är en handling som ljuger, och den ligger då hos betalaren som
    /// bevis på något som inte hänt.</para>
    ///
    /// <para><b>⚠️⚠️ ALLT OM UTSTÄLLAREN ÄR SNAPSHOTTAT.</b> Det var fel 10: bankgirot lästes live
    /// ur klubbnoden vid rendering, så en ändrad uppgift ändrade vad ett GAMMALT kvitto visade.</para>
    ///
    /// <para><b>⚠️⚠️ Momspåståendet är DATA, inte en textrad i koden.</b> Det gamla kvittot skrev
    /// hårdkodat att föreningen inte är momsregistrerad — på allas handlingar (fel 8). Här kommer
    /// det ur <see cref="LedgerIssuerSettings"/> vid utfärdandet och fryses.</para>
    ///
    /// <para>Kvittot kan inte ändras eller raderas (<c>TR_LedgerReceipt_NoUpdateDelete</c>). En
    /// felaktig betalning makuleras och rättas med en motbokning; kvittot rörs aldrig.</para>
    /// </summary>
    [TableName("LedgerReceipt")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerReceipt
    {
        public int Id { get; set; }

        public int IssuerType { get; set; }
        public int IssuerId { get; set; }

        /// <summary>Kvittoserien — <b>skild från verifikationsserien</b>. Två serier, två syften.</summary>
        public int SeriesId { get; set; }

        public int Number { get; set; }

        public int PaymentId { get; set; }

        public DateTime IssuedUtc { get; set; }
        public int IssuedByMemberId { get; set; }

        public string IssuerName { get; set; } = "";
        public string? IssuerOrgNumber { get; set; }
        public string? IssuerAddress { get; set; }
        public string? IssuerEmail { get; set; }

        /// <summary>Swishnummer eller bankgiro <b>som det var när kvittot skrevs</b>.</summary>
        public string? PaymentDetails { get; set; }

        public bool IssuerIsVatRegistered { get; set; }
        public string? IssuerVatNumber { get; set; }

        /// <summary><c>null</c> = momsen berör inte kvittot.</summary>
        public decimal? VatAmount { get; set; }

        public string Description { get; set; } = "";

        public decimal Amount { get; set; }
    }
}
