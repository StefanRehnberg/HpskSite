using System.Globalization;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// The day axis of the plan — the single list of days that the Bemanning grid AND Dagsprogram both
    /// render, so they can never disagree about what days the event has.
    ///
    /// <para><b>Arrangör-owned, span-seeded.</b> The days you STAFF are not the days you COMPETE: build-up,
    /// materiel runs and teardown all carry crew and sit outside <c>competitionDate..competitionEndDate</c>.
    /// So the span only seeds the list on first use (keeping an ordinary one-day competition at zero setup)
    /// and the organiser owns it from there.</para>
    ///
    /// <para>Never throws — a missing table degrades to "no days", which the grid renders as a single
    /// undated column rather than an error.</para>
    /// </summary>
    public class StaffDayService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly ILogger<StaffDayService> _logger;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        public StaffDayService(IScopeProvider scopeProvider, IContentService contentService, ILogger<StaffDayService> logger)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _logger = logger;
        }

        public List<StaffDay> GetDays(int competitionId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.Fetch<StaffDay>(
                    "SELECT * FROM StaffDay WHERE CompetitionId = @0 ORDER BY DayDate, SortOrder, Id", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StaffDay: read failed for competition {CompetitionId}", competitionId);
                return new List<StaffDay>();
            }
        }

        /// <summary>
        /// Seed the day list from the competition's own span the first time the plan is opened, so the
        /// common case needs no setup at all. Only ever runs when the list is EMPTY — once the organiser
        /// has touched the days, we never add to them behind their back (a deleted day must stay deleted).
        /// </summary>
        public List<StaffDay> EnsureSeeded(int competitionId, int byMemberId)
        {
            var days = GetDays(competitionId);
            if (days.Count > 0) return days;

            var (start, end) = ReadSpan(competitionId);
            if (start == null) return days;

            var last = end ?? start.Value;
            if (last < start.Value) last = start.Value;
            // A pathological span must not create hundreds of columns.
            if ((last - start.Value).TotalDays > 30) last = start.Value;

            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var order = 0;
                for (var d = start.Value.Date; d <= last.Date; d = d.AddDays(1))
                {
                    scope.Database.Insert(new StaffDay
                    {
                        CompetitionId = competitionId,
                        DayDate = d,
                        Label = "",
                        Kind = StaffDayKind.Competition,
                        SortOrder = order++,
                        CreatedByMemberId = byMemberId,
                        CreatedDate = DateTime.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StaffDay: seeding failed for competition {CompetitionId}", competitionId);
            }
            return GetDays(competitionId);
        }

        public (bool ok, string? message, int id) SaveDay(SaveStaffDayRequest req, int byMemberId)
        {
            if (!DateTime.TryParseExact(req.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return (false, "Ange ett datum.", 0);

            var label = (req.Label ?? "").Trim();
            if (label.Length > 80) label = label[..80];
            var kind = StaffDayKind.Normalise(req.Kind);

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var clash = db.SingleOrDefault<StaffDay>(
                "SELECT * FROM StaffDay WHERE CompetitionId = @0 AND DayDate = @1", req.CompetitionId, date.Date);
            if (clash != null && clash.Id != req.Id)
                return (false, $"{date.ToString("d MMMM", Sv)} finns redan i planen.", clash.Id);

            if (req.Id > 0)
            {
                var row = db.SingleOrDefault<StaffDay>(
                    "SELECT * FROM StaffDay WHERE Id = @0 AND CompetitionId = @1", req.Id, req.CompetitionId);
                if (row == null) return (false, "Dagen hittades inte.", 0);
                row.DayDate = date.Date; row.Label = label; row.Kind = kind;
                db.Update(row);
                return (true, null, row.Id);
            }

            var created = new StaffDay
            {
                CompetitionId = req.CompetitionId,
                DayDate = date.Date,
                Label = label,
                Kind = kind,
                SortOrder = GetDays(req.CompetitionId).Count,
                CreatedByMemberId = byMemberId,
                CreatedDate = DateTime.UtcNow,
            };
            created.Id = Convert.ToInt32(db.Insert(created));
            return (true, null, created.Id);
        }

        /// <summary>
        /// Remove a day. Refuses while crew are booked on it — silently orphaning people into an
        /// "undated" bucket is how a plan quietly loses a build team.
        /// </summary>
        public (bool ok, string? message) DeleteDay(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.SingleOrDefault<StaffDay>(
                "SELECT * FROM StaffDay WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
            if (row == null) return (false, "Dagen hittades inte.");

            var n = CountAssignmentsOn(db, competitionId, row.DayDate);
            if (n > 0)
                return (false, $"{n} uppdrag ligger på den dagen. Flytta eller ta bort dem först.");

            db.Execute("DELETE FROM StaffDay WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
            return (true, null);
        }

        /// <summary>Which of the comp's days actually carry crew (drives the delete guard in the UI).</summary>
        public HashSet<string> DatesWithAssignments(int competitionId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                // StartsAt wins; otherwise the linked pass's date. Mirrors the grid's own day resolution.
                var rows = scope.Database.Fetch<DateTime?>(@"
                    SELECT COALESCE(CAST(sa.StartsAt AS DATE), sa.DayDate, CAST(sp.PassDate AS DATE))
                    FROM StaffAssignment sa
                    LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
                    WHERE sa.CompetitionId = @0", competitionId);
                foreach (var d in rows)
                    if (d is { } v) set.Add(v.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StaffDay: assignment-date scan failed for competition {CompetitionId}", competitionId);
            }
            return set;
        }

        private static int CountAssignmentsOn(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int competitionId, DateTime date)
        {
            try
            {
                return db.ExecuteScalar<int>(@"
                    SELECT COUNT(1) FROM StaffAssignment sa
                    LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
                    WHERE sa.CompetitionId = @0
                      AND COALESCE(CAST(sa.StartsAt AS DATE), sa.DayDate, CAST(sp.PassDate AS DATE)) = @1",
                    competitionId, date.Date);
            }
            catch { return 0; }
        }

        public StaffDayView ToView(StaffDay d, HashSet<string>? withWork = null)
        {
            var key = d.DayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new StaffDayView
            {
                Id = d.Id,
                Date = key,
                Label = d.Label,
                Kind = d.Kind,
                KindLabel = StaffDayKind.Label(d.Kind),
                IsParticipantFacing = StaffDayKind.IsParticipantFacing(d.Kind),
                HasAssignments = withWork?.Contains(key) == true,
            };
        }

        /// <summary>competitionDate + competitionEndDate, defensively (an unset date reads as MinValue).</summary>
        private (DateTime? start, DateTime? end) ReadSpan(int competitionId)
        {
            try
            {
                var c = _contentService.GetById(competitionId);
                if (c == null) return (null, null);
                DateTime? Real(string alias)
                {
                    if (!c.HasProperty(alias)) return null;
                    var v = c.GetValue<DateTime?>(alias);
                    return v is { } d && d != default && d.Year > 1900 ? d : null;
                }
                return (Real("competitionDate"), Real("competitionEndDate"));
            }
            catch { return (null, null); }
        }
    }
}
