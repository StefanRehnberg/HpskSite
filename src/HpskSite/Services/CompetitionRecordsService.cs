using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Single writer for the CompetitionRecords table. Maintains the IsCurrent flag and
    /// ReplacedByRecordId chain so we can show current records cheaply AND keep full
    /// history. Reads are uncached at this stage; the table is small.
    /// </summary>
    public class CompetitionRecordsService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<CompetitionRecordsService> _logger;

        public CompetitionRecordsService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<CompetitionRecordsService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────

        public async Task<List<CompetitionRecord>> GetCurrentForScopeAsync(string level, string scopeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionRecord>(
                @"WHERE Level = @0 AND ScopeId = @1 AND IsCurrent = 1
                  ORDER BY Discipline, RecordType, ClassCode",
                level, scopeId);
        }

        public async Task<List<CompetitionRecord>> GetCurrentForMemberAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionRecord>(
                @"WHERE IsCurrent = 1 AND HolderMemberId = @0
                  ORDER BY Level, Discipline, RecordType, ClassCode",
                memberId);
        }

        public async Task<List<CompetitionRecord>> GetHistoryAsync(
            string level, string scopeId, string discipline, string recordType, string classCode)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionRecord>(
                @"WHERE Level = @0 AND ScopeId = @1 AND Discipline = @2
                    AND RecordType = @3 AND ClassCode = @4
                  ORDER BY RecordDate DESC, Id DESC",
                level, scopeId, discipline, recordType, classCode);
        }

        public async Task<CompetitionRecord?> GetByIdAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultByIdAsync<CompetitionRecord>(id);
        }

        // ── Writes ────────────────────────────────────────────────────

        public async Task<(bool Success, int RecordId, string? Message)> CreateAsync(
            CreateRecordRequest req, int actingMemberId)
        {
            // Validate against the registry — class must be valid for the discipline+type.
            if (!RecordClassRegistry.IsValid(req.Discipline, req.RecordType, req.ClassCode))
            {
                return (false, 0, $"Klassen {req.ClassCode} finns inte för {RecordDisciplines.DisplayName(req.Discipline)} {RecordTypes.DisplayName(req.RecordType)}.");
            }

            var seriesCount = RecordClassRegistry.GetSeriesCount(req.Discipline, req.RecordType);
            var maxScore = RecordClassRegistry.GetMaxScore(req.Discipline, req.RecordType);
            if (req.TotalScore < 0 || req.TotalScore > maxScore)
            {
                return (false, 0, $"Poäng {req.TotalScore} är utanför giltigt intervall [0, {maxScore}] för {seriesCount} serier.");
            }

            if (string.IsNullOrWhiteSpace(req.HolderName))
            {
                return (false, 0, "Skytt eller lagnamn måste anges.");
            }

            using var db = _databaseFactory.CreateDatabase();

            // Find the previous current record for this exact key. It must be flipped
            // before the new row is inserted (or as part of the same transaction).
            var prior = await db.SingleOrDefaultAsync<CompetitionRecord>(
                @"WHERE Level = @0 AND ScopeId = @1 AND Discipline = @2
                    AND RecordType = @3 AND ClassCode = @4 AND IsCurrent = 1",
                req.Level, req.ScopeId, req.Discipline, req.RecordType, req.ClassCode);

            using var tx = db.GetTransaction();
            try
            {
                var entry = new CompetitionRecord
                {
                    Level = req.Level,
                    ScopeId = req.ScopeId,
                    Discipline = req.Discipline,
                    RecordType = req.RecordType,
                    ClassCode = req.ClassCode,
                    TotalScore = req.TotalScore,
                    SeriesCount = seriesCount,
                    RecordDate = req.RecordDate,
                    CompetitionName = req.CompetitionName,
                    HolderMemberId = req.HolderMemberId,
                    HolderName = req.HolderName,
                    TeamName = req.TeamName,
                    TeamMembersJson = req.TeamMembersJson,
                    Notes = req.Notes,
                    IsCurrent = true,
                    EnteredByMemberId = actingMemberId,
                    EnteredAt = DateTime.UtcNow
                };
                var newId = Convert.ToInt32(await db.InsertAsync(entry));

                if (prior != null)
                {
                    prior.IsCurrent = false;
                    prior.ReplacedByRecordId = newId;
                    await db.UpdateAsync(prior);
                }

                tx.Complete();
                _logger.LogInformation(
                    "Created record {RecordId} ({Level}/{ScopeId}/{Discipline}/{RecordType}/{ClassCode}) by member {ActingMemberId}",
                    newId, req.Level, req.ScopeId, req.Discipline, req.RecordType, req.ClassCode, actingMemberId);
                return (true, newId, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create record");
                return (false, 0, "Kunde inte spara rekordet: " + ex.Message);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateMetaAsync(
            int recordId, UpdateRecordMetaRequest req)
        {
            using var db = _databaseFactory.CreateDatabase();
            var record = await db.SingleOrDefaultByIdAsync<CompetitionRecord>(recordId);
            if (record == null) return (false, "Rekordet hittades inte.");

            // Re-validate score if changed.
            if (req.TotalScore.HasValue)
            {
                var maxScore = RecordClassRegistry.GetMaxScore(record.Discipline, record.RecordType);
                if (req.TotalScore.Value < 0 || req.TotalScore.Value > maxScore)
                    return (false, $"Poäng utanför giltigt intervall [0, {maxScore}].");
                record.TotalScore = req.TotalScore.Value;
            }

            if (req.RecordDate.HasValue) record.RecordDate = req.RecordDate.Value;
            if (req.CompetitionName != null) record.CompetitionName = req.CompetitionName;
            if (req.HolderName != null && !string.IsNullOrWhiteSpace(req.HolderName)) record.HolderName = req.HolderName;
            if (req.HolderMemberIdSet) record.HolderMemberId = req.HolderMemberId;
            if (req.TeamName != null) record.TeamName = req.TeamName;
            if (req.TeamMembersJson != null) record.TeamMembersJson = req.TeamMembersJson;
            if (req.Notes != null) record.Notes = req.Notes;

            await db.UpdateAsync(record);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteAsync(int recordId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var record = await db.SingleOrDefaultByIdAsync<CompetitionRecord>(recordId);
            if (record == null) return (false, "Rekordet hittades inte.");

            using var tx = db.GetTransaction();
            try
            {
                if (record.IsCurrent)
                {
                    // Find the most recent prior record for this same key (it's the one
                    // whose ReplacedByRecordId == this record). If found, re-promote it.
                    var prior = await db.SingleOrDefaultAsync<CompetitionRecord>(
                        @"WHERE Level = @0 AND ScopeId = @1 AND Discipline = @2
                            AND RecordType = @3 AND ClassCode = @4 AND ReplacedByRecordId = @5",
                        record.Level, record.ScopeId, record.Discipline, record.RecordType, record.ClassCode, recordId);

                    if (prior != null)
                    {
                        prior.IsCurrent = true;
                        prior.ReplacedByRecordId = null;
                        await db.UpdateAsync(prior);
                    }
                }
                else
                {
                    // Mid-history delete: detach the link from the row whose ReplacedByRecordId
                    // points at this one, so the chain is consistent. The "next" record's
                    // ReplacedByRecordId points to the one we're deleting; null it out.
                    await db.ExecuteAsync(
                        "UPDATE CompetitionRecords SET ReplacedByRecordId = NULL WHERE ReplacedByRecordId = @0",
                        recordId);
                }

                await db.DeleteAsync(record);
                tx.Complete();
                _logger.LogInformation("Deleted record {RecordId}", recordId);
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete record {RecordId}", recordId);
                return (false, "Kunde inte ta bort rekordet: " + ex.Message);
            }
        }
    }

    public class CreateRecordRequest
    {
        public string Level { get; set; } = "";
        public string ScopeId { get; set; } = "";
        public string Discipline { get; set; } = "";
        public string RecordType { get; set; } = "";
        public string ClassCode { get; set; } = "";
        public int TotalScore { get; set; }
        public DateTime RecordDate { get; set; }
        public string? CompetitionName { get; set; }
        public int? HolderMemberId { get; set; }
        public string HolderName { get; set; } = "";
        public string? TeamName { get; set; }
        public string? TeamMembersJson { get; set; }
        public string? Notes { get; set; }
    }

    public class UpdateRecordMetaRequest
    {
        public int? TotalScore { get; set; }
        public DateTime? RecordDate { get; set; }
        public string? CompetitionName { get; set; }
        public int? HolderMemberId { get; set; }
        public bool HolderMemberIdSet { get; set; }   // whether to apply the HolderMemberId field
        public string? HolderName { get; set; }
        public string? TeamName { get; set; }
        public string? TeamMembersJson { get; set; }
        public string? Notes { get; set; }
    }
}
