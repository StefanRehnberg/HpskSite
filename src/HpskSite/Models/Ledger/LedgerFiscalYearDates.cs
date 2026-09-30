namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Reglerna för ett räkenskapsårs start- och slutdatum. Rena funktioner, ingen databas.
    ///
    /// <para><b>⚠️ Född ur en felrapport (Michael Henriksson, 2026-09-28).</b> Ett år lades upp med
    /// fel startdatum (1 oktober i stället för 1 januari). När han rättade datumen och sparade
    /// svarade sidan <i>"Sparat. Allt fanns redan."</i> — uppsättningen hoppade över ett år som
    /// redan fanns, och de nya datumen kastades tyst. Året gick alltså inte att rätta alls.</para>
    ///
    /// <para>Längdregeln följer bokföringslagen (3 kap.): ett räkenskapsår är normalt tolv
    /// månader, men det första — eller ett år då föreningen lägger om räkenskapsåret — får vara
    /// kortare eller högst arton månader.</para>
    /// </summary>
    public static class LedgerFiscalYearDates
    {
        /// <summary>Längsta tillåtna räkenskapsår, i månader.</summary>
        public const int MaxMonths = 18;

        /// <summary>
        /// Null när perioden är giltig, annars skälet i klartext.
        /// </summary>
        public static string? LengthRefusal(DateTime start, DateTime end)
        {
            start = start.Date;
            end = end.Date;

            if (end <= start)
                return "Räkenskapsårets slut måste ligga efter dess början.";

            // Sista tillåtna dag = dagen före samma datum arton månader senare.
            if (end > start.AddMonths(MaxMonths).AddDays(-1))
                return $"Ett räkenskapsår får vara högst {MaxMonths} månader. "
                     + $"{start:yyyy-MM-dd} till {end:yyyy-MM-dd} är längre än så.";

            return null;
        }

        /// <summary>Två perioder överlappar när ingen av dem slutar före den andra börjar.</summary>
        public static bool Overlaps(DateTime aStart, DateTime aEnd, DateTime bStart, DateTime bEnd)
            => aStart.Date <= bEnd.Date && bStart.Date <= aEnd.Date;
    }
}
