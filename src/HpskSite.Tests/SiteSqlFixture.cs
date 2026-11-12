using Microsoft.Data.SqlClient;
using NPoco;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna 2a: en NPoco-databas mot <c>HPSK_TEST_SQL</c> (dev-databasen med migreringen körd) i en TRANSAKTION som
    /// alltid rullas tillbaka, så ett prov aldrig lämnar rader efter sig — inte ens när det faller. Samma NPoco som
    /// Umbraco kör, men utan Umbracos scope (provet skriver aldrig i någon annans transaktion).
    /// </summary>
    public sealed class SiteSqlFixture : IDisposable
    {
        public Database Db { get; }

        public SiteSqlFixture()
        {
            var cs = Environment.GetEnvironmentVariable("HPSK_TEST_SQL")
                     ?? throw new InvalidOperationException("HPSK_TEST_SQL är inte satt.");
            Db = new Database(cs, DatabaseType.SqlServer2012, SqlClientFactory.Instance);
            Db.OpenSharedConnection();
            Db.BeginTransaction();
        }

        public void Dispose()
        {
            try { Db.AbortTransaction(); } catch { /* redan avslutad */ }
            Db.Dispose();
        }
    }
}
