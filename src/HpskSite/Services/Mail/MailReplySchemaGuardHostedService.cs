using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Services.Mail
{
    /// <summary>
    /// Startkontroll: skriker om <c>MailReply</c> saknas i databasen.
    ///
    /// <para><b>⚠️⚠️ VARFÖR DEN FINNS.</b> 2026-09-09 deployades svara-i-appen till prod utan att
    /// migreringen kördes. Följden var inte ett synligt fel utan en TYST återvändsgränd: varje
    /// kompletteringsmejl gick ut med en blå <i>Svara klubben</i>-knapp, sidan öppnades utan
    /// problem (den läser bara förfrågan), medlemmen skrev sitt svar — och sparningen föll på
    /// <i>Invalid object name 'MailReply'</i>. Klubben hörde ingenting och trodde att medlemmen
    /// tigit; medlemmen hade svarat. Exakt den tystnad hela funktionen byggdes för att ta bort,
    /// återinförd av ett glömt operatörssteg.</para>
    ///
    /// <para><b>⚠️ Saknad tabell är ALLTID ett larm här, till skillnad från vapenvalvet.</b> Det
    /// valvet larmar bara när det finns krypterad data att förlora — en omigrerad miljö har inget
    /// att förlora. Men den här funktionen skickar ut svarslänkar oavsett tabellens existens, så en
    /// saknad tabell betyder alltid att länkar som redan ligger i medlemmars inkorgar är döda.
    /// Det finns inget ofarligt "ännu inte migrerat"-läge.</para>
    ///
    /// <para><b>Den STOPPAR inte starten, med flit.</b> Att spräcka hela pistol.nu för en enskild
    /// funktion vore en självförvållad driftstörning som är värre än felet. Samma avvägning, och
    /// samma skäl, som <c>FirearmKeyGuardHostedService</c>.</para>
    ///
    /// <para>Kör EN gång vid start — schemat ändras inte under en process.</para>
    /// </summary>
    public class MailReplySchemaGuardHostedService : BackgroundService
    {
        /// <summary>Låt sajten starta klart först — databasen kan ännu inte vara nåbar.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MailReplySchemaGuardHostedService> _logger;

        public MailReplySchemaGuardHostedService(
            IServiceScopeFactory scopeFactory,
            ILogger<MailReplySchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
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
                using var scope = _scopeFactory.CreateScope();
                var replies = scope.ServiceProvider.GetRequiredService<MailReplyService>();

                if (replies.TableExists())
                {
                    _logger.LogInformation("Svarslagret: MailReply finns. Medlemssvar kan tas emot.");
                    return;
                }

                // ⚠️ Prod kör Serilog på Warning och uppåt, så en Information-rad hade varit
                // osynlig just där felet gör mest skada. Critical är rätt nivå: funktionen ÄR
                // trasig, och den är trasig på ett sätt ingen användare kan rapportera begripligt.
                _logger.LogCritical(
                    "SVARSLAGRET ÄR TRASIGT: tabellen MailReply finns inte i databasen. Varje "
                    + "'Svara klubben'-länk som redan skickats ut är en återvändsgränd — medlemmen "
                    + "får ett felmeddelande och klubben får aldrig svaret. Kör "
                    + "Migrations/create-mail-reply-table.sql (guardad, säker att köra om).");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Svarslagrets startkontroll kunde inte genomföras.");
            }
        }
    }
}
