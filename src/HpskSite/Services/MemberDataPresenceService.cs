using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Cheap "does this member have any data in discipline X?" lookups for the
    /// member-list dot indicators on the club page and for the per-discipline
    /// tab dots inside the mini Dashboard modal.
    ///
    /// Sources scanned: PrecisionResultEntry, MilsnabbResultEntry, DuellResultEntry,
    /// NationellHelmatchResultEntry, MagnumPrecisionResultEntry, FaltskytteResultEntry,
    /// and TrainingScores (Discipline column).
    /// </summary>
    public class MemberDataPresenceService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public MemberDataPresenceService(IUmbracoDatabaseFactory databaseFactory)
        {
            _databaseFactory = databaseFactory;
        }

        /// <summary>
        /// Per-discipline presence map for a single member. Returns one entry
        /// per known discipline with true/false. Single round-trip; EXISTS-style
        /// per table so the cost is independent of result-row volume.
        /// </summary>
        public async Task<Dictionary<string, bool>> GetMemberPresenceAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var rows = await db.FetchAsync<string>(@"
                SELECT 'Precision' AS Discipline WHERE EXISTS(SELECT 1 FROM PrecisionResultEntry WHERE MemberId = @0)
                UNION ALL SELECT 'Milsnabb' WHERE EXISTS(SELECT 1 FROM MilsnabbResultEntry WHERE MemberId = @0)
                UNION ALL SELECT 'Duell' WHERE EXISTS(SELECT 1 FROM DuellResultEntry WHERE MemberId = @0)
                UNION ALL SELECT 'NationellHelmatch' WHERE EXISTS(SELECT 1 FROM NationellHelmatchResultEntry WHERE MemberId = @0)
                UNION ALL SELECT 'MagnumPrecision' WHERE EXISTS(SELECT 1 FROM MagnumPrecisionResultEntry WHERE MemberId = @0)
                UNION ALL SELECT 'Faltskytte' WHERE EXISTS(SELECT 1 FROM FaltskytteResultEntry WHERE MemberId = @0)
                UNION ALL SELECT DISTINCT Discipline FROM TrainingScores WHERE MemberId = @0 AND Discipline IS NOT NULL AND Discipline <> ''",
                memberId);

            var present = new HashSet<string>(rows);
            return KnownDisciplines.ToDictionary(d => d, d => present.Contains(d));
        }

        /// <summary>
        /// Per-member, per-discipline presence map. One batched query that
        /// returns every (MemberId, Discipline) tuple in the system; the caller
        /// filters by club membership in memory. Suitable for ~30-200 members
        /// per club; typical cost 30-50 ms.
        /// </summary>
        public async Task<Dictionary<int, HashSet<string>>> GetClubPresenceAsync()
        {
            using var db = _databaseFactory.CreateDatabase();

            var rows = await db.FetchAsync<MemberDisciplineRow>(@"
                SELECT MemberId, 'Precision' AS Discipline FROM PrecisionResultEntry GROUP BY MemberId
                UNION SELECT MemberId, 'Milsnabb' FROM MilsnabbResultEntry GROUP BY MemberId
                UNION SELECT MemberId, 'Duell' FROM DuellResultEntry GROUP BY MemberId
                UNION SELECT MemberId, 'NationellHelmatch' FROM NationellHelmatchResultEntry GROUP BY MemberId
                UNION SELECT MemberId, 'MagnumPrecision' FROM MagnumPrecisionResultEntry GROUP BY MemberId
                UNION SELECT MemberId, 'Faltskytte' FROM FaltskytteResultEntry GROUP BY MemberId
                UNION SELECT MemberId, Discipline FROM TrainingScores WHERE Discipline IS NOT NULL AND Discipline <> '' GROUP BY MemberId, Discipline");

            var result = new Dictionary<int, HashSet<string>>();
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.MemberId, out var set))
                {
                    set = new HashSet<string>();
                    result[row.MemberId] = set;
                }
                set.Add(row.Discipline ?? "");
            }
            return result;
        }

        public static readonly string[] KnownDisciplines = new[]
        {
            "Precision", "Milsnabb", "Duell", "NationellHelmatch", "MagnumPrecision", "Faltskytte"
        };

        private class MemberDisciplineRow
        {
            public int MemberId { get; set; }
            public string? Discipline { get; set; }
        }
    }
}
