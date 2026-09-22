namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Vilket databasschema en utställares rader bor i.
    ///
    /// <para><b>⚠️⚠️ SANDLÅDA OCH SKARP DATA LIGGER I OLIKA TABELLER.</b> Inte i samma tabell med
    /// en flagga — fysiskt åtskilda, i schemat <c>sbx</c> respektive <c>dbo</c>. Stefans krav
    /// 2026-09-22: <i>"blandning av sandlåda och skarp data får inte vara möjlig"</i>. Regler om
    /// vem som får ändra vad räcker inte; raden ska inte kunna hamna i fel tabell oavsett vilken
    /// kod som körs.</para>
    ///
    /// <para><b>Det som gör blandning omöjlig är inte schemat utan de motsatta CHECK-villkoren
    /// i databasen:</b> <c>dbo.Ledger*</c> kräver <c>IssuerId &gt; 0</c>, <c>sbx.Ledger*</c>
    /// kräver <c>IssuerId &lt; 0</c>. En skrivning i fel schema <b>avvisas</b>; en läsning i fel
    /// schema ger <b>tomt</b> — aldrig blandat. Blandat är den farliga varianten.</para>
    ///
    /// <para><b>⚠️ VARJE ledger-fråga ska gå via <see cref="Sql"/>.</b> Det är inte en stilfråga:
    /// en fråga som skickar rå SQL med <c>dbo.Ledger</c> och ett negativt utställar-id läser tom
    /// tabell i stället för sandlådans data, och skriver — om den skriver — mot en spärr. Att det
    /// går att GREPPA efter överträdelser är hela poängen med formen; se
    /// <c>hpsk-verify/ledger-schema-seam-verify.mjs</c>, som felar på varje ledger-fråga som inte
    /// passerat här.</para>
    ///
    /// <para><b>⚠️ <c>LedgerIssuer</c> är UNDANTAGET.</b> Registret över båda slagen måste vara EN
    /// lista — annars kan två utställare få samma id. Den bor bara i <c>dbo</c> och ska aldrig
    /// skrivas om.</para>
    /// </summary>
    public static class LedgerSchema
    {
        public const string Live = "dbo";
        public const string Sandbox = "sbx";

        /// <summary>
        /// Schemat för en utställare. <b>Tecknet avgör</b> — negativa id är sandlådor, och den
        /// regeln är låst i databasen (<c>CK_LedgerIssuer_SandboxNegative</c>).
        /// </summary>
        public static string For(int issuerId) => issuerId < 0 ? Sandbox : Live;

        /// <summary>
        /// Skriver om en ledger-fråga till rätt schema.
        ///
        /// <para>Frågan skrivs alltid med <c>dbo.Ledger…</c> och pekas om hit. Det gör att en
        /// fråga går att läsa som den ser ut i databasen, och att omskrivningen sker på ETT
        /// ställe i stället för i 89 strängar.</para>
        ///
        /// <para>⚠️ <c>LedgerIssuer</c> lämnas orörd — se klassens sammanfattning. Kontrollen är
        /// exakt på ordgränsen, annars hade <c>LedgerIssuerSettings</c> också undantagits.</para>
        /// </summary>
        public static string Sql(int issuerId, string sql)
        {
            if (issuerId >= 0) return sql;

            // Registret först åt sidan, så det inte råkar skrivas om.
            const string keep = "\u0001LEDGERISSUER\u0001";

            return sql
                .Replace("dbo.LedgerIssuer ", keep + " ")
                .Replace("dbo.LedgerIssuer\r", keep + "\r")
                .Replace("dbo.LedgerIssuer\n", keep + "\n")
                .Replace("dbo.Ledger", "sbx.Ledger")
                .Replace(keep, "dbo.LedgerIssuer");
        }
    }
}
