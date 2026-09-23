using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En utgift föreningen har åtagit sig: ett utlägg någon lagt ut för, eller en
    /// leverantörsfaktura som kommit in.
    ///
    /// <para><b>⚠️⚠️ UTLÄGG OCH LEVERANTÖRSFAKTURA ÄR SAMMA FORM, inte två funktioner.</b> Båda är
    /// "åtagandet uppstod före pengarna" — exakt den andra betalningsformen i den låsta ramen. Det
    /// som skiljer är MOTPARTEN (en medlem som ska ha tillbaka sina pengar, eller ett företag med
    /// en förfallodag), och det är ett fält. Byggda som två tabeller hade attest, kvitto, betalning
    /// och bokslutssteg funnits i två exemplar, fria att glida isär.</para>
    ///
    /// <para><b>⚠️⚠️ KONTANTMETODEN: ATT REGISTRERA EN UTGIFT BOKFÖR INGENTING.</b> Föreningen
    /// bokför enligt kontantmetoden (beslutat 2026-09-16, se <c>payment-two-shapes-not-two-payers</c>),
    /// så verifikationen skrivs när pengarna FAKTISKT betalas — inte när fakturan kommer. Raden här
    /// är alltså arbetslistan och beslutsunderlaget, inte bokföringen. En utgift som fortfarande är
    /// obetald vid årets slut är en <b>leverantörsskuld</b>, och det är bokslutets sak — spegelbilden
    /// av kundfordringssteget.</para>
    ///
    /// <para><b>⚠️ Kvittot lagras vid REGISTRERINGEN men blir en <see cref="LedgerAttachment"/>-rad
    /// först vid bokföringen.</b> Attesten sker mot kvittot, alltså innan det finns någon
    /// verifikation att hänga det på. Lagringen är innehållsadresserad (<c>LedgerAttachmentStorage</c>),
    /// så filen kan ligga färdig och bilagraden skapas när verifikationen finns.</para>
    /// </summary>
    [TableName("LedgerExpense")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerExpense
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Ur <see cref="LedgerExpenseKind"/>.</summary>
        public string Kind { get; set; } = LedgerExpenseKind.Utlagg;

        /// <summary>
        /// Medlemmen som ska ha pengarna tillbaka. <b>0 för en leverantörsfaktura</b> — ett företag
        /// har inget medlems-id.
        ///
        /// <para><b>⚠️ Det är det här fältet attestspärren läser</b>, inte
        /// <see cref="RegisteredByMemberId"/>: den som ska ha pengarna får aldrig godkänna sin egen
        /// utbetalning.</para>
        /// </summary>
        public int PayeeMemberId { get; set; }

        /// <summary>
        /// Mottagarens namn. <b>Snapshot</b> — en leverantör finns inte i något register hos oss,
        /// och en medlem kan byta namn eller lämna föreningen medan utgiften ligger i bokföringen.
        /// </summary>
        public string PayeeName { get; set; } = "";

        /// <summary>Vad utgiften avser. Det är den texten som hamnar i bokföringen.</summary>
        public string Description { get; set; } = "";

        public decimal Amount { get; set; }

        /// <summary>Fakturans datum, eller dagen utlägget gjordes.</summary>
        public DateTime ExpenseDate { get; set; }

        /// <summary>
        /// Förfallodagen. Null för ett utlägg — <b>och det är inte ett saknat värde</b>. Ett utlägg
        /// har ingen extern förfallodag; att hitta på en hade gjort varje utlägg försenat.
        /// </summary>
        public DateTime? DueDate { get; set; }

        /// <summary>Kostnadskontot — vad utgiften VAR.</summary>
        public int AccountNumber { get; set; }

        /// <summary>Projektet utgiften hör till. Se <see cref="LedgerProject"/>.</summary>
        public int? ProjectId { get; set; }

        /// <summary>Ur <see cref="LedgerExpenseStatus"/>.</summary>
        public string Status { get; set; } = LedgerExpenseStatus.Registered;

        public int RegisteredByMemberId { get; set; }

        public DateTime RegisteredUtc { get; set; }

        public int? ApprovedByMemberId { get; set; }

        public DateTime? ApprovedUtc { get; set; }

        public string? ApprovalNote { get; set; }

        /// <summary>
        /// <b>⚠️ Sant när den som attesterade också registrerade posten.</b> Ingen spärr — en liten
        /// förening kan ha en enda person som både öppnar posten och attesterar, och en spärr där
        /// hade gjort funktionen oanvändbar för just dem. Men det SÄGS, på raden och på
        /// verifikationen, så en revisor ser att de två rollerna inte var åtskilda.
        /// </summary>
        public bool ApprovedBySelf { get; set; }

        public string? RejectedReason { get; set; }

        public DateTime? PaidDate { get; set; }

        /// <summary>Betalkontot pengarna gick ur.</summary>
        public int? PaymentAccountNumber { get; set; }

        /// <summary>Verifikationen som skrevs när utgiften betalades. Null tills dess.</summary>
        public int? JournalEntryId { get; set; }

        // ── Kvittot ──────────────────────────────────────────────────────────────────────────
        // Lagrat vid registreringen, bilagrad vid bokföringen. Se klassens huvud.

        public string? ReceiptFileName { get; set; }

        /// <summary>Innehållshash + ändelse — namnet på disk, aldrig originalnamnet.</summary>
        public string? ReceiptStoredAs { get; set; }

        public long ReceiptSizeBytes { get; set; }

        [Ignore]
        public bool HasReceipt => !string.IsNullOrWhiteSpace(ReceiptStoredAs);

        [Ignore]
        public bool IsApproved => Status == LedgerExpenseStatus.Approved
                                  || Status == LedgerExpenseStatus.Paid;

        [Ignore]
        public bool IsPaid => Status == LedgerExpenseStatus.Paid;
    }

    /// <summary>Utgiftens två former. Samma tabell, samma flöde — olika motpart.</summary>
    public static class LedgerExpenseKind
    {
        /// <summary>Någon la ut egna pengar och ska ha dem tillbaka.</summary>
        public const string Utlagg = "utlagg";

        /// <summary>En räkning kom in, med en förfallodag.</summary>
        public const string Faktura = "faktura";

        public static bool IsKnown(string? value)
            => value == Utlagg || value == Faktura;

        public static string Label(string? value) => value switch
        {
            Utlagg => "Utlägg",
            Faktura => "Leverantörsfaktura",
            _ => "Utgift"
        };
    }

    /// <summary>
    /// Utgiftens läge.
    ///
    /// <para><b>⚠️ `Paid` betyder att verifikationen är skriven</b>, inte att någon tryckt på en
    /// knapp i sin bank. Vi har ingen bankkoppling; betalningen är kassörens påstående, precis som
    /// på inbetalningssidan.</para>
    /// </summary>
    public static class LedgerExpenseStatus
    {
        public const string Registered = "registered";
        public const string Approved = "approved";
        public const string Paid = "paid";
        public const string Rejected = "rejected";

        public static string Label(string? value) => value switch
        {
            Registered => "Väntar på attest",
            Approved => "Attesterad, väntar på betalning",
            Paid => "Betald och bokförd",
            Rejected => "Avvisad",
            _ => "Okänt läge"
        };
    }

    /// <summary>
    /// Utgiften som ytan visar den — med kontonamn, projektnamn och verifikationsnummer utskrivna.
    /// </summary>
    public class LedgerExpenseView
    {
        public int Id { get; set; }
        public string Kind { get; set; } = "";
        public string KindLabel { get; set; } = "";
        public string PayeeName { get; set; } = "";
        public int PayeeMemberId { get; set; }
        public string Description { get; set; } = "";
        public decimal Amount { get; set; }
        public DateTime ExpenseDate { get; set; }
        public DateTime? DueDate { get; set; }
        public int AccountNumber { get; set; }
        public string AccountName { get; set; } = "";
        public int? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public string Status { get; set; } = "";
        public string StatusLabel { get; set; } = "";

        public string RegisteredByName { get; set; } = "";
        public string? ApprovedByName { get; set; }
        public DateTime? ApprovedUtc { get; set; }
        public string? ApprovalNote { get; set; }
        public bool ApprovedBySelf { get; set; }
        public string? RejectedReason { get; set; }

        public DateTime? PaidDate { get; set; }
        public int? JournalEntryId { get; set; }
        public string? JournalEntryNumber { get; set; }

        public bool HasReceipt { get; set; }
        public string? ReceiptFileName { get; set; }

        /// <summary>Förfallen och fortfarande obetald. Härlett mot ett datum anroparen skickar in.</summary>
        public bool IsOverdue { get; set; }

        public bool CanEdit { get; set; }
        public bool CanApprove { get; set; }
        public bool CanPay { get; set; }
    }

    /// <summary>
    /// Utgiftssidans regler, som <b>rena funktioner</b> — ingen databas, inget "nu".
    ///
    /// <para>Ligger utanför tjänsten med flit: attestspärren är den enda internkontroll föreningen
    /// har över utbetalningar, och den får inte vara den enda delen av bygget som bara går att
    /// pröva genom hela stacken.</para>
    /// </summary>
    public static class LedgerExpenseRules
    {
        /// <summary>Utgifter större än så här är nästan alltid en felskrivning, inte ett inköp.</summary>
        public const decimal SanityCeiling = 1_000_000m;

        /// <summary>
        /// Kontrollerar en utgift som ska sparas.
        /// </summary>
        /// <returns>Felmeddelande på svenska, eller null när den duger.</returns>
        public static string? Validate(LedgerExpense e)
        {
            if (!LedgerExpenseKind.IsKnown(e.Kind))
                return "Välj om det är ett utlägg eller en leverantörsfaktura.";

            if (string.IsNullOrWhiteSpace(e.Description))
                return "Skriv vad utgiften avser — det är den texten som står i bokföringen.";

            if (string.IsNullOrWhiteSpace(e.PayeeName))
                return e.Kind == LedgerExpenseKind.Utlagg
                    ? "Ange vem som ska ha pengarna tillbaka."
                    : "Ange vilken leverantör räkningen kommer från.";

            if (e.Amount <= 0)
                return "Beloppet måste vara större än noll.";

            if (e.Amount > SanityCeiling)
                return $"Beloppet ser ut som en felskrivning. Är det verkligen {e.Amount:N0} kr?";

            if (e.ExpenseDate == default)
                return "Ange vilket datum utgiften gäller.";

            if (e.AccountNumber <= 0)
                return "Välj vilket konto utgiften hör till.";

            // ⚠️ Ett utlägg har ingen förfallodag, och att tillåta en hade gjort listan "förfallna
            //    utgifter" obegriplig: den som lagt ut pengar har inte satt någon frist.
            if (e.Kind == LedgerExpenseKind.Utlagg && e.DueDate.HasValue)
                return "Ett utlägg har ingen förfallodag — den hör till en leverantörsfaktura.";

            if (e.DueDate is DateTime d && d.Date < e.ExpenseDate.Date)
                return "Förfallodagen kan inte ligga före fakturadatumet.";

            // ⚠️ Ett utlägg BEHÖVER en medlem, för det är dit pengarna ska tillbaka. En
            //    leverantörsfaktura har ingen, och ska inte ha någon.
            if (e.Kind == LedgerExpenseKind.Utlagg && e.PayeeMemberId <= 0)
                return "Välj vilken medlem som lagt ut pengarna.";

            return null;
        }

        /// <summary>
        /// Får den här personen attestera utgiften?
        ///
        /// <para><b>⚠️⚠️ DEN SOM SKA HA PENGARNA FÅR ALDRIG GODKÄNNA SIN EGEN UTBETALNING.</b> Det
        /// är den enda regeln som alltid är rätt och alltid går att följa — det finns alltid någon
        /// annan i styrelsen. Att bara varna här hade gjort attesten till en formalitet i exakt det
        /// fall den finns för.</para>
        ///
        /// <para><b>⚠️ Att registreraren attesterar är däremot BARA en varning.</b> I en liten
        /// förening är det ofta samma person som öppnar posten och betalar räkningarna, och en
        /// spärr hade gjort funktionen oanvändbar för dem. Den registreras i stället, se
        /// <see cref="LedgerExpense.ApprovedBySelf"/>.</para>
        /// </summary>
        /// <returns>Felmeddelande på svenska, eller null när attesten får ske.</returns>
        public static string? ApprovalRefusal(LedgerExpense e, int approverMemberId)
        {
            if (approverMemberId <= 0)
                return "Du måste vara inloggad för att attestera.";

            if (e.Status == LedgerExpenseStatus.Paid)
                return "Utgiften är redan betald och bokförd.";

            if (e.Status == LedgerExpenseStatus.Rejected)
                return "Utgiften är avvisad. Registrera den på nytt om den ändå ska betalas.";

            if (e.PayeeMemberId > 0 && e.PayeeMemberId == approverMemberId)
                return "Du kan inte attestera din egen utbetalning. Be någon annan i styrelsen "
                     + "godkänna den.";

            return null;
        }

        /// <summary>
        /// Får utgiften betalas ut?
        ///
        /// <para><b>⚠️ Attesten är ett VILLKOR för betalningen, inte en anteckning bredvid den.</b>
        /// Går det att betala en oattesterad utgift är attesten frivillig i praktiken, och då är
        /// internkontrollen en text i ett dokument.</para>
        /// </summary>
        public static string? PaymentRefusal(LedgerExpense e)
        {
            if (e.Status == LedgerExpenseStatus.Paid)
                return "Utgiften är redan betald och bokförd.";

            if (e.Status == LedgerExpenseStatus.Rejected)
                return "Utgiften är avvisad och ska inte betalas.";

            if (e.Status != LedgerExpenseStatus.Approved)
                return "Utgiften måste attesteras innan den betalas.";

            return null;
        }

        /// <summary>
        /// Får utgiften ändras?
        ///
        /// <para><b>⚠️ En betald utgift är låst</b> — verifikationen är skriven, och den kan inte
        /// ändras. En attesterad går att ändra, men <b>attesten faller då</b>: godkännandet gällde
        /// ett annat belopp. Den regeln bor i tjänsten, eftersom den SKRIVER.</para>
        /// </summary>
        public static bool IsEditable(LedgerExpense e)
            => e.Status != LedgerExpenseStatus.Paid;

        /// <summary>
        /// Föll attesten av den här ändringen?
        ///
        /// <para>Bara sakuppgifterna räknas: belopp, datum, konto, mottagare och form. Att rätta en
        /// stavning i beskrivningen ska inte tvinga fram en ny attest — men ett ändrat belopp
        /// måste, eftersom attesten var ett godkännande av just den summan.</para>
        /// </summary>
        public static bool ApprovalIsVoidedBy(LedgerExpense before, LedgerExpense after)
            => before.Amount != after.Amount
               || before.ExpenseDate.Date != after.ExpenseDate.Date
               || before.AccountNumber != after.AccountNumber
               || before.PayeeMemberId != after.PayeeMemberId
               || before.Kind != after.Kind;

        /// <summary>
        /// Är utgiften förfallen sett från ett bestämt datum?
        ///
        /// <para><b>⚠️ `today` skickas in.</b> Systemklockan i en regel gör den omöjlig att pröva,
        /// och "förfallen" är exakt den sortens påstående som måste gå att mäta på ett valt datum.</para>
        /// </summary>
        public static bool IsOverdue(LedgerExpense e, DateTime today)
            => e.Status != LedgerExpenseStatus.Paid
               && e.Status != LedgerExpenseStatus.Rejected
               && e.DueDate is DateTime d
               && d.Date < today.Date;
    }
}
