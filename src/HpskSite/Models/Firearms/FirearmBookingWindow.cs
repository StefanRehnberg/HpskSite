namespace HpskSite.Models.Firearms
{
    /// <summary>
    /// Bokningsfönstret: hur ett par av tidpunkter tolkas, och när två fönster överlappar.
    ///
    /// <para><b>Varför en egen, ren klass:</b> reglerna låg i tre kopior — <c>NormaliseWindow</c> i
    /// <c>FirearmBookingService</c>, <c>TryWindow</c> i <c>LoanWeaponApiController</c>, och
    /// överlappsvillkoret dessutom i två SQL-satser plus ett C#-filter. Kopiorna hade redan glidit
    /// isär: tillgänglighetslistan normaliserade ett bakvänt fönster (14:00–10:00) till hela dagen
    /// och visade vapnet som ledigt, medan bokningen vägrade samma fönster med "Bokningen måste
    /// sluta efter att den börjar". Alltså en rad som ser bokbar ut och nekas i nästa klick.</para>
    ///
    /// <para>Rena predikat utan databas, av samma skäl som <c>FirearmAccessRules</c>: annars är den
    /// enda regel hela funktionen vilar på den enda som inte går att testa.</para>
    /// </summary>
    public static class FirearmBookingWindow
    {
        /// <summary>Hur långt fram en bokning får läggas.</summary>
        public const int MaxDaysAhead = 365;

        /// <summary>
        /// Hur lång en enskild bokning får vara. Utan taket kan en person belägga ett av klubbens
        /// lånevapen en hel månad.
        /// </summary>
        public const int MaxDurationDays = 14;

        /// <summary>
        /// Tolkar ett fönster och vägrar det orimliga. <paramref name="error"/> är <c>null</c> när
        /// fönstret duger.
        ///
        /// <para><b>Tom eller obefintlig sluttid = HELA DAGEN</b> (00:00–23:59). Det är vad en
        /// medlem menar med "jag vill låna det på lördag", och utan tolkningen blir fönstret noll
        /// sekunder långt, krockar med ingenting och ger en bokning som inte bokar.</para>
        ///
        /// <para><b>⚠️ En sluttid som ligger FÖRE starttiden är ett FEL, inte hela dagen.</b> Det är
        /// den enda punkten där kopiorna sa emot varandra. Att tysta det till "hela dagen" vore
        /// generöst mot en felskrivning och samtidigt en tystnad om något medlemmen faktiskt
        /// menade — hen skrev in två klockslag.</para>
        ///
        /// <para><paramref name="now"/> är injicerbart så gränserna går att testa utan att flytta
        /// systemklockan. Lokal tid, aldrig UTC: hela kodbasen jämför mot tävlingsdatum i lokal
        /// tid, och UTC här hade gett en timmes fel mot varje sådant datum.</para>
        /// </summary>
        public static bool TryNormalise(
            DateTime from, DateTime to, DateTime now,
            out DateTime normalisedFrom, out DateTime normalisedTo, out string? error)
        {
            normalisedFrom = from;
            normalisedTo = to;
            error = null;

            if (from == default)
            {
                error = "Ange när bokningen börjar.";
                return false;
            }

            var wholeDay =
                to == default ||
                (to.Date == from.Date && from.TimeOfDay == TimeSpan.Zero && to.TimeOfDay == TimeSpan.Zero);

            if (wholeDay)
            {
                normalisedFrom = from.Date;
                normalisedTo = from.Date.AddDays(1).AddSeconds(-1);
            }
            else if (to <= from)
            {
                error = "Bokningen måste sluta efter att den börjar.";
                return false;
            }

            var today = now.Date;
            if (normalisedFrom < today)
            {
                error = "Du kan inte boka bakåt i tiden.";
                return false;
            }
            if (normalisedFrom > today.AddDays(MaxDaysAhead))
            {
                error = $"Du kan boka högst {MaxDaysAhead} dagar framåt.";
                return false;
            }
            if ((normalisedTo - normalisedFrom).TotalDays > MaxDurationDays)
            {
                error = $"En bokning kan vara högst {MaxDurationDays} dagar.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Etiketten som visas för ett normaliserat fönster. Ligger här för att listan och
        /// bokningen ska beskriva samma fönster med samma ord.
        /// </summary>
        public static string Label(DateTime from, DateTime to)
        {
            if (from.TimeOfDay == TimeSpan.Zero && to.Date == from.Date && to.Hour == 23)
                return $"{from:yyyy-MM-dd} (hela dagen)";

            return from.Date == to.Date
                ? $"{from:yyyy-MM-dd} {from:HH\\:mm}–{to:HH\\:mm}"
                : $"{from:yyyy-MM-dd HH\\:mm} – {to:yyyy-MM-dd HH\\:mm}";
        }

        /// <summary>
        /// Överlappar två fönster?
        ///
        /// <para><b>⚠️ KANT-I-KANT TILLÅTS.</b> En bokning som slutar 12:00 och en som börjar 12:00
        /// krockar inte — överlämningen sker just då, och utan det kunde två pass i följd aldrig
        /// dela ett vapen.</para>
        ///
        /// <para><b>⚠️ SQL-satserna i <c>FirearmBookingService</c> måste spegla den här funktionen
        /// exakt</b> (<c>NOT (ToTime &lt;= @from OR FromTime &gt;= @to)</c>). Inget unikt index kan
        /// uttrycka "överlappar i tid", så spärren ligger i en läsning inne i transaktionen — och
        /// det är därför regeln finns i både C# och SQL. Ändra aldrig den ena utan den andra.</para>
        /// </summary>
        public static bool Overlaps(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo)
            => !(aTo <= bFrom || aFrom >= bTo);
    }
}
