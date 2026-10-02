using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Bangranskningens behörighet, aviseringar och sekretess (fas 3) — på ETT ställe, så att
    /// kontrollern, stationsläsningen i FaltskytteController och granskarens stationssida ger samma svar.
    ///
    /// <para><b>⚠️ Stationerna är hemliga för skyttar.</b> En granskare får läsa tävlingens stationer
    /// bara medan en granskning pågår eller är godkänd (<see cref="ReviewerMayReadStations"/>), och
    /// en granskare som själv är anmäld i tävlingen varnas — godkännandet stämplas då så.</para>
    /// </summary>
    public class CourseReviewAccess
    {
        public const string CaseKind = "Bana";

        private readonly CourseReviewService _reviews;
        private readonly CompetitionApplicationService _apps;
        private readonly KretsUppdragService _uppdrag;
        private readonly KretsLinkTokenService _tokens;
        private readonly KretsCalendarService _calendar;
        private readonly ClubService _clubs;
        private readonly AdminAuthorizationService _auth;
        private readonly IContentService _content;
        private readonly EmailService _email;
        private readonly IConfiguration _config;
        private readonly ILogger<CourseReviewAccess> _logger;

        public CourseReviewAccess(CourseReviewService reviews, CompetitionApplicationService apps, KretsUppdragService uppdrag,
            KretsLinkTokenService tokens, KretsCalendarService calendar, AdminAuthorizationService auth, IContentService content,
            EmailService email, IConfiguration config, ClubService clubs, ILogger<CourseReviewAccess> logger)
        {
            _reviews = reviews;
            _apps = apps;
            _uppdrag = uppdrag;
            _tokens = tokens;
            _calendar = calendar;
            _clubs = clubs;
            _auth = auth;
            _content = content;
            _email = email;
            _config = config;
            _logger = logger;
        }

        public string SiteUrl => (_config["Email:SiteUrl"] ?? _config["SiteUrl"] ?? "https://pistol.nu").TrimEnd('/');

        public RegionCalendarSettings? Settings(int regionId)
        {
            try { return _apps.Settings(regionId); }
            catch (Exception ex) { _logger.LogWarning(ex, "Kretsens inställningar kunde inte läsas för {Id}.", regionId); return null; }
        }

        public string ReviewerKind(int regionId) => CourseReviewerKind.Normalize(Settings(regionId)?.CourseReviewer);

        /// <summary>
        /// Får den inloggade granska banor i kretsen — och vilken roll stämplas? Granskaren (bangranskaren
        /// eller kretsinstruktören, efter kretsens val), kretsadmin eller sajtadmin (support).
        /// </summary>
        public async Task<(bool Allowed, string Role)> AuthorityAsync(int regionId, int memberId)
        {
            var region = _calendar.Region(regionId);
            if (region == null || memberId <= 0) return (false, "");
            var kind = ReviewerKind(regionId);
            if (_uppdrag.IsCourseReviewer(regionId, memberId, kind))
                return (true, kind == CourseReviewerKind.Kretsinstruktor ? "Kretsinstruktör" : BoardRoleDefinitions.GetLabel(BoardRoleDefinitions.RoleBangranskare));
            if (await _auth.IsCurrentUserAdminAsync()) return (true, "Sajtadministratör (support)");
            if (await _auth.IsRegionalAdminForRegion(region.Code)) return (true, "Kretsadministratör");
            return (false, "");
        }

        /// <summary>
        /// Får den inloggade medlemmen läsa tävlingens stationer i egenskap av granskare? Bara när
        /// granskningen ligger hos kretsen eller är godkänd. Anropas av FaltskytteController och
        /// granskarens stationssida — utöver tävlingens vanliga funktionärer.
        /// </summary>
        public async Task<bool> ReviewerMayReadStations(int competitionId, int memberId)
        {
            if (memberId <= 0) return false;
            var r = _reviews.TablesExist() ? _reviews.For(competitionId) : null;
            if (r == null || r.IsForbundet) return false;
            if (r.Status is not (CourseReviewStatus.Inskickad or CourseReviewStatus.Godkand)) return false;
            return (await AuthorityAsync(r.RegionId, memberId)).Allowed;
        }

        /// <summary>Är medlemmen anmäld som skytt i tävlingen? (anmälningarna är opublicerade noder)</summary>
        public bool IsRegistered(int competitionId, int memberId)
        {
            if (memberId <= 0) return false;
            try
            {
                var hub = _content.GetPagedChildren(competitionId, 0, 100, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
                if (hub == null) return false;
                return _content.GetPagedChildren(hub.Id, 0, 5000, out _)
                    .Where(r => r.ContentType.Alias == "competitionRegistration")
                    .Any(r => r.GetValue<int>("memberId") == memberId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Anmälningarna kunde inte läsas för tävling {Id}.", competitionId);
                return false;
            }
        }

        public CaseLinkPayload? ReadLink(string? token)
        {
            var p = _tokens.ReadCaseLink(token);
            return p != null && p.CaseKind == CaseKind ? p : null;
        }

        /// <summary>
        /// Mejlar banans granskare. Utan granskare: kretsens kontaktadress får en länk utan inloggning
        /// (60 dagar, bär kontrollsumman) och kretsadministratörerna får inkorgen — samma länkläge som
        /// resultatgranskningen.
        /// </summary>
        public async Task<(int Sent, List<string> Links, bool LinkMode)> NotifyReviewersAsync(CompetitionCourseReview r,
            string subject, IEnumerable<string> paragraphs, string baseUrl, MailReplyTo replyTo)
        {
            var links = new List<string>();
            int sent = 0;
            bool linkMode = false;
            try
            {
                baseUrl = baseUrl.TrimEnd('/');
                var rec = _uppdrag.CourseRecipients(r.RegionId, r.ReviewerKind);
                linkMode = rec.IsFallback;
                var paras = paragraphs.ToList();
                var who = CourseReviewerKind.Label(r.ReviewerKind).ToLowerInvariant();
                foreach (var to in rec.To)
                {
                    string url, btn, notice;
                    if (to.MemberId > 0)
                    {
                        url = $"{baseUrl}/kretsen/bangranskning?krets={r.RegionId}";
                        btn = "Öppna kretsens bangranskning";
                        notice = rec.IsFallback
                            ? $"Du får mejlet för att du är kretsadministratör och kretsen inte har {who} på pistol.nu."
                            : $"Du får mejlet för att du är {(r.ReviewerKind == CourseReviewerKind.Kretsinstruktor ? "kretsinstruktör" : "kretsens bangranskare")}.";
                    }
                    else
                    {
                        var token = _tokens.CreateCaseLink(CaseKind, r.Id, r.RegionId, r.Checksum, to.Email);
                        url = $"{baseUrl}/kretsen/bana-arende?t={Uri.EscapeDataString(token)}";
                        links.Add(url);
                        btn = "Granska banan";
                        notice = "Kretsen har inte utsett någon som granskar banor på pistol.nu, så banan kommer som en länk som fungerar utan inloggning i 60 dagar. "
                               + $"Utser kretsen en bangranskare hamnar nästa bana direkt hos den personen: {baseUrl}/kretsen/kom-igang?krets={r.RegionId}";
                        try { _reviews.SetLinkSentTo(r.Id, to.Email); } catch { }
                    }
                    if (await _email.SendKretsgranskningAsync(to.Email, to.Name, subject, paras, btn, url, notice, replyTo))
                        sent++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bangranskning {Id}: kretsen kunde inte aviseras.", r.Id);
            }
            return (sent, links, linkMode);
        }
        /// <summary>
        /// Tävlingens läge i bangranskningen: erbjuds den, vilken väg, krävs den, sista dag. EN plats,
        /// så att kontrollern och påminnelsesvepet aldrig räknar olika. Null = ingen tävling.
        /// </summary>
        /// <param name="publishedScope">Omfattningen ur den publicerade cachen när anroparen har den;
        /// annars läses egenskapen ur innehållet (bakgrundssvepet har ingen Umbraco-kontext).</param>
        public CourseReviewCompetition? Resolve(int competitionId, string? publishedScope = null)
        {
            try
            {
                var comp = _content.GetById(competitionId);
                if (comp == null || comp.ContentType.Alias != "competition") return null;
                var type = comp.GetValue<string>("competitionType") ?? "";
                var level = CompetitionTypes.Common.CompetitionLevel.Normalize(comp.GetValue<string>("competitionLevel"));
                var scope = CompetitionTypes.Common.ChampionshipCategory.NormalizeScope(publishedScope ?? comp.GetValue<string>("competitionScope"));
                var date = comp.GetValue<DateTime?>("competitionDate");
                var c = new CourseReviewCompetition
                {
                    Id = comp.Id,
                    Name = comp.GetValue<string>("competitionName") is { Length: > 0 } n ? n : comp.Name ?? "",
                    Date = date.HasValue && date.Value.Year > 1900 ? date : null,
                    ClubId = comp.GetValue<int>("clubId"),
                    Venue = comp.GetValue<string>("venue") ?? "",
                    Level = level,
                    Scope = scope,
                    TypeLabel = HpskSite.Models.CompetitionTypes.GetFuzzy(type)?.Name ?? type,
                    StationConfig = comp.GetValue<string>("stationConfig")
                };
                c.HasStations = !string.IsNullOrWhiteSpace(c.StationConfig) && c.StationConfig!.Trim() != "{}";
                var isSmOrLdm = scope == CompetitionTypes.Common.Utilities.CompetitionScopeHelper.SvensktMasterskap
                                || scope == CompetitionTypes.Common.Utilities.CompetitionScopeHelper.Landsdelsmasterskap;
                c.IsNational = level is "Nationell" or "Riks";
                c.Offered = CourseReviewRules.Offered(type, comp.GetValue<bool>("isClubOnly"),
                    CompetitionTypes.Common.CompetitionLevel.IsKretsOrAbove(level) || isSmOrLdm, comp.GetValue<bool>("isAwardingStandardMedals"));
                c.Route = CourseReviewRules.RouteFor(isSmOrLdm);
                c.OfferBlockedByMissingLevel = !c.Offered && CourseReviewRules.IsFieldType(type)
                    && !comp.GetValue<bool>("isClubOnly") && string.IsNullOrEmpty(level) && !isSmOrLdm;
                var cr =_calendar.CompetitionRegion(competitionId);
                if (cr != null) { c.RegionId = cr.Value.Region.Id; c.RegionName = cr.Value.Region.Name; }
                c.Organiser = c.ClubId > 0 ? (_clubs.GetClubNameById(c.ClubId) ?? c.RegionName) : c.RegionName;
                var s = c.RegionId > 0 ? Settings(c.RegionId) : null;
                c.Required = c.Offered && CourseReviewRules.Required(c.IsNational, isSmOrLdm, s?.RequireCourseReview == true, s?.RequireCourseReviewSince, c.Date);
                c.Deadline = c.Required ? CourseReviewRules.Deadline(c.Date, c.Route == CourseReviewRoute.Forbundet, s?.FieldPrereqWeeks) : null;
                return c;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bangranskning: tävling {Id} kunde inte läsas.", competitionId);
                return null;
            }
        }
    }

    /// <summary>En tävlings läge i bangranskningen, räknat av <see cref="CourseReviewAccess.Resolve"/>.</summary>
    public class CourseReviewCompetition
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public DateTime? Date { get; set; }
        public int ClubId { get; set; }
        public int RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public string Organiser { get; set; } = "";
        public string Venue { get; set; } = "";
        public string Level { get; set; } = "";
        public string Scope { get; set; } = "";
        public string TypeLabel { get; set; } = "";
        public bool Offered { get; set; }
        public bool Required { get; set; }
        public bool IsNational { get; set; }
        public string Route { get; set; } = CourseReviewRoute.Krets;
        public DateTime? Deadline { get; set; }
        public string? StationConfig { get; set; }
        public bool HasStations { get; set; }
        /// <summary>
        /// En fälttävling som inte erbjuds granskning ENBART för att kategorin inte är angiven (och den
        /// inte ger standardmedaljer). Kortet säger då vad som saknas i stället för att tiga.
        /// </summary>
        public bool OfferBlockedByMissingLevel { get; set; }
    }
}
