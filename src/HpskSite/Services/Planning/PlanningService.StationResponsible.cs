using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Stefan 2026-10-25: vem som är ANSVARIG för att utforma en station har en egen plats
    /// (<see cref="PlanningStationResponsibleRow"/>), EN rad per station på stationernas planering (<see cref="StationOwnerId"/>).
    /// Förut skapade "Ge stationer en ansvarig" ett område med den ansvariges namn — ett område är en del av tävlingens
    /// organisation, inte en uppgift en person har för några stationer. Konfiguratorn läser samma rader för att avgöra vem som
    /// får ändra vilken station (<c>FaltskytteConfigurationService.StationRightsAsync</c>).
    /// </summary>
    public partial class PlanningService
    {
        /// <summary>Vem som blir ansvarig: sort, personen i planeringen (den ansvarige eller kontakten), klubbens/kretsens id och namnet.</summary>
        public sealed record StationWho(string Kind, int? ParticipantId, int? HostId, string Name);

        /// <summary>De ansvariga på stationernas planering, en rad per station.</summary>
        public List<PlanningStationResponsibleRow> StationResponsibles(int planningId)
        {
            var owner = StationOwnerId(planningId);
            if (owner == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningStationResponsibleRow>("SELECT * FROM PlanningStationResponsible WHERE PlanningId = @0 ORDER BY Station", owner);
        }

        /// <summary>
        /// Gör <paramref name="who"/> ansvarig för <paramref name="stations"/>. En station som någon annan var ansvarig för
        /// byter ansvarig. <paramref name="release"/> = stationer samma ansvarige hade och inte längre ska ha (dialogen "Välj
        /// stationer…" skickar de förra) — de blir utan ansvarig, men bara om de fortfarande är hens. Bara på stationernas
        /// planering (anroparen har prövat <c>WholeStationsElsewhere</c>). <paramref name="maxStation"/> = stationernas antal.
        /// </summary>
        public string? SetStationResponsible(int planningId, IReadOnlyCollection<int> stations, IReadOnlyCollection<int> release,
            StationWho who, int maxStation, int byParticipantId)
        {
            var wanted = stations.Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
            if (wanted.Count == 0 && release.Count == 0) return "Välj minst en station.";
            if (wanted.Count > 0 && wanted.Max() > PlanningStations.Max) return $"Högst {PlanningStations.Max} stationer.";
            if (wanted.Count > 0 && maxStation > 0 && wanted.Max() > maxStation)
                return $"Tävlingen har {maxStation} stationer. Ändra antalet stationer först.";
            var name = Clean(who.Name, 200);
            if (name == null) return "Ange vem som är ansvarig.";
            if (who.Kind is not (PlanningStationResponsibleRow.KindPerson or PlanningStationResponsibleRow.KindClub
                    or PlanningStationResponsibleRow.KindRegion or PlanningStationResponsibleRow.KindName))
                return "Okänd sorts ansvarig.";
            if (who.Kind == PlanningStationResponsibleRow.KindPerson && !(who.ParticipantId > 0)) return "Välj vem i planeringen som är ansvarig.";
            if (who.Kind is PlanningStationResponsibleRow.KindClub or PlanningStationResponsibleRow.KindRegion && !(who.HostId > 0))
                return "Välj klubben eller kretsen.";
            var owner = StationOwnerId(planningId);
            if (owner != planningId) return "Stationerna sköts i en annan planering i familjen.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (who.ParticipantId > 0 && db.ExecuteScalar<int>("SELECT COUNT(*) FROM PlanningMember WHERE Id = @0 AND PlanningId = @1 AND RemovedAt IS NULL",
                    who.ParticipantId, planningId) == 0)
                return "Den ansvarige finns inte i planeringen.";
            var now = Now;
            // Under lås: två som ändrar samtidigt får inte båda tro att stationen var deras att ge bort.
            var current = db.Fetch<PlanningStationResponsibleRow>("SELECT * FROM PlanningStationResponsible WITH (UPDLOCK, HOLDLOCK) WHERE PlanningId = @0", planningId)
                .ToDictionary(r => r.Station);
            var key = new PlanningStationResponsibleRow { Kind = who.Kind, ParticipantId = who.ParticipantId, HostId = who.HostId, Name = name }.Key;
            foreach (var n in release.Where(n => !wanted.Contains(n)))
                if (current.TryGetValue(n, out var r) && r.Key == key) db.Delete(r);
            foreach (var n in wanted)
            {
                if (current.TryGetValue(n, out var r))
                {
                    r.Kind = who.Kind; r.ParticipantId = who.ParticipantId > 0 ? who.ParticipantId : null; r.HostId = who.HostId > 0 ? who.HostId : null;
                    r.Name = name; r.AssignedAt = now; r.AssignedBy = byParticipantId;
                    db.Update(r);
                }
                else db.Insert(new PlanningStationResponsibleRow
                {
                    PlanningId = planningId, Station = n, Kind = who.Kind, ParticipantId = who.ParticipantId > 0 ? who.ParticipantId : null,
                    HostId = who.HostId > 0 ? who.HostId : null, Name = name, AssignedAt = now, AssignedBy = byParticipantId,
                });
            }
            Bump(db, planningId);
            return null;
        }

        /// <summary>Stationerna blir utan ansvarig.</summary>
        public string? ClearStationResponsible(int planningId, IReadOnlyCollection<int> stations)
        {
            if (stations.Count == 0) return "Välj minst en station.";
            if (StationOwnerId(planningId) != planningId) return "Stationerna sköts i en annan planering i familjen.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var list = stations.Where(n => n > 0).Distinct().ToList();
            if (list.Count == 0) return "Välj minst en station.";
            db.Execute($"DELETE FROM PlanningStationResponsible WHERE PlanningId = @0 AND Station IN ({string.Join(",", list)})", planningId);
            Bump(db, planningId);
            return null;
        }
    }
}
