using HpskSite.Models.Kretsgranskning;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Förbundets stomprogram (fas 4): årets fasta datum som sajtadmin för in. Egen tjänst med bara
    /// databasen som beroende, eftersom kretskalendern läser den och kretsplaneringen i sin tur
    /// läser kalendern.
    /// </summary>
    public class StomprogramService
    {
        private readonly IScopeProvider _scopeProvider;
        public StomprogramService(IScopeProvider scopeProvider) { _scopeProvider = scopeProvider; }

        public bool TableExists()
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.StomprogramItem', 'U') IS NULL THEN 0 ELSE 1 END") == 1;
            }
            catch { return false; }
        }

        public List<StomprogramItem> Range(DateTime from, DateTime to)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StomprogramItem>(
                "SELECT * FROM StomprogramItem WHERE StartDate <= @1 AND ISNULL(EndDate, StartDate) >= @0 ORDER BY StartDate, Name",
                from.Date, to.Date);
        }

        public List<StomprogramItem> Year(int year) =>
            Range(new DateTime(year, 1, 1), new DateTime(year, 12, 31));

        public StomprogramItem Add(DateTime start, DateTime? end, string name, string? discipline, string? note, int actorId)
        {
            var it = new StomprogramItem
            {
                StartDate = start.Date, EndDate = end?.Date, Name = name.Trim(), Discipline = string.IsNullOrWhiteSpace(discipline) ? null : discipline,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedAt = DateTime.Now, CreatedByMemberId = actorId
            };
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Insert(it);
            scope.Complete();
            return it;
        }

        public bool Delete(int id)
        {
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute("DELETE FROM StomprogramItem WHERE Id = @0", id);
            scope.Complete();
            return n > 0;
        }

    }
}
