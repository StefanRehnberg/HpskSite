using HpskSite.Models;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Reads and writes CompetitionParticipantStatus (DNS / DNF) rows.
    ///
    /// Deliberately tiny and discipline-agnostic: the table only answers "will more results
    /// arrive for this shooter?". Interpretation — how a withdrawn shooter is ranked or
    /// displayed — belongs to each discipline's result pipeline, not here.
    /// </summary>
    public class ParticipantStatusService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<ParticipantStatusService> _logger;

        public ParticipantStatusService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<ParticipantStatusService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────

        /// <summary>
        /// Every status row for a competition. Returns an empty list rather than throwing when
        /// the table is missing, so an un-migrated environment degrades to today's behaviour
        /// (no statuses) instead of taking the whole result list down.
        /// </summary>
        public async Task<List<CompetitionParticipantStatus>> GetForCompetitionAsync(int competitionId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return await db.FetchAsync<CompetitionParticipantStatus>(
                    "WHERE CompetitionId = @0 ORDER BY MemberId, ShootingClass", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not read CompetitionParticipantStatus for competition {CompetitionId} — " +
                    "has create-competition-participant-status-table.sql been run?", competitionId);
                return new List<CompetitionParticipantStatus>();
            }
        }

        /// <summary>
        /// Lookup keyed "memberId|shootingClass" (class lowercased) for O(1) probing while
        /// building result lists.
        /// </summary>
        public static Dictionary<string, CompetitionParticipantStatus> BuildLookup(
            IEnumerable<CompetitionParticipantStatus> statuses)
        {
            var lookup = new Dictionary<string, CompetitionParticipantStatus>();
            foreach (var s in statuses)
            {
                lookup[Key(s.MemberId, s.ShootingClass)] = s;
            }
            return lookup;
        }

        /// <summary>
        /// Folds the class onto its canonical name before keying, so a row written with the Id
        /// ("C_Vet_Y") and a probe carrying the display Name ("C Vet Y") still meet. Without the
        /// fold the two forms are different keys and a DNS/DNF silently fails to apply to exactly
        /// the classes where Id and Name differ — the veteran, dam, junior and optic classes.
        /// </summary>
        public static string Key(int memberId, string? shootingClass) =>
            $"{memberId}|{ShootingClasses.NormalizeKey(shootingClass)}";

        /// <summary>
        /// Raden för en klass, oavsett om den lagrades i Id- eller namnform. Samma vikning som
        /// <see cref="Key"/>, så en status satt från startlistan hittas av resultatinmatningen.
        /// </summary>
        private static CompetitionParticipantStatus? FindRow(
            IEnumerable<CompetitionParticipantStatus> rows, string? shootingClass)
        {
            var key = ShootingClasses.NormalizeKey(shootingClass);
            return rows.FirstOrDefault(r => ShootingClasses.NormalizeKey(r.ShootingClass) == key);
        }

        // ── Writes ────────────────────────────────────────────────────

        public async Task<(bool Success, string? Message)> SetStatusAsync(
            int competitionId, int memberId, string shootingClass, string status,
            int? fromSeriesNumber, string? note, int actingMemberId)
        {
            if (!CompetitionParticipantStatus.IsValidStatus(status))
                return (false, "Ogiltig status. Tillåtna värden är DNS och DNF.");

            if (fromSeriesNumber is <= 0)
                return (false, "Serienumret måste vara 1 eller högre.");

            // A DNS never started, so there is no series to break off at. Storing one would
            // invent a partial round that does not exist.
            if (status == CompetitionParticipantStatus.Dns)
                fromSeriesNumber = null;

            // ⚠️ Klassen når hit i BÅDA formerna: startlisteredigeraren skickar Id ("C_Vet_Y"),
            // resultatinmatningen skickar namnet ("C Vet Y"). Ett exakt strängmatchat uppslag
            // hittade därför inte den andras rad, och skytten fick TVÅ statusrader för samma
            // klass — den ena osynlig från den skärm som inte skrev den. Kanonisera in, och
            // matcha befintliga rader (även gamla, skrivna i Id-form) på den vikta nyckeln.
            var canonicalClass = ShootingClasses.ToCanonicalName(shootingClass);

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var existing = FindRow(
                    await db.FetchAsync<CompetitionParticipantStatus>(
                        "WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId),
                    shootingClass);

                if (existing != null)
                {
                    existing.ShootingClass = canonicalClass;
                    existing.Status = status;
                    existing.FromSeriesNumber = fromSeriesNumber;
                    existing.Note = note;
                    existing.SetBy = actingMemberId;
                    existing.SetAt = DateTime.Now;
                    await db.UpdateAsync(existing);
                }
                else
                {
                    await db.InsertAsync(new CompetitionParticipantStatus
                    {
                        CompetitionId = competitionId,
                        MemberId = memberId,
                        ShootingClass = canonicalClass,
                        Status = status,
                        FromSeriesNumber = fromSeriesNumber,
                        Note = note,
                        SetBy = actingMemberId,
                        SetAt = DateTime.Now
                    });
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set participant status for MemberId {MemberId} in competition {CompetitionId}",
                    memberId, competitionId);
                return (false, "Statusen kunde inte sparas.");
            }
        }

        /// <summary>Clear a status — the shooter is back in the competition as normal.</summary>
        public async Task<(bool Success, string? Message)> ClearStatusAsync(
            int competitionId, int memberId, string shootingClass)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                // Samma vikning som SetStatusAsync: raden kan vara skriven i Id-form från
                // startlistan och tas bort i namnform från resultatinmatningen. Radera på Id
                // så att en gammal rad i "fel" form också försvinner.
                var rows = await db.FetchAsync<CompetitionParticipantStatus>(
                    "WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId);
                var matching = rows
                    .Where(r => ShootingClasses.NormalizeKey(r.ShootingClass)
                                == ShootingClasses.NormalizeKey(shootingClass))
                    .ToList();

                var affected = 0;
                foreach (var row in matching)
                {
                    affected += await db.ExecuteAsync(
                        "DELETE FROM CompetitionParticipantStatus WHERE Id = @0", row.Id);
                }

                return (affected > 0, affected > 0 ? null : "Hittade ingen status att ta bort.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear participant status for MemberId {MemberId} in competition {CompetitionId}",
                    memberId, competitionId);
                return (false, "Statusen kunde inte tas bort.");
            }
        }
    }
}
