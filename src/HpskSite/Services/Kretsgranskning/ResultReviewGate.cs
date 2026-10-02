using HpskSite.Models;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Kretsens GRIND för standardmedaljer (fas 2). Beslut 2026-10-02: granskningen är en stämpel,
    /// inte en grind — utom i en krets som själv slagit på "Kräv kretsens godkännande".
    ///
    /// <para><b>EN regel, anropad efter varje materialisering</b> av platsmedaljer (precision,
    /// fält, spring) och efter varje granskningsbeslut. Materialiseringen skriver som i dag
    /// <c>Verified</c>; grinden flyttar sedan tävlingens platsmedaljer till <c>Reported</c> när den
    /// gäller, och tillbaka när listan är godkänd.</para>
    ///
    /// <para><b>⚠️ Den rör aldrig</b> en medalj som en människa verifierat (<c>VerifiedByMemberId</c>
    /// satt) eller som är låst i en guldansökan, och den gäller bara tävlingar från och med dagen
    /// kretsen slog på grinden — redan publicerade tävlingar rörs inte.</para>
    /// </summary>
    public class ResultReviewGate
    {
        private readonly KretsCalendarService _calendar;
        private readonly CompetitionApplicationService _settings;
        private readonly ResultReviewService _reviews;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<ResultReviewGate> _logger;

        public ResultReviewGate(KretsCalendarService calendar, CompetitionApplicationService settings,
            ResultReviewService reviews, IScopeProvider scopeProvider, ILogger<ResultReviewGate> logger)
        {
            _calendar = calendar;
            _settings = settings;
            _reviews = reviews;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>Gäller grinden för tävlingens krets och datum?</summary>
        public bool GateApplies(int competitionId)
        {
            try
            {
                var cr = _calendar.CompetitionRegion(competitionId);
                if (cr == null) return false;
                var s = _settings.Settings(cr.Value.Region.Id);
                if (s == null || !s.RequireResultApproval) return false;
                var date = cr.Value.Competition.EndDate ?? cr.Value.Competition.Date;
                if (date == null) return false;
                return s.RequireResultApprovalSince == null || date.Value.Date >= s.RequireResultApprovalSince.Value.Date;
            }
            catch (Exception ex)
            {
                // En grind som inte kan avgöras får inte stoppa medaljerna — fail open, som i dag.
                _logger.LogWarning(ex, "Resultatgrinden kunde inte avgöras för tävling {Id}.", competitionId);
                return false;
            }
        }

        /// <summary>Väntar tävlingens standardmedaljer på kretsens godkännande?</summary>
        public bool MedalsPending(int competitionId)
        {
            if (!GateApplies(competitionId)) return false;
            var review = _reviews.For(competitionId, false);
            return review == null || !review.IsApproved;
        }

        /// <summary>Tävlingens platsmedaljer per valör och status — underlag för granskarens checklista.</summary>
        public List<(string MedalType, string Status, int Count)> OnSiteMedalCounts(int competitionId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.Fetch<MedalCountRow>(
                        "SELECT MedalType, Status, COUNT(*) AS N FROM StandardMedalAward WHERE CompetitionId = @0 AND Source = @1 GROUP BY MedalType, Status",
                        competitionId, StandardMedals.SourceOnSite)
                    .Select(r => (r.MedalType ?? "", r.Status ?? "", r.N)).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Platsmedaljerna kunde inte räknas för tävling {Id}.", competitionId);
                return new();
            }
        }

        private class MedalCountRow
        {
            public string? MedalType { get; set; }
            public string? Status { get; set; }
            public int N { get; set; }
        }

        /// <summary>
        /// Stämmer av varje tävling i kretsen som har platsmedaljer — körs när kretsen slår grinden
        /// på eller av, så att medaljer som hölls inne släpps (och tvärtom) utan att någon behöver
        /// trycka Uppdatera på varje resultatlista.
        /// </summary>
        public int ReconcileRegion(int regionId)
        {
            List<int> ids;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                ids = scope.Database.Fetch<int>(
                    "SELECT DISTINCT CompetitionId FROM StandardMedalAward WHERE Source = @0 AND CompetitionId IS NOT NULL AND CompetitionId > 0",
                    StandardMedals.SourceOnSite);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resultatgrinden kunde inte lista tävlingar för krets {Region}.", regionId);
                return 0;
            }
            var n = 0;
            foreach (var id in ids)
                if (_calendar.CompetitionRegion(id)?.Region.Id == regionId)
                    n += ReconcileMedals(id);
            return n;
        }

        /// <summary>
        /// Får tävlingens platsmedaljer att stämma med grinden. Returnerar antalet rader som flyttades.
        /// </summary>
        public int ReconcileMedals(int competitionId)
        {
            try
            {
                var pending = MedalsPending(competitionId);
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                var n = pending
                    ? db.Execute(
                        "UPDATE StandardMedalAward SET Status = @1, VerifiedAt = NULL, UpdatedAt = @2 " +
                        "WHERE CompetitionId = @0 AND Source = @3 AND Status = @4 AND GoldApplicationId IS NULL " +
                        "AND (VerifiedByMemberId IS NULL OR VerifiedByMemberId = 0)",
                        competitionId, StandardMedals.StatusReported, DateTime.Now, StandardMedals.SourceOnSite, StandardMedals.StatusVerified)
                    // Platsmedaljer är aldrig Reported annars — bara grinden sätter det läget — så
                    // att släppa dem kan inte råka verifiera något grinden inte höll inne.
                    : db.Execute(
                        "UPDATE StandardMedalAward SET Status = @1, UpdatedAt = @2 " +
                        "WHERE CompetitionId = @0 AND Source = @3 AND Status = @4",
                        competitionId, StandardMedals.StatusVerified, DateTime.Now, StandardMedals.SourceOnSite, StandardMedals.StatusReported);
                if (n > 0)
                    _logger.LogInformation("Resultatgrinden: tävling {Id}, {N} platsmedaljer satta till {Status}.",
                        competitionId, n, pending ? StandardMedals.StatusReported : StandardMedals.StatusVerified);
                return n;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Resultatgrinden kunde inte stämma av medaljerna för tävling {Id}.", competitionId);
                return 0;
            }
        }
    }
}
