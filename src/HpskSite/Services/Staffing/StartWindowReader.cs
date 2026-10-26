using System.Globalization;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using HpskSite.Models.Staffing;
using HpskSite.Models.StartLists;
using HpskSite.Services.Competitions;
using HpskSite.Services.StartLists;
using Newtonsoft.Json;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    public enum StartWindowSource { Faltskytte, Precision }

    /// <summary>
    /// En skyttes start: vem, när, vilken klass och var. <see cref="End"/> är det KÄNDA slutet (precisionens skjutlag har ett); fält har
    /// bara en starttid, och då räknar <see cref="ShootTime"/> ut fönstret. <see cref="Published"/> = startlistan är publicerad.
    /// </summary>
    public sealed record StartWindow(int MemberId, string MemberName, DateTime Start, DateTime? End, string Title, string Where,
        string SourceKey, string? ClassKey, bool Published, StartWindowSource Source);

    /// <summary>
    /// Planera dagen steg 1 (Expertgruppen K2): <b>alla starter i en tävling med EN läsning</b> — en fråga för fältets patruller, en
    /// läsning av precisionens skjutlag — i stället för en schemaläsning per medlem. Veckan, krockarna, kandidatlistan och förslagen
    /// läser starterna här; ingen av dem har en egen väg in i startlistorna.
    /// <list type="bullet">
    /// <item><b>Publicerad eller inte:</b> medlemmens egna ytor (Mitt schema) läser bara publicerat — grinden är oförändrad.
    /// Planeraren (<c>includeUnpublished</c>) läser även en startlista som ännu inte publicerats, så att bemanningen kan byggas medan
    /// startlistan görs (förut var planeringen blind innan publiceringen).</item>
    /// <item><b>Fält</b> (fält och magnumfält): <c>FaltskyttePatrol</c> + <c>FaltskyttePatrolMember</c>; en skytt anmäld som ej startande (DNS) räknas inte.</item>
    /// <item><b>Precision, milsnabb m.fl.:</b> skjutlagen (<c>StartListConfiguration.Teams</c>) i <c>IStartListStore</c>. Finns det en officiell
    /// lista läses den; annars, för planeraren, den senast genererade. Springskytte (individuella listor och stafett) läses inte —
    /// den grenen ingår inte i verktyget.</item>
    /// </list>
    /// </summary>
    public class StartWindowReader
    {
        private readonly IScopeProvider _scopes;
        private readonly IStartListStore _startLists;
        private readonly ICompetitionStore _competitions;
        private readonly ILogger<StartWindowReader> _logger;

        public StartWindowReader(IScopeProvider scopes, IStartListStore startLists, ICompetitionStore competitions, ILogger<StartWindowReader> logger)
        {
            _scopes = scopes; _startLists = startLists; _competitions = competitions; _logger = logger;
        }

        /// <summary>Läser verktyget starterna i den här tävlingens gren? Fält- och precisionsfamiljerna; springskytte och övrigt: nej.</summary>
        public bool Covers(int competitionId)
        {
            var type = _competitions.GetSaved(competitionId)?.CompetitionType ?? "";
            return FunctionaryRoles.FaltFamily.Contains(type, StringComparer.OrdinalIgnoreCase)
                || FunctionaryRoles.PrecisionFamily.Contains(type, StringComparer.OrdinalIgnoreCase);
        }

        public List<StartWindow> ForCompetition(int competitionId, bool includeUnpublished)
        {
            if (competitionId <= 0) return new();
            var comp = _competitions.GetSaved(competitionId);
            if (comp == null) return new();
            try
            {
                var type = comp.CompetitionType ?? "";
                if (FunctionaryRoles.FaltFamily.Contains(type, StringComparer.OrdinalIgnoreCase)) return Falt(competitionId, comp.FaltskyttePatrolsPublished, includeUnpublished);
                if (FunctionaryRoles.PrecisionFamily.Contains(type, StringComparer.OrdinalIgnoreCase)) return Precision(competitionId, comp, includeUnpublished);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Starterna i tävling {Comp} gick inte att läsa.", competitionId); }
            return new();
        }

        // ---------------------------------------------------------------- fält

        private sealed class PatrolRow
        {
            public int PatrolId { get; set; }
            public int PatrolNumber { get; set; }
            public DateTime StartTime { get; set; }
            public string? Label { get; set; }
            public int MemberId { get; set; }
            public int Position { get; set; }
            public string ShootingClass { get; set; } = "";
            public string MemberName { get; set; } = "";
            public string? Status { get; set; }
        }

        /// <summary>
        /// Får startlistan läsas? Publicerad: alla. Opublicerad: bara planeraren (<c>includeUnpublished</c>). Medlemmens egna ytor (Mitt schema)
        /// ber aldrig om det opublicerade — en startlista som inte släppts ska inte synas i någons schema.
        /// </summary>
        internal static bool MayRead(bool published, bool includeUnpublished) => published || includeUnpublished;

        private List<StartWindow> Falt(int competitionId, bool published, bool includeUnpublished)
        {
            if (!MayRead(published, includeUnpublished)) return new();
            List<PatrolRow> rows;
            using (var s = _scopes.CreateScope(autoComplete: true))
                rows = s.Database.Fetch<PatrolRow>(@"
SELECT p.Id AS PatrolId, p.PatrolNumber, p.StartTime, p.Label, m.MemberId, m.Position, m.ShootingClass, m.MemberName, m.Status
FROM FaltskyttePatrolMember m JOIN FaltskyttePatrol p ON p.Id = m.PatrolId
WHERE p.CompetitionId = @0 AND p.StartTime IS NOT NULL AND m.MemberId > 0", competitionId);
            return rows.Where(r => !string.Equals(r.Status, "DNS", StringComparison.OrdinalIgnoreCase))
                .Select(r =>
                {
                    var where = $"Patrull {r.PatrolNumber}";
                    if (!string.IsNullOrWhiteSpace(r.Label)) where += $" ({r.Label})";
                    if (r.Position > 0) where += $" · nr {r.Position}";
                    return new StartWindow(r.MemberId, r.MemberName, r.StartTime, null, $"Klass {ShootingClasses.DisplayName(r.ShootingClass)}", where,
                        $"patrull-{r.PatrolId}-{r.ShootingClass}", r.ShootingClass, published, StartWindowSource.Faltskytte);
                })
                .OrderBy(w => w.Start).ThenBy(w => w.MemberName, StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true)).ToList();
        }

        // ---------------------------------------------------------------- precision

        private List<StartWindow> Precision(int competitionId, HpskSite.Models.Competitions.CompetitionInfo comp, bool includeUnpublished)
        {
            var lists = (includeUnpublished ? _startLists.List(competitionId) : _startLists.ListPublished(competitionId))
                .Where(r => !r.LegacyHub || r.Kind == StartListKind.Qualifying).ToList();
            var chosen = lists.Where(r => r.IsOfficial).ToList();
            if (chosen.Count == 0 && includeUnpublished && _startLists.Primary(competitionId) is { } draft) chosen.Add(draft);

            var singleDay = comp.CompetitionDate?.Date;
            if (comp.CompetitionEndDate?.Date > singleDay) singleDay = null;   // flera dagar: ett skjutlag utan datum har ingen dag
            var result = new List<StartWindow>();
            foreach (var node in chosen)
            {
                var cfg = Parse(node.ConfigurationData);
                if (cfg?.Teams == null) continue;
                var finals = node.Kind == StartListKind.Finals;
                foreach (var team in cfg.Teams)
                {
                    var date = ParseDate(team.Date) ?? singleDay;
                    var from = ParseTime(team.StartTime);
                    if (date == null || from == null || team.Shooters == null) continue;
                    var start = date.Value.Date + from.Value;
                    var to = ParseTime(team.EndTime);
                    DateTime? end = to != null && date.Value.Date + to.Value > start ? date.Value.Date + to.Value : null;
                    foreach (var sh in team.Shooters.Where(x => x.MemberId > 0))
                    {
                        var where = $"Skjutlag {team.TeamNumber}";
                        if (!string.IsNullOrWhiteSpace(team.Label)) where += $" ({team.Label})";
                        if (sh.Position > 0) where += $" · plats {sh.Position}";
                        var cls = ShootingClasses.DisplayName(sh.ChampionshipClass ?? sh.WeaponClass);
                        result.Add(new StartWindow(sh.MemberId, sh.Name, start, end, finals ? $"Final — klass {cls}" : $"Klass {cls}", where,
                            $"lag-{node.Id}-{team.TeamNumber}-{sh.Position}", sh.WeaponClass, node.IsOfficial, StartWindowSource.Precision));
                    }
                }
            }
            return result.OrderBy(w => w.Start).ThenBy(w => w.MemberName, StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true)).ToList();
        }

        private static StartListConfiguration? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<StartListConfiguration>(json); } catch { return null; }
        }

        /// <summary>"HH:mm" eller "H:mm" → klockslag. Tomt eller fel = okänt.</summary>
        internal static TimeSpan? ParseTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return TimeSpan.TryParseExact(s.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;
        }

        internal static DateTime? ParseDate(string? s) =>
            DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
