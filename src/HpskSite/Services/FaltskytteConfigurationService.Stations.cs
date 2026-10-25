using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Models.Planning;
using Newtonsoft.Json.Linq;

namespace HpskSite.Services
{
    /// <summary>
    /// Stefan 2026-10-25: en planerings stationer är EN fältkonfiguration ("en konfiguration, ansvar per station").
    /// Varje station kan ha en ansvarig (planeringens område med stationer: en person, en klubb eller en krets); den
    /// ansvarige ändrar bara sina stationer och sparar dem en och en (<see cref="UpdateStationsAsync"/> — flera kan arbeta
    /// samtidigt utan att skriva över varandra). Banläggaren godkänner station för station, och helheten först när alla
    /// är godkända (<see cref="StationBlob.WholeBlocker"/>).
    /// </summary>
    public partial class FaltskytteConfigurationService
    {
        /// <summary>
        /// Vad medlemmen får göra med stationerna. <see cref="IsCourse"/> = konfigurationen är en planerings stationer;
        /// <see cref="All"/> = får ändra alla (kan ändra konfigurationen); <see cref="Stations"/> = de stationer medlemmen
        /// är ansvarig för; <see cref="Responsible"/> = vem som är ansvarig för varje station.
        /// </summary>
        public sealed record StationRights(bool IsCourse, bool All, HashSet<int> Stations, Dictionary<int, string> Responsible, int StationCount);

        private sealed class StationResponsibleRight
        {
            public int Station { get; set; }
            public string Name { get; set; } = "";
            public int? ResponsibleMemberId { get; set; }
        }

        /// <summary>Lägger en planerings konfiguration i planeringens projekt (planeringen har prövat behörigheten).</summary>
        public async Task SetProjectForPlanningAsync(int configId, int projectId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("UPDATE FaltskytteConfiguration SET ProjectId = @1 WHERE Id = @0 AND ProjectId IS NULL", configId, projectId);
        }

        /// <summary>Planeringen vars stationer konfigurationen är (länken utan område), eller null.</summary>
        public async Task<int?> CoursePlanningIdAsync(int configId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return await db.FirstOrDefaultAsync<int?>(
                    "SELECT TOP 1 l.PlanningId FROM PlanningLink l JOIN Planning p ON p.Id = l.PlanningId AND p.DeletedAt IS NULL WHERE l.TargetKind = N'faltconfig' AND l.TargetId = @0 AND l.AreaId IS NULL", configId);
            }
            catch { return null; }
        }

