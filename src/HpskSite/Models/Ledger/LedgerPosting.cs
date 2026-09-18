namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En begäran om att bokföra. Anroparen beskriver AFFÄRSHÄNDELSEN; tjänsten gör bokföringen.
    ///
    /// <para><b>⚠️ RADERNA PEKAR PÅ ROLLER, ALDRIG PÅ KONTONUMMER.</b> Det är hela skälet att
    /// kontoplanen är ofarlig att välja fel: en förening kan bygga om sin utan att en kodrad ändras.
    /// Undantaget är <see cref="LedgerPostingLine.AccountNumber"/>, som finns för den manuella
    /// bokför-ytan där kassören själv väljer konto — men systemgenererade postningar får aldrig
    /// använda det.</para>
    /// </summary>
    public class LedgerPostingRequest
    {
        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Styr vilken period posten hamnar i, och därmed vilket räkenskapsår som gäller.</summary>
        public DateTime AccountingDate { get; set; }

        /// <summary>När det faktiskt hände. Null = samma som bokföringsdatumet.</summary>
        public DateTime? EventDate { get; set; }

        public string Description { get; set; } = "";

        public int? CounterpartyType { get; set; }
        public int? CounterpartyId { get; set; }

        /// <summary>Snapshot — motparten kan byta namn eller raderas.</summary>
        public string? CounterpartyName { get; set; }

        /// <summary>Ur <see cref="LedgerSourceType"/>.</summary>
        public string SourceType { get; set; } = LedgerSourceType.Manual;

        public int? SourceId { get; set; }

        public int? PaymentId { get; set; }

        /// <summary>
        /// Projektet som gäller för HELA verifikationen, om inget annat sägs per rad. Se
        /// <see cref="LedgerProject"/>.
        ///
        /// <para><b>Det här är den ergonomiska halvan av dimensionen.</b> Nästan varje postning
        /// hör i sin helhet till ett projekt — en anmälningsavgift till tävlingen den avser, ett
        /// utlägg till det man köpte in till. Anroparen sätter det en gång här i stället för på
        /// varje rad, och en systemgenererad postning ur en tävling kan därmed märka sina rader
        /// utan att någon människa väljer något.</para>
        ///
        /// <para>En rad som sätter <see cref="LedgerPostingLine.ProjectId"/> vinner över det här
        /// värdet. Det är så en betalning som täcker två projekt bokförs.</para>
        /// </summary>
        public int? ProjectId { get; set; }

        public int CreatedByMemberId { get; set; }

        /// <summary>
        /// Satt när posten är en RÄTTELSE.
        /// <para><b>⚠️ Måste vara känd innan posten skrivs.</b> Fältet kan inte fyllas i efteråt —
        /// <c>UPDATE</c> är förbjudet på en bokförd verifikation, och triggern skiljer inte på
        /// "en ändrad siffra" och "ett fält som fylls i senare". Skrivvägen måste alltså känna till
        /// originalet i förväg; det är därför <c>CreateCorrection</c> finns.</para>
        /// </summary>
        public int? CorrectsEntryId { get; set; }

        public List<LedgerPostingLine> Lines { get; set; } = new();
    }

    /// <summary>
    /// En rad i begäran. Exakt en av <see cref="Debit"/> och <see cref="Credit"/> är större än noll.
    /// </summary>
    public class LedgerPostingLine
    {
        /// <summary>Nyckel ur <see cref="LedgerAccountRoles"/>. Tom när <see cref="AccountNumber"/> är satt.</summary>
        public string? Role { get; set; }

        /// <summary>
        /// Uttryckligt konto, bara för den manuella bokför-ytan.
        /// <para>⚠️ Använd ALDRIG från en systemgenererad postning — då står kontoplanen i koden igen.</para>
        /// </summary>
        public int? AccountNumber { get; set; }

        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        public string? Text { get; set; }

        /// <summary>
        /// Momssats i procent. Null = ta kontots <see cref="LedgerAccount.DefaultVatRate"/>.
        /// Sätt <c>0</c> för att uttryckligen bokföra raden momsfritt trots kontots förval.
        /// </summary>
        public decimal? VatRate { get; set; }

        /// <summary>
        /// Momsens riktning för raden: <c>true</c> = utgående (försäljning), <c>false</c> = ingående
        /// (inköp). Null = härled ur kontoklassen (3 = intäkt → utgående, annars ingående).
        /// </summary>
        public bool? VatIsOutgoing { get; set; }

        /// <summary>
        /// Projekt för just den här raden. Null = ärv <see cref="LedgerPostingRequest.ProjectId"/>.
        ///
        /// <para>Sätts bara när en verifikation delar sig mellan projekt. Den vanliga vägen är att
        /// låta hela begäran bära projektet.</para>
        ///
        /// <para>⚠️ Momsraden och avrundningsraden som tjänsten lägger till ärver källradens
        /// projekt. Annars hade projektets resultat inte gått ihop med bokföringens.</para>
        /// </summary>
        public int? ProjectId { get; set; }
    }

    /// <summary>Vad bokföringen resulterade i. <see cref="Error"/> är null när det gick.</summary>
    public class LedgerPostingResult
    {
        public bool Success => Error is null;

        public string? Error { get; set; }

        public int EntryId { get; set; }

        public int Number { get; set; }

        /// <summary>Numret som det ska stå på en handling, t.ex. <c>V-214</c>.</summary>
        public string FormattedNumber { get; set; } = "";

        public int FiscalYearId { get; set; }

        /// <summary>Raderna som faktiskt skrevs, inklusive moms- och avrundningsrader.</summary>
        public List<LedgerJournalEntryLine> Lines { get; set; } = new();

        public static LedgerPostingResult Failed(string error) => new() { Error = error };
    }

    /// <summary>
    /// Beloppsräkningen, som REN funktion — utan databas, utan Umbraco.
    ///
    /// <para>Ligger för sig själv därför att den är det enda i bokföringen som räknar, och därmed
    /// det enda som kan räkna fel. Bakad in i tjänsten hade den bara gått att pröva genom att
    /// skriva riktiga verifikationer.</para>
    /// </summary>
    public static class LedgerAmounts
    {
        /// <summary>Största differens som får avrundas bort i stället för att avvisas.</summary>
        public const decimal RoundingTolerance = 0.02m;

        /// <summary>
        /// Delar ett BRUTTObelopp i netto och moms.
        ///
        /// <para><b>⚠️ BELOPPET ÄR ALLTID BRUTTO — det som faktiskt rörde sig.</b> En skytt swishar
        /// 250 kr; det är summan på kontoutdraget och på kvittot, och den kan inte vara något annat.
        /// Att tolka radens belopp som netto och lägga moms ovanpå skulle göra verifikationen
        /// omöjlig att stämma av mot banken — och avstämningen är det enda som bevisar
        /// fullständigheten.</para>
        ///
        /// <para>Momsen avrundas till ören; nettot är resten, så delarna summerar exakt till bruttot
        /// även när satsen inte går jämnt upp. Räknades båda var för sig kunde de skilja sig ett öre
        /// från beloppet som betalades.</para>
        /// </summary>
        public static (decimal Net, decimal Vat) SplitGross(decimal gross, decimal vatRatePercent)
        {
            if (vatRatePercent <= 0) return (gross, 0m);

            var vat = Math.Round(gross * vatRatePercent / (100m + vatRatePercent), 2,
                                 MidpointRounding.AwayFromZero);
            return (gross - vat, vat);
        }

        /// <summary>
        /// Differensen mellan debet och kredit. Noll betyder balanserad.
        /// </summary>
        public static decimal Imbalance(IEnumerable<LedgerJournalEntryLine> lines)
            => lines.Sum(l => l.Debit) - lines.Sum(l => l.Credit);

        /// <summary>
        /// Om en differens får rättas med en öresavrundning.
        /// <para><b>⚠️ Bara ören.</b> Momsdelningen kan lämna ett öre; en krona kommer ur ett
        /// räknefel eller en felskrivning, och den ska avvisas och inte gömmas i en avrundningsrad.</para>
        /// </summary>
        public static bool IsRoundable(decimal imbalance)
            => imbalance != 0 && Math.Abs(imbalance) <= RoundingTolerance;

        /// <summary>
        /// Härleder momsens riktning ur kontoklassen när anroparen inte sagt något:
        /// klass 3 är intäkter, alltså utgående moms; allt annat behandlas som inköp.
        /// </summary>
        public static bool IsOutgoingVatAccount(int accountNumber)
            => accountNumber >= 3000 && accountNumber < 4000;
    }
}
