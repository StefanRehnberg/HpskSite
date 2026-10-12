using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Data.SqlClient;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// DataProtection-nycklarna i <c>dbo.DataProtectionKey</c> (skalplanen D2, 2026-10-12).
    ///
    /// <para><b>Varför:</b> nycklarna signerar inloggningscookies, antiforgery och de långlivade
    /// länkarna som skrivs ut och mejlas — stations-QR, betallänkar, svarslänkar, justering, ICS.
    /// I <c>App_Data</c> försvinner de vid en serverflytt (alla loggas ut, varje utskriven QR dör)
    /// och kan inte delas mellan två instanser. I databasen följer de med.</para>
    ///
    /// <para><b>⚠️ Egen anslutning med rå SqlClient, aldrig Umbracos scope.</b> DataProtection läser
    /// nycklar mitt i godtyckliga förfrågningar; att låna den ambienta scopen där är precis formen som
    /// låste prod (se CLAUDE.md om <c>IsolatedBackgroundService</c>). Tabellen är vår egen, så den
    /// egna anslutningen konkurrerar inte om någons lås.</para>
    ///
    /// <para><b>Övergången:</b> är tabellen tom vid första läsningen kopieras nyckelfilerna i
    /// <c>App_Data/DataProtection-Keys</c> in, så befintliga inloggningar och utskrivna länkar
    /// fortsätter gälla. Saknas tabellen helt (migreringen inte körd) används filerna som förut och
    /// det larmas Critical — appen ska inte dö på ett glömt operatörssteg. Andra databasfel KASTAR:
    /// att i tysthet skapa nya nycklar på disk vid ett tillfälligt fel hade gett två nyckelringar som
    /// glider isär.</para>
    /// </summary>
    public sealed class SqlDataProtectionKeyRepository : IXmlRepository
    {
        private readonly string _connectionString;
        private readonly IXmlRepository _legacyFiles;
        private readonly DirectoryInfo _legacyDirectory;
        private readonly ILogger _logger;
        private readonly object _gate = new();
        private bool? _tableExists;

        public SqlDataProtectionKeyRepository(string connectionString, DirectoryInfo legacyDirectory,
            IXmlRepository legacyFiles, ILogger logger)
        {
            _connectionString = connectionString;
            _legacyDirectory = legacyDirectory;
            _legacyFiles = legacyFiles;
            _logger = logger;
        }

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            if (!TableExists()) return _legacyFiles.GetAllElements();

            var keys = ReadAll();
            if (keys.Count > 0) return keys;

            // Första läsningen efter migreringen: ta in nycklarna som redan är i bruk.
            lock (_gate)
            {
                keys = ReadAll();
                if (keys.Count > 0) return keys;

                var imported = 0;
                if (_legacyDirectory.Exists)
                {
                    foreach (var file in _legacyDirectory.GetFiles("*.xml"))
                    {
                        try
                        {
                            Insert(XElement.Load(file.FullName), Path.GetFileNameWithoutExtension(file.Name));
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "DataProtection: nyckelfilen {File} kunde inte föras in i databasen.", file.Name);
                        }
                    }
                }
                _logger.LogWarning("DataProtection: {Count} nycklar fördes in från App_Data till databasen.", imported);
                return ReadAll();
            }
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            if (!TableExists())
            {
                _legacyFiles.StoreElement(element, friendlyName);
                return;
            }
            Insert(element, friendlyName);
        }

        private bool TableExists()
        {
            if (_tableExists == true) return true;
            using var conn = Open();
            using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.DataProtectionKey', N'U') IS NULL THEN 0 ELSE 1 END", conn);
            var exists = (int)cmd.ExecuteScalar()! == 1;
            if (!exists && _tableExists != false)
                _logger.LogCritical(
                    "DataProtection: tabellen dbo.DataProtectionKey saknas — nycklarna ligger kvar i App_Data. " +
                    "Kör Migrations/create-dataprotection-key-table.sql. Vid en serverflytt slutar alla inloggningar och utskrivna länkar gälla.");
            _tableExists = exists;
            return exists;
        }

        private List<XElement> ReadAll()
        {
            var list = new List<XElement>();
            using var conn = Open();
            using var cmd = new SqlCommand("SELECT Xml FROM dbo.DataProtectionKey ORDER BY Id", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(XElement.Parse(reader.GetString(0)));
            return list;
        }

        private void Insert(XElement element, string friendlyName)
        {
            using var conn = Open();
            using var cmd = new SqlCommand(
                @"IF NOT EXISTS (SELECT 1 FROM dbo.DataProtectionKey WHERE FriendlyName = @n)
                    INSERT INTO dbo.DataProtectionKey (FriendlyName, Xml) VALUES (@n, @x)", conn);
            cmd.Parameters.AddWithValue("@n", friendlyName ?? Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("@x", element.ToString(SaveOptions.DisableFormatting));
            cmd.ExecuteNonQuery();
        }

        private SqlConnection Open()
        {
            var conn = new SqlConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
