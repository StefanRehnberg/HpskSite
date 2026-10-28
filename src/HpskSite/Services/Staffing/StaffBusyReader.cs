using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Planera dagen: <b>var finns personens egna starter?</b> — på ETT ställe. Förslag, kandidatlista och veckan läste förut bara tävlingen planeringen är kopplad till;
    /// i en familj (SM-veckan: precision på måndagen, fält på tisdagen, en planering med delar) missade de skyttens start i systertävlingen, så en funktionär kunde
    /// föreslås på en station medan hen själv skulle skjuta i den andra tävlingen. Här läses starterna över ALLA tävlingar som familjens planeringar bemannar,
    /// och en tävling utan planering läser bara sig själv.
    /// </summary>
    public class StaffBusyReader
    {
        private readonly PlanningService _planning;
        private readonly StartWindowReader _starts;

        private readonly Umbraco.Cms.Infrastructure.Scoping.IScopeProvider _scopes;

        public StaffBusyReader(PlanningService planning, StartWindowReader starts, Umbraco.Cms.Infrastructure.Scoping.IScopeProvider scopes)
        {
            _planning = planning; _starts = starts; _scopes = scopes;
        }

        private sealed class RegRow { public int MemberId { get; set; } public string? ClassKey { get; set; } }

        /// <summary>
        /// Vilka medlemmar som är ANMÄLDA till tävlingarna bemanningen gäller, med sina klasser (visningsnamn). En anmälan är inte en start: startlistan kan saknas helt.
        /// Därför säger bemanningen "anmäld" och "har en start" var för sig (Stefan 2026-10-28: funktionärerna är anmälda men listan visade att ingen tävlar).
        /// </summary>
        public Dictionary<int, List<string>> RegistrationsByMember(StaffOwner owner)
        {
            var comps = CompetitionsOf(owner);
            var result = new Dictionary<int, List<string>>();
            if (comps.Count == 0) return result;
            List<RegRow> rows;
            using (var s = _scopes.CreateScope(autoComplete: true))
                rows = s.Database.Fetch<RegRow>($@"SELECT r.MemberId, c.ClassKey FROM CompetitionRegistration r
                    LEFT JOIN CompetitionRegistrationClass c ON c.RegistrationId = r.Id WHERE r.CompetitionId IN ({string.Join(",", comps)}) AND r.IsActive = 1 AND r.MemberId > 0");
            foreach (var g in rows.GroupBy(x => x.MemberId))
                result[g.Key] = g.Select(x => x.ClassKey).Where(c => !string.IsNullOrEmpty(c)).Distinct().Select(c => HpskSite.Models.ShootingClasses.DisplayName(c!)).ToList();
            return result;
        }

        /// <summary>Tävlingarna som ägarens bemanning gäller: den egna, och för en planering i en familj alla tävlingar som familjens delar bemannar.</summary>
        public IReadOnlyList<int> CompetitionsOf(StaffOwner owner)
        {
            var comps = new List<int>();
            if (owner.HasCompetition) comps.Add(owner.Comp);
            foreach (var row in _planning.Family(owner.PlanningId))
                if (_planning.StaffingCompetitionFor(row.Id) is int c && c > 0 && !comps.Contains(c)) comps.Add(c);
            return comps;
        }

        /// <summary>Alla starter per medlem över <see cref="CompetitionsOf"/>. Opublicerade ingår för planeraren.</summary>
        public Dictionary<int, List<StartWindow>> StartsByMember(StaffOwner owner, bool includeUnpublished = true) =>
            CompetitionsOf(owner).SelectMany(c => _starts.ForCompetition(c, includeUnpublished)).GroupBy(w => w.MemberId).ToDictionary(g => g.Key, g => g.ToList());

        /// <summary>
        /// Skjuttiden per dag (nyckel "yyyy-MM-dd"): från första start till sista slut över alla starter i tävlingarna. Skiftschemat mäter hålen mot den.
        /// Slutet är det kända slutet, annars starten + planeringens skjuttid. En dag utan startlista saknas — då finns ingen skjuttid att mäta mot. En start som går över midnatt räknas till 23:59.
        /// </summary>
        public Dictionary<string, (TimeSpan From, TimeSpan To)> DayWindows(StaffOwner owner, ShootTime shoot)
        {
            var result = new Dictionary<string, (TimeSpan From, TimeSpan To)>();
            foreach (var w in StartsByMember(owner).Values.SelectMany(x => x))
            {
                var key = w.Start.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                var from = w.Start.TimeOfDay;
                var endAt = w.End is DateTime e && e > w.Start ? e : w.Start + shoot.Duration;   // fältets patruller har inget känt slut: starten + skjuttiden
                var to = endAt.Date > w.Start.Date ? new TimeSpan(23, 59, 0) : endAt.TimeOfDay;
                if (to <= from) continue;
                result[key] = result.TryGetValue(key, out var cur) ? (from < cur.From ? from : cur.From, to > cur.To ? to : cur.To) : (from, to);
            }
            return result;
        }
    }
}
