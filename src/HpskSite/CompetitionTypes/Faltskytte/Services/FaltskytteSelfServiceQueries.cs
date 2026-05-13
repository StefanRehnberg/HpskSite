using HpskSite.CompetitionTypes.Faltskytte.Models;
using NPoco;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Tiny query helpers for the Fältskytte self-service result-entry mode.
    /// Shared between the controller (auth checks on read/write endpoints) and
    /// the StationPage view (patrol resolution + cursor advance) so the
    /// membership rules live in exactly one place.
    /// </summary>
    public static class FaltskytteSelfServiceQueries
    {
        /// <summary>
        /// Returns every patrol in the given competition that has the given
        /// member registered as a patrol member. Multi-class shooters can be
        /// in more than one patrol per competition (one per weapon class).
        /// </summary>
        public static async Task<List<FaltskyttePatrol>> GetPatrolsForMemberAsync(
            IDatabase db, int competitionId, int memberId)
        {
            return await db.FetchAsync<FaltskyttePatrol>(
                @"WHERE CompetitionId = @0
                  AND Id IN (SELECT PatrolId FROM FaltskyttePatrolMember WHERE MemberId = @1)
                  ORDER BY PatrolNumber",
                competitionId, memberId);
        }

        /// <summary>
        /// True when the given member is registered as a patrol member of the
        /// given patrol. Used to gate self-service writes (the writer must be
        /// in the patrol whose results are being saved).
        /// </summary>
        public static async Task<bool> IsMemberInPatrolAsync(
            IDatabase db, int patrolId, int memberId)
        {
            var n = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM FaltskyttePatrolMember WHERE PatrolId = @0 AND MemberId = @1",
                patrolId, memberId);
            return n > 0;
        }

        /// <summary>
        /// Looks up a patrol by (competitionId, patrolNumber) — that pair is
        /// UNIQUE so this returns a single patrol or null.
        /// </summary>
        public static async Task<FaltskyttePatrol?> GetPatrolAsync(
            IDatabase db, int competitionId, int patrolNumber)
        {
            return await db.FirstOrDefaultAsync<FaltskyttePatrol>(
                "WHERE CompetitionId = @0 AND PatrolNumber = @1",
                competitionId, patrolNumber);
        }

        /// <summary>
        /// Advances the patrol's CurrentStation cursor to the given station —
        /// only forward. The cursor is monotonic: it tracks the highest station
        /// the patrol has reached so older stations stay locked for shooters
        /// (staff can still edit anything). Going to an older station via QR
        /// scan or page load is a no-op — the cursor stays where it was.
        /// Same-station re-scan is also a no-op.
        /// </summary>
        public static async Task AdvanceCursorAsync(
            IDatabase db, int patrolId, int stationNumber)
        {
            await db.ExecuteAsync(
                @"UPDATE FaltskyttePatrol
                  SET CurrentStation = @0
                  WHERE Id = @1
                    AND (CurrentStation IS NULL OR CurrentStation < @0)",
                stationNumber, patrolId);
        }
    }
}
