using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Projektgrupperna — föreningens sparade urval av projekt. Se <see cref="LedgerProjectGroup"/>
    /// för varför det är många-till-många och aldrig en förälder.
    ///
    /// <para><b>⚠️ Allt här är MUTABELT och får vara det.</b> Ingenting i den här tjänsten rör en
    /// konteringsrad. Att skapa, döpa om, radera en grupp eller flytta ett projekt mellan grupper
    /// ändrar bara vilka frågor som går att ställa — aldrig vad som är bokfört.</para>
    /// </summary>
    public class LedgerProjectGroupService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerProjectGroupService> _logger;

        public LedgerProjectGroupService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerProjectGroupService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        public List<LedgerProjectGroup> List(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return new LedgerDb(db, issuerId).Fetch<LedgerProjectGroup>(
                "SELECT * FROM dbo.LedgerProjectGroup WHERE IssuerType = @0 AND IssuerId = @1 ORDER BY Name",
                issuerType, issuerId);
        }

        /// <summary>Alla medlemskap i föreningens grupper — en fråga, inte en per grupp.</summary>
        public List<LedgerProjectGroupMember> Members(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return new LedgerDb(db, issuerId).Fetch<LedgerProjectGroupMember>(
                @"SELECT m.* FROM dbo.LedgerProjectGroupMember m
                    JOIN dbo.LedgerProjectGroup g ON g.Id = m.GroupId
                   WHERE g.IssuerType = @0 AND g.IssuerId = @1",
                issuerType, issuerId);
        }

        public LedgerProjectGroup? Get(int groupId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return new LedgerDb(db, groupId).SingleOrDefault<LedgerProjectGroup>(
                "SELECT * FROM dbo.LedgerProjectGroup WHERE Id = @0", groupId);
        }

        public LedgerProjectGroupResult Create(
            int issuerType, int issuerId, string name, int byMemberId,
            string? description = null, string? sourceType = null, int? sourceId = null)
        {
            name = (name ?? "").Trim();

            var invalid = ValidateName(name);
            if (invalid is not null) return LedgerProjectGroupResult.Failed(invalid);

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            if (NameTaken(ldb, issuerType, issuerId, name, exceptId: null))
                return LedgerProjectGroupResult.Failed($"Det finns redan en grupp som heter \"{name}\".");

            var group = new LedgerProjectGroup
            {
                IssuerType = issuerType,
                IssuerId = issuerId,
                Name = name,
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                SourceType = sourceType,
                SourceId = sourceId,
                CreatedUtc = DateTime.UtcNow,
                CreatedByMemberId = byMemberId
            };

            try
            {
                // ⚠️ SQL, inte db.Insert — NPoco:s [TableName] bär inget schema, och en sandlådas
                //    grupp hade hamnat i dbo. Tabellen har inga triggrar, så OUTPUT går bra här.
                group.Id = ldb.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerProjectGroup
                          (IssuerType, IssuerId, Name, Description, SourceType, SourceId, CreatedUtc, CreatedByMemberId)
                      OUTPUT INSERTED.Id
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7)",
                    group.IssuerType, group.IssuerId, group.Name,
                    (object?)group.Description ?? DBNull.Value,
                    (object?)group.SourceType ?? DBNull.Value,
                    (object?)group.SourceId ?? DBNull.Value,
                    group.CreatedUtc, group.CreatedByMemberId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Projektgruppen {Namn} kunde inte skapas för {Typ}/{Id}.",
                    name, issuerType, issuerId);
                return LedgerProjectGroupResult.Failed("Gruppen kunde inte sparas. Försök igen.");
            }

            return new LedgerProjectGroupResult { Group = group };
        }

        public LedgerProjectGroupResult Update(int groupId, string name, string? description)
        {
            name = (name ?? "").Trim();

            var invalid = ValidateName(name);
            if (invalid is not null) return LedgerProjectGroupResult.Failed(invalid);

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, groupId);

            var group = ldb.SingleOrDefault<LedgerProjectGroup>(
                "SELECT * FROM dbo.LedgerProjectGroup WHERE Id = @0", groupId);
            if (group is null) return LedgerProjectGroupResult.Failed("Gruppen hittades inte.");

            if (NameTaken(ldb, group.IssuerType, group.IssuerId, name, exceptId: groupId))
                return LedgerProjectGroupResult.Failed($"Det finns redan en grupp som heter \"{name}\".");

            group.Name = name;
            group.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

            ldb.Execute("UPDATE dbo.LedgerProjectGroup SET Name = @1, Description = @2 WHERE Id = @0",
                group.Id, group.Name, (object?)group.Description ?? DBNull.Value);

            return new LedgerProjectGroupResult { Group = group };
        }

        /// <summary>
        /// Raderar gruppen. <b>Ingenting annat raderas</b> — gruppen äger inget, den pekar. Projekten
        /// och deras bokförda rader står orörda.
        /// </summary>
        public bool Delete(int groupId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, groupId);

            using var tx = ldb.GetTransaction();
            ldb.Execute("DELETE FROM dbo.LedgerProjectGroupMember WHERE GroupId = @0", groupId);
            var rows = ldb.Execute("DELETE FROM dbo.LedgerProjectGroup WHERE Id = @0", groupId);
            tx.Complete();

            return rows == 1;
        }

        /// <summary>Lägger till eller tar bort ett projekt ur en grupp. Idempotent åt båda hållen.</summary>
        public void SetMember(int groupId, int projectId, bool member, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, groupId);

            if (member)
            {
                ldb.Execute(
                    @"IF NOT EXISTS (SELECT 1 FROM dbo.LedgerProjectGroupMember WHERE GroupId = @0 AND ProjectId = @1)
                        INSERT INTO dbo.LedgerProjectGroupMember (GroupId, ProjectId, AddedUtc, AddedByMemberId)
                        VALUES (@0, @1, @2, @3)",
                    groupId, projectId, DateTime.UtcNow, byMemberId);
            }
            else
            {
                ldb.Execute("DELETE FROM dbo.LedgerProjectGroupMember WHERE GroupId = @0 AND ProjectId = @1",
                    groupId, projectId);
            }
        }

        /// <summary>
        /// Seriens förvalda grupp — hämtas eller skapas.
        ///
        /// <para><b>⚠️ Skapas LAT, när seriens FÖRSTA projekt får sin första krona</b> — aldrig när
        /// serien skapas. Samma regel som projektet självt: en grupp utan ett enda projekt med
        /// utfall är brus i en lista som ska vara kort.</para>
        ///
        /// <para>Namnkrock med en grupp kassören skapat själv: då sätts serien ändå inte på den
        /// gruppen, eftersom kassören kan ha menat något annat med namnet. Seriens grupp får årtal
        /// eller id i namnet i stället.</para>
        /// </summary>
        public LedgerProjectGroup? EnsureSeriesGroup(
            int issuerType, int issuerId, int seriesId, IReadOnlyList<string> nameCandidates)
        {
            using (var db = _databaseFactory.CreateDatabase())
            {
                var existing = new LedgerDb(db, issuerId).FirstOrDefault<LedgerProjectGroup>(
                    @"SELECT * FROM dbo.LedgerProjectGroup
                       WHERE IssuerType = @0 AND IssuerId = @1 AND SourceType = @2 AND SourceId = @3",
                    issuerType, issuerId, LedgerProjectSource.Series, seriesId);

                if (existing is not null) return existing;
            }

            foreach (var candidate in nameCandidates)
            {
                var created = Create(issuerType, issuerId, candidate, 0,
                    sourceType: LedgerProjectSource.Series, sourceId: seriesId);

                if (created.Success) return created.Group;
            }

            return null;
        }

        private static string? ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Gruppen måste ha ett namn.";
            if (name.Length > LedgerProjectSource.MaxNameLength)
                return $"Gruppens namn får vara högst {LedgerProjectSource.MaxNameLength} tecken.";
            return null;
        }

        private static bool NameTaken(LedgerDb ldb, int issuerType, int issuerId, string name, int? exceptId)
            => ldb.ExecuteScalar<int>(
                   @"SELECT COUNT(1) FROM dbo.LedgerProjectGroup
                      WHERE IssuerType = @0 AND IssuerId = @1 AND Name = @2 AND (@3 IS NULL OR Id <> @3)",
                   issuerType, issuerId, name, (object?)exceptId ?? DBNull.Value) > 0;
    }

    public class LedgerProjectGroupResult
    {
        public bool Success => Error is null;
        public string? Error { get; set; }
        public LedgerProjectGroup? Group { get; set; }

        public static LedgerProjectGroupResult Failed(string error) => new() { Error = error };
    }
}
