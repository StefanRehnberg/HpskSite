using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>A single shooting-range compliance reminder on the home hub (permit/document expiry).</summary>
    public class RangeReminderItem
    {
        public string RangeName { get; set; } = "";
        public string Label { get; set; } = "";   // "Tillstånd" / "Besiktning" / ...
        public DateTime Due { get; set; }
        public bool Overdue { get; set; }
        public string Url { get; set; } = "";      // /skjutbanor?range={id}
    }

    public class RangeHubSummary
    {
        public List<RangeReminderItem> Reminders { get; set; } = new();
        public bool HasAny => Reminders.Count > 0;
    }

    /// <summary>
    /// Hub reminders for shooting-range compliance: permits/documents that are overdue or expire within
    /// ~90 days, for ranges the member is responsible for (claimed by a club they administer, or where
    /// they're a steward). Only surfaced when there's something due. Indexed SQL; exception-safe.
    /// </summary>
    public class RangeHubService
    {
        private readonly ShootingRangeService _ranges;
        private readonly AdminAuthorizationService _auth;
        private const int WarningDays = 90;

        public RangeHubService(ShootingRangeService ranges, AdminAuthorizationService auth)
        {
            _ranges = ranges;
            _auth = auth;
        }

        public async Task<RangeHubSummary> GetSummaryAsync(int memberId)
        {
            var s = new RangeHubSummary();
            if (memberId <= 0) return s;

            try
            {
                var map = new Dictionary<int, ShootingRange>();

                // ranges claimed by the clubs the member administers
                List<int> clubIds;
                try { clubIds = await _auth.GetManagedClubIds(); } catch { clubIds = new List<int>(); }
                foreach (var clubId in clubIds)
                    foreach (var r in await _ranges.GetRangesForClubAsync(clubId))
                        map[r.Id] = r;

                // ranges where the member is a steward (may not be a club admin)
                var stewardIds = await _ranges.GetStewardedRangeIdsAsync(memberId);
                var missing = stewardIds.Where(id => !map.ContainsKey(id)).ToList();
                if (missing.Count > 0)
                    foreach (var r in await _ranges.GetByIdsAsync(missing))
                        map[r.Id] = r;

                if (map.Count == 0) return s;

                var today = DateTime.Today;
                var horizon = today.AddDays(WarningDays);

                foreach (var r in map.Values)
                {
                    foreach (var p in await _ranges.GetPermitsAsync(r.Id))
                        if (p.ExpiryDate.HasValue && p.ExpiryDate.Value.Date <= horizon)
                            s.Reminders.Add(new RangeReminderItem
                            {
                                RangeName = r.Name,
                                Label = "Tillstånd",
                                Due = p.ExpiryDate.Value,
                                Overdue = p.ExpiryDate.Value.Date < today,
                                Url = $"/skjutbanor?range={r.Id}"
                            });

                    foreach (var d in await _ranges.GetDocumentsAsync(r.Id))
                        if (d.ValidUntil.HasValue && d.ValidUntil.Value.Date <= horizon)
                            s.Reminders.Add(new RangeReminderItem
                            {
                                RangeName = r.Name,
                                Label = DocLabel(d.DocType),
                                Due = d.ValidUntil.Value,
                                Overdue = d.ValidUntil.Value.Date < today,
                                Url = $"/skjutbanor?range={r.Id}"
                            });
                }

                s.Reminders = s.Reminders.OrderBy(x => x.Due).Take(6).ToList();
            }
            catch { }

            return s;
        }

        // DocType is a free-string constant; map by keyword so we're robust to the exact stored value.
        private static string DocLabel(string? docType)
        {
            if (string.IsNullOrWhiteSpace(docType)) return "Dokument";
            var t = docType.ToLowerInvariant();
            if (t.Contains("polis") || t.Contains("police") || t.Contains("tillst")) return "Polistillstånd";
            if (t.Contains("besikt")) return "Besiktning";
            if (t.Contains("env") || t.Contains("milj")) return "Miljötillstånd";
            if (t.Contains("buller")) return "Bullerutredning";
            if (t.Contains("mark")) return "Markundersökning";
            if (t.Contains("insur") || t.Contains("försäk") || t.Contains("forsak")) return "Försäkring";
            if (t.Contains("skotsel") || t.Contains("skötsel")) return "Skötselplan";
            return "Dokument";
        }
    }
}
