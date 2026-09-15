using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Startkontroll: skriker om <c>EkonomiEnkatSvar</c> saknas — eller saknar en kolumn.
    ///
    /// <para><b>⚠️⚠️ VARFÖR DEN FINNS.</b> 2026-09-15 deployades <c>/ekonomifragor</c> till prod och
    /// sidan renderade perfekt: elva frågor, alla fält, allt såg rätt ut. Men INSERT:en föll, och
    /// den som fyllt i formuläret fick bara <i>"Svaret kunde tyvärr inte sparas"</i>. Sidan mejlas
    /// ut till alla medlemmar med en uppmaning att lägga en halvtimme på den — en tyst trasig
    /// sparning där är värre än att sidan inte funnits. <b>Exakt samma form som
    /// <c>MailReplySchemaGuardHostedService</c> byggdes för, och jag tillämpade inte lärdomen
    /// när den här tabellen skapades.</b></para>
    ///
    /// <para><b>⚠️ KOLUMNERNA KONTROLLERAS, INTE BARA TABELLEN — det är hela poängen.</b> Frågelistan
    /// har vuxit två gånger (åtta frågor → nio → elva), och NPoco genererar ett <c>INSERT</c> med
    /// EN kolumn per egenskap i POCO:n. En tabell som skapades med en kortare lista finns alltså,
    /// svarar på <c>SELECT</c>, och fäller ändå varje sparning på <i>Invalid column name 'SvarF11'</i>.
    /// Ett guard som bara frågar "finns tabellen?" hade svarat "allt är bra" om precis det läget.</para>
    ///
    /// <para><b>Saknad tabell är ALLTID ett larm här</b>, till skillnad från vapenvalvet som bara
    /// larmar när det finns krypterad data att förlora. Den här sidan är publik och utskicket kan
    /// redan ligga i tjugotusen inkorgar — det finns inget ofarligt "ännu inte migrerat"-läge.</para>
    ///
    /// <para><b>Den STOPPAR inte starten, med flit.</b> Att spräcka hela pistol.nu för en enskild
    /// funktion vore en självförvållad driftstörning som är värre än felet. Samma avvägning som
    /// <c>FirearmKeyGuardHostedService</c> och <c>MailReplySchemaGuardHostedService</c>.</para>
    ///
    /// <para><b>⚠️⚠️ ANVÄNDER <see cref="IUmbracoDatabaseFactory"/>, ALDRIG <c>IScopeProvider</c>.</b>
    /// En första version skapade ett Umbraco-scope här och fick
    /// <i>"The Scope … being disposed is not the Ambient Scope"</i> vid disponeringen — Umbracos
    /// ambienta scope är en <c>AsyncLocal</c> och flyter med execution context in i bakgrundstråden.
    /// Det är samma mekanism som låste prod 2026-08-29 genom att lämna innehållslåset -333 hängande
    /// i en övergiven transaktion. En schemakontroll behöver inget scope: den ställer två skalära
    /// frågor och ska inte delta i någon transaktion alls.</para>
    /// </summary>
    public class EkonomiEnkatSchemaGuardHostedService : BackgroundService
    {
        /// <summary>Låt sajten starta klart först — databasen kan ännu inte vara nåbar.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<EkonomiEnkatSchemaGuardHostedService> _logger;

        public EkonomiEnkatSchemaGuardHostedService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<EkonomiEnkatSchemaGuardHostedService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.Delay(StartupDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var tableExists = db.ExecuteScalar<int>(
                    "SELECT CASE WHEN OBJECT_ID('dbo.EkonomiEnkatSvar','U') IS NULL THEN 0 ELSE 1 END");

                if (tableExists == 0)
                {
                    _logger.LogCritical(
                        "EKONOMIFRÅGORNA ÄR TRASIGA: tabellen EkonomiEnkatSvar finns inte. Sidan "
                        + "/ekonomifragor renderar men VARJE svar går förlorat — och sidan mejlas ut "
                        + "till medlemmarna med en uppmaning att lägga en halvtimme på den. Kör "
                        + "Migrations/create-ekonomienkat-svar-table.sql (guardad, säker att köra om).");
                    return;
                }

                // ⚠️ Kolumnlistan HÄRLEDS ur frågelistan, den skrivs aldrig av för hand. En andra
                // lista glider isär från den första, och då kontrollerar guarden ett schema som
                // inte längre är det koden skriver mot.
                var saknade = Models.EkonomiEnkat.Fragor
                    .Select(f => "Svar" + f.Id)
                    .Where(col => db.ExecuteScalar<int>(
                        $"SELECT CASE WHEN COL_LENGTH('dbo.EkonomiEnkatSvar','{col}') IS NULL THEN 1 ELSE 0 END") == 1)
                    .ToList();

                if (saknade.Count > 0)
                {
                    _logger.LogCritical(
                        "EKONOMIFRÅGORNA ÄR TRASIGA: tabellen EkonomiEnkatSvar saknar kolumnerna {Saknade}. "
                        + "Tabellen skapades med en kortare frågelista, så sidan renderar och SELECT "
                        + "fungerar — men varje sparning faller på 'Invalid column name'. Kör "
                        + "Migrations/create-ekonomienkat-svar-table.sql igen (guardad per kolumn).",
                        string.Join(", ", saknade));
                    return;
                }

                _logger.LogInformation(
                    "Ekonomifrågorna: EkonomiEnkatSvar finns med alla {Antal} svarskolumner.",
                    Models.EkonomiEnkat.Fragor.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ekonomifrågornas startkontroll kunde inte genomföras.");
            }
        }
    }
}
