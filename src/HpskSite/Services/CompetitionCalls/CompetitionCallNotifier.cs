using HpskSite.Models;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.Hosting;
using HpskSite.Services.Kretsgranskning;
using HpskSite.Services.Mail;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Mejlen i kretsutlysningen (F3): klubben får veta att den är utsedd, och kretsen får veta när ingen klubbadministratör kunde nås.
    ///
    /// <para><b>⚠️ Utseendet är registrerat FÖRE mejlet.</b> <c>Award</c> skriver tillståndet och händelseraden i sin egen transaktion;
    /// den här klassen anropas EFTER commit och ett mejlfel ruller aldrig tillbaka utseendet. Utfallet skrivs som händelsen
    /// <c>AwardMailed</c> med antalet mejl — <c>0</c> är det som kretsens gula kort <i>Att behandla</i> visar.</para>
    ///
    /// <para><b>⚠️ En länk i mejlet ändrar aldrig något.</b> Den öppnar bara klubbens tävlingsflik (inloggning krävs).</para>
    /// <para><b>Svarsadressen är kretsens</b> (<see cref="ReplyContactResolver.ForRegion"/>): klubben som svarar "vi tar den" ska nå kretsen,
    /// inte sajtägaren (regeln om Reply-To). Ett dubbelklick på Utse mejlar inte två gånger (<see cref="RateCounterService.TryClaimOnce"/>).</para>
    /// <para>Skickas aldrig i <c>Task.Run</c> (ambient scope). <c>notified == 0</c> loggas som varning.</para>
    /// </summary>
    public sealed class CompetitionCallNotifier
    {
        private readonly IMemberService _members;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _reply;
        private readonly KretsUppdragService _uppdrag;
        private readonly KretsCalendarService _krets;
        private readonly RateCounterService _rate;
        private readonly CompetitionCallService _calls;
        private readonly IUmbracoContextAccessor _ctx;
        private readonly ILogger<CompetitionCallNotifier> _logger;

        public CompetitionCallNotifier(IMemberService members, EmailService email, ReplyContactResolver reply, KretsUppdragService uppdrag,
            KretsCalendarService krets, RateCounterService rate, CompetitionCallService calls, IUmbracoContextAccessor ctx, ILogger<CompetitionCallNotifier> logger)
        {
            _members = members; _email = email; _reply = reply; _uppdrag = uppdrag; _krets = krets; _rate = rate; _calls = calls; _ctx = ctx; _logger = logger;
        }

        /// <summary>Klubbens egna administratörer med e-postadress (en gång per adress). Kretsens och sajtens administratörer räknas inte.</summary>
        public IReadOnlyList<(string Name, string Email)> ClubAdmins(int clubId)
        {
            var list = new List<(string, string)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _members.GetMembersInRole($"ClubAdmin_{clubId}"))
                if (!string.IsNullOrWhiteSpace(m.Email) && seen.Add(m.Email.Trim())) list.Add((m.Name ?? "", m.Email.Trim()));
            return list;
        }

        /// <summary>
        /// Aviserar den utsedda klubben, och kretsen om ingen klubbadministratör kunde nås. Returnerar antalet mejl till klubben.
        /// </summary>
        public async Task<int> AwardedAsync(CompetitionCall call, string clubName, string publicBase, int actorId, string actorName)
        {
            var sent = 0;
            try
            {
                var clubId = call.AwardedClubId ?? 0;
                var admins = ClubAdmins(clubId);
                var claim = $"call-awarded:{call.Id}:{clubId}:{call.AwardedAt?.Ticks ?? 0}";
                if (admins.Count > 0 && _rate.TryClaimOnce(claim, TimeSpan.FromDays(1)))
                {
                    var replyTo = _reply.ForRegion(call.RegionId);
                    var regionName = _krets.Region(call.RegionId)?.Name ?? "kretsen";
                    var when = call.CompetitionDate.HasValue
                        ? $"den {CompetitionCallRules.WeekdaySv(call.CompetitionDate.Value).ToLowerInvariant()} {call.CompetitionDate.Value:d MMMM yyyy}"
                        : "på en dag som ännu inte är bestämd";
                    var paras = new List<string>
                    {
                        $"{regionName} har utsett {clubName} att arrangera {call.Name} {when}.",
                        string.IsNullOrWhiteSpace(call.Description) ? "" : $"Från kretsen: {call.Description}",
                        call.LockedByKrets ? "Datumet är fastställt av kretsen. Vill ni ha en annan dag: föreslå det till kretsen, eller ändra datumet själva – kretsen får då en notis."
                                           : "",
                        "Svara på det här mejlet om något är oklart – svaret går till kretsen.",
                    };
                    foreach (var (name, email) in admins)
                        if (await _email.SendKretsgranskningAsync(email, name, $"Ni är utsedda att arrangera {call.Name}", paras,
                                "Öppna klubbens tävlingar", ClubCompetitionsUrl(clubId, publicBase), // länken ändrar aldrig något
                                $"Du får mejlet för att du är administratör för {clubName} på pistol.nu.", replyTo))
                            sent++;
                }
                if (sent == 0)
                {
                    _logger.LogWarning("Utlysning {Id}: ingen administratör för klubb {Club} kunde mejlas om utseendet.", call.Id, clubId);
                    await NotifyKretsNoAdminAsync(call, clubName, publicBase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utlysning {Id}: utseendet kunde inte aviseras.", call.Id);
            }
            try { _calls.RecordAwardMailed(call.Id, sent, actorId, actorName); }
            catch (Exception ex) { _logger.LogError(ex, "Utlysning {Id}: aviseringens utfall kunde inte skrivas.", call.Id); }
            return sent;
        }

        /// <summary>
        /// Klubben har föreslagit ett annat datum (F4, beslut 6): kretsens tävlingsansvarige får en notis. Förslaget är redan skrivet som händelse;
        /// ett mejlfel ändrar inget. Svarsadressen är kretsens. Returnerar antalet mejl (0 loggas som varning och syns i händelsen).
        /// </summary>
        public async Task<int> DateProposedAsync(CompetitionCall call, string clubName, DateTime proposed, string? note, string publicBase)
        {
            var sent = 0;
            try
            {
                if (!_rate.TryClaimOnce($"call-date-proposed:{call.Id}:{call.AwardedClubId}:{proposed:yyyyMMdd}", TimeSpan.FromHours(1))) return 0;
                var rec = _uppdrag.Recipients(call.RegionId, BoardRoleDefinitions.RoleTavlingsansvarig);
                var paras = new List<string>
                {
                    $"{clubName} föreslår att {call.Name} flyttas till {CompetitionCallRules.WeekdaySv(proposed).ToLowerInvariant()} {proposed:d MMMM yyyy}.",
                    call.CompetitionDate.HasValue ? $"Datumet är nu {call.CompetitionDate.Value:d MMMM yyyy} och fastställt av kretsen." : "",
                    string.IsNullOrWhiteSpace(note) ? "" : $"Meddelande från klubben: {note.Trim()}",
                    "Förslaget ändrar ingenting förrän kretsen själv ändrar datumet.",
                };
                var replyTo = _reply.ForRegion(call.RegionId);
                foreach (var r in rec.To)
                    if (await _email.SendKretsgranskningAsync(r.Email, r.Name, $"Förslag på nytt datum: {call.Name}", paras,
                            "Öppna kretsens utlysningar", $"{publicBase}/kretsen/ansokningar?krets={call.RegionId}",
                            "Du får mejlet för att du ansvarar för kretsens tävlingar på pistol.nu.", replyTo))
                        sent++;
                if (sent == 0) _logger.LogWarning("Utlysning {Id}: ingen i kretsen kunde mejlas om datumförslaget.", call.Id);
            }
            catch (Exception ex) { _logger.LogError(ex, "Utlysning {Id}: datumförslaget kunde inte aviseras.", call.Id); }
            return sent;
        }

        /// <summary>
        /// Klubben har ändrat ett datum kretsen fastställt (efter varning och bekräftelse): kretsens tävlingsansvarige får en notis, så att
        /// årsprogrammet kan rättas. Ändringen är redan gjord och loggad; ett mejlfel ändrar ingenting. Returnerar antalet mejl.
        /// </summary>
        public async Task<int> DateChangedByClubAsync(CompetitionCall call, string clubName, DateTime? from, DateTime? to, int registrations, string publicBase)
        {
            var sent = 0;
            try
            {
                if (!_rate.TryClaimOnce($"call-date-changed:{call.Id}:{to:yyyyMMdd}", TimeSpan.FromHours(1))) return 0;
                var rec = _uppdrag.Recipients(call.RegionId, BoardRoleDefinitions.RoleTavlingsansvarig);
                string D(DateTime? d) => d.HasValue ? $"{CompetitionCallRules.WeekdaySv(d.Value).ToLowerInvariant()} {d.Value:d MMMM yyyy}" : "inget datum";
                var paras = new List<string>
                {
                    $"{clubName} har ändrat datumet för {call.Name} från {D(from)} till {D(to)}. Kretsen hade fastställt datumet.",
                    registrations > 0 ? $"Det fanns redan {registrations} anmälningar. De anmälda har inte fått något automatiskt besked, och inga betalningar har rörts." : "",
                    "Kretsens årsprogram och kalender visar det nya datumet. Hör av dig till klubben om ändringen inte är överenskommen.",
                };
                var replyTo = _reply.ForRegion(call.RegionId);
                foreach (var r in rec.To)
                    if (await _email.SendKretsgranskningAsync(r.Email, r.Name, $"Datum ändrat: {call.Name}", paras,
                            "Öppna kretsens utlysningar", $"{publicBase}/kretsen/ansokningar?krets={call.RegionId}",
                            "Du får mejlet för att du ansvarar för kretsens tävlingar på pistol.nu.", replyTo))
                        sent++;
                if (sent == 0) _logger.LogWarning("Utlysning {Id}: ingen i kretsen kunde mejlas om det ändrade datumet.", call.Id);
            }
            catch (Exception ex) { _logger.LogError(ex, "Utlysning {Id}: datumändringen kunde inte aviseras.", call.Id); }
            return sent;
        }

        /// <summary>
        /// Påminnelse (F6): den utsedda klubben har inte skapat tävlingen. Spärren (<paramref name="claimKey"/>) tas FÖRE utskicket, så ett
        /// omstartat svep aldrig mejlar två gånger. Går ingen mejlad loggas det och raden syns i kretsens gula kort. Returnerar antalet mejl.
        /// </summary>
        public async Task<int> CreateReminderAsync(CompetitionCall call, string clubName, string claimKey, string publicBase)
        {
            var sent = 0;
            try
            {
                if (!_rate.TryClaimOnce(claimKey, TimeSpan.FromDays(CompetitionCallRules.CreateReminderAfterDays))) return 0;
                var clubId = call.AwardedClubId ?? 0;
                var admins = ClubAdmins(clubId);
                var replyTo = _reply.ForRegion(call.RegionId);
                var when = call.CompetitionDate.HasValue
                    ? $"den {CompetitionCallRules.WeekdaySv(call.CompetitionDate.Value).ToLowerInvariant()} {call.CompetitionDate.Value:d MMMM yyyy}"
                    : "på en dag som ännu inte är bestämd";
                var paras = new List<string>
                {
                    $"{clubName} är utsedd att arrangera {call.Name} {when}, men tävlingen är ännu inte skapad på pistol.nu.",
                    "Skapa den under klubbens Tävlingar → Tävlingar kretsen har utlyst. Tävlingen är dold tills ni själva trycker Publicera.",
                    "Kan klubben inte arrangera den? Svara på det här mejlet så får kretsen veta det i tid – svaret går till kretsen.",
                };
                foreach (var (name, email) in admins)
                    if (await _email.SendKretsgranskningAsync(email, name, $"Skapa tävlingen: {call.Name}", paras,
                            "Öppna klubbens tävlingar", ClubCompetitionsUrl(clubId, publicBase),
                            $"Du får mejlet för att du är administratör för {clubName} på pistol.nu.", replyTo))
                        sent++;
                if (sent == 0) _logger.LogWarning("Utlysning {Id}: ingen administratör för klubb {Club} kunde påminnas om att skapa tävlingen.", call.Id, clubId);
            }
            catch (Exception ex) { _logger.LogError(ex, "Utlysning {Id}: påminnelsen om att skapa tävlingen kunde inte skickas.", call.Id); }
            return sent;
        }

        /// <summary>Påminnelse (F6): sista dag för önskemål har gått, klubbar har önskat och ingen är utsedd. Går till kretsens tävlingsansvarige.</summary>
        public async Task<int> AwardReminderAsync(CompetitionCall call, IReadOnlyList<string> wishingClubs, string claimKey, string publicBase)
        {
            var sent = 0;
            try
            {
                if (!_rate.TryClaimOnce(claimKey, TimeSpan.FromDays(CompetitionCallRules.CreateReminderAfterDays))) return 0;
                var rec = _uppdrag.Recipients(call.RegionId, BoardRoleDefinitions.RoleTavlingsansvarig);
                var paras = new List<string>
                {
                    $"Sista dag för önskemål om {call.Name} har gått, men ingen klubb är utsedd ännu.",
                    wishingClubs.Count == 0 ? "" : $"Klubbar som önskat: {string.Join(", ", wishingClubs)}.",
                    "Utse en klubb under Utlysta tävlingar. Klubben får då ett mejl och kan skapa tävlingen.",
                };
                var replyTo = _reply.ForRegion(call.RegionId);
                foreach (var r in rec.To)
                    if (await _email.SendKretsgranskningAsync(r.Email, r.Name, $"Utse arrangör: {call.Name}", paras,
                            "Öppna kretsens utlysningar", $"{publicBase}/kretsen/ansokningar?krets={call.RegionId}",
                            "Du får mejlet för att du ansvarar för kretsens tävlingar på pistol.nu.", replyTo))
                        sent++;
                if (sent == 0) _logger.LogWarning("Utlysning {Id}: ingen i kretsen kunde påminnas om att utse arrangör.", call.Id);
            }
            catch (Exception ex) { _logger.LogError(ex, "Utlysning {Id}: påminnelsen om att utse arrangör kunde inte skickas.", call.Id); }
            return sent;
        }

        /// <summary>Klubbens Tävlingar-flik i adminpanelen (klubbens sida + <c>#clubAdmin/competitions</c>). Utan klubbsida: sajtens startsida — länken ändrar aldrig något.</summary>
        private string ClubCompetitionsUrl(int clubId, string publicBase)
        {
            if (clubId > 0 && _ctx.TryGetUmbracoContext(out var ctx))
            {
                var n = ctx.Content?.GetById(clubId);
                if (n != null && n.ContentType.Alias == "club") return publicBase + n.Url(mode: UrlMode.Relative) + "#clubAdmin/competitions";
            }
            return publicBase;
        }

        /// <summary>Kretsens tävlingsansvarige (annars kretsens kontakt och administratörer): klubben saknar administratör att mejla.</summary>
        private async Task NotifyKretsNoAdminAsync(CompetitionCall call, string clubName, string publicBase)
        {
            if (!_rate.TryClaimOnce($"call-awarded-krets:{call.Id}:{call.AwardedClubId}:{call.AwardedAt?.Ticks ?? 0}", TimeSpan.FromDays(1))) return;
            var rec = _uppdrag.Recipients(call.RegionId, BoardRoleDefinitions.RoleTavlingsansvarig);
            var inbox = $"{publicBase}/kretsen/ansokningar?krets={call.RegionId}";
            var paras = new List<string>
            {
                $"{clubName} är utsedd att arrangera {call.Name}, men klubben har ingen administratör på pistol.nu som kunde få besked.",
                "Kontakta klubben på annat sätt. Har klubben svarat kan ni registrera dess önskemål under Utlysta tävlingar.",
            };
            var replyTo = _reply.ForRegion(call.RegionId);
            var sent = 0;
            foreach (var r in rec.To)
                if (await _email.SendKretsgranskningAsync(r.Email, r.Name, $"Klubben nåddes inte: {call.Name}", paras,
                        "Öppna kretsens utlysningar", inbox, "Du får mejlet för att du ansvarar för kretsens tävlingar på pistol.nu.", replyTo))
                    sent++;
            if (sent == 0) _logger.LogWarning("Utlysning {Id}: varken klubben eller kretsen kunde mejlas.", call.Id);
        }
    }
}
