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

        public StomprogramItem Add(DateTime start, DateTime? end, string name, string? discipline, string? note, int actorId, bool isPeriod = false)
        {
            var it = new StomprogramItem
            {
                StartDate = start.Date, EndDate = end?.Date, Name = name.Trim(), Discipline = string.IsNullOrWhiteSpace(discipline) ? null : discipline,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), IsPeriod = isPeriod, CreatedAt = DateTime.Now, CreatedByMemberId = actorId
            };
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Insert(it);
            scope.Complete();
            return it;
        }

        public StomprogramItem? Get(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefault<StomprogramItem>("SELECT * FROM StomprogramItem WHERE Id = @0", id);
        }

        /// <summary>Rättar en rad. Skapad-uppgifterna står kvar — raden är samma rad.</summary>
        public bool Update(int id, DateTime start, DateTime? end, string name, string? discipline, string? note, bool isPeriod)
        {
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute(
                "UPDATE StomprogramItem SET StartDate = @1, EndDate = @2, Name = @3, Discipline = @4, Note = @5, IsPeriod = @6 WHERE Id = @0",
                id, start.Date, end?.Date, name.Trim(), string.IsNullOrWhiteSpace(discipline) ? null : discipline,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim(), isPeriod);
            scope.Complete();
            return n > 0;
        }

        /// <summary>
        /// Finns raden redan? Samma gren och samma dagar — oavsett namn, eftersom sajtadmin ofta döper
        /// om en inläst rad ("…precision och magnumprecision" → en rad per gren med eget namn), och en
        /// andra inläsning då annars hade lagt in den igen. Samma namn och startdag räcker också.
        /// Returnerar den befintliga raden, så att ytan kan säga vad den heter.
        /// </summary>
        public static StomprogramItem? FindDuplicate(IEnumerable<StomprogramItem> existing, DateTime start, DateTime? end, string name, string? discipline) =>
            existing.FirstOrDefault(e => e.StartDate.Date == start.Date
                && string.Equals(e.Discipline ?? "", discipline ?? "", StringComparison.OrdinalIgnoreCase)
                && ((e.EndDate?.Date ?? e.StartDate.Date) == (end?.Date ?? start.Date)
                    || string.Equals(e.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)));

        public bool Delete(int id)
        {
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute("DELETE FROM StomprogramItem WHERE Id = @0", id);
            scope.Complete();
            return n > 0;
        }

    }
}
