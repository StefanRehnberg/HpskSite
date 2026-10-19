using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>En sedd film. <c>LastWatchedUtc</c> är UTC (registrerad i <c>StoredTimeBasis</c>).</summary>
    [TableName("MemberTutorialWatch")]
    [PrimaryKey("MemberId", AutoIncrement = false)]
    public class MemberTutorialWatchRow
    {
        public int MemberId { get; set; }
        public string TutorialId { get; set; } = "";
        public DateTime LastWatchedUtc { get; set; }
        public int ViewCount { get; set; }
    }

    /// <summary>
    /// Vilka filmer en medlem sett (skalplanen B5, 2026-10-19). Låg som JSON i medlemsegenskapen
    /// <c>watchedTutorials</c>, så varje uppspelad film var en hel medlemssparning. Nu en rad per
    /// (medlem, film), skriven med en upsert.
    /// </summary>
    public class TutorialWatchService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public TutorialWatchService(IUmbracoDatabaseFactory databaseFactory) => _databaseFactory = databaseFactory;

        public void MarkWatched(int memberId, string tutorialId)
        {
            using var db = _databaseFactory.CreateDatabase();
            // ⚠️ Inget @@ROWCOUNT: NPoco läser det som en parameter. IF EXISTS med låset i stället.
            db.Execute(@"
SET XACT_ABORT ON; BEGIN TRANSACTION;
IF EXISTS (SELECT 1 FROM MemberTutorialWatch WITH (UPDLOCK, HOLDLOCK) WHERE MemberId = @0 AND TutorialId = @1)
    UPDATE MemberTutorialWatch SET ViewCount = ViewCount + 1, LastWatchedUtc = @2 WHERE MemberId = @0 AND TutorialId = @1;
ELSE
    INSERT INTO MemberTutorialWatch (MemberId, TutorialId, LastWatchedUtc, ViewCount) VALUES (@0, @1, @2, 1);
COMMIT TRANSACTION;",
                memberId, tutorialId, DateTime.UtcNow);
        }

        public List<MemberTutorialWatchRow> GetForMember(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<MemberTutorialWatchRow>(
                "SELECT MemberId, TutorialId, LastWatchedUtc, ViewCount FROM MemberTutorialWatch WHERE MemberId = @0 ORDER BY LastWatchedUtc",
                memberId);
        }
    }
}
