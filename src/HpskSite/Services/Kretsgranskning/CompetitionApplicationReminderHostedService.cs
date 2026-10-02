using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Hosting;
using HpskSite.Services.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Påminnelser för tävlingsansökan (fas 4), var 12:e timme:
    /// <list type="bullet">
    /// <item>arrangören, 8 veckor före, om en godkänd ansökan fortfarande saknar tävling;</item>
    /// <item>kretsen, efter 14 dagar, om en inskickad ansökan inte rörts;</item>
    /// <item>kretsen, 14 dagar före Förbundets gräns, om nationella ansökningar ligger obehandlade.</item>
    /// </list>
    ///
    /// <para><b>Claim-then-send</b> med ett unikt index som spärr (KretsgranskningReminder).
    /// Varje mejl har ett nästa steg som går att göra direkt.</para>
    /// </summary>
    public class CompetitionApplicationReminderHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<CompetitionApplicationReminderHostedService> _logger;

        public CompetitionApplicationReminderHostedService(IServiceScopeFactory scopeFactory, IConfiguration config,
            ILogger<CompetitionApplicationReminderHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(DateTime.Today); }
                catch (Exception ex) { _logger.LogError(ex, "Tävlingsansökans påminnelser kunde inte köras."); }

                try { await Task.Delay(Interval, stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        /// <summary>Ett varv. Publik så att den kan köras för hand (supportväg) och i en svit.</summary>
        public async Task<int> RunOnceAsync(DateTime today)
        {
            using var scope = _scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var apps = sp.GetRequiredService<CompetitionApplicationService>();
            if (!apps.TablesExist()) return 0;

            var due = CompetitionApplicationReminders.Compute(apps.ForReminders(today), today);
            var sentReminders = 0;
            foreach (var d in due)
            {
                if (!apps.TryClaimReminder(d.Key)) continue;
                int n;
                try { n = await SendAsync(sp, d); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Påminnelse {Key} kunde inte skickas.", d.Key);
                    n = 0;
                }
                apps.SetReminderRecipients(d.Key, n);
                if (n > 0) sentReminders++;
            }

            // Resultatgranskningen (fas 2) i samma svep och samma spärrtabell.
            try { sentReminders += await sp.GetRequiredService<ResultReviewReminderService>().RunOnceAsync(today); }
            catch (Exception ex) { _logger.LogError(ex, "Resultatgranskningens påminnelser kunde inte köras."); }
            return sentReminders;
        }

        private string SiteUrl() => (_config["Email:SiteUrl"] ?? _config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');

        private async Task<int> SendAsync(IServiceProvider sp, CompetitionApplicationReminders.Due d)
        {
            var email = sp.GetRequiredService<EmailService>();
            var cal = sp.GetRequiredService<KretsCalendarService>();
            var uppdrag = sp.GetRequiredService<KretsUppdragService>();
            var tokens = sp.GetRequiredService<KretsLinkTokenService>();
            var replyTo = sp.GetRequiredService<ReplyContactResolver>();
            var region = cal.Region(d.RegionId);
            var site = SiteUrl();
            int sent = 0;

            if (d.Kind == CompetitionApplicationReminders.KindCreateCompetition && d.App != null)
            {
                var a = d.App;
                var clubs = sp.GetRequiredService<ClubService>();
                var members = sp.GetRequiredService<IMemberService>();
                var to = new List<(string, string)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string? e, string n) { if (!string.IsNullOrWhiteSpace(e) && seen.Add(e.Trim())) to.Add((e.Trim(), n)); }
                Add(a.ContactEmail, a.ContactName ?? "");
                Add(members.GetById(a.CreatedByMemberId)?.Email, "");
                if (a.ClubId > 0) Add(clubs.GetClubById(a.ClubId)?.ContactEmail, clubs.GetClubNameById(a.ClubId) ?? "");
                var weeks = Math.Max(1, (int)Math.Round((a.EffectiveDate - DateTime.Today).TotalDays / 7));
                foreach (var (e, n) in to)
                    if (await email.SendKretsgranskningAsync(e, n, $"Påminnelse: lägg in {a.Name}",
                            new[] { $"Kretsen har sagt ja till {a.Name} den {a.EffectiveDate:yyyy-MM-dd} — om ungefär {weeks} veckor — men tävlingen finns inte på pistol.nu ännu.",
                                    "Logga in och gå till klubbens Administration → Tävlingar → Ansökningar och tryck Skapa tävlingen. Guiden är förifylld med det ansökan innehåller." },
                            "Logga in på pistol.nu", site + "/login-register/?tab=login",
                            "Du får påminnelsen en gång, för att ansökan är godkänd men tävlingen inte skapad.",
                            replyTo.ForRegion(a.RegionId)))
                        sent++;
                return sent;
            }

            if (d.Kind == CompetitionApplicationReminders.KindKretsStale && d.App != null)
            {
                var a = d.App;
                var rec = uppdrag.Recipients(a.RegionId, BoardRoleDefinitions.RoleTavlingsansvarig);
                foreach (var r in rec.To)
                {
                    var url = r.MemberId > 0
                        ? $"{site}/kretsen/ansokningar?krets={a.RegionId}"
                        : $"{site}/kretsen/arende?t={Uri.EscapeDataString(tokens.CreateCaseLink("Ansokan", a.Id, a.RegionId, CompetitionApplicationRules.Checksum(a), r.Email))}";
                    if (await email.SendKretsgranskningAsync(r.Email, r.Name, $"Ansökan väntar på kretsen: {a.Name}",
                            new[] { $"Ansökan om {a.Name} ({a.CompetitionDate:yyyy-MM-dd}) har väntat på {region?.Name ?? "kretsen"} i över {CompetitionApplicationReminders.KretsStaleDays} dagar.",
                                    "Klubben ser att den ligger obehandlad. Godkänn, avslå eller be om komplettering." },
                            "Behandla ansökan", url,
                            rec.IsFallback ? $"Kretsen har inte utsett någon tävlingsansvarig. Utser ni någon hamnar ansökningarna hos den personen: {site}/kretsen/kom-igang?krets={a.RegionId}" : null,
                            replyTo.ForRegion(a.RegionId)))
                        sent++;
                }
                return sent;
            }

            if (d.Kind == CompetitionApplicationReminders.KindForbundet && d.Apps != null)
            {
                var rec = uppdrag.Recipients(d.RegionId, BoardRoleDefinitions.RoleTavlingsansvarig);
                var year = DateTime.Today.Year + 1;
                var names = string.Join(", ", d.Apps.Select(a => a.Name));
                foreach (var r in rec.To)
                    if (await email.SendKretsgranskningAsync(r.Email, r.Name, $"Förbundets sista dag närmar sig ({CompetitionApplicationRules.ForbundetDeadline(year).ToString("d MMMM", new System.Globalization.CultureInfo("sv-SE"))})",
                            new[] { $"Förbundets sista dag för nationella tävlingar och landsdelstävlingar {year} är {CompetitionApplicationRules.ForbundetDeadline(year):yyyy-MM-dd}.",
                                    $"{d.Apps.Count} {(d.Apps.Count == 1 ? "ansökan" : "ansökningar")} väntar fortfarande på kretsens yttrande: {names}.",
                                    "Förbundet skriver: skicka hellre in ett ofullständigt underlag i tid än ett färdigt underlag alltför sent." },
                            r.MemberId > 0 ? "Öppna kretsens ansökningar" : null,
                            r.MemberId > 0 ? $"{site}/kretsen/ansokningar?krets={d.RegionId}" : null,
                            rec.IsFallback ? $"Kretsen har inte utsett någon tävlingsansvarig: {site}/kretsen/kom-igang?krets={d.RegionId}" : null,
                            replyTo.ForRegion(d.RegionId)))
                        sent++;
                return sent;
            }
            return 0;
        }
    }
}
