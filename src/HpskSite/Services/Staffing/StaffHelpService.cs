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

        public StaffHelpService(IScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        // ---- slots (organiser) ----

        public List<HelpSlotView> GetSlots(int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffHelpSlot>(
                    "SELECT * FROM StaffHelpSlot WHERE CompetitionId = @0 ORDER BY SlotDate, SortOrder, StartTime, Id", competitionId)
                .Select(ToSlotView).ToList();
        }

        public int SaveSlot(SaveHelpSlotRequest req, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            StaffHelpSlot row;
            if (req.Id > 0)
                row = db.SingleOrDefault<StaffHelpSlot>("SELECT * FROM StaffHelpSlot WHERE Id = @0", req.Id)
                      ?? new StaffHelpSlot { CompetitionId = req.CompetitionId, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            else
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM StaffHelpSlot WHERE CompetitionId = @0", req.CompetitionId) ?? 0;
                row = new StaffHelpSlot { CompetitionId = req.CompetitionId, SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            row.CompetitionId = req.CompetitionId;
            row.SlotDate = ParseDate(req.Date) ?? row.SlotDate;
            row.StartTime = CleanTime(req.StartTime);
            row.EndTime = CleanTime(req.EndTime);
            row.Headline = (req.Headline ?? "").Trim();
            row.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
            if (row.Id > 0) db.Update(row); else row.Id = Convert.ToInt32(db.Insert(row));
            return row.Id;
        }

        public void DeleteSlot(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("DELETE FROM StaffHelpSlot WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
        }

        // ---- member sign-up ----

        public MyHelpSignupView? GetMySignup(int competitionId, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.SingleOrDefault<StaffHelpSignup>(
                "SELECT * FROM StaffHelpSignup WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId);
            if (row == null) return null;
            return new MyHelpSignupView { Comment = row.Comment, Slots = ParseChoices(row.SlotsJson) };
        }

        /// <summary>Upsert a member's sign-up. Empty comment + no slots clears it (member un-signs).</summary>
        public void SaveMySignup(int competitionId, int memberId, string memberName, string? comment, List<HelpSlotChoice> slots)
        {
            // Keep only choices for slots that still exist in this competition, and drop empty time windows.
            var validSlotIds = GetSlots(competitionId).Select(s => s.Id).ToHashSet();
            var clean = (slots ?? new()).Where(c => validSlotIds.Contains(c.SlotId)).Select(c => new HelpSlotChoice
            {
                SlotId = c.SlotId,
                Times = (c.Times ?? new()).Where(t => !string.IsNullOrWhiteSpace(t.From) || !string.IsNullOrWhiteSpace(t.To))
                    .Select(t => new HelpTimeWindow { From = CleanTime(t.From), To = CleanTime(t.To) }).ToList(),
            }).ToList();

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var existing = db.SingleOrDefault<StaffHelpSignup>(
                "SELECT * FROM StaffHelpSignup WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId);

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
                    CompetitionId = competitionId,
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

        public List<HelpSignupReviewView> GetReview(int competitionId)
        {
            var slots = GetSlots(competitionId).ToDictionary(s => s.Id);
            List<StaffHelpSignup> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                rows = scope.Database.Fetch<StaffHelpSignup>(
                    "SELECT * FROM StaffHelpSignup WHERE CompetitionId = @0 ORDER BY MemberName", competitionId);

            return rows.Select(r => new HelpSignupReviewView
            {
                MemberId = r.MemberId,
                MemberName = r.MemberName,
                Comment = r.Comment,
                Updated = r.ModifiedDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.GetCultureInfo("sv-SE")),
                Slots = ParseChoices(r.SlotsJson).Select(c =>
                {
                    slots.TryGetValue(c.SlotId, out var s);
                    return new HelpSignupReviewSlot
                    {
                        SlotId = c.SlotId,
                        Label = s == null ? $"(borttaget pass #{c.SlotId})" : SlotLabel(s),
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
