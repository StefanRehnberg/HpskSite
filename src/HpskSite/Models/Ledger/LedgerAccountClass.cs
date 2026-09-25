namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// BAS-kontoklassernas innebörd och teckenregel, på ETT ställe.
    ///
    /// <para><b>⚠️⚠️ REGELN FANNS REDAN — I TRE SQL-FRÅGOR.</b> <c>LedgerBudgetService</c> och
    /// <c>LedgerProjectService.Summarise</c> bär var sin kopia av
    /// <c>CASE WHEN … THEN Credit − Debit ELSE Debit − Credit END</c>. Bokslutet blev den fjärde,
    /// och tre kopior av en teckenregel är tre chanser att få ett minustecken åt fel håll i en
    /// handling som går till årsmötet. Den här klassen är svaret; frågorna behåller sin SQL men
    /// måste ge <b>samma</b> tal, och det är mätt i <c>LedgerAccountClassTests</c>.</para>
    ///
    /// <para><b>⚠️⚠️ KLASS 8 ÄR DELAD, enligt BAS: 8000–8399 är finansiella INTÄKTER (8310
    /// Ränteintäkter), 8400–8999 är KOSTNADER (8410 Räntekostnader, bokslutsdispositioner, skatt).</b>
    /// Fram till 2026-09-25 räknades hela klass 8 som intäkt, och räntekostnaden hamnade under
    /// intäkterna i budgeten, som en negativ intäkt i resultaträkningen och som "I" i SIE-filen
    /// (Michael Henriksson, Åmåls PK). ⚠️ Samma gräns finns som SQL i
    /// <c>LedgerBudgetService</c> och <c>LedgerProjectService</c> — ändras den här måste de ändras i
    /// samma andetag, annars visar Rapport och Bokslut olika resultat för samma år.</para>
    /// </summary>
    public static class LedgerAccountClass
    {
        /// <summary>Tillgångar.</summary>
        public const int Assets = 1;

        /// <summary>Eget kapital och skulder.</summary>
        public const int EquityAndLiabilities = 2;

        /// <summary>Klassen ur kontonumret. 1930 → 1.</summary>
        public static int Of(int accountNumber) => accountNumber / 1000;

        /// <summary>Hör kontot till balansräkningen (klass 1 och 2)?</summary>
        public static bool IsBalance(int accountNumber)
            => Of(accountNumber) is Assets or EquityAndLiabilities;

        /// <summary>Hör kontot till resultaträkningen (klass 3–8)?</summary>
        public static bool IsResult(int accountNumber) => Of(accountNumber) is >= 3 and <= 8;

        /// <summary>Sista kontot i klass 8 som är en finansiell INTÄKT. 8400 och uppåt är kostnader.</summary>
        public const int LastFinancialIncomeAccount = 8399;

        /// <summary>
        /// Är kontot intäkt-riktat? Klass 3, och 8000–8399 i klass 8.
        /// <para>⚠️ Se klassens sammanfattning om varför klass 8 är delad.</para>
        /// </summary>
        public static bool IsRevenueDirected(int accountNumber)
            => Of(accountNumber) == 3 || (accountNumber >= 8000 && accountNumber <= LastFinancialIncomeAccount);

        /// <summary>
        /// Beloppet i kontots EGEN riktning, alltid positivt när det går åt "rätt" håll.
        ///
        /// <para>En intäkt på 146 000 blir +146000, en kostnad på 95 000 blir +95000. Det är vad
        /// som gör att budget och utfall går att jämföra rakt av — se
        /// <c>create-ledger-budget-tables.sql</c>: teckenlös budget mot tecknat utfall är ett sätt
        /// att få varje differens att peka åt fel håll.</para>
        /// </summary>
        public static decimal InOwnDirection(int accountNumber, decimal debit, decimal credit)
            => IsRevenueDirected(accountNumber) ? credit - debit : debit - credit;

        /// <summary>
        /// Saldot som balansräkningen visar det: tillgångar debet-positiva, skulder och eget
        /// kapital kredit-positiva.
        ///
        /// <para><b>⚠️ Eget kapital är normalt en KREDITPOST.</b> Räknas klass 2 debet-positivt
        /// blir föreningens egna kapital negativt i balansräkningen, och den ser ut att vara
        /// skuldsatt med hela sitt kapital.</para>
        /// </summary>
        public static decimal BalanceAmount(int accountNumber, decimal debit, decimal credit)
            => Of(accountNumber) == Assets ? debit - credit : credit - debit;
    }
}
