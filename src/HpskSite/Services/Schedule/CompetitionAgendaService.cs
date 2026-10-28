using System.Globalization;
using HpskSite.Models.Schedule;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// CRUD over a competition's day programme (CompetitionAgendaItem) — the "Dagsprogram" tab on
    /// /tavlingsplanering. Reads are graceful when the table hasn't been migrated yet: the feature
    /// simply isn't there rather than 500-ing the planning page.
    /// </summary>
    public class CompetitionAgendaService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CompetitionAgendaService> _logger;

        public CompetitionAgendaService(IScopeProvider scopeProvider, ILogger<CompetitionAgendaService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public List<CompetitionAgendaItem> GetForCompetition(int competitionId)
        {
            if (competitionId <= 0) return new List<CompetitionAgendaItem>();
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.Fetch<CompetitionAgendaItem>(
                    "SELECT * FROM CompetitionAgendaItem WHERE CompetitionId = @0 ORDER BY ItemDate, StartTime, SortOrder, Id",
                    competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agenda read failed for comp {Comp} — has create-competition-agenda-table.sql been run?", competitionId);
                return new List<CompetitionAgendaItem>();
            }
        }

        /// <summary>Create or update. Returns (ok, message, id).</summary>
        public (bool ok, string? message, int id) Save(SaveAgendaItemRequest req, int actingMemberId)
        {
            if (req.CompetitionId <= 0) return (false, "Ogiltig tävling.", 0);
            if (string.IsNullOrWhiteSpace(req.Title)) return (false, "Punkten måste ha en rubrik.", 0);

            var start = NormalizeTime(req.StartTime);
            var end = NormalizeTime(req.EndTime);
            if (start != null && end != null && string.CompareOrdinal(end, start) < 0)
                return (false, "Sluttiden är före starttiden.", 0);

            try
            {
                using var scope = _scopeProvider.CreateScope();
                var db = scope.Database;
                var now = DateTime.Now;

                if (req.Id > 0)
                {
                    var existing = db.SingleOrDefault<CompetitionAgendaItem>(
                        "SELECT * FROM CompetitionAgendaItem WHERE Id = @0 AND CompetitionId = @1", req.Id, req.CompetitionId);
                    if (existing == null) { scope.Complete(); return (false, "Punkten kunde inte hittas.", 0); }

                    existing.ItemDate = ParseDate(req.ItemDate);
                    existing.StartTime = start;
                    existing.EndTime = end;
                    existing.Title = Trim(req.Title, 200)!;
                    existing.Location = Trim(req.Location, 200);
                    existing.Note = Trim(req.Note, 500);
                    existing.Audience = NormalizeAudience(req.Audience);
                    existing.Icon = Trim(req.Icon, 50);
                    existing.ModifiedDate = now;
                    db.Update(existing);
                    scope.Complete();
                    return (true, null, existing.Id);
                }

                var row = new CompetitionAgendaItem
                {
                    CompetitionId = req.CompetitionId,
                    ItemDate = ParseDate(req.ItemDate),
                    StartTime = start,
                    EndTime = end,
                    Title = Trim(req.Title, 200)!,
                    Location = Trim(req.Location, 200),
                    Note = Trim(req.Note, 500),
                    Audience = NormalizeAudience(req.Audience),
                    Icon = Trim(req.Icon, 50),
                    CreatedByMemberId = actingMemberId,
                    CreatedDate = now,
                    ModifiedDate = now,
                };
                db.Insert(row);
                scope.Complete();
                return (true, null, row.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agenda save failed for comp {Comp}", req.CompetitionId);
                return (false, "Kunde inte spara. Har databastabellen skapats?", 0);
            }
        }

        public bool Delete(int id, int competitionId)
        {
            if (id <= 0 || competitionId <= 0) return false;
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var n = scope.Database.Execute(
                    "DELETE FROM CompetitionAgendaItem WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
                scope.Complete();
                return n > 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agenda delete failed for item {Id}", id);
                return false;
            }
        }

        /// <summary>Samma punkt (titel) finns redan den dagen — mallen lägger inte in den igen.</summary>
        private static bool Has(IEnumerable<CompetitionAgendaItem> existing, AgendaTemplate.Row t, DateTime? date) =>
            existing.Any(e => e.ItemDate?.Date == date?.Date && string.Equals(e.Title?.Trim(), t.Title, StringComparison.OrdinalIgnoreCase));

        /// <summary>Mallens rader med uppgift om vilka som redan finns den dagen (för dialogen).</summary>
        public IReadOnlyList<(int index, AgendaTemplate.Row row, bool exists)> TemplateFor(int competitionId, DateTime? date)
        {
            var existing = GetForCompetition(competitionId);
            return AgendaTemplate.Default.Select((t, i) => (i, t, Has(existing, t, date))).ToList();
        }

        /// <summary>
        /// Lägger in de valda raderna ur mallen (null = alla). Rader som redan finns den dagen hoppas över, så att ett
        /// dubbelklick eller en andra körning inte dubblerar dagen. Returns how many rows were created.
        /// </summary>
        public (bool ok, string? message, int created) SeedDefaults(int competitionId, DateTime? compDate, int actingMemberId, IReadOnlyCollection<int>? picks = null)
        {
            if (competitionId <= 0) return (false, "Ogiltig tävling.", 0);

            var created = 0;
            try
            {
                var existing = GetForCompetition(competitionId);
                using var scope = _scopeProvider.CreateScope();
                var now = DateTime.Now;
                var order = existing.Count;
                for (var i = 0; i < AgendaTemplate.Default.Length; i++)
                {
                    var t = AgendaTemplate.Default[i];
                    if (picks != null && !picks.Contains(i)) continue;
                    if (Has(existing, t, compDate)) continue;
                    scope.Database.Insert(new CompetitionAgendaItem
                    {
                        CompetitionId = competitionId,
                        ItemDate = compDate?.Date,
                        StartTime = t.StartTime,
                        EndTime = t.EndTime,
                        Title = t.Title,
                        Audience = t.Audience,
                        Icon = t.Icon,
                        SortOrder = order++,
                        CreatedByMemberId = actingMemberId,
                        CreatedDate = now,
                        ModifiedDate = now,
                    });
                    created++;
                }
                scope.Complete();
                return (true, null, created);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Agenda seed failed for comp {Comp}", competitionId);
                return (false, "Kunde inte skapa standardprogrammet. Har databastabellen skapats?", created);
            }
        }

        // --- helpers ---

        private static string? Trim(string? s, int max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var t = s.Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        private static string NormalizeAudience(string? a) => a switch
        {
            AgendaAudience.Shooters => AgendaAudience.Shooters,
            AgendaAudience.Staff => AgendaAudience.Staff,
            _ => AgendaAudience.All,
        };

        /// <summary>Stores "HH:mm" or null. Anything unparseable becomes null rather than junk.</summary>
        private static string? NormalizeTime(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = MyScheduleService.ParseTimeOfDay(raw);
            return t == null ? null : $"{(int)t.Value.TotalHours:00}:{t.Value.Minutes:00}";
        }

        private static DateTime? ParseDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var txt = raw.Trim();
            if (DateTime.TryParseExact(txt, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d.Date;
            if (DateTime.TryParse(txt, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out d)) return d.Date;
            if (DateTime.TryParse(txt, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.Date;
            return null;
        }
    }
}
