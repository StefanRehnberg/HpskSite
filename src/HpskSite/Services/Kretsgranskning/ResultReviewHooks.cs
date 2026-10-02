using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Resultatgranskningens två sidoeffekter, på ETT ställe:
    /// <list type="bullet">
    /// <item><see cref="AfterResultDataWrittenAsync"/> — anropas från VARJE skrivväg för
    /// <c>resultData</c> (precision: CreateResultsList, ToggleResultsOfficial,
    /// RefreshResultArtifactAsync; fält: <c>FaltskytteResultArtifactService.RefreshAsync</c>;
    /// spring: ComputeStore). <b>⚠️ En ny skrivväg MÅSTE anropa den.</b> Annars kan en godkänd
    /// lista ändras tyst och fortsätta bära "Granskad av kretsen".</item>
    /// <item><see cref="NotifyReviewersAsync"/> — mejlet till kretsens resultatgranskare. Utan
    /// granskare går det till kretsens kontaktadress som en länk utan inloggning (60 dagar) och
    /// till kretsadministratörerna, samma länkläge som tävlingsansökan.</item>
    /// </list>
    /// Sväljer sina egna fel: en granskningskrok får aldrig fälla en resultatsparning.
    /// </summary>
    public class ResultReviewHooks
    {
        public const string CaseKind = "Resultat";

        private readonly ResultReviewService _reviews;
        private readonly ResultReviewGate _gate;
        private readonly KretsUppdragService _uppdrag;
        private readonly KretsCalendarService _calendar;
        private readonly KretsLinkTokenService _tokens;
        private readonly EmailService _email;
        private readonly ReplyContactResolver _replyTo;
        private readonly IConfiguration _config;
        private readonly ILogger<ResultReviewHooks> _logger;

        public ResultReviewHooks(ResultReviewService reviews, ResultReviewGate gate, KretsUppdragService uppdrag,
            KretsCalendarService calendar, KretsLinkTokenService tokens, EmailService email, ReplyContactResolver replyTo,
            IConfiguration config, ILogger<ResultReviewHooks> logger)
        {
            _reviews = reviews;
            _gate = gate;
            _uppdrag = uppdrag;
            _calendar = calendar;
            _tokens = tokens;
            _email = email;
            _replyTo = replyTo;
            _config = config;
            _logger = logger;
        }

        /// <summary>Webbplatsens bas-URL för länkar i mejl som skickas utanför en begäran.</summary>
        public string SiteUrl => (_config["Email:SiteUrl"] ?? _config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');

        public async Task AfterResultDataWrittenAsync(int competitionId, string? resultData)
        {
            try
            {
                var r = _reviews.OnResultDataChanged(competitionId, false, resultData ?? "");
                if (r == null) return;

                _gate.ReconcileMedals(competitionId);

                var name = _calendar.CompetitionRegion(competitionId)?.Competition.Name ?? $"tävling {competitionId}";
                await NotifyReviewersAsync(r, $"Resultatlistan har ändrats: {name}", new[]
                {
                    $"Resultatlistan för {name} har ändrats efter att den skickades in till kretsen.",
                    "Den ligger nu som inskickad igen. Ett tidigare godkännande gäller inte den ändrade listan."
                }, SiteUrl, _replyTo.ForCompetitionOrganiser(competitionId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resultatgranskningens krok misslyckades för tävling {Id}.", competitionId);
            }
        }

        /// <summary>
        /// Mejlar kretsens resultatgranskare om granskningen. Returnerar antalet skickade och de
        /// länkar utan inloggning som skapades (sajtadmin får dem i svaret — supportvägen, och så
        /// går länkläget att verifiera utan SMTP).
        /// </summary>
        public async Task<(int Sent, List<string> Links, bool LinkMode)> NotifyReviewersAsync(CompetitionResultReview r,
            string subject, IEnumerable<string> paragraphs, string baseUrl, MailReplyTo replyTo)
        {
            var links = new List<string>();
            var sent = 0;
            var linkMode = false;
            try
            {
                baseUrl = baseUrl.TrimEnd('/');
                var rec = _uppdrag.Recipients(r.RegionId, BoardRoleDefinitions.RoleResultatgranskare);
                linkMode = rec.IsFallback;
                var inbox = $"{baseUrl}/kretsen/granskning?krets={r.RegionId}";
                var paras = paragraphs.ToList();
                foreach (var to in rec.To)
                {
                    string url, btn, notice;
                    if (to.MemberId > 0)
                    {
                        url = inbox; btn = "Öppna kretsens granskning";
                        notice = rec.IsFallback
                            ? "Du får mejlet för att du är kretsadministratör och kretsen inte har utsett någon resultatgranskare."
                            : "Du får mejlet för att du är kretsens resultatgranskare.";
                    }
                    else
                    {
                        var token = _tokens.CreateCaseLink(CaseKind, r.Id, r.RegionId, r.Checksum, to.Email);
                        url = $"{baseUrl}/kretsen/resultat-arende?t={Uri.EscapeDataString(token)}";
                        links.Add(url);
                        btn = "Granska resultatlistan";
                        notice = "Kretsen har inte utsett någon resultatgranskare på pistol.nu, så listan kommer som en länk som fungerar utan inloggning i 60 dagar. "
                               + $"Utser kretsen någon hamnar nästa lista direkt hos den personen: {baseUrl}/kretsen/kom-igang?krets={r.RegionId}";
                        try { _reviews.SetLinkSentTo(r.Id, to.Email); } catch { }
                    }
                    if (await _email.SendKretsgranskningAsync(to.Email, to.Name, subject, paras, btn, url, notice, replyTo))
                        sent++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resultatgranskning {Id}: kretsen kunde inte aviseras.", r.Id);
            }
            return (sent, links, linkMode);
        }
    }
}
