using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HpskSite.Models.Ledger;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Läser verifikationsliggarens schema och svarar på om det är helt. Enda uppslaget —
    /// <see cref="LedgerSchemaGuardHostedService"/> (som skriker i loggen vid uppstart) och
    /// <c>/health/ledger</c> (som svarar på begäran) ställer <b>samma</b> frågor genom den här
    /// klassen.
    ///
    /// <para><b>⚠️ DÄRFÖR FINNS DEN: två kontroller av samma sak blir två svar som är fria att
    /// säga emot varandra.</b> Guardens kolumnlista härleds ur POCO:erna just för att den inte ska
    /// kunna glida från verkligheten; en andra, handskriven kontroll i en controller hade
    /// återinfört exakt det problemet ett steg bort. Lägger du till en tabell i lagret räcker det
    /// att lägga till typen i <see cref="LedgerTypes"/> — båda ytorna följer med.</para>
    ///
    /// <para><b>⚠️ LÄSER ENBART METADATA, OCH DELTAR ALDRIG I EN TRANSAKTION.</b> Ett Umbraco-scope
    /// tar innehållslås, och ett innehållslås som blir hängande i en övergiven transaktion låste
    /// prod i tre timmar 2026-08-29. Därför <see cref="IUmbracoDatabaseFactory.CreateDatabase"/>
    /// rakt av, skalära frågor, inget scope.</para>
    ///
    /// <para><b>⚠️ SVARAR, HÄNGER ALDRIG.</b> Prods anslutningssträng har <c>Command Timeout=120</c>.
    /// En kontroll som hänger i två minuter är värdelös på en hälsoendpoint — och blir dessutom
    /// ännu en väntande session under en låsstorm. Både kommandots timeout och
    /// <c>LOCK_TIMEOUT</c> sätts lågt: vi vill ha ett SVAR, inte ett resultat.</para>
    /// </summary>
    public class LedgerSchemaInspector
    {
        /// <summary>
        /// POCO:erna som utgör liggaren. Kolumnerna läses ur dem, aldrig ur en handskriven lista.
        /// <para>Lägger du till en tabell i lagret: lägg till typen här, annars kontrolleras den
        /// inte — och då är vi tillbaka i det tysta läget hela lagret finns för att undvika.</para>
        /// </summary>
        private static readonly Type[] LedgerTypes =
        {
            typeof(LedgerIssuerSettings),
            typeof(LedgerNumberSeries),
            typeof(LedgerFiscalYear),
            typeof(LedgerAccount),
            typeof(LedgerAccountRole),
            typeof(LedgerProject),
            typeof(LedgerJournalEntry),
            typeof(LedgerJournalEntryLine),
            typeof(LedgerAttachment),
            typeof(LedgerApproval),
            typeof(LedgerAuditEvent),
            typeof(LedgerJournalEntryDraft),
            typeof(LedgerJournalEntryDraftLine),
            typeof(LedgerNumberGap),
            typeof(LedgerPayment),
            typeof(LedgerReceipt)
        };

        /// <summary>
        /// Triggarna som bär oföränderligheten och periodlåsningen. Det är de här som gör lagret
        /// till en garanti i stället för en överenskommelse — och de har ingen annan larmklocka.
        /// </summary>
        private static readonly string[] RequiredTriggers =
        {
            "TR_LedgerJournalEntry_NoUpdateDelete",
            "TR_LedgerJournalEntryLine_NoUpdateDelete",
            "TR_LedgerJournalEntry_NoWriteInEstablishedYear",
            "TR_LedgerReceipt_NoUpdateDelete"
        };

        // Båda skripten namnges: en saknad momskolumn kommer ur det andra, och ett meddelande som
        // pekar på fel skript skickar operatören att köra om ett som inte hjälper.
        public const string MigrationScript =
            "Migrations/create-ledger-tables.sql + add-vat-to-ledger.sql + create-ledger-draft-tables.sql "
            + "+ create-ledger-payment-tables.sql + add-project-dimension-to-ledger.sql";

        /// <summary>Hur länge en låsbegäran får vänta. Blockerar något är det svaret vi vill ha.</summary>
        private const int LockTimeoutMs = 3000;

        /// <summary>Backstop om något annat än låset hänger. Måste vara &gt; LockTimeoutMs.</summary>
        private const int CommandTimeoutSeconds = 10;

        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public LedgerSchemaInspector(IUmbracoDatabaseFactory databaseFactory)
        {
            _databaseFactory = databaseFactory;
        }

        public int TableCount => LedgerTypes.Length;

        public int TriggerCount => RequiredTriggers.Length;

        /// <summary>
        /// Kontrollerar schemat och returnerar vad som fattas. Kastar aldrig — ett fel blir
        /// <see cref="LedgerSchemaStatus.CouldNotCheck"/>, eftersom "vi vet inte" är ett annat
        /// svar än "allt är helt" och de två aldrig får se likadana ut.
        /// </summary>
        public LedgerSchemaReport Inspect()
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                db.CommandTimeout = CommandTimeoutSeconds;

                // 1. Tabellerna. Saknas ALLA är liggaren helt enkelt inte migrerad ännu — ett
                //    väntat läge före första körningen, som inte ska låta som en katastrof.
                var expectedTables = LedgerTypes.Select(TableNameOf).ToArray();
                var existingTables = FetchNames(
                    db,
                    "SELECT t.name FROM sys.tables t WHERE SCHEMA_NAME(t.schema_id) = 'dbo' AND t.name IN (@0)",
                    expectedTables);

                var missingTables = expectedTables
                    .Where(t => !existingTables.Contains(t))
                    .ToList();

                if (missingTables.Count == expectedTables.Length)
                {
                    return new LedgerSchemaReport { Status = LedgerSchemaStatus.NotMigrated };
                }

                if (missingTables.Count > 0)
                {
                    return new LedgerSchemaReport
                    {
                        Status = LedgerSchemaStatus.HalfMigrated,
                        MissingTables = missingTables
                    };
                }

                // 2. Kolumnerna, härledda ur POCO:erna. NPoco genererar en kolumn per egenskap, så
                //    EN saknad kolumn fäller varje sparning mot den tabellen — inte bara läsningen.
                //    ⚠️ ETT anrop för alla tabeller. Per kolumn blir det ~170 tur och retur, vilket
                //    en endpoint som får pollas inte ska kosta.
                var existingColumns = new HashSet<string>(
                    FetchNames(
                        db,
                        @"SELECT t.name + '.' + c.name
                            FROM sys.columns c
                            JOIN sys.tables t ON t.object_id = c.object_id
                           WHERE SCHEMA_NAME(t.schema_id) = 'dbo' AND t.name IN (@0)",
                        expectedTables),
                    StringComparer.OrdinalIgnoreCase);

                var missingColumns = new List<string>();
                foreach (var type in LedgerTypes)
                {
                    var table = TableNameOf(type);
                    foreach (var column in ColumnsOf(type))
                    {
                        if (!existingColumns.Contains($"{table}.{column}"))
                        {
                            missingColumns.Add($"{table}.{column}");
                        }
                    }
                }

                if (missingColumns.Count > 0)
                {
                    return new LedgerSchemaReport
                    {
                        Status = LedgerSchemaStatus.MissingColumns,
                        MissingColumns = missingColumns
                    };
                }

                // 3. ⚠️ TRIGGARNA. En tabell utan sina triggers ser fullständigt frisk ut: den tar
                //    emot poster, den läser tillbaka dem, ingenting felar. Det enda som är borta är
                //    garantin att siffrorna inte kan ändras i efterhand.
                // ⚠️⚠️ `is_disabled = 0` ÄR INTE EN DETALJ — EN AVSTÄNGD TRIGGER ÄR INGEN GARANTI.
                // Frågan läste tidigare bara namnet, och `DISABLE TRIGGER` tar inte bort raden ur
                // sys.triggers. En operatör som stänger av spärren för att rätta en rad och glömmer
                // slå på den igen lämnar alltså liggaren helt oskyddad medan /health/ledger svarar
                // OK — vilket är sämre än ingen kontroll, för då litar någon på svaret. En avstängd
                // trigger räknas därför som SAKNAD.
                var existingTriggers = FetchNames(
                    db,
                    "SELECT name FROM sys.triggers WHERE name IN (@0) AND is_disabled = 0",
                    RequiredTriggers);

                var missingTriggers = RequiredTriggers
                    .Where(t => !existingTriggers.Contains(t))
                    .ToList();

                if (missingTriggers.Count > 0)
                {
                    return new LedgerSchemaReport
                    {
                        Status = LedgerSchemaStatus.MissingTriggers,
                        MissingTriggers = missingTriggers
                    };
                }

                // 4. Rollmappningen — men bara för utställare som faktiskt bokfört något. En saknad
                //    roll är en betalning som inte går att bokföra, och det ska upptäckas här och
                //    inte av kassören mitt i en tävlingsdag.
                //    ⚠️ Enda frågan som rör riktiga rader och alltså kan blockera bakom en skrivare.
                //    ⚠️⚠️ LOCK_TIMEOUT MÅSTE SÄTTAS I EN EGEN SATS PÅ EN DELAD ANSLUTNING.
                //    NPoco genererar `SELECT * FROM <T>` så fort SQL-strängen inte BÖRJAR med
                //    SELECT — ett `SET LOCK_TIMEOUT …;` före frågan gav därför
                //    "Invalid object name 'IssuerRoleGap'", alltså en fråga efter en tabell
                //    uppkallad efter POCO:n. Och timeouten gäller per SESSION, så de två satserna
                //    måste dela anslutning för att den ska betyda något.
                List<IssuerRoleGap> gaps;
                db.OpenSharedConnection();
                try
                {
                    db.Execute($"SET LOCK_TIMEOUT {LockTimeoutMs}");
                    gaps = db.Fetch<IssuerRoleGap>(
                        @"SELECT e.IssuerType, e.IssuerId, COUNT(DISTINCT r.RoleKey) AS MappedRoles
                            FROM dbo.LedgerJournalEntry e
                            LEFT JOIN dbo.LedgerAccountRole r
                                   ON r.IssuerType = e.IssuerType AND r.IssuerId = e.IssuerId
                           GROUP BY e.IssuerType, e.IssuerId
                          HAVING COUNT(DISTINCT r.RoleKey) < @0",
                        LedgerAccountRoles.All.Length);
                }
                finally
                {
                    db.CloseSharedConnection();
                }

                if (gaps.Count > 0)
                {
                    return new LedgerSchemaReport
                    {
                        Status = LedgerSchemaStatus.RolesIncomplete,
                        RequiredRoles = LedgerAccountRoles.All.Length,
                        IssuerRoleGaps = gaps
                            .Select(g => $"typ {g.IssuerType}/id {g.IssuerId}: {g.MappedRoles} av {LedgerAccountRoles.All.Length}")
                            .ToList()
                    };
                }

                return new LedgerSchemaReport { Status = LedgerSchemaStatus.Ok };
            }
            catch (Exception ex)
            {
                return new LedgerSchemaReport
                {
                    Status = LedgerSchemaStatus.CouldNotCheck,
                    Error = ex.Message
                };
            }
        }

        /// <summary>
        /// ⚠️ <c>IN (@0)</c> med en array expanderar NPoco till en parameter per element. Taket
        /// ligger kring 2100 och nås tyst; här är listorna 16 respektive 4 och alltså aldrig nära.
        /// </summary>
        private static HashSet<string> FetchNames(IUmbracoDatabase db, string sql, string[] values)
            => new HashSet<string>(db.Fetch<string>(sql, new object[] { values }), StringComparer.OrdinalIgnoreCase);

        /// <summary>Tabellnamnet ur POCO:ns <see cref="TableNameAttribute"/>.</summary>
        private static string TableNameOf(Type type)
            => type.GetCustomAttribute<TableNameAttribute>()?.Value ?? type.Name;

        /// <summary>
        /// Kolumnerna NPoco skriver för typen: varje läs- och skrivbar egenskap som inte är
        /// <see cref="IgnoreAttribute"/> eller <see cref="ResultColumnAttribute"/>.
        /// </summary>
        private static IEnumerable<string> ColumnsOf(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .Where(p => p.CanRead && p.CanWrite)
                   .Where(p => p.GetCustomAttribute<IgnoreAttribute>() == null)
                   .Where(p => p.GetCustomAttribute<ResultColumnAttribute>() == null)
                   .Select(p => p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name);

        private class IssuerRoleGap
        {
            public int IssuerType { get; set; }
            public int IssuerId { get; set; }
            public int MappedRoles { get; set; }
        }
    }

    /// <summary>
    /// Vad kontrollen kom fram till.
    ///
    /// <para><b>⚠️ <see cref="NotMigrated"/> och <see cref="CouldNotCheck"/> är EGNA lägen, inte
    /// varianter av "fel".</b> "Bokföringen är inte tagen i bruk ännu" är ett riktigt och väntat
    /// tillstånd, och "vi kunde inte fråga" är inte samma sak som "allt är helt". Slås något av
    /// dem ihop med de andra är kontrollen tillbaka i den tystnad den finns för att bryta.</para>
    /// </summary>
    public enum LedgerSchemaStatus
    {
        Ok,
        NotMigrated,
        HalfMigrated,
        MissingColumns,
        MissingTriggers,
        RolesIncomplete,
        CouldNotCheck
    }

    public class LedgerSchemaReport
    {
        public LedgerSchemaStatus Status { get; set; }

        public List<string> MissingTables { get; set; } = new();

        public List<string> MissingColumns { get; set; } = new();

        public List<string> MissingTriggers { get; set; } = new();

        public List<string> IssuerRoleGaps { get; set; } = new();

        public int RequiredRoles { get; set; }

        public string? Error { get; set; }

        /// <summary>
        /// Sant bara när schemat är trasigt — alltså när någon måste köra en migrering. Varken
        /// "inte migrerad ännu" eller en ofullständig rollmappning hör hit: det första är ett
        /// väntat tillstånd, det andra en inställning som saknas och inte ett brutet schema.
        /// </summary>
        public bool IsBroken =>
            Status is LedgerSchemaStatus.HalfMigrated
                   or LedgerSchemaStatus.MissingColumns
                   or LedgerSchemaStatus.MissingTriggers;
    }
}
