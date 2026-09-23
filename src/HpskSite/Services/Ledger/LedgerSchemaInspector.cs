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
            typeof(LedgerReceipt),
            typeof(LedgerBudget),
            typeof(LedgerBudgetLine),
            // ⚠️ Bankavstämningen (P11). Lägg ALLTID till en ny liggartabell här i samma
            //    omgång som migreringen — annars är en tabell som saknas i prod osynlig för
            //    startkontrollen, och det är precis det tysta läget hela lagret finns för.
            typeof(LedgerBankImport),
            typeof(LedgerBankRow),

            // ⚠️ Anläggningsregistret (P9) och utgiftssidan (P6). Båda kräver sin migrering FÖRE
            //    deploy — NPoco genererar SET-satser mot dem så fort POCO:n finns, så en saknad
            //    tabell fäller varje skrivning. Startkontrollen är det som gör den saknaden
            //    högljudd i stället för tyst.
            typeof(LedgerAsset),
            typeof(LedgerExpense)
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
            "TR_LedgerReceipt_NoUpdateDelete",
            // ⚠️ Budgetens spärr hör hit av samma skäl som de andra: utan den är
            // "en antagen budget kan inte skrivas om" en överenskommelse, inte en garanti.
            "TR_LedgerBudget_AdoptedIsFinal",
            "TR_LedgerBudgetLine_AdoptedIsFinal",
            // ⚠️ Bilagan har samma krav: underlaget är räkenskapsinformation och bevaras i sju
            // år. Utan den här raden ser en prod som saknar migreringen fullständigt frisk ut.
            "TR_LedgerAttachment_NoDelete"
        };

        /// <summary>
        /// Sandlådans egna oföränderlighetstriggrar.
        ///
        /// <para><b>⚠️ En sandlåda ska bete sig som verkligheten.</b> Går en verifikation att
        /// radera där övar kassören in en vana som inte finns i skarp drift, och sandlådan slutar
        /// pröva det den finns för. Årslåsningen har ingen motsvarighet här — den hänger på
        /// <c>LedgerFiscalYear</c> i dbo.</para>
        /// </summary>
        private static readonly string[] RequiredSandboxTriggers =
        {
            "TR_sbx_LedgerJournalEntry_NoUpdateDelete",
            "TR_sbx_LedgerJournalEntryLine_NoUpdateDelete",
            "TR_sbx_LedgerReceipt_NoUpdateDelete",
            "TR_sbx_LedgerBudget_AdoptedIsFinal",
            "TR_sbx_LedgerBudgetLine_AdoptedIsFinal",
            "TR_sbx_LedgerAttachment_NoDelete"
        };

        // Båda skripten namnges: en saknad momskolumn kommer ur det andra, och ett meddelande som
        // pekar på fel skript skickar operatören att köra om ett som inte hjälper.
        public const string MigrationScript =
            "Migrations/create-ledger-tables.sql + add-vat-to-ledger.sql + create-ledger-draft-tables.sql "
            + "+ create-ledger-payment-tables.sql + add-project-dimension-to-ledger.sql "
            + "+ create-ledger-budget-tables.sql";

        /// <summary>Skriptet som skapar sandlådans schema. Ett eget svar kräver ett eget skript.</summary>
        public const string SandboxMigrationScript =
            "Migrations/create-sbx-schema.sql + sbx-negative-identities.sql";

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

        public int SandboxTriggerCount => RequiredSandboxTriggers.Length;

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
                var existingTables = FetchTables(db, LedgerSchema.Live, expectedTables);

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
                var missingColumns = MissingColumnsIn(db, LedgerSchema.Live);

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

                // 4. ⚠️⚠️ SANDLÅDAN ÄR ETT EGET SCHEMA, OCH DEN MÅSTE KONTROLLERAS SEPARAT.
                //    En halvskapad sbx ser ut som ingenting från dbo:s håll: den skarpa sidan är
                //    hel, varje skarp fråga går igenom, och det enda som är trasigt är den yta vi
                //    ber klubbarna prova i. En läsning i ett schema som saknar tabellen kastar,
                //    och en läsning i ett schema som saknar en KOLUMN fäller varje sparning —
                //    men bara för negativa utställare, alltså bara i sandlådan.
                var sandboxMissing = new List<string>();
                var sandboxTables = FetchTables(db, LedgerSchema.Sandbox, expectedTables);

                // Inget alls = sandlådan är inte skapad ännu. Ett väntat läge, inte ett haveri —
                // och medvetet skilt från "halvt skapad", som är det farliga.
                var sandboxStatus = sandboxTables.Count == 0
                    ? LedgerSandboxSchemaStatus.NotCreated
                    : LedgerSandboxSchemaStatus.Ok;

                if (sandboxStatus == LedgerSandboxSchemaStatus.Ok)
                {
                    sandboxMissing.AddRange(expectedTables
                        .Where(t => !sandboxTables.Contains(t))
                        .Select(t => $"{LedgerSchema.Sandbox}.{t}"));

                    sandboxMissing.AddRange(MissingColumnsIn(db, LedgerSchema.Sandbox)
                        .Select(c => $"{LedgerSchema.Sandbox}.{c}"));

                    var sandboxTriggers = FetchNames(
                        db,
                        "SELECT name FROM sys.triggers WHERE name IN (@0) AND is_disabled = 0",
                        RequiredSandboxTriggers);

                    sandboxMissing.AddRange(RequiredSandboxTriggers.Where(t => !sandboxTriggers.Contains(t)));

                    if (sandboxMissing.Count > 0)
                    {
                        return new LedgerSchemaReport
                        {
                            Status = LedgerSchemaStatus.SandboxIncomplete,
                            SandboxStatus = LedgerSandboxSchemaStatus.Incomplete,
                            SandboxMissing = sandboxMissing
                        };
                    }
                }

                // 5. ⚠️⚠️ DE MOTSATTA CHECK-VILLKOREN. Det är INTE schemat som gör blandning
                //    omöjlig — det är de här. dbo kräver IssuerId > 0, sbx kräver IssuerId < 0,
                //    så en skrivning i fel schema AVVISAS och en läsning i fel schema ger TOMT.
                //    Tas en av dem bort ser allting friskt ut ända tills en sandlåderad ligger i
                //    den skarpa tabellen, och då är den oföränderlig.
                //    Kontrolleras bara när sandlådan finns: skriptet skapar båda sidorna.
                var missingGuards = new List<string>();

                if (sandboxStatus == LedgerSandboxSchemaStatus.Ok)
                {
                    var guardTables = LedgerTypes
                        .Where(HasIssuerId)
                        .Select(TableNameOf)
                        .ToArray();

                    var expectedGuards = guardTables
                        .SelectMany(t => new[] { $"CK_dbo_{t}_Live", $"CK_sbx_{t}_Sandbox" })
                        .ToArray();

                    // ⚠️ `is_not_trusted` läses INTE som saknad: dev:s gamla rader tvingade fram
                    //    NOCHECK på dbo-sidan, men villkoret gäller varje NY rad ändå — och det
                    //    är nya rader som är risken.
                    var existingGuards = FetchNames(
                        db,
                        "SELECT name FROM sys.check_constraints WHERE name IN (@0) AND is_disabled = 0",
                        expectedGuards);

                    missingGuards.AddRange(expectedGuards.Where(g => !existingGuards.Contains(g)));

                    if (missingGuards.Count > 0)
                    {
                        return new LedgerSchemaReport
                        {
                            Status = LedgerSchemaStatus.MissingGuards,
                            SandboxStatus = sandboxStatus,
                            MissingGuards = missingGuards
                        };
                    }
                }

                // 6. Rollmappningen — men bara för utställare som faktiskt bokfört något. En saknad
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
                    // LEDGER-SEAM-OK: kontrollen fragar avsiktligt bara den SKARPA liggaren.
                    // En sandlada utan kontoroller ar inte ett driftlarm - den ar ett test som
                    // inte gar att bokfora i, och det marker den som testar direkt.
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
                        SandboxStatus = sandboxStatus,
                        RequiredRoles = LedgerAccountRoles.All.Length,
                        IssuerRoleGaps = gaps
                            .Select(g => $"typ {g.IssuerType}/id {g.IssuerId}: {g.MappedRoles} av {LedgerAccountRoles.All.Length}")
                            .ToList()
                    };
                }

                return new LedgerSchemaReport
                {
                    Status = LedgerSchemaStatus.Ok,
                    SandboxStatus = sandboxStatus
                };
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

        /// <summary>Tabellerna som finns i ett givet schema, av dem vi väntar oss.</summary>
        private static HashSet<string> FetchTables(IUmbracoDatabase db, string schema, string[] expected)
            => new HashSet<string>(
                db.Fetch<string>(
                    "SELECT t.name FROM sys.tables t WHERE SCHEMA_NAME(t.schema_id) = @0 AND t.name IN (@1)",
                    schema, expected),
                StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Kolumnerna som saknas i ett schema, härledda ur POCO:erna. Samma lista för båda
        /// schemana — sandlådans tabeller är klonade ur de skarpa och ska aldrig skilja sig.
        /// </summary>
        private static List<string> MissingColumnsIn(IUmbracoDatabase db, string schema)
        {
            var expectedTables = LedgerTypes.Select(TableNameOf).ToArray();

            var existingColumns = new HashSet<string>(
                db.Fetch<string>(
                    @"SELECT t.name + '.' + c.name
                        FROM sys.columns c
                        JOIN sys.tables t ON t.object_id = c.object_id
                       WHERE SCHEMA_NAME(t.schema_id) = @0 AND t.name IN (@1)",
                    schema, expectedTables),
                StringComparer.OrdinalIgnoreCase);

            var missing = new List<string>();
            foreach (var type in LedgerTypes)
            {
                var table = TableNameOf(type);
                foreach (var column in ColumnsOf(type))
                {
                    if (!existingColumns.Contains($"{table}.{column}"))
                    {
                        missing.Add($"{table}.{column}");
                    }
                }
            }

            return missing;
        }

        /// <summary>Bär tabellen en utställare? Bara de kan bära de motsatta CHECK-villkoren.</summary>
        private static bool HasIssuerId(Type type)
            => type.GetProperty("IssuerId", BindingFlags.Public | BindingFlags.Instance) is not null;

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
        SandboxIncomplete,
        MissingGuards,
        RolesIncomplete,
        CouldNotCheck
    }

    /// <summary>
    /// Sandlådeschemats läge.
    ///
    /// <para><b>⚠️ <see cref="NotCreated"/> och <see cref="Incomplete"/> är inte varianter av
    /// varandra.</b> Att sandlådan inte är skapad är ett väntat läge före den tas i bruk; att den
    /// är halvskapad betyder att klubbarna möter fel i den yta vi bett dem prova i, medan den
    /// skarpa sidan ser fullständigt frisk ut.</para>
    /// </summary>
    public enum LedgerSandboxSchemaStatus
    {
        Ok,
        NotCreated,
        Incomplete
    }

    public class LedgerSchemaReport
    {
        public LedgerSchemaStatus Status { get; set; }

        public List<string> MissingTables { get; set; } = new();

        public List<string> MissingColumns { get; set; } = new();

        public List<string> MissingTriggers { get; set; } = new();

        /// <summary>De motsatta CHECK-villkoren som saknas. Utan dem är blandning möjlig igen.</summary>
        public List<string> MissingGuards { get; set; } = new();

        public LedgerSandboxSchemaStatus SandboxStatus { get; set; }

        /// <summary>Tabeller, kolumner och triggrar som saknas i <c>sbx</c>.</summary>
        public List<string> SandboxMissing { get; set; } = new();

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
                   or LedgerSchemaStatus.MissingTriggers
                   or LedgerSchemaStatus.SandboxIncomplete
                   or LedgerSchemaStatus.MissingGuards;
    }
}
