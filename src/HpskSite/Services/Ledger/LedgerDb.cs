using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// En databasanslutning som vet <b>vilken utställare</b> den arbetar för, och därmed vilket
    /// schema frågorna ska gå mot.
    ///
    /// <para><b>⚠️⚠️ FINNS FÖR ATT SCHEMAT SKA VÄLJAS EN GÅNG, INTE PER FRÅGA.</b> Alternativet
    /// var att skriva om 89 SQL-strängar och hoppas att ingen glöms. En glömd sträng läser tom
    /// tabell i stället för sandlådans data — eller, före CHECK-villkoren, skrev i fel. Här sätts
    /// utställaren när anslutningen öppnas, och varje fråga ärver den.</para>
    ///
    /// <para>Frågorna skrivs fortfarande med <c>dbo.Ledger…</c>, precis som de ser ut i databasen.
    /// Omskrivningen sker i <see cref="LedgerSchema.Sql"/>.</para>
    ///
    /// <para><b>⚠️ Äger INTE anslutningen.</b> Anroparen skapar och gör sig av med sin
    /// <c>IUmbracoDatabase</c> som förut — den här är bara ett lager runt om. Att låta den äga
    /// anslutningen hade gjort det lätt att stänga någon annans.</para>
    /// </summary>
    public sealed class LedgerDb
    {
        private readonly IUmbracoDatabase _db;
        private readonly int _issuerId;

        public LedgerDb(IUmbracoDatabase db, int issuerId)
        {
            _db = db;
            _issuerId = issuerId;
        }

        /// <summary>Den råa anslutningen, för det som inte rör ledger-tabellerna.</summary>
        public IUmbracoDatabase Raw => _db;

        /// <summary>Schemat den här anslutningen arbetar mot. Mest för loggning och tester.</summary>
        public string Schema => LedgerSchema.For(_issuerId);

        private string S(string sql) => LedgerSchema.Sql(_issuerId, sql);

        public List<T> Fetch<T>(string sql, params object[] args) => _db.Fetch<T>(S(sql), args);

        public T? FirstOrDefault<T>(string sql, params object[] args) => _db.FirstOrDefault<T>(S(sql), args);

        public T? SingleOrDefault<T>(string sql, params object[] args) => _db.SingleOrDefault<T>(S(sql), args);

        public T ExecuteScalar<T>(string sql, params object[] args) => _db.ExecuteScalar<T>(S(sql), args);

        public int Execute(string sql, params object[] args) => _db.Execute(S(sql), args);

        /// <summary>
        /// ⚠️ <c>Insert</c> går via NPoco:s <c>[TableName]</c>, som inte bär något schema — den
        /// hamnar därför alltid i anslutningens standardschema (<c>dbo</c>). För en sandlåda MÅSTE
        /// infogningen därför skrivas som SQL och gå genom <see cref="Execute"/>.
        /// Metoden finns här bara för att fånga den som försöker.
        /// </summary>
        public void Insert<T>(T poco) => throw new InvalidOperationException(
            "NPoco.Insert kan inte välja schema — skriv ett INSERT och kör det via LedgerDb.Execute, "
            + "annars hamnar sandlådans rad i den skarpa tabellen.");

        public ITransaction GetTransaction() => _db.GetTransaction();
    }
}
