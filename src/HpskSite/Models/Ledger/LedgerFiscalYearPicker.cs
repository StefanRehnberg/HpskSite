namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Vilket räkenskapsår föreningen ARBETAR i, och vilket datum beredskapen ska prövas mot.
    ///
    /// <para><b>⚠️⚠️ "KAN VI BOKFÖRA?" ÄR INTE "KAN VI BOKFÖRA I DAG?"</b> Ytan frågade tidigare
    /// med <c>DateTime.Today</c> rakt av, och en förening som lagt upp 2025 fick därför
    /// <i>"Det finns inget räkenskapsår som omfattar 2026-09-22. Lägg upp året i
    /// ekonomiinställningarna först."</i> — trots att året fanns, var öppet och gick alldeles
    /// utmärkt att bokföra i. Ett falskt larm på en yta vars hela poäng är att säga när något
    /// faktiskt är fel.</para>
    ///
    /// <para><b>Det är inget kantfall.</b> Att lägga upp ett PASSERAT år och mata in det för att
    /// jämföra med den befintliga bokföringen är precis vad en klubb gör när den provar oss
    /// (Stefan 2026-09-22, sandlådan för Hallands krets). Det ska fungera.</para>
    ///
    /// <para>Ren och statisk med flit — regeln går att pröva utan databas, och den är den enda
    /// platsen frågan besvaras.</para>
    /// </summary>
    public static class LedgerFiscalYearPicker
    {
        /// <summary>
        /// Året arbetet sker i.
        ///
        /// <list type="number">
        /// <item>Året som omfattar i dag — oavsett status. Är det fastställt ska svaret bli
        /// "året är fastställt", inte "året finns inte".</item>
        /// <item>Annars det senaste ÖPPNA året. Det är här den som matar in ett passerat år hamnar.</item>
        /// <item>Annars det senaste året överhuvudtaget, så spärren får något att uttala sig om.</item>
        /// </list>
        /// </summary>
        public static LedgerFiscalYear? Working(IEnumerable<LedgerFiscalYear>? years, DateTime today)
        {
            if (years is null) return null;

            var list = years.ToList();
            if (list.Count == 0) return null;

            var covering = list.FirstOrDefault(y => y.StartDate.Date <= today.Date && y.EndDate.Date >= today.Date);
            if (covering is not null) return covering;

            return list.Where(y => y.Status == LedgerFiscalYearStatus.Open)
                       .OrderByDescending(y => y.Year)
                       .FirstOrDefault()
                ?? list.OrderByDescending(y => y.Year).First();
        }

        /// <summary>
        /// Datumet beredskapen prövas mot.
        ///
        /// <para><b>⚠️ Utan något räkenskapsår alls svarar den I DAG</b>, och det är med flit: då
        /// ÄR "lägg upp året" rätt besked, och det beskedet kommer ur spärren själv.</para>
        /// </summary>
        public static DateTime ProbeDate(IEnumerable<LedgerFiscalYear>? years, DateTime today)
        {
            var working = Working(years, today);
            if (working is null) return today.Date;

            return Clamp(today, working);
        }

        /// <summary>
        /// Ett datum inuti året — i dag när i dag ryms, annars närmaste kant.
        ///
        /// <para><b>⚠️ Den som förifyller ett datum MÅSTE skriva ut vilket år det gäller.</b> En
        /// kassör som ser 2025-12-31 dyka upp i fältet utan förklaring läser det som ett fel; och
        /// en förening som glömt lägga upp det nya året skulle annars kunna datera en
        /// januaripost till förra årets sista dag utan att något sa ifrån.</para>
        /// </summary>
        public static DateTime Clamp(DateTime date, LedgerFiscalYear year)
        {
            if (date.Date < year.StartDate.Date) return year.StartDate.Date;
            if (date.Date > year.EndDate.Date) return year.EndDate.Date;
            return date.Date;
        }

        /// <summary>
        /// Upplysningen när i dag ligger utanför arbetsåret. <b>Inte en spärr</b> — bokföringen
        /// fungerar, det är bara datumet som måste ligga i året.
        /// </summary>
        public static string? DateNote(IEnumerable<LedgerFiscalYear>? years, DateTime today)
        {
            var working = Working(years, today);
            if (working is null) return null;

            if (working.StartDate.Date <= today.Date && working.EndDate.Date >= today.Date)
                return null;

            return $"I dag ligger utanför räkenskapsåret {working.Year}. "
                 + $"Poster bokförs med ett datum mellan {working.StartDate:yyyy-MM-dd} och "
                 + $"{working.EndDate:yyyy-MM-dd} — lägg annars upp året som saknas.";
        }
    }
}
