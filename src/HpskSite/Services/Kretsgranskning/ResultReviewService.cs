using HpskSite.Models.Kretsgranskning;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Kretsens granskning av resultatlistor (fas 2): lagring, övergångar och händelselogg.
    /// Behörighet hör till kontrollern; tjänsten prövar bara att läget tillåter handlingen, så att
    /// samma regler gäller inloggat och via länk. Varje övergång skriver raden och händelsen i samma
    /// transaktion.
    /// </summary>
    public class ResultReviewService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<ResultReviewService> _logger;

        public ResultReviewService(IScopeProvider scopeProvider, ILogger<ResultReviewService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public bool TablesExist()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.tables WHERE name IN ('CompetitionResultReview','CompetitionResultReviewEvent')") == 2;
        }

        /// <summary>
        /// Vad som saknas av fas 2:s schema, eller null när allt finns. Kolumnerna på
        /// RegionCalendarSettings räknas med: NPoco skriver dem vid varje sparning av kretsens
        /// inställningar, så utan dem faller även fas 4:s inställningar — inte bara granskningen.
        /// </summary>
        public string? SchemaProblem()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var missing = new List<string>();
            if (!TablesExist()) missing.Add("tabellerna CompetitionResultReview/CompetitionResultReviewEvent");
            var cols = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('RegionCalendarSettings') AND name IN ('RequireResultApproval','RequireResultApprovalSince','TwoReviewersAtChampionships')");
            if (cols < 3) missing.Add("kolumnerna RequireResultApproval/RequireResultApprovalSince/TwoReviewersAtChampionships på RegionCalendarSettings");
            return missing.Count == 0 ? null : string.Join(" och ", missing);
        }

        private static DateTime? _featureStart;

        /// <summary>
        /// Dagen granskningen började gälla i den här miljön = dagen granskningstabellen skapades
        /// (migreringen körs strax före deployen). Tävlingar som avslutades FÖRE den dagen räknas
        /// aldrig som "inte inskickade" och påminns inte — beslutet att redan genomförda tävlingar
        /// inte rörs. Avsiktligt ingen inställning: en sådan glöms, och då påminns varje gammal
        /// tävling dagen efter deployen. Null när tabellen saknas.
        /// </summary>
        public DateTime? FeatureStart()
        {
            if (_featureStart.HasValue) return _featureStart;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var d = scope.Database.ExecuteScalar<DateTime?>("SELECT create_date FROM sys.tables WHERE name = 'CompetitionResultReview'");
                if (d.HasValue) _featureStart = d.Value.Date;
                return _featureStart;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resultatgranskningens startdag kunde inte läsas.");
                return null;
            }
        }

        public CompetitionResultReview? Get(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<CompetitionResultReview>(id);
        }

        public CompetitionResultReview? For(int competitionId, bool sub)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.FirstOrDefault<CompetitionResultReview>(
                    "SELECT * FROM CompetitionResultReview WHERE CompetitionId = @0 AND IsSubCompetition = @1", competitionId, sub);
            }
            catch (Exception ex)
            {
                // En saknad tabell får inte ta ner resultatsidan — den visar bara ingen märkning.
                _logger.LogWarning(ex, "Resultatgranskningen kunde inte läsas för tävling {Id}.", competitionId);
                return null;
            }
        }

        public List<CompetitionResultReview> ForRegion(int regionId, int? year = null)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var sql = "SELECT * FROM CompetitionResultReview WHERE RegionId = @0";
            if (year.HasValue) sql += " AND YEAR(SubmittedAt) = @1";
            sql += " ORDER BY CASE WHEN Status = 'Inskickad' THEN 0 ELSE 1 END, SubmittedAt";
            return scope.Database.Fetch<CompetitionResultReview>(sql, regionId, year ?? 0);
        }

        public List<CompetitionResultReview> Pending()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionResultReview>("SELECT * FROM CompetitionResultReview WHERE Status = @0", ResultReviewStatus.Inskickad);
        }

        public HashSet<int> SubmittedCompetitionIds()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>("SELECT CompetitionId FROM CompetitionResultReview WHERE IsSubCompetition = 0").ToHashSet();
        }

        public List<CompetitionResultReviewEvent> Events(int reviewId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<CompetitionResultReviewEvent>(
                "SELECT * FROM CompetitionResultReviewEvent WHERE ReviewId = @0 ORDER BY At, Id", reviewId);
        }

        /// <summary>
        /// Arrangören skickar in (eller skickar in igen efter en återsändning eller ändring). En
        /// godkänd lista som inte ändrats skickas inte in igen.
        /// </summary>
        public (CompetitionResultReview? Review, string? Error) Submit(int competitionId, bool sub, int regionId,
            string resultData, bool gated, bool requiresTwo, bool weaponCheckAttested, string? note, int actorId, string actorName)
        {
            var checksum = ResultReviewRules.Checksum(resultData);
            var existing = For(competitionId, sub);
            var now = DateTime.Now;

            if (existing != null && existing.IsApproved && !ResultReviewRules.Changed(existing, checksum))
                return (null, "Resultatlistan är redan granskad av kretsen och har inte ändrats sedan dess.");

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            CompetitionResultReview r;
            string kind;
            if (existing == null)
            {
                r = new CompetitionResultReview { CompetitionId = competitionId, IsSubCompetition = sub };
                kind = ResultReviewEventKind.Submitted;
            }
            else
            {
                r = existing;
                kind = ResultReviewEventKind.Resubmitted;
            }
            r.RegionId = regionId;
            r.Status = ResultReviewStatus.Inskickad;
            r.Gated = gated;
            r.RequiresTwo = requiresTwo;
            r.Checksum = checksum;
            r.Snapshot = null;
            r.WeaponCheckAttested = weaponCheckAttested;
            r.ArrangerNote = Trim(note, 2000);
            r.SubmittedAt = now;
            r.SubmittedByMemberId = actorId;
            r.SubmittedByName = Trim(actorName, 200);
            r.FirstApprovedAt = null; r.FirstApproverMemberId = null; r.FirstApproverName = null;
            r.DecidedAt = null; r.DecidedByMemberId = null; r.DeciderName = null; r.DeciderRole = null;
            r.Comment = null; r.Channel = null;
            r.UpdatedAt = now;
            if (r.Id == 0) db.Insert(r); else db.Update(r);
            AddEvent(db, r.Id, actorId, actorName, kind, r.ArrangerNote);
            scope.Complete();
            return (r, null);
        }

        /// <summary>
        /// Kretsens godkännande. <paramref name="currentResultData"/> är listan som den ser ut NU —
        /// har den ändrats sedan inskicket vägras godkännandet, eftersom granskaren då inte såg det
        /// som godkänns.
        /// </summary>
        public (CompetitionResultReview? Review, ResultReviewRules.ApproveOutcome Outcome, string? Error) Approve(int id,
            string currentResultData, string? comment, KretsDecider who)
        {
            var r = Get(id);
            if (r == null) return (null, ResultReviewRules.ApproveOutcome.NotPending, "Granskningen hittades inte.");
            if (ResultReviewRules.Changed(r, ResultReviewRules.Checksum(currentResultData)))
                return (null, ResultReviewRules.ApproveOutcome.NotPending, "Resultatlistan har ändrats sedan den skickades in. Arrangören behöver skicka in den igen.");

            var outcome = ResultReviewRules.Approve(r, who.MemberId, who.Name);
            switch (outcome)
            {
                case ResultReviewRules.ApproveOutcome.NotPending:
                    return (null, outcome, "Resultatlistan väntar inte på kretsen.");
                case ResultReviewRules.ApproveOutcome.SameReviewerTwice:
                    return (null, outcome, "Två olika granskare krävs. Du har redan godkänt — en annan granskare behöver göra det andra godkännandet.");
            }

            var now = DateTime.Now;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            if (outcome == ResultReviewRules.ApproveOutcome.FirstOfTwo)
            {
                r.FirstApprovedAt = now;
                r.FirstApproverMemberId = who.MemberId > 0 ? who.MemberId : null;
                r.FirstApproverName = Trim(who.Name, 200);
                r.UpdatedAt = now;
                db.Update(r);
                AddEvent(db, r.Id, who.MemberId, who.Name, ResultReviewEventKind.FirstApproval, Trim(comment, 4000));
            }
            else
            {
                r.Status = ResultReviewStatus.Godkand;
                r.Snapshot = currentResultData;
                r.DecidedAt = now;
                r.DecidedByMemberId = who.MemberId > 0 ? who.MemberId : null;
                r.DeciderName = Trim(who.Name, 200);
                r.DeciderRole = Trim(who.Role, 200);
                r.Comment = Trim(comment, 4000);
                r.Channel = who.Channel;
                if (who.Channel == KretsChannel.Lank) r.LinkSentTo = who.LinkSentTo;
                r.UpdatedAt = now;
                db.Update(r);
                AddEvent(db, r.Id, who.MemberId, who.Name, ResultReviewEventKind.Approved, r.Comment);
            }
            scope.Complete();
            return (r, outcome, null);
        }

        /// <summary>Kretsen återsänder med en kommentar (obligatorisk — den är vad arrangören ska rätta).</summary>
        public (CompetitionResultReview? Review, string? Error) Return(int id, string? comment, KretsDecider who)
        {
            var r = Get(id);
            if (r == null) return (null, "Granskningen hittades inte.");
            if (!ResultReviewRules.KretsCanAct(r)) return (null, "Resultatlistan väntar inte på kretsen.");
            comment = Trim(comment, 4000);
            if (string.IsNullOrEmpty(comment)) return (null, "Skriv vad arrangören behöver rätta.");
            var now = DateTime.Now;
            r.Status = ResultReviewStatus.Atersand;
            r.Comment = comment;
            r.DecidedAt = now;
            r.DecidedByMemberId = who.MemberId > 0 ? who.MemberId : null;
            r.DeciderName = Trim(who.Name, 200);
            r.DeciderRole = Trim(who.Role, 200);
            r.Channel = who.Channel;
            r.FirstApprovedAt = null; r.FirstApproverMemberId = null; r.FirstApproverName = null;
            r.UpdatedAt = now;
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Update(r);
            AddEvent(scope.Database, r.Id, who.MemberId, who.Name, ResultReviewEventKind.Returned, comment);
            scope.Complete();
            return (r, null);
        }

        /// <summary>
        /// Anropas från resultatlistans skrivvägar efter en omräkning. Har en INSKICKAD eller GODKÄND
        /// lista ändrats går den tillbaka till inskickad (godkännandet gällde en annan lista).
        /// Returnerar granskningen när något ändrades, annars null.
        /// </summary>
        public CompetitionResultReview? OnResultDataChanged(int competitionId, bool sub, string resultData)
        {
            var r = For(competitionId, sub);
            if (r == null || r.Status == ResultReviewStatus.Atersand) return null;
            var checksum = ResultReviewRules.Checksum(resultData);
            if (!ResultReviewRules.Changed(r, checksum)) return null;

            var wasApproved = r.IsApproved;
            var now = DateTime.Now;
            r.Status = ResultReviewStatus.Inskickad;
            r.Checksum = checksum;
            r.FirstApprovedAt = null; r.FirstApproverMemberId = null; r.FirstApproverName = null;
            r.UpdatedAt = now;
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Update(r);
            AddEvent(scope.Database, r.Id, 0, null, ResultReviewEventKind.ChangedAfterApproval,
                wasApproved ? "Resultatlistan ändrades efter kretsens godkännande." : "Resultatlistan ändrades medan den låg hos kretsen.");
            scope.Complete();
            return r;
        }

        public void SetLinkSentTo(int id, string email)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("UPDATE CompetitionResultReview SET LinkSentTo = @1 WHERE Id = @0", id,
                email.Length > 200 ? email[..200] : email);
        }

        private static void AddEvent(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int reviewId, int byId, string? byName, string kind, string? text)
            => db.Insert(new CompetitionResultReviewEvent
            {
                ReviewId = reviewId, At = DateTime.Now, ByMemberId = byId,
                ByName = string.IsNullOrWhiteSpace(byName) ? null : byName.Trim(), Kind = kind, Text = text
            });

        private static string? Trim(string? s, int max)
        {
            var t = (s ?? "").Trim();
            if (t.Length == 0) return null;
            return t.Length > max ? t[..max] : t;
        }
    }
}
