using HpskSite.Models.Kretsgranskning;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Kretsens granskning av banan i fältskytte (fas 3): lagring, övergångar och händelselogg.
    /// Behörighet hör till kontrollern; tjänsten prövar bara att läget tillåter handlingen, så att
    /// samma regler gäller inloggat och via länk. Varje övergång skriver raden och händelsen i samma
    /// transaktion.
    ///
    /// <para><b>⚠️ Ingen krok i stationConfigs skrivvägar.</b> Om banan ändrats efter inskick eller
    /// godkännande avgörs vid läsning (<see cref="CourseReviewRules.Changed"/>) mot tävlingens
    /// nuvarande stationConfig — se regelns kommentar för varför.</para>
    /// </summary>
    public class CourseReviewService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CourseReviewService> _logger;

        public CourseReviewService(IScopeProvider scopeProvider, ILogger<CourseReviewService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public bool TablesExist()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name IN ('CompetitionCourseReview','CompetitionCourseReviewEvent')") == 2;
        }

        /// <summary>Vad som saknas av fas 3:s schema, eller null när allt finns.</summary>
        public string? SchemaProblem()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var missing = new List<string>();
            if (!TablesExist()) missing.Add("tabellerna CompetitionCourseReview/CompetitionCourseReviewEvent");
            var cols = scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('RegionCalendarSettings') AND name IN ('RequireCourseReview','RequireCourseReviewSince')");
            if (cols < 2) missing.Add("kolumnerna RequireCourseReview/RequireCourseReviewSince på RegionCalendarSettings");
            return missing.Count == 0 ? null : string.Join(" och ", missing);
        }

        private static DateTime? _featureStart;

        /// <summary>
        /// Dagen bangranskningen började gälla = dagen tabellen skapades (migreringen körs strax före
        /// deployen). Samma regel som resultatgranskningen: påminnelser gäller bara tävlingar efter den.
        /// </summary>
        public DateTime? FeatureStart()
        {
            if (_featureStart.HasValue) return _featureStart;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var d = scope.Database.ExecuteScalar<DateTime?>("SELECT create_date FROM sys.tables WHERE name = 'CompetitionCourseReview'");
                if (d.HasValue) _featureStart = d.Value.Date;
                return _featureStart;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bangranskningens startdag kunde inte läsas.");
                return null;
            }
        }

        public CompetitionCourseReview? Get(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<CompetitionCourseReview>(id);
        }

        public CompetitionCourseReview? For(int competitionId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.FirstOrDefault<CompetitionCourseReview>("WHERE CompetitionId = @0", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bangranskningen kunde inte läsas för tävling {Id}.", competitionId);
                return null;
            }
        }

        public List<CompetitionCourseReview> ForRegion(int regionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCourseReview>(
                "SELECT * FROM CompetitionCourseReview WHERE RegionId = @0 ORDER BY CASE WHEN Status = 'Inskickad' THEN 0 ELSE 1 END, SubmittedAt", regionId);
        }

        public List<CompetitionCourseReview> Pending()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCourseReview>("SELECT * FROM CompetitionCourseReview WHERE Status = @0", CourseReviewStatus.Inskickad);
        }

        public HashSet<int> SubmittedCompetitionIds()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>("SELECT CompetitionId FROM CompetitionCourseReview").ToHashSet();
        }

        public List<CompetitionCourseReviewEvent> Events(int reviewId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionCourseReviewEvent>(
                "SELECT * FROM CompetitionCourseReviewEvent WHERE ReviewId = @0 ORDER BY At, Id", reviewId);
        }

        /// <summary>
        /// Arrangören skickar in banan (eller igen efter återsändning eller ändring). En godkänd bana
        /// som inte ändrats skickas inte in igen.
        /// </summary>
        public (CompetitionCourseReview? Review, string? Error) Submit(int competitionId, int regionId, string route,
            string stationConfig, bool required, string reviewerKind, string? note, int actorId, string actorName)
        {
            var checksum = CourseReviewRules.Checksum(stationConfig);
            var existing = For(competitionId);
            if (existing != null && existing.IsApproved && !CourseReviewRules.Changed(existing, checksum))
                return (null, existing.IsForbundet
                    ? "Förbundet har redan godkänt banan, och den har inte ändrats sedan dess."
                    : "Banan är redan granskad av kretsen och har inte ändrats sedan dess.");
            if (existing != null && existing.Status == CourseReviewStatus.Inskickad && !CourseReviewRules.Changed(existing, checksum))
                return (null, existing.IsForbundet ? "Underlaget är redan registrerat som skickat till Förbundet." : "Banan ligger redan hos kretsen.");

            var now = DateTime.Now;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var r = existing ?? new CompetitionCourseReview { CompetitionId = competitionId };
            var kind = existing == null ? CourseReviewEventKind.Submitted : CourseReviewEventKind.Resubmitted;
            r.RegionId = regionId;
            r.Route = route;
            r.Status = CourseReviewStatus.Inskickad;
            r.Required = required;
            r.ReviewerKind = CourseReviewerKind.Normalize(reviewerKind);
            r.Checksum = checksum;
            r.Snapshot = null;
            r.ApprovedCustomTargets = null;
            r.ArrangerNote = Trim(note, 2000);
            r.SubmittedAt = now;
            r.SubmittedByMemberId = actorId;
            r.SubmittedByName = Trim(actorName, 200);
            r.DecidedAt = null; r.DecidedByMemberId = null; r.DeciderName = null; r.DeciderRole = null;
            r.DeciderIsCompetitor = false; r.Comment = null; r.Channel = null;
            r.SentToForbundetAt = route == CourseReviewRoute.Forbundet ? now : null;
            r.UpdatedAt = now;
            if (r.Id == 0) db.Insert(r); else db.Update(r);
            AddEvent(db, r.Id, actorId, actorName, route == CourseReviewRoute.Forbundet ? CourseReviewEventKind.SentToForbundet : kind, r.ArrangerNote);
            scope.Complete();
            return (r, null);
        }

        /// <summary>
        /// Kretsens godkännande. <paramref name="currentStationConfig"/> är banan som den ser ut NU —
        /// har den ändrats sedan inskicket vägras godkännandet. Mål utanför SHB:s förteckning måste
        /// alla vara uttryckligen godkända (<paramref name="approvedCustomTargets"/>).
        /// </summary>
        public (CompetitionCourseReview? Review, string? Error) Approve(int id, string currentStationConfig,
            IReadOnlyCollection<string> customTargets, IReadOnlyCollection<string> approvedCustomTargets,
            string? comment, KretsDecider who, bool deciderIsCompetitor)
        {
            var r = Get(id);
            if (r == null) return (null, "Granskningen hittades inte.");
            if (!CourseReviewRules.KretsCanAct(r)) return (null, "Banan väntar inte på kretsen.");
            if (CourseReviewRules.Changed(r, CourseReviewRules.Checksum(currentStationConfig)))
                return (null, "Banan har ändrats sedan den skickades in. Arrangören behöver skicka in den igen.");
            var missing = customTargets.Where(t => !approvedCustomTargets.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                return (null, "Godkänn varje mål utanför SHB:s förteckning uttryckligen: " + string.Join(", ", missing) + ".");

            var now = DateTime.Now;
            r.Status = CourseReviewStatus.Godkand;
            r.Snapshot = currentStationConfig;
            r.ApprovedCustomTargets = customTargets.Count == 0 ? null : Trim(string.Join(", ", customTargets), 2000);
            r.DecidedAt = now;
            r.DecidedByMemberId = who.MemberId > 0 ? who.MemberId : null;
            r.DeciderName = Trim(who.Name, 200);
            r.DeciderRole = Trim(who.Role, 200);
            r.DeciderIsCompetitor = deciderIsCompetitor;
            r.Comment = Trim(comment, 4000);
            r.Channel = who.Channel;
            if (who.Channel == KretsChannel.Lank) r.LinkSentTo = who.LinkSentTo;
            r.UpdatedAt = now;
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Update(r);
            AddEvent(scope.Database, r.Id, who.MemberId, who.Name, CourseReviewEventKind.Approved,
                (deciderIsCompetitor ? "[Granskaren är anmäld som skytt i tävlingen] " : "") + (r.Comment ?? ""));
            scope.Complete();
            return (r, null);
        }

        /// <summary>Kretsen återsänder med en kommentar (obligatorisk — den är vad arrangören ska ändra).</summary>
        public (CompetitionCourseReview? Review, string? Error) Return(int id, string? comment, KretsDecider who)
        {
            var r = Get(id);
            if (r == null) return (null, "Granskningen hittades inte.");
            if (!CourseReviewRules.KretsCanAct(r)) return (null, "Banan väntar inte på kretsen.");
            comment = Trim(comment, 4000);
            if (string.IsNullOrEmpty(comment)) return (null, "Skriv vad arrangören behöver ändra.");
            var now = DateTime.Now;
            r.Status = CourseReviewStatus.Atersand;
            r.Comment = comment;
            r.DecidedAt = now;
            r.DecidedByMemberId = who.MemberId > 0 ? who.MemberId : null;
            r.DeciderName = Trim(who.Name, 200);
            r.DeciderRole = Trim(who.Role, 200);
            r.Channel = who.Channel;
            r.UpdatedAt = now;
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Update(r);
            AddEvent(scope.Database, r.Id, who.MemberId, who.Name, CourseReviewEventKind.Returned, comment);
            scope.Complete();
            return (r, null);
        }

        /// <summary>
        /// Förbundsvägen (SM/LDM): arrangören registrerar Förbundets svar — godkänt eller ändringar.
        /// Kretsen beslutar ingenting här; Förbundet gör det utanför pistol.nu.
        /// </summary>
        public (CompetitionCourseReview? Review, string? Error) RegisterForbundetAnswer(int competitionId, bool approved,
            string? text, string currentStationConfig, int actorId, string actorName)
        {
            var r = For(competitionId);
            if (r == null || !r.IsForbundet) return (null, "Registrera först att underlaget skickats till Förbundet.");
            text = Trim(text, 4000);
            if (!approved && string.IsNullOrEmpty(text)) return (null, "Skriv vilka ändringar Förbundet begär.");
            var now = DateTime.Now;
            r.Status = approved ? CourseReviewStatus.Godkand : CourseReviewStatus.Atersand;
            if (approved) r.Snapshot = currentStationConfig;
            r.Comment = text;
            r.DecidedAt = now;
            r.DecidedByMemberId = null;
            r.DeciderName = "Förbundet";
            r.DeciderRole = "registrerat av " + Trim(actorName, 150);
            r.UpdatedAt = now;
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Update(r);
            AddEvent(scope.Database, r.Id, actorId, actorName, CourseReviewEventKind.ForbundetAnswer,
                (approved ? "Förbundet har godkänt." : "Förbundet begär ändringar.") + (text != null ? " " + text : ""));
            scope.Complete();
            return (r, null);
        }

        public void SetLinkSentTo(int id, string email)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("UPDATE CompetitionCourseReview SET LinkSentTo = @1 WHERE Id = @0", id,
                email.Length > 200 ? email[..200] : email);
        }

        private static void AddEvent(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int reviewId, int byId, string? byName, string kind, string? text)
            => db.Insert(new CompetitionCourseReviewEvent
            {
                ReviewId = reviewId, At = DateTime.Now, ByMemberId = byId,
                ByName = string.IsNullOrWhiteSpace(byName) ? null : byName.Trim(), Kind = kind,
                Text = string.IsNullOrWhiteSpace(text) ? null : text
            });

        private static string? Trim(string? s, int max)
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0) return null;
            return t.Length > max ? t[..max] : t;
        }
    }
}
