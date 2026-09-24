using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En anläggningstillgång — klubbstugan, tavelställen, vapenskåpet.
    ///
    /// <para><b>⚠️⚠️ ANSKAFFNINGSVÄRDET STÅR KVAR PÅ TILLGÅNGSKONTOT.</b> Avskrivningen bokförs mot
    /// ett eget konto för ackumulerade avskrivningar (7830 debet / <b>1229</b> kredit), aldrig
    /// direkt mot 1220. Balansräkningen visar då båda — vad pjäsen kostade och vad som skrivits av
    /// — och skillnaden är det bokförda värdet. Det är BAS-praxis och det revisorn förväntar sig.</para>
    ///
    /// <para>⚠️ Fram till 2026-09-24 bokförde vi DIREKT mot 1220 "så att en lekman kan läsa
    /// balansräkningen". Michael Henriksson (Åmåls PK) rättade oss: <i>"Man tar inte bort
    /// inköpssumman och minskar den med avskrivningen."</i> Direktmodellen tappade
    /// anskaffningsvärdet ur liggaren, och den krockade med SIE-importen: en förening som tar in
    /// sin kontoplan får med 1229 och dess ingående saldo, och då låg två modeller i samma bok.
    /// Återinför den inte.</para>
    ///
    /// <para><b>⚠️ Kontona väljs PER TILLGÅNG ur föreningens egen kontoplan, inte via roller.</b>
    /// En klubbstuga skrivs av mot 7820/1119 och en pistol mot 7830/1229; en roll hade tvingat
    /// fram ett val för hela föreningen. Numren lagras på raden, som på en konteringsrad.</para>
    /// </summary>
    [TableName("LedgerAsset")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerAsset
    {
        public int Id { get; set; }

        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        public string Name { get; set; } = "";

        public string? Note { get; set; }

        /// <summary>Balanskontot tillgången står på, t.ex. 1220.</summary>
        public int AssetAccountNumber { get; set; }

        /// <summary>Kostnadskontot avskrivningen bokförs på, t.ex. 7830.</summary>
        public int DepreciationAccountNumber { get; set; }

        /// <summary>
        /// Balanskontot de ackumulerade avskrivningarna samlas på, t.ex. 1229 — ett minuskonto
        /// under <see cref="AssetAccountNumber"/>. Förslaget är
        /// <see cref="LedgerDepreciation.AccumulatedAccountFor"/>.
        /// </summary>
        public int AccumulatedDepreciationAccountNumber { get; set; }

        /// <summary>
        /// När tillgången TOGS I BRUK — inte när fakturan betalades.
        /// <para>⚠️ Avskrivningen börjar här. Det är skillnaden som gör att något som köpts i
        /// december inte skrivs av för ett helt år.</para>
        /// </summary>
        public DateTime InUseDate { get; set; }

        public decimal AcquisitionAmount { get; set; }

        /// <summary>Nyttjandeperiod i år. 5 är vanligt för inventarier; föreningen väljer.</summary>
        public int UsefulLifeYears { get; set; }

        /// <summary>Restvärde vid periodens slut. Nästan alltid 0 i en förening.</summary>
        public decimal ResidualValue { get; set; }

        public DateTime? DisposedDate { get; set; }

        public string? DisposalReason { get; set; }

        public int? DisposedByMemberId { get; set; }

        public int CreatedByMemberId { get; set; }

        public DateTime CreatedUtc { get; set; }

        [Ignore]
        public bool IsDisposed => DisposedDate.HasValue;

        /// <summary>Det som ska skrivas av över perioden.</summary>
        [Ignore]
        public decimal DepreciableAmount =>
            AcquisitionAmount - ResidualValue is var d && d > 0 ? d : 0m;
    }

    /// <summary>
    /// Avskrivningsreglerna. <b>Rena funktioner</b> — ingen databas, inget datum "nu".
    ///
    /// <para><b>⚠️⚠️ RAK AVSKRIVNING, PROPORTIONERAD PER MÅNAD.</b> Den som köper ett vapenskåp i
    /// november ska inte skriva av ett helt år på det. Månadsproportionering är dessutom det enda
    /// som fungerar när föreningens räkenskapsår inte är ett kalenderår.</para>
    ///
    /// <para><b>⚠️ Ingen avskrivning kan ta värdet under restvärdet</b>, och summan över hela
    /// perioden är exakt <see cref="LedgerAsset.DepreciableAmount"/> — sista årets belopp är
    /// vad som återstår, inte en ny uträkning. Annars blir det ören kvar på kontot i evighet.</para>
    /// </summary>
    public static class LedgerDepreciation
    {
        /// <summary>
        /// Avskrivningen för en period.
        ///
        /// <para>⚠️ Perioden är räkenskapsårets, inte kalenderårets — anroparen skickar in årets
        /// gränser. Utanför nyttjandeperioden är svaret 0, aldrig ett negativt tal.</para>
        /// </summary>
        public static decimal ForPeriod(LedgerAsset asset, DateTime from, DateTime to)
        {
            // ⚠️⚠️ PERIODBELOPPET ÄR SKILLNADEN MELLAN TVÅ ACKUMULERADE VÄRDEN, aldrig en egen
            //    avrundning av periodens egna månader. Skillnaden teleskoperar: summan över alla
            //    perioder blir därför EXAKT det avskrivningsbara beloppet, oavsett hur illa
            //    beloppet delar sig på månaderna.
            //
            //    ⚠️ Första utsågan rundade periodens månader var för sig och drog av det
            //    ackumulerade. Den lämnade ett öre kvar på 10 000 / 36 månader — alltså ett
            //    konto som aldrig går att nolla. Enhetstestet fångade det innan något wirats in.
            var acc = AccumulatedThrough(asset, to.Date);
            var accBefore = AccumulatedThrough(asset, from.Date.AddDays(-1));

            var amount = acc - accBefore;
            return amount > 0m ? amount : 0m;
        }

        /// <summary>
        /// Ackumulerat till och med ett datum. Driver bokfört värde i registret.
        /// </summary>
        public static decimal AccumulatedThrough(LedgerAsset asset, DateTime through)
        {
            if (asset.UsefulLifeYears <= 0 || asset.DepreciableAmount <= 0) return 0m;

            var perMonth = asset.DepreciableAmount / (asset.UsefulLifeYears * 12);
            var months = MonthsInPeriod(asset, DateTime.MinValue, through);

            var acc = Round(perMonth * months);
            return acc > asset.DepreciableAmount ? asset.DepreciableAmount : acc;
        }

        /// <summary>Kvarvarande bokfört värde.</summary>
        public static decimal BookValue(LedgerAsset asset, DateTime through) =>
            asset.AcquisitionAmount - AccumulatedThrough(asset, through);

        /// <summary>
        /// Förslaget på konto för ackumulerade avskrivningar: tillgångskontots grupp med 9 sist.
        /// 1220 → 1229, 1221 → 1229, 1110 → 1119, 1150 → 1159 — BAS-konventionens minuskonto.
        /// <para>⚠️ Bara ett FÖRSLAG. Kassören väljer, och en förening med egen kontoplan kan ha
        /// lagt kontot någon annanstans.</para>
        /// </summary>
        public static int AccumulatedAccountFor(int assetAccountNumber) =>
            assetAccountNumber / 10 * 10 + 9;

        /// <summary>
        /// Beloppen i utrangeringens verifikation, om tillgången lämnar föreningen på
        /// <paramref name="when"/>.
        ///
        /// <para><b>⚠️⚠️ HELA ANSKAFFNINGSVÄRDET BORT FRÅN TILLGÅNGSKONTOT, HELA DET ACKUMULERADE
        /// BORT FRÅN MINUSKONTOT.</b> Står något kvar på något av dem ligger en tillgång i
        /// balansräkningen som föreningen inte har. Det som återstår — det bokförda värdet — är en
        /// förlust. En försäljning är en egen intäkt; beloppet vet bara kassören.</para>
        ///
        /// <para>Det ackumulerade är PLANENS, räknat fram till utrangeringsdagen. Det förutsätter
        /// att varje års avskrivning är bokförd; tjänsten vägrar innan den kommer hit om den inte
        /// är det.</para>
        /// </summary>
        public static (decimal Accumulated, decimal BookValue) Disposal(LedgerAsset asset, DateTime when)
        {
            var disposed = CopyDisposedOn(asset, when);
            var accumulated = AccumulatedThrough(disposed, when.Date);
            return (accumulated, asset.AcquisitionAmount - accumulated);
        }

        /// <summary>
        /// En kopia utrangerad på <paramref name="when"/>. Planen kapas vid utrangeringen, så
        /// beloppen för utrangeringsåret räknas på kopian medan raden ännu inte är utrangerad.
        /// </summary>
        public static LedgerAsset CopyDisposedOn(LedgerAsset a, DateTime when) => new()
        {
            Id = a.Id,
            IssuerType = a.IssuerType,
            IssuerId = a.IssuerId,
            Name = a.Name,
            AssetAccountNumber = a.AssetAccountNumber,
            DepreciationAccountNumber = a.DepreciationAccountNumber,
            AccumulatedDepreciationAccountNumber = a.AccumulatedDepreciationAccountNumber,
            InUseDate = a.InUseDate,
            AcquisitionAmount = a.AcquisitionAmount,
            UsefulLifeYears = a.UsefulLifeYears,
            ResidualValue = a.ResidualValue,
            DisposedDate = when.Date
        };

        /// <summary>Datumet då tillgången är färdigavskriven.</summary>
        public static DateTime FullyDepreciatedOn(LedgerAsset asset) =>
            asset.InUseDate.Date.AddMonths(Max(asset.UsefulLifeYears, 0) * 12).AddDays(-1);

        /// <summary>
        /// Hela månader tillgången varit i bruk inom perioden.
        ///
        /// <para><b>⚠️ Månaden den togs i bruk räknas MED</b>, och avskrivningen slutar när
        /// nyttjandeperioden är slut — inte när räkenskapsåret är det. En tillgång som utrangerats
        /// skrivs av fram till utrangeringen och inte längre.</para>
        /// </summary>
        private static int MonthsInPeriod(LedgerAsset asset, DateTime from, DateTime to)
        {
            var start = asset.InUseDate.Date;
            var end = FullyDepreciatedOn(asset);

            // ⚠️ Utrangeringen kapar perioden. Att fortsätta skriva av något föreningen gjort sig
            //    av med hade byggt upp en kostnad för en tillgång som inte finns.
            if (asset.DisposedDate is DateTime d && d.Date < end) end = d.Date;

            var a = Later(start, from.Date);
            var b = Earlier(end, to.Date);

            if (b < a) return 0;

            // Antal påbörjade månader mellan a och b.
            var months = ((b.Year - a.Year) * 12) + b.Month - a.Month + 1;

            var cap = asset.UsefulLifeYears * 12;
            return months < 0 ? 0 : months > cap ? cap : months;
        }

        private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
        private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
        private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;
        private static int Max(int a, int b) => a > b ? a : b;
        private static decimal Max(decimal a, decimal b) => a > b ? a : b;
    }

    /// <summary>En tillgång med sitt läge för ett bestämt räkenskapsår — det ytan visar.</summary>
    public class LedgerAssetView
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public string? Note { get; set; }

        public int AssetAccountNumber { get; set; }

        public string AssetAccountName { get; set; } = "";

        public int DepreciationAccountNumber { get; set; }

        public string DepreciationAccountName { get; set; } = "";

        public int AccumulatedDepreciationAccountNumber { get; set; }

        public string AccumulatedDepreciationAccountName { get; set; } = "";

        public DateTime InUseDate { get; set; }

        public decimal AcquisitionAmount { get; set; }

        public int UsefulLifeYears { get; set; }

        public decimal ResidualValue { get; set; }

        /// <summary>Årets avskrivning enligt plan.</summary>
        public decimal PlannedThisYear { get; set; }

        /// <summary>
        /// Vad som FAKTISKT bokförts på tillgången under året.
        /// <para><b>⚠️ HÄRLETT ur liggaren</b>, aldrig en flagga på raden — samma princip som
        /// medlemsavgifternas "bokförd". En flagga kan glömmas; en verifikation kan inte.</para>
        /// </summary>
        public decimal PostedThisYear { get; set; }

        public decimal BookValue { get; set; }

        public bool IsDisposed { get; set; }

        public DateTime? DisposedDate { get; set; }

        public string? DisposalReason { get; set; }

        /// <summary>Färdigavskriven — planen är slut, oavsett om den utrangerats.</summary>
        public bool IsFullyDepreciated { get; set; }

        /// <summary>Årets avskrivning återstår att bokföra.</summary>
        public decimal Remaining => PlannedThisYear - PostedThisYear is var r && r > 0.004m ? r : 0m;

        public bool NeedsPosting => Remaining > 0m;
    }
}
