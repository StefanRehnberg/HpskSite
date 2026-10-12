using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Identiteten för en tabell som tar över rader från Umbraco-noder med BEVARADE id:n (skalplanen fas C).
    ///
    /// <para><b>⚠️ Nya rader räknar NEDÅT (<c>IDENTITY(-1,-1)</c>).</b> De flyttade raderna bär nodens id, och
    /// nod-id:n är en global, stigande serie som fortsätter växa så länge sajten lever. Får nya rader positiva
    /// id:n kommer något av dem förr eller senare att vara samma tal som en innehållsnod — och varje fast gräns
    /// uppåt nås av en stigande serie. Negativa id:n utesluter krocken per konstruktion. Samma lösning som
    /// ekonomins sandlåda.</para>
    ///
    /// <para><b>⚠️ Följden:</b> en kontroll som <c>id &gt; 0</c> eller <c>id &lt;= 0</c> är en BUGG för de här
    /// tabellerna. "Ingen" är 0 (spärrat i databasen); fråga <c>id != 0</c>. <c>ORDER BY Id</c> är dessutom
    /// omvänd bland de nya raderna.</para>
    /// </summary>
    public static class NegativeIdentity
    {
        /// <summary>
        /// Sätter identiteten så att nästa rad får ett negativt id under alla befintliga. Måste köras efter en
        /// flytt: <c>IDENTITY_INSERT</c> med positiva värden får inte lämna identiteten på något som räknar
        /// in i nodernas serie. Returnerar värdet identiteten sattes till.
        /// </summary>
        public static int Reseed(IUmbracoDatabase db, string table)
        {
            // Med rader i tabellen blir nästa id (värdet − 1); i en tabell som aldrig haft rader blir det värdet
            // självt. Båda är negativa och fria, och 0 kan aldrig uppstå.
            var lowest = db.ExecuteScalar<int>($"SELECT ISNULL(MIN(Id), 0) FROM {table} WHERE Id < 0");
            var seed = Math.Min(lowest, -1);
            db.Execute($"DBCC CHECKIDENT('{table}', RESEED, {seed}) WITH NO_INFOMSGS");
            return seed;
        }
    }
}
