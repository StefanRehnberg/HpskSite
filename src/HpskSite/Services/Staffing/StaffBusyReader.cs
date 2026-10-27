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

        public StaffBusyReader(PlanningService planning, StartWindowReader starts)
        {
            _planning = planning; _starts = starts;
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
    }
}
