using HpskSite.Models;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// CRUD for Stormästarmärket inteckningspoäng entries (<see cref="MarkenStormastarEntry"/>).
    /// Reads degrade gracefully when the table hasn't been created yet
    /// (create-marken-stormastar-table.sql) — the rest of Märken keeps working. Only Verified rows
    /// count toward the 30-point eligibility threshold; the controller does the summing.
    /// </summary>
    public class MarkenStormastarService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public MarkenStormastarService(IUmbracoDatabaseFactory databaseFactory)
        {
            _databaseFactory = databaseFactory;
        }

        public async Task<MarkenStormastarEntry?> GetAsync(int id)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return await db.SingleOrDefaultAsync<MarkenStormastarEntry>("WHERE Id = @0", id);
            }
            catch { return null; }
        }

        public async Task<int> InsertAsync(MarkenStormastarEntry e)
        {
            e.CreatedAt = DateTime.Now; e.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(e);
            return e.Id;
        }

        public async Task<(bool, string?)> SetStatusAsync(int id, string status, int validatorId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var e = await db.SingleOrDefaultAsync<MarkenStormastarEntry>("WHERE Id = @0", id);
            if (e == null) return (false, "Inteckningen hittades inte.");
            e.Status = status;
            if (status == Marken.StatusVerified) { e.ValidatedByMemberId = validatorId; e.ValidatedDate = DateTime.Now; }
            e.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(e);
            return (true, null);
        }

        public async Task<(bool, string?)> DeleteAsync(int id)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var e = await db.SingleOrDefaultAsync<MarkenStormastarEntry>("WHERE Id = @0", id);
                if (e == null) return (true, null);
                await db.DeleteAsync(e);
                return (true, null);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public async Task<List<MarkenStormastarEntry>> GetPendingAsync(IEnumerable<int>? clubIds)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                if (clubIds == null)
                    return await db.FetchAsync<MarkenStormastarEntry>("WHERE Status = @0 ORDER BY CreatedAt", Marken.SeriesStatusPending);
                var ids = clubIds.Distinct().ToList();
                if (ids.Count == 0) return new();
                return await db.FetchAsync<MarkenStormastarEntry>(
                    $"WHERE Status = @0 AND ClubId IN ({string.Join(",", ids)}) ORDER BY CreatedAt", Marken.SeriesStatusPending);
            }
            catch { return new(); }
        }

        public async Task<List<MarkenStormastarEntry>> GetForMemberAsync(int memberId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return await db.FetchAsync<MarkenStormastarEntry>(
                    "WHERE MemberId = @0 ORDER BY [Year] DESC, CreatedAt DESC", memberId);
            }
            catch { return new(); }
        }
    }
}