        public async Task<StationRights> StationRightsAsync(FaltskytteConfiguration config, int? memberId)
        {
            var all = await CanEditAsync(config, memberId);
            var count = StationCountOf(config.JsonBlob);
            var planningId = await CoursePlanningIdAsync(config.Id);
            if (planningId == null) return new StationRights(false, all, new HashSet<int>(), new Dictionary<int, string>(), count);
            var mine = new HashSet<int>();
            var who = new Dictionary<int, string>();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                // Stefan 2026-10-25: den ansvarige per station (PlanningStationResponsible) — den som får ändra stationen är
                // personen i planeringen (den ansvarige själv, eller kontakten för en klubb/krets/ett namn). ⚠️ Frågan börjar
                // med SELECT (NPoco). Familjen = huvudplaneringen och dess delplaneringar (raderna ligger på stationernas planering).
                var rows = await db.FetchAsync<StationResponsibleRight>(@"
                    SELECT r.Station, r.Name, pm.MemberId AS ResponsibleMemberId
                      FROM PlanningStationResponsible r
                      JOIN Planning f ON f.Id = r.PlanningId AND f.DeletedAt IS NULL
                      JOIN Planning p ON p.Id = @0
                      LEFT JOIN PlanningMember pm ON pm.Id = r.ParticipantId AND pm.RemovedAt IS NULL
                     WHERE f.Id = COALESCE(p.ParentPlanningId, p.Id) OR f.ParentPlanningId = COALESCE(p.ParentPlanningId, p.Id)", planningId.Value);
                foreach (var r in rows)
                {
                    who.TryAdd(r.Station, r.Name);
                    if (memberId > 0 && r.ResponsibleMemberId == memberId) mine.Add(r.Station);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Fältkonfiguration {Id}: stationernas ansvariga kunde inte läsas.", config.Id); }
            return new StationRights(true, all, mine, who, count);
        }

        private static int StationCountOf(string? blob)
        {
            try
            {
                var o = string.IsNullOrWhiteSpace(blob) ? new JObject() : JObject.Parse(blob);
                var max = StationBlob.Checksums(blob).Keys.DefaultIfEmpty(0).Max();
                return Math.Max(o.Value<int?>(CourseAssembly.StationCountKey) ?? 0, max);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Sparar BARA <paramref name="stations"/> ur <paramref name="jsonBlob"/>. Den ansvarige får spara sina stationer, den
        /// som kan ändra konfigurationen alla. <paramref name="expected"/>: stationens kontrollsumma när fliken läste den —
        /// har någon annan ändrat stationen sedan dess vägras sparningen (stationen för stationen, inte hela banan).
        /// En godkänd station som ändras blir ett utkast igen.
        /// </summary>
        public async Task<(bool Success, string? Message, Dictionary<int, string>? Checksums)> UpdateStationsAsync(
            int configId, int memberId, IReadOnlyCollection<int> stations, string jsonBlob, IDictionary<int, string>? expected)
        {
            using var db = _databaseFactory.CreateDatabase();
            for (var attempt = 0; ; attempt++)
            {
            var config = await db.SingleOrDefaultAsync<FaltskytteConfiguration>("WHERE Id = @0", configId);
            if (config == null) return (false, "Konfigurationen hittades inte.", null);
            if (stations.Count == 0) return (true, null, StationBlob.Checksums(config.JsonBlob));
            if (IsApproved(config)) return (false, "Stationerna är godkända som helhet och kan inte ändras. Ta tillbaka godkännandet först.", null);
            var rights = await StationRightsAsync(config, memberId);
            var notMine = stations.Where(n => !rights.All && !rights.Stations.Contains(n)).ToList();
            if (notMine.Count > 0)
                return (false, $"Du är inte ansvarig för station {string.Join(", ", notMine)}" +
                    (rights.Responsible.TryGetValue(notMine[0], out var w) ? $" — {w} är det." : "."), null);
            foreach (var n in stations)
                if (expected != null && expected.TryGetValue(n, out var exp) && exp != StationBlob.Checksum(config.JsonBlob, n))
                    return (false, $"Station {n} har ändrats av någon annan sedan du öppnade den. Ladda om sidan — dina ändringar på station {n} är inte sparade.", null);
            var merged = StationBlob.Merge(config.JsonBlob, jsonBlob, stations);
            var tooFar = TooFarMessage(merged);
            if (tooFar != null) return (false, tooFar, null);
            // Godkännandet: en godkänd eller väntande station vars innehåll ändrats är ett utkast igen.
            var reopen = new Dictionary<int, StationApproval?>();
            foreach (var n in StationBlob.Changed(config.JsonBlob, merged))
                if (StationBlob.Approvals(config.JsonBlob).GetValueOrDefault(n) is { } a && a.Status != StationBlob.Returned) reopen[n] = null;
            if (reopen.Count > 0) merged = StationBlob.WithApprovals(merged, reopen);
            if (await SwapBlobAsync(db, config, merged)) return (true, null, StationBlob.Checksums(merged));
            // Någon annan sparade (en annan station) mellan läsningen och skrivningen: läs om och slå ihop igen.
            if (attempt >= 4) return (false, "Stationerna sparades av flera samtidigt. Försök igen.", null);
            }
        }

        /// <summary>
        /// Skriver blobben bara om den i databasen är den som lästes (jämförs byte för byte — kolumnens sortering skiljer inte
        /// på stora och små bokstäver). Två som sparar var sin station samtidigt skrev annars över varandras ändring
        /// (stationssviten 2026-10-25: station 3 försvann när station 1 sparades i samma ögonblick).
        /// </summary>
        private static async Task<bool> SwapBlobAsync(NPoco.IDatabase db, FaltskytteConfiguration config, string blob)
        {
            var n = await db.ExecuteAsync(@"UPDATE FaltskytteConfiguration SET JsonBlob = @1, ModifiedDate = @2
                 WHERE Id = @0 AND ((JsonBlob IS NULL AND @3 IS NULL) OR CAST(JsonBlob AS varbinary(max)) = CAST(@3 AS varbinary(max)))",
                config.Id, blob, DateTime.Now, config.JsonBlob);
            return n == 1;
        }

        /// <summary>
        /// Godkännandet av EN station. <paramref name="action"/>: <c>request</c> (den ansvarige: klar, be om godkännande),
        /// <c>approve</c> och <c>return</c> (en banläggare som kan ändra konfigurationen), <c>reopen</c> (ta tillbaka).
        /// </summary>
        public async Task<(bool Success, string? Message)> SetStationApprovalAsync(int configId, int memberId, int station, string action, string? comment)
        {
            using var db = _databaseFactory.CreateDatabase();
            for (var attempt = 0; ; attempt++)
            {
            var config = await db.SingleOrDefaultAsync<FaltskytteConfiguration>("WHERE Id = @0", configId);
            if (config == null) return (false, "Konfigurationen hittades inte.");
            if (IsApproved(config)) return (false, "Stationerna är godkända som helhet. Ta tillbaka godkännandet först.");
            var rights = await StationRightsAsync(config, memberId);
            if (!rights.IsCourse) return (false, "Godkännande per station finns bara för en planerings stationer.");
            var sum = StationBlob.Checksum(config.JsonBlob, station);
            if (sum.Length == 0) return (false, $"Station {station} har inget innehåll än.");
            var isBanlaggare = await _certificationService.HasActiveCertAsync(memberId, CertificationTypes.Banlaggare);
            var canJudge = isBanlaggare && (rights.All || await _authService.IsCurrentUserAdminAsync());
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            StationApproval? next;
            switch (action)
            {
                case "request":
                    if (!rights.All && !rights.Stations.Contains(station)) return (false, $"Du är inte ansvarig för station {station}.");
                    next = new StationApproval(StationBlob.Pending, memberId, now, null, sum);
                    break;
                case "approve":
                    if (!canJudge) return (false, "Det är en banläggare som kan ändra stationerna som godkänner dem.");
                    next = new StationApproval(StationBlob.Approved, memberId, now, null, sum);
                    break;
                case "return":
                    if (!canJudge) return (false, "Det är en banläggare som kan ändra stationerna som skickar tillbaka en station.");
                    if (string.IsNullOrWhiteSpace(comment)) return (false, "Skriv vad som behöver ändras.");
                    next = new StationApproval(StationBlob.Returned, memberId, now, comment.Trim().Length > 1000 ? comment.Trim()[..1000] : comment.Trim(), null);
                    break;
                case "reopen":
                    if (!canJudge && !rights.All) return (false, "Det är banläggaren som tar tillbaka ett godkännande.");
                    next = null;
                    break;
                default: return (false, "Okänd åtgärd.");
            }
            if (await SwapBlobAsync(db, config, StationBlob.WithApprovals(config.JsonBlob, new Dictionary<int, StationApproval?> { [station] = next })))
                return (true, null);
            if (attempt >= 4) return (false, "Stationerna sparades av flera samtidigt. Försök igen.");
            }
        }

        /// <summary>
        /// En hel sparning (den gamla vägen, för den som kan ändra allt) av en planerings stationer: godkännandena är serverns
        /// (fliken kan ha en gammal kopia), och en godkänd station som ändrats blir ett utkast igen.
        /// </summary>
        private static string KeepServerApprovals(string? serverBlob, string incoming)
        {
            JObject inc;
            try { inc = JObject.Parse(incoming); } catch { return incoming; }
            inc.Remove(StationBlob.ApprovalKey);
            var withServer = StationBlob.WithApprovals(inc.ToString(Newtonsoft.Json.Formatting.None),
                StationBlob.Approvals(serverBlob).ToDictionary(kv => kv.Key, kv => (StationApproval?)kv.Value));
            var reopen = StationBlob.Changed(serverBlob, withServer)
                .Where(n => StationBlob.Approvals(serverBlob).GetValueOrDefault(n) is { } a && a.Status != StationBlob.Returned)
                .ToDictionary(n => n, n => (StationApproval?)null);
            return reopen.Count == 0 ? withServer : StationBlob.WithApprovals(withServer, reopen);
        }
    }
}
