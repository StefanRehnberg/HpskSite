using System.Globalization;
using HpskSite.Models.Staffing;
using Newtonsoft.Json;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Self-sign-up on the /bemanna page (rework): the organiser defines "help-needed" slots (day/shift
    /// rows); a member checks the ones they can help with, adds optional per-slot times, and leaves a comment.
    /// No role selection. Managers review the sign-ups (slots + times + comment).
    /// </summary>
    public class StaffHelpService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly StaffOwnerResolver _owners;
        private readonly RoleCatalogService _roles;
        private readonly StaffDayService _days;
        private readonly StaffPassService _pass;

        public StaffHelpService(IScopeProvider scopeProvider, StaffOwnerResolver owners, RoleCatalogService roles,
            StaffDayService days, StaffPassService pass)
        {
            _scopeProvider = scopeProvider;
            _owners = owners;
            _roles = roles;
            _days = days;
            _pass = pass;
        }

        // ---- behoven per dag (det medlemmen kan anmäla sig till) ----

        /// <summary>
        /// Planens dagar med rollerna som behövs (antal &gt; 0) och hur många som fattas. Läser bara — dagarna
        /// tas ur planen, och har planen inga dagar ännu ur ägarens datum (utan att skriva dem).
        /// Bemannade räknas per roll och dag som rutnätet gör (starttid → dag → passets dag), utom de som tackat nej.
        /// </summary>
        public List<NeedDayView> NeedDays(StaffOwner owner)
        {
            var needs = _pass.GetAllCrewNeeds(owner);
            if (needs.Count == 0) return new();

            var days = _days.GetDays(owner)
                .Select(d => (Date: d.DayDate.Date, d.Kind, Label: string.IsNullOrWhiteSpace(d.Label) ? null : d.Label))
                .ToList();
            if (days.Count == 0)
            {
                var (start, end) = _owners.Span(owner);
                if (start is { } s && s != default)
                {
                    var last = end is { } e && e != default && e >= s ? e.Date : s.Date;
                    if ((last - s.Date).TotalDays > 30) last = s.Date;
                    for (var d = s.Date; d <= last; d = d.AddDays(1)) days.Add((d, StaffDayKind.Competition, null));
                }
            }

            var assigned = new Dictionary<(string Role, DateTime Day), int>();
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var rows = scope.Database.Fetch<RoleDayCount>(@"
                    SELECT sa.RoleKey, COALESCE(CAST(sa.StartsAt AS DATE), sa.DayDate, CAST(sp.PassDate AS DATE)) AS Day, COUNT(*) AS N
                    FROM StaffAssignment sa LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
                    WHERE " + StaffOwner.Where(0, "sa.") + @"
                    GROUP BY sa.RoleKey, COALESCE(CAST(sa.StartsAt AS DATE), sa.DayDate, CAST(sp.PassDate AS DATE))",
                    owner.P);
                foreach (var r in rows)
                    if (r.Day is { } d) assigned[(r.RoleKey.ToLowerInvariant(), d.Date)] = r.N;
            }

            var discipline = _owners.Discipline(owner);
            var catalog = _roles.For(owner, discipline);
            // Planeringstestet k2 (S2 fynd 2): behovet "per station" (stationschef, stationsmarkörer) räknades inte, så
            // rollen fanns varken på /bemanna, i Hitta funktionärer eller i relämejlet. Nu: summan över stationerna.
            var stationNeeds = needs.Where(n => string.Equals(n.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase)).ToList();
            var stations = stationNeeds.Count > 0 ? _pass.FamilyAreaStations(owner) : new List<int>();
            var result = new List<NeedDayView>();
            foreach (var (date, kind, label) in days.OrderBy(d => d.Date))
            {
                var view = new NeedDayView
                {
                    Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Kind = StaffDayKind.Normalise(kind),
                    KindLabel = StaffDayKind.Label(StaffDayKind.Normalise(kind)),
                    DayLabel = label,
                };
                foreach (var role in catalog)
                {
                    var n = CrewNeedRules.ForDay(needs, CrewNeedScope.All, role.Key, date);
                    if (stationNeeds.Any(r => string.Equals(r.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase)))
                        n += stations.Count > 0
                            ? stations.Sum(st => CrewNeedRules.For(stationNeeds, CrewNeedScope.Station, role.Key, date, st.ToString(CultureInfo.InvariantCulture), null))
                            : CrewNeedRules.ForDay(stationNeeds, CrewNeedScope.Station, role.Key, date);
                    if (n <= 0) continue;
                    assigned.TryGetValue((role.Key.ToLowerInvariant(), date), out var have);
                    view.Roles.Add(new NeedRoleView { RoleKey = role.Key, RoleName = role.DisplayName, Needed = n, Missing = Math.Max(0, n - have) });
                }
                if (view.Roles.Count > 0) result.Add(view);
            }
            return result;
        }

        private class RoleDayCount
        {
            public string RoleKey { get; set; } = "";
            public DateTime? Day { get; set; }
            public int N { get; set; }
        }

        // ---- slots (organiser) ----

        public List<HelpSlotView> GetSlots(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffHelpSlot>(
                    "SELECT * FROM StaffHelpSlot WHERE " + StaffOwner.Where(0) + " ORDER BY SlotDate, SortOrder, StartTime, Id", owner.P)
                .Select(ToSlotView).ToList();
        }

        public int SaveSlot(SaveHelpSlotRequest req, StaffOwner owner, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            StaffHelpSlot row;
            if (req.Id > 0)
            {
                // Bara ett pass i den här tävlingen — ett id utifrån får aldrig flytta en annan tävlings pass hit.
                var existing = db.SingleOrDefault<StaffHelpSlot>(
                    "SELECT * FROM StaffHelpSlot WHERE Id = @0 AND " + StaffOwner.Where(1), req.Id, owner.P);
                if (existing == null) return 0;
                row = existing;
            }
            else
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM StaffHelpSlot WHERE " + StaffOwner.Where(0), owner.P) ?? 0;
                row = new StaffHelpSlot { SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            row.PlanningId = owner.PlanningIdOrNull;
            row.CompetitionId = owner.CompetitionIdOrNull;
            row.SlotDate = ParseDate(req.Date) ?? row.SlotDate;
            row.StartTime = CleanTime(req.StartTime);
            row.EndTime = CleanTime(req.EndTime);
            row.Headline = (req.Headline ?? "").Trim();
            row.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
            if (row.Id > 0) db.Update(row); else row.Id = Convert.ToInt32(db.Insert(row));
            return row.Id;
        }

        public void DeleteSlot(int id, StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("DELETE FROM StaffHelpSlot WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P);
        }

        // ---- member sign-up ----

        public MyHelpSignupView? GetMySignup(StaffOwner owner, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.FirstOrDefault<StaffHelpSignup>(
                "SELECT * FROM StaffHelpSignup WHERE " + StaffOwner.Where(0) + " AND MemberId = @1", owner.P, memberId);
            if (row == null) return null;
            return new MyHelpSignupView { Comment = row.Comment, Slots = ParseChoices(row.SlotsJson) };
        }

        /// <summary>Upsert a member's sign-up. Empty comment + no slots clears it (member un-signs).</summary>
        public void SaveMySignup(StaffOwner owner, int memberId, string memberName, string? comment, List<HelpSlotChoice> slots)
        {
            // Keep only choices for slots that still exist in this competition, and drop empty time windows.
            // En dag ur planen godtas när dagen har ett behov, och rollen (tom = var det behövs) behövs den dagen.
            var validSlotIds = GetSlots(owner).Select(s => s.Id).ToHashSet();
            var needDays = NeedDays(owner).ToDictionary(d => d.Date, d => d.Roles.Select(r => r.RoleKey).ToHashSet(StringComparer.OrdinalIgnoreCase));
            bool Valid(HelpSlotChoice c) => c.SlotId > 0
                ? validSlotIds.Contains(c.SlotId)
                : c.IsDayChoice && needDays.TryGetValue(c.Date!.Trim(), out var roles)
                  && (string.IsNullOrWhiteSpace(c.RoleKey) || roles.Contains(c.RoleKey.Trim()));
            var clean = (slots ?? new()).Where(Valid).Select(c => new HelpSlotChoice
            {
                SlotId = Math.Max(0, c.SlotId),
                Date = c.SlotId > 0 ? null : c.Date!.Trim(),
                RoleKey = c.SlotId > 0 ? null : (string.IsNullOrWhiteSpace(c.RoleKey) ? "" : c.RoleKey.Trim()),
                Times = (c.Times ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.From) || !string.IsNullOrWhiteSpace(t.To))
                    .Select(t => new HelpTimeWindow { From = CleanTime(t.From), To = CleanTime(t.To) }).ToList(),
            }).ToList();

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var existing = db.FirstOrDefault<StaffHelpSignup>(
                "SELECT * FROM StaffHelpSignup WHERE " + StaffOwner.Where(0) + " AND MemberId = @1", owner.P, memberId);

            var noContent = clean.Count == 0 && string.IsNullOrWhiteSpace(comment);
            if (noContent)
            {
                if (existing != null) db.Execute("DELETE FROM StaffHelpSignup WHERE Id = @0", existing.Id);
                return;
            }

            var json = JsonConvert.SerializeObject(clean);
            var now = DateTime.UtcNow;
            if (existing != null)
            {
                existing.MemberName = memberName;
                existing.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
                existing.SlotsJson = json;
                existing.ModifiedDate = now;
                db.Update(existing);
            }
            else
            {
                db.Insert(new StaffHelpSignup
                {
                    PlanningId = owner.PlanningIdOrNull,
                    CompetitionId = owner.CompetitionIdOrNull,
                    MemberId = memberId,
                    MemberName = memberName,
                    Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
                    SlotsJson = json,
                    CreatedDate = now,
                    ModifiedDate = now,
                });
            }
        }

        // ---- manager review ----

        public List<HelpSignupReviewView> GetReview(StaffOwner owner)
        {
            var slots = GetSlots(owner).ToDictionary(s => s.Id);
            var roleNames = _roles.For(owner, _owners.Discipline(owner))
                .ToDictionary(r => r.Key, r => r.DisplayName, StringComparer.OrdinalIgnoreCase);
            List<StaffHelpSignup> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                rows = scope.Database.Fetch<StaffHelpSignup>(
                    "SELECT * FROM StaffHelpSignup WHERE " + StaffOwner.Where(0) + " ORDER BY MemberName", owner.P);

            return rows.Select(r => new HelpSignupReviewView
            {
                MemberId = r.MemberId,
                MemberName = r.MemberName,
                Comment = r.Comment,
                Updated = r.ModifiedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("sv-SE")),
                Slots = ParseChoices(r.SlotsJson).Select(c =>
                {
                    if (c.IsDayChoice)
                        return new HelpSignupReviewSlot
                        {
                            SlotId = 0,
                            Date = c.Date,
                            RoleKey = c.RoleKey,
                            Label = DayLabel(c.Date!) + " · " + (string.IsNullOrWhiteSpace(c.RoleKey) ? "var det behövs"
                                : roleNames.TryGetValue(c.RoleKey, out var rn) ? rn : c.RoleKey),
                            TimesText = string.Join(", ", (c.Times ?? new()).Select(t => $"{t.From}–{t.To}")),
                        };
                    slots.TryGetValue(c.SlotId, out var s);
                    return new HelpSignupReviewSlot
                    {
                        SlotId = c.SlotId,
                        Label = s == null ? $"(borttaget, #{c.SlotId})" : SlotLabel(s),
                        TimesText = string.Join(", ", (c.Times ?? new()).Select(t => $"{t.From}–{t.To}")),
                    };
                }).ToList(),
            }).ToList();
        }

        // ---- helpers ----

        private static HelpSlotView ToSlotView(StaffHelpSlot s) => new()
        {
            Id = s.Id,
            Date = s.SlotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            Headline = s.Headline,
            Description = s.Description,
        };

        private static string DayLabel(string date) =>
            DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d.ToString("ddd d MMM", CultureInfo.GetCultureInfo("sv-SE")) : date;

        private static string SlotLabel(HelpSlotView s)
        {
            var t = (!string.IsNullOrEmpty(s.StartTime) || !string.IsNullOrEmpty(s.EndTime)) ? $" ({s.StartTime}–{s.EndTime})" : "";
            return $"{s.Date} · {s.Headline}{t}";
        }

        private static List<HelpSlotChoice> ParseChoices(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new();
            try { return JsonConvert.DeserializeObject<List<HelpSlotChoice>>(json) ?? new(); }
            catch { return new(); }
        }

        private static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var d)) return d.Date;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.Date;
            return null;
        }

        private static string? CleanTime(string? s)
        {
            s = s?.Trim();
            return string.IsNullOrEmpty(s) ? null : (s.Length > 5 ? s.Substring(0, 5) : s);
        }
    }
}
