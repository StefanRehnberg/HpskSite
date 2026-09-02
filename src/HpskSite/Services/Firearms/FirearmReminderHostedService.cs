using HpskSite.Services.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Påminner medlemmen innan licensen förfaller. Tre steg: <b>90 dagar · 30 dagar · förfallen</b>
    /// (Stefans val 2026-09-02).
    ///
    /// <para><b>⚠️ Svepet läser BARA klartextkolumnen <c>LicenseExpiresOn</c>.</b> Det är hela skälet
    /// datumet inte ligger i den krypterade bloben: en nattlig avkryptering av varje medlems vapen
    /// vore precis den breda avkryptering konstruktionen finns för att undvika, och läsloggen skulle
    /// dessutom fyllas av systembrus.</para>
    ///
    /// <para><b>⚠️ CLAIM-THEN-SEND.</b> Raden i <c>FirearmReminder</c> skrivs FÖRE utskicket, och det
    /// unika indexet är det som garanterar att ingen påminns två gånger — inte den här koden. En
    /// krasch mellan skrivning och utskick kostar en missad påminnelse; motsatt ordning kostar spam,
    /// och spam är vad som gör att folk stänger av notiser helt.</para>
    ///
    /// <para><b>Opt-in prövas SIST, per medlem</b> — motsatt ordning jämfört med schemasvepet. Där
    /// är opt-in en förutsättning för att arbetet ska vara värt att göra; här ska påminnelsen synas i
    /// appen även för den som inte har push, så svepet får inte hoppa över hens vapen.</para>
    /// </summary>
    public class FirearmReminderHostedService : BackgroundService
    {
        /// <summary>Dagar kvar vid respektive steg. 0 = förfallen.</summary>
        private static readonly int[] Stages = { 90, 30, 0 };

        /// <summary>
        /// En gång per dygn räcker: ett förfallodatum rör sig inte, och en licensförnyelse tar
        /// veckor. Ett tätare svep skulle bara läsa samma rader om.
        /// </summary>
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(4);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<FirearmReminderHostedService> _logger;

        public FirearmReminderHostedService(
            IServiceScopeFactory scopeFactory,
            ILogger<FirearmReminderHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "FirearmReminderHostedService started (steps {Stages}, every {Hours} h).",
                string.Join("/", Stages), Interval.TotalHours);

            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunSafelyAsync(stoppingToken);
                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        private async Task RunSafelyAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scopeProvider = scope.ServiceProvider.GetRequiredService<IScopeProvider>();
                var push = scope.ServiceProvider.GetRequiredService<WebPushService>();

                var due = LoadDue(scopeProvider);
                if (due.Count == 0) return;

                var pushOptIn = push.IsConfigured
                    ? push.GetLicenseReminderMemberIds()
                    : new HashSet<int>();

                var claimed = 0;
                var pushed = 0;

                foreach (var row in due)
                {
                    if (ct.IsCancellationRequested) return;

                    var stage = StageFor(row.DaysLeft);
                    if (stage is null) continue;

                    // Skriv först. Vinner vi inte kapplöpningen är påminnelsen redan skickad.
                    if (!TryClaim(scopeProvider, row, stage.Value)) continue;
                    claimed++;

                    if (!pushOptIn.Contains(row.MemberId)) continue;

                    var (title, body) = Message(row, stage.Value);
                    try
                    {
                        // Länken går till vapenfliken, som är där förnyelsen faktiskt börjar.
                        await push.SendLicenseReminderAsync(
                            row.MemberId, title, body,
                            "/user-profile-page/#firearms-member-pane",
                            $"licens-{row.FirearmId}");
                        pushed++;
                    }
                    catch (Exception ex)
                    {
                        // Utskicket får inte rulla tillbaka claimet: raden är sann ("vi försökte
                        // vid det här datumet"), och ett omförsök varje halvdygn vore spam.
                        _logger.LogWarning(ex,
                            "Licenspåminnelse kunde inte skickas till medlem {MemberId} (vapen {FirearmId}).",
                            row.MemberId, row.FirearmId);
                    }
                }

                if (claimed > 0)
                    _logger.LogInformation(
                        "Licenspåminnelser: {Claimed} registrerade, {Pushed} skickade som push.",
                        claimed, pushed);
            }
            catch (OperationCanceledException) { /* avstängning */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Licenspåminnelsesvepet misslyckades.");
            }
        }

        /// <summary>
        /// Vapen vars licens förfaller inom det längsta steget, eller redan har förfallit.
        ///
        /// <para><b>⚠️ Bara MEDLEMMARS vapen.</b> Ett klubbvapens licens är föreningens ansvar och
        /// har ingen enskild person att påminna — en påminnelse till "klubben" har ingen mottagare.
        /// Det är en egen post om det ska byggas.</para>
        ///
        /// <para><b>⚠️ Bara <c>Innehas</c>.</b> Ett planerat vapen har ingen licens att förnya, och
        /// ett avvecklat ska inte påminna om något.</para>
        ///
        /// <para>Ett förfallet vapen tas med tills det är 400 dagar gammalt. Utan ett tak skulle
        /// varje gammal rad läsas om vid varje svep i all framtid; med taket slutar den efter ett år,
        /// och då har medlemmen fått tre påminnelser.</para>
        /// </summary>
        private List<DueRow> LoadDue(IScopeProvider scopeProvider)
        {
            try
            {
                using var uow = scopeProvider.CreateScope(autoComplete: true);
                return uow.Database.Fetch<DueRow>(
                    @"SELECT Id AS FirearmId, ScopeId AS MemberId, Alias, LicenseExpiresOn,
                             DATEDIFF(day, CAST(GETDATE() AS DATE), LicenseExpiresOn) AS DaysLeft
                        FROM Firearm
                       WHERE ScopeKind = 'Member'
                         AND IsActive = 1
                         AND AcquisitionStatus = 'Innehas'
                         AND LicenseExpiresOn IS NOT NULL
                         AND DATEDIFF(day, CAST(GETDATE() AS DATE), LicenseExpiresOn) <= @0
                         AND DATEDIFF(day, CAST(GETDATE() AS DATE), LicenseExpiresOn) >= @1
                       ORDER BY LicenseExpiresOn",
                    Stages.Max(), -400);
            }
            catch (Exception ex)
            {
                // Tabellen saknas = migreringen inte körd. Funktionen är då helt enkelt av.
                _logger.LogDebug(ex, "Licenspåminnelser: kunde inte läsa Firearm (migrering saknas?).");
                return new List<DueRow>();
            }
        }

        /// <summary>
        /// Vilket steg dagarna kvar hör till.
        ///
        /// <para><b>⚠️ Det SMALASTE steget som passar, inte det bredaste.</b> 25 dagar kvar hör till
        /// 30-dagarssteget och inte till 90 — annars skulle en medlem som lade in datumet sent få
        /// "90 dagar kvar" när det är en månad, alltså ett påstående som är fel.</para>
        /// </summary>
        private static int? StageFor(int daysLeft)
        {
            if (daysLeft < 0) return 0;
            foreach (var stage in Stages.Where(s => s > 0).OrderBy(s => s))
                if (daysLeft <= stage) return stage;
            return null;
        }

        private static (string Title, string Body) Message(DueRow row, int stage)
        {
            var name = string.IsNullOrWhiteSpace(row.Alias) ? "Ditt vapen" : row.Alias;
            var date = row.LicenseExpiresOn.ToString("yyyy-MM-dd");

            if (stage == 0)
                return ($"Licensen har förfallit — {name}",
                        $"Licensen gick ut {date}. Behöver du ett nytt föreningsintyg? Begär det från din klubb.");

            var word = stage >= 90 ? "tre månader" : "en månad";
            return ($"Licensen förfaller om {word} — {name}",
                    $"Licensen går ut {date}. Ska den förnyas behöver du oftast ett föreningsintyg " +
                    "från klubben — begär det i god tid.");
        }

        /// <summary>
        /// Registrerar påminnelsen. <c>false</c> = redan skickad (eller en samtidig körning hann
        /// först), och då ska ingenting skickas.
        /// </summary>
        private bool TryClaim(IScopeProvider scopeProvider, DueRow row, int stage)
        {
            try
            {
                using var uow = scopeProvider.CreateScope(autoComplete: true);
                uow.Database.Execute(
                    @"INSERT INTO FirearmReminder (FirearmId, MemberId, Stage, ExpiresOnAtSend, SentAt)
                      VALUES (@0, @1, @2, @3, @4)",
                    row.FirearmId, row.MemberId, stage, row.LicenseExpiresOn.Date, DateTime.Now);
                return true;
            }
            catch (Exception)
            {
                // Unikt index avvisade insättningen = redan registrerad. Fråga inte databasen igen;
                // avslaget ÄR svaret, och det är leverantörsoberoende.
                return false;
            }
        }

        private class DueRow
        {
            public int FirearmId { get; set; }
            public int MemberId { get; set; }
            public string? Alias { get; set; }
            public DateTime LicenseExpiresOn { get; set; }
            public int DaysLeft { get; set; }
        }
    }
}
