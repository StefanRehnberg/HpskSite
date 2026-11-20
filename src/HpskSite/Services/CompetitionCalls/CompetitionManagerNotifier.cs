using HpskSite.Models.Competitions;
using HpskSite.Services.Hosting;
using HpskSite.Services.Mail;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Mejlen i klubbprogrammet (F7). Utseendet/önskemålet är redan skrivet FÖRE mejlet; ett mejlfel ändrar aldrig något.
    /// <para><b>Svarsadressen är klubbens</b> (<see cref="ReplyContactResolver.ForClub"/>, regeln om Reply-To). Spärren
    /// (<see cref="RateCounterService.TryClaimOnce"/>) tas före utskicket, så ett dubbelklick aldrig mejlar två gånger.
    /// Skickas aldrig i <c>Task.Run</c> (ambient scope). Mejlen bär aldrig andra medlemmars namn.</para>
    /// </summary>
    public sealed class CompetitionManagerNotifier
    {
        private readonly IMemberService _members;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _reply;
        private readonly RateCounterService _rate;
        private readonly CompetitionCallNotifier _callNotifier;
        private readonly CompetitionManagerLinkService _links;
        private readonly ILogger<CompetitionManagerNotifier> _logger;

        public CompetitionManagerNotifier(IMemberService members, EmailService email, ReplyContactResolver reply, RateCounterService rate,
            CompetitionCallNotifier callNotifier, CompetitionManagerLinkService links, ILogger<CompetitionManagerNotifier> logger)
        {
            _members = members; _email = email; _reply = reply; _rate = rate; _callNotifier = callNotifier; _links = links; _logger = logger;
        }

        private static string When(CompetitionInfo c) => c.CompetitionDate.HasValue
            ? $"{CompetitionCallRules.WeekdaySv(c.CompetitionDate.Value).ToLowerInvariant()} {c.CompetitionDate.Value:d MMMM yyyy}"
            : "en dag som ännu inte är bestämd";

        private (string Name, string Email)? Recipient(int memberId)
        {
            var m = memberId > 0 ? _members.GetById(memberId) : null;
            if (m == null || string.IsNullOrWhiteSpace(m.Email)) return null;
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return (n.Length > 0 ? n : m.Name ?? "", m.Email.Trim());
        }

        /// <summary>Den utsedde får besked med "Öppna tävlingen" och en länk för att tacka nej. Returnerar antalet mejl (0 = personen nåddes inte).</summary>
        public async Task<int> AppointedAsync(CompetitionInfo comp, string clubName, int memberId, string publicBase)
        {
            try
            {
                var rec = Recipient(memberId);
                if (rec == null) { _logger.LogWarning("Tävling {Comp}: tävlingsledare {Member} saknar e-postadress och kunde inte mejlas.", comp.Id, memberId); return 0; }
                if (!_rate.TryClaimOnce($"mgr-appointed:{comp.Id}:{memberId}:{DateTime.Today:yyyyMMdd}", TimeSpan.FromDays(1))) return 0;
                var paras = new List<string>
                {
                    $"{clubName} har utsett dig till tävlingsledare för {comp.DisplayName}, {When(comp)}.",
                    "Som tävlingsledare kan du hantera tävlingen på pistol.nu och hitta funktionärer. Du får inte tillgång till klubbens ekonomi.",
                    "Svara på det här mejlet om du har frågor – svaret går till klubben.",
                };
                var ok = await _email.SendKretsgranskningAsync(rec.Value.Email, rec.Value.Name, $"Du är utsedd till tävlingsledare för {comp.DisplayName}", paras,
                    "Öppna tävlingen", $"{publicBase}/competitionmanagement?competitionId={comp.Id}",
                    $"Du får mejlet för att du är medlem i {clubName} på pistol.nu. Länken för att tacka nej fungerar en gång och bara för dig.",
                    _reply.ForClub(comp.ClubId),
                    "Jag kan inte, tacka nej", _links.DeclineUrl(comp.Id, memberId, publicBase));
                return ok ? 1 : 0;
            }
            catch (Exception ex) { _logger.LogError(ex, "Tävling {Comp}: utseendet kunde inte aviseras till medlem {Member}.", comp.Id, memberId); return 0; }
        }

        /// <summary>De som önskat men inte utsetts får veta att tävlingsledare är tillsatta. Inga namn, ingen länk som ändrar något. Returnerar antalet mejl.</summary>
        public async Task<int> FilledAsync(CompetitionInfo comp, string clubName, IReadOnlyCollection<int> memberIds, string publicBase)
        {
            var sent = 0;
            foreach (var mid in memberIds)
            {
                try
                {
                    var rec = Recipient(mid);
                    if (rec == null) continue;
                    if (!_rate.TryClaimOnce($"mgr-filled:{comp.Id}:{mid}", TimeSpan.FromDays(30))) continue;
                    var paras = new List<string>
                    {
                        $"Tävlingsledare är tillsatta för {comp.DisplayName}, {When(comp)}.",
                        "Tack för att du ville – vi hör av oss nästa gång.",
                    };
                    if (await _email.SendKretsgranskningAsync(rec.Value.Email, rec.Value.Name, $"Tävlingsledare är tillsatta för {comp.DisplayName}", paras,
                            null, null, $"Du får mejlet för att du önskat bli tävlingsledare i {clubName} på pistol.nu.", _reply.ForClub(comp.ClubId)))
                        sent++;
                }
                catch (Exception ex) { _logger.LogError(ex, "Tävling {Comp}: beskedet \"tillsatt\" kunde inte skickas till medlem {Member}.", comp.Id, mid); }
            }
            return sent;
        }

        /// <summary>Klubbens administratörer får veta att någon önskat. Returnerar antalet mejl.</summary>
        public async Task<int> WishedAsync(CompetitionInfo comp, string clubName, string memberName, string publicBase)
        {
            var sent = 0;
            try
            {
                if (!_rate.TryClaimOnce($"mgr-wished:{comp.Id}:{memberName}:{DateTime.Today:yyyyMMdd}", TimeSpan.FromDays(1))) return 0;
                var paras = new List<string>
                {
                    $"{memberName} vill vara tävlingsledare för {comp.DisplayName}, {When(comp)}.",
                    "Du utser tävlingsledare under klubbens Tävlingar → Tävlingsledare.",
                };
                foreach (var (name, email) in _callNotifier.ClubAdmins(comp.ClubId))
                    if (await _email.SendKretsgranskningAsync(email, name, $"{memberName} vill vara tävlingsledare", paras, "Öppna klubbens tävlingar",
                            $"{publicBase}/competitionmanagement?competitionId={comp.Id}", $"Du får mejlet för att du är administratör för {clubName} på pistol.nu.", _reply.ForClub(comp.ClubId)))
                        sent++;
            }
            catch (Exception ex) { _logger.LogError(ex, "Tävling {Comp}: önskemålet kunde inte aviseras.", comp.Id); }
            return sent;
        }

        /// <summary>Den utsedde har tackat nej: klubbens administratörer får veta, och tävlingen saknar åter tävlingsledare.</summary>
        public async Task<int> DeclinedAsync(CompetitionInfo comp, string clubName, string memberName, bool noManagersLeft, string publicBase)
        {
            var sent = 0;
            try
            {
                if (!_rate.TryClaimOnce($"mgr-declined:{comp.Id}:{memberName}:{DateTime.Today:yyyyMMdd}", TimeSpan.FromDays(1))) return 0;
                var paras = new List<string>
                {
                    $"{memberName} kan inte vara tävlingsledare för {comp.DisplayName}, {When(comp)}, och har tackat nej.",
                    noManagersLeft ? "Tävlingen har nu ingen tävlingsledare. Utse någon under klubbens Tävlingar → Tävlingsledare." : "",
                };
                foreach (var (name, email) in _callNotifier.ClubAdmins(comp.ClubId))
                    if (await _email.SendKretsgranskningAsync(email, name, $"{memberName} har tackat nej: {comp.DisplayName}", paras, "Öppna klubbens tävlingar",
                            $"{publicBase}/competitionmanagement?competitionId={comp.Id}", $"Du får mejlet för att du är administratör för {clubName} på pistol.nu.", _reply.ForClub(comp.ClubId)))
                        sent++;
                if (sent == 0) _logger.LogWarning("Tävling {Comp}: ingen administratör kunde mejlas om att {Member} tackat nej.", comp.Id, memberName);
            }
            catch (Exception ex) { _logger.LogError(ex, "Tävling {Comp}: avböjandet kunde inte aviseras.", comp.Id); }
            return sent;
        }
    }
}
