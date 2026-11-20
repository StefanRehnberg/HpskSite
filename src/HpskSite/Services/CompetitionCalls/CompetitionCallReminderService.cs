using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.Kretsgranskning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Påminnelserna i kretsutlysningen (F6), ett varv i <see cref="CompetitionApplicationReminderHostedService"/>-svepet (var 12:e timme):
    /// <list type="bullet">
    /// <item>den utsedda klubben, 14 dagar efter utseendet och sedan var 14:e dag, om tävlingen inte är skapad;</item>
    /// <item>kretsens tävlingsansvarige, efter sista dag för önskemål, om klubbar har önskat men ingen är utsedd.</item>
    /// </list>
    /// Vad som är "dags" avgörs av <see cref="CompetitionCallRules.DueReminders"/> (en ren funktion, enhetstestad). Spärren tas före utskicket
    /// (<c>RateCounterService.TryClaimOnce</c> i notifieraren), så ett omstartat svep aldrig mejlar två gånger.
    /// </summary>
    public sealed class CompetitionCallReminderService
    {
        private readonly CompetitionCallService _calls;
        private readonly CompetitionCallNotifier _notifier;
        private readonly KretsCalendarService _krets;
        private readonly IConfiguration _config;
        private readonly ILogger<CompetitionCallReminderService> _logger;

        public CompetitionCallReminderService(CompetitionCallService calls, CompetitionCallNotifier notifier, KretsCalendarService krets,
            IConfiguration config, ILogger<CompetitionCallReminderService> logger)
        {
            _calls = calls; _notifier = notifier; _krets = krets; _config = config; _logger = logger;
        }

        /// <summary>Ett varv. Returnerar antalet mejl som skickades. Saknas tabellerna görs ingenting.</summary>
        public async Task<int> RunOnceAsync(DateTime today)
        {
            if (!_calls.TablesExist()) return 0;
            var publicBase = (_config["Email:SiteUrl"] ?? _config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');
            var open = _calls.OpenForReminders();
            var wishCache = new Dictionary<int, int>();
            int Wishes(int id) => wishCache.TryGetValue(id, out var n) ? n : wishCache[id] = _calls.ActiveWishCount(id);

            var sent = 0;
            foreach (var d in CompetitionCallRules.DueReminders(open, Wishes, today))
            {
                var call = open.First(c => c.Id == d.CallId);
                try
                {
                    if (d.Kind == CompetitionCallRules.ReminderClubCreate)
                    {
                        var clubName = _krets.ClubsInRegion(call.RegionId).GetValueOrDefault(call.AwardedClubId ?? 0, "klubben");
                        sent += await _notifier.CreateReminderAsync(call, clubName, d.Key, publicBase);
                    }
                    else
                    {
                        var clubs = _krets.ClubsInRegion(call.RegionId);
                        var names = _calls.Wishes(call.Id).Where(w => w.Status == "Aktiv")
                            .Select(w => clubs.GetValueOrDefault(w.ClubId, "")).Where(n => n.Length > 0).ToList();
                        sent += await _notifier.AwardReminderAsync(call, names, d.Key, publicBase);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Utlysning {Id}: påminnelsen {Kind} kunde inte köras.", call.Id, d.Kind); }
            }
            return sent;
        }
    }
}
