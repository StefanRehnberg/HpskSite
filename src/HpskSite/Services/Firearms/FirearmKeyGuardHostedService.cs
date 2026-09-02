using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Startkontroll: skriker högljutt om det finns krypterad vapendata men ingen nyckel att läsa
    /// den med.
    ///
    /// <para><b>Varför den måste finnas.</b> Utan den startar appen glatt och varje vapenuppgift
    /// börjar besvaras med ett fel — vilket för en användare är oskiljbart från "det står inget här".
    /// Det värsta utfallet i hela funktionen är en tyst nyckelförlust som ingen märker förrän någon
    /// behöver ett föreningsintyg. Kontrollen gör tillståndet synligt i loggen samma minut appen
    /// startar.</para>
    ///
    /// <para><b>Den STOPPAR inte starten, med flit.</b> Att spräcka hela pistol.nu — tävlingar,
    /// anmälningar, resultat — för att en funktion är felkonfigurerad vore en självförvållad
    /// driftstörning som är mycket värre än felet den skyddar mot. Läsvägarna vägrar ändå var för
    /// sig, med meddelanden som namnger konfigurationsnyckeln.</para>
    ///
    /// <para>Kör EN gång vid start. Nycklarna kommer ur konfigurationen och ändras inte under en
    /// process, så en återkommande svepning skulle bara upprepa samma svar.</para>
    /// </summary>
    public class FirearmKeyGuardHostedService : BackgroundService
    {
        /// <summary>Låt sajten starta klart först — databasen kan ännu inte vara nåbar.</summary>
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FirearmKeyGuardHostedService> _logger;

        public FirearmKeyGuardHostedService(
            IServiceScopeFactory scopeFactory,
            ILogger<FirearmKeyGuardHostedService> logger)
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
                var keyRing = scope.ServiceProvider.GetRequiredService<FirearmMasterKeyRing>();
                var vault = scope.ServiceProvider.GetRequiredService<FirearmVaultService>();

                foreach (var error in keyRing.ConfigurationErrors)
                    _logger.LogError("Vapenregistret: felaktig nyckelkonfiguration — {Error}", error);

                var inventory = vault.TryGetInventory();

                if (inventory is null)
                {
                    // Tabellen finns inte, alltså finns ingen krypterad data. En omigrerad miljö är
                    // inte ett larm; den har inget att förlora.
                    _logger.LogInformation(
                        "Vapenregistret: FirearmKeyVault finns inte i databasen (migreringen är inte körd). " +
                        "Ingen krypterad data kan finnas.");
                    return;
                }

                if (inventory.TotalVaults == 0)
                {
                    _logger.LogInformation("Vapenregistret: inga valv ännu. Nyckel konfigurerad: {Configured}.",
                        keyRing.IsConfigured);
                    return;
                }

                if (!keyRing.IsConfigured)
                {
                    _logger.LogCritical(
                        "VAPENREGISTRET ÄR OLÄSBART: {Count} valv finns i databasen men ingen rotnyckel är " +
                        "konfigurerad ('{Path}'). Ingen medlems vapenuppgifter kan läsas. Återställ nyckeln " +
                        "ur backupen — se Documentation/FIREARM_KEY_MANAGEMENT.md. Radera INTE valven.",
                        inventory.TotalVaults, FirearmMasterKeyRing.MasterKeysPath);
                    return;
                }

                if (inventory.UnreadableVaults > 0)
                {
                    _logger.LogCritical(
                        "VAPENREGISTRET DELVIS OLÄSBART: {Unreadable} av {Total} valv är inpackade med " +
                        "nyckelversion {Missing}, som inte finns i '{Path}'. Lägg tillbaka den versionen — " +
                        "en borttagen gammal nyckel får inte tas bort förrän varje valv är ompackat.",
                        inventory.UnreadableVaults, inventory.TotalVaults,
                        string.Join(", ", inventory.MissingVersions), FirearmMasterKeyRing.MasterKeysPath);
                    return;
                }

                if (!keyRing.CanWrapNewKeys)
                {
                    _logger.LogError(
                        "Vapenregistret: befintliga valv är läsbara, men den aktuella nyckelversionen " +
                        "{Version} saknas — nya vapen kan inte läggas in. Se '{Path}'.",
                        keyRing.CurrentKeyVersion, FirearmMasterKeyRing.MasterKeysPath);
                    return;
                }

                _logger.LogInformation(
                    "Vapenregistret: {Total} valv, alla läsbara. Aktuell nyckelversion {Version}.",
                    inventory.TotalVaults, keyRing.CurrentKeyVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Vapenregistrets startkontroll kunde inte genomföras.");
            }
        }
    }
}
