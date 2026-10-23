using HpskSite.Models.Planning;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// P2b: tar fram bemanningens ägare (<see cref="StaffOwner"/>) ur en tävling eller en planering, och det
    /// ägaren behöver veta om sig själv — gren, namn och dagar. Den ENDA platsen som avgör det; varken
    /// tjänsterna eller vyerna slår upp planeringens koppling själva.
    /// <para>Scoped: svaren minns inom en begäran (samma tävling slås upp många gånger per sida).</para>
    /// </summary>
    public class StaffOwnerResolver
    {
        private readonly PlanningService _planning;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly Dictionary<int, StaffOwner> _byComp = new();
        private readonly Dictionary<int, StaffOwner?> _byPlanning = new();
        private readonly Dictionary<int, PlanningRow?> _planningRows = new();

        public StaffOwnerResolver(PlanningService planning, HpskSite.Services.Competitions.ICompetitionStore competitions)
        {
            _planning = planning;
            _competitions = competitions;
        }

        /// <summary>Tävlingens bemanning: dess planering om den har en, annars tävlingen ensam.</summary>
        public StaffOwner ForCompetition(int competitionId)
        {
            if (_byComp.TryGetValue(competitionId, out var cached)) return cached;
            var p = competitionId > 0 ? _planning.ForCompetition(competitionId) : null;
            var owner = p != null ? new StaffOwner(p.Id, competitionId) : StaffOwner.ForCompetitionOnly(competitionId);
            _byComp[competitionId] = owner;
            return owner;
        }

        /// <summary>Planeringens bemanning, med den tävling planeringen är kopplad till. Null = finns inte.</summary>
        public StaffOwner? ForPlanning(int planningId)
        {
            if (_byPlanning.TryGetValue(planningId, out var cached)) return cached;
            var p = Planning(planningId);
            StaffOwner? owner = null;
            if (p != null)
            {
                var link = _planning.LinkFor(planningId);
                owner = new StaffOwner(planningId, link?.TargetId > 0 ? link.TargetId : null);
            }
            _byPlanning[planningId] = owner;
            return owner;
        }

        /// <summary>Ägaren till en befintlig rad. Planeringen avgör när raden har en.</summary>
        public StaffOwner? OfRow(int? planningId, int? competitionId)
        {
            if (planningId is > 0) return ForPlanning(planningId.Value);
            return competitionId is > 0 ? ForCompetition(competitionId.Value) : null;
        }

        public PlanningRow? Planning(int planningId)
        {
            if (planningId <= 0) return null;
            if (_planningRows.TryGetValue(planningId, out var row)) return row;
            row = _planning.Get(planningId);
            _planningRows[planningId] = row;
            return row;
        }

        /// <summary>Grenen: tävlingens när planeringen är kopplad, annars planeringens egen.</summary>
        public string Discipline(StaffOwner owner)
        {
            if (owner.HasCompetition)
            {
                try { return _competitions.Get(owner.Comp)?.CompetitionType ?? _competitions.GetSaved(owner.Comp)?.CompetitionType ?? ""; }
                catch { return ""; }
            }
            return Planning(owner.PlanningId)?.DisciplineKey ?? "";
        }

        /// <summary>Namnet: tävlingens, annars planeringens.</summary>
        public string Name(StaffOwner owner)
        {
            if (owner.HasCompetition)
            {
                try
                {
                    var n = _competitions.Get(owner.Comp)?.CompetitionName ?? _competitions.GetSaved(owner.Comp)?.CompetitionName;
                    if (!string.IsNullOrWhiteSpace(n)) return n!;
                }
                catch { }
            }
            return Planning(owner.PlanningId)?.Name ?? "tävlingen";
        }

        /// <summary>
        /// Dagarna bemanningen seedas ur: tävlingens första och sista dag när planeringen är kopplad,
        /// annars planeringens mål i tid (Stefans svar 2026-10-21).
        /// </summary>
        public (DateTime? Start, DateTime? End) Span(StaffOwner owner)
        {
            if (owner.HasCompetition)
            {
                try
                {
                    var c = _competitions.GetSaved(owner.Comp);
                    if (c != null) return (c.CompetitionDate, c.CompetitionEndDate);
                }
                catch { }
                return (null, null);
            }
            var p = Planning(owner.PlanningId);
            return (p?.TargetStart, p?.TargetEnd);
        }

        /// <summary>Var organisatören hanterar bemanningen.</summary>
        public string ManageUrl(StaffOwner owner) =>
            owner.HasPlanning ? $"/tavlingsplanering/{owner.PlanningId}/bemanning" : $"/tavlingsplanering?c={owner.Comp}";
    }
}
