namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Underlaget till momsdeklarationen för en period — rutorna Skatteverket frågar efter, räknade
    /// ur bokföringen.
    ///
    /// <para><b>⚠️⚠️ ETT UNDERLAG, INTE EN DEKLARATION.</b> Föreningen lämnar deklarationen själv hos
    /// Skatteverket. Vi räknar fram rutorna och visar varifrån varje krona kommer; vi skickar
    /// ingenting.</para>
    ///
    /// <para><b>⚠️⚠️ MOMSKONTONA ÄR KONTROLLEN.</b> Rutorna byggs ur de rader bokföringen själv
    /// räknat moms på. Står det något på momskontona som inte kommer därifrån — en SIE-import, en
    /// post bokförd direkt på 2610 — så stämmer rutorna inte med bokföringen, och det SÄGS i
    /// stället för att döljas (<see cref="OutputDifference"/>, <see cref="InputDifference"/>).</para>
    /// </summary>
    public class LedgerVatReturn
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        /// <summary>Utgående moms och underlag per sats (25, 12, 6).</summary>
        public List<RateRow> Rates { get; set; } = new();

        /// <summary>Ruta 05 — momspliktig försäljning, exklusive moms.</summary>
        public decimal Box05 => Rates.Sum(r => r.SalesNet);
        /// <summary>Ruta 10 — utgående moms 25 %.</summary>
        public decimal Box10 => RateVat(25m);
        /// <summary>Ruta 11 — utgående moms 12 %.</summary>
        public decimal Box11 => RateVat(12m);
        /// <summary>Ruta 12 — utgående moms 6 %.</summary>
        public decimal Box12 => RateVat(6m);
        /// <summary>Ruta 48 — ingående moms att dra av.</summary>
        public decimal Box48 { get; set; }
        /// <summary>Ruta 49 — moms att betala (positivt) eller få tillbaka (negativt).</summary>
        public decimal Box49 => Box10 + Box11 + Box12 - Box48;

        /// <summary>Rörelsen på kontot för utgående moms under perioden (kredit − debet).</summary>
        public decimal LedgerOutput { get; set; }
        /// <summary>Rörelsen på kontot för ingående moms under perioden (debet − kredit).</summary>
        public decimal LedgerInput { get; set; }

        /// <summary>Det på utgående-momskontot som INTE kommer från en momsrad. 0 = stämmer.</summary>
        public decimal OutputDifference => LedgerOutput - (Box10 + Box11 + Box12);
        /// <summary>Det på ingående-momskontot som INTE kommer från en momsrad. 0 = stämmer.</summary>
        public decimal InputDifference => LedgerInput - Box48;

        public bool Reconciles => Math.Abs(OutputDifference) < 0.005m && Math.Abs(InputDifference) < 0.005m;

        public int EntryCount { get; set; }

        public int OutputAccount { get; set; }
        public int InputAccount { get; set; }

        private decimal RateVat(decimal rate) => Rates.Where(r => r.Rate == rate).Sum(r => r.OutputVat);

        /// <summary>
        /// Beloppet som det skrivs i rutan: <b>hela kronor, örena stryks</b> — Skatteverkets regel
        /// för momsdeklarationen. Visas bredvid det exakta beloppet, aldrig i stället för det.
        /// </summary>
        public static decimal WholeKronor(decimal amount) => Math.Truncate(amount);

        public class RateRow
        {
            public decimal Rate { get; set; }
            /// <summary>Försäljning exklusive moms med den här satsen.</summary>
            public decimal SalesNet { get; set; }
            public decimal OutputVat { get; set; }
        }
    }

    /// <summary>
    /// En källrad som bär moms, med tecken — det rutorna summeras ur.
    /// <para>Positivt = försäljning (utgående) respektive inköp (ingående). En rättelse eller
    /// återbetalning är negativ.</para>
    /// </summary>
    public readonly record struct VatLine(decimal Rate, decimal Net, decimal Vat, bool Outgoing);

    /// <summary>
    /// Momsuträkningen. <b>Rena funktioner</b> — ingen databas.
    /// </summary>
    public static class LedgerVatReturnCalculator
    {
        /// <summary>
        /// Källraderna med moms i EN verifikation, med tecken.
        ///
        /// <para><b>⚠️⚠️ RÄTTELSER.</b> En rättelse vänder originalets rader men bär INGEN
        /// momsuppgift på källraden — den nollas med flit så att bokföringen inte räknar moms på
        /// momsen (<c>BuildCorrectionLines</c>). Rättelsen räknas därför som originalets momsrader
        /// med <b>omvänt tecken</b>, i den period rättelsen bokfördes. Utan det hade en tillbakatagen
        /// kioskförsäljning stått kvar i ruta 05 medan momsen försvann från kontot.</para>
        ///
        /// <para>Riktningen (utgående/ingående) avgörs av vilket momskonto verifikationen träffade —
        /// det är bokföringens eget beslut, aldrig en gissning ur kontoklassen.</para>
        /// </summary>
        public static List<VatLine> LinesOf(
            IReadOnlyList<LedgerJournalEntryLine> lines,
            IReadOnlyList<LedgerJournalEntryLine>? correctedOriginal,
            int outputAccount, int inputAccount)
        {
            var source = correctedOriginal ?? lines;
            var sign = correctedOriginal is null ? 1m : -1m;

            var outgoing = source.Any(l => l.AccountNumber == outputAccount);
            var incoming = source.Any(l => l.AccountNumber == inputAccount);

            var result = new List<VatLine>();

            foreach (var l in source.Where(l => l.VatAmount is > 0m && l.VatRate is > 0m))
            {
                // En källrad i kredit är en försäljning; i debet ett inköp. Utgående moms räknas
                // kredit-positivt, ingående debet-positivt — så en återbetalning blir negativ.
                var isOut = outgoing && !(incoming && l.Debit > 0m);

                var direction = isOut
                    ? (l.Credit > 0m ? 1m : -1m)
                    : (l.Debit > 0m ? 1m : -1m);

                var net = l.Credit > 0m ? l.Credit : l.Debit;
                result.Add(new VatLine(l.VatRate!.Value, sign * direction * net, sign * direction * l.VatAmount!.Value, isOut));
            }

            return result;
        }

        /// <summary>Rutorna ur alla momsrader i perioden, och kontrollen mot momskontona.</summary>
        public static LedgerVatReturn Compute(
            DateTime from, DateTime to, IEnumerable<VatLine> lines,
            decimal ledgerOutput, decimal ledgerInput, int entryCount, int outputAccount, int inputAccount)
        {
            var all = lines.ToList();

            return new LedgerVatReturn
            {
                From = from,
                To = to,
                Rates = all.Where(l => l.Outgoing)
                    .GroupBy(l => l.Rate)
                    .OrderByDescending(g => g.Key)
                    .Select(g => new LedgerVatReturn.RateRow
                    {
                        Rate = g.Key,
                        SalesNet = g.Sum(x => x.Net),
                        OutputVat = g.Sum(x => x.Vat)
                    }).ToList(),
                Box48 = all.Where(l => !l.Outgoing).Sum(l => l.Vat),
                LedgerOutput = ledgerOutput,
                LedgerInput = ledgerInput,
                EntryCount = entryCount,
                OutputAccount = outputAccount,
                InputAccount = inputAccount
            };
        }
    }
}
