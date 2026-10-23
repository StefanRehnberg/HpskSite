using System;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Services.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Ansökan ur planeringen (P3 steg F, beslut 9.7). En ansökan som startas från en planering bär planeringens id
    /// (<see cref="CompetitionApplication.PlanningId"/>); när tävlingen skapas ur ansökan kopplas den till planeringen
    /// här — samma koppling som planeringens egen "Koppla till en tävling", med bemanningens speglingar efteråt.
    /// </summary>
    public class PlanningApplicationLink
    {
        private readonly IScopeProvider _scopes;
        private readonly PlanningService _planning;
        private readonly StaffingService _staffing;
        private readonly StaffDayService _staffDays;
        private readonly ILogger<PlanningApplicationLink> _logger;

        public PlanningApplicationLink(IScopeProvider scopes, PlanningService planning, StaffingService staffing,
            StaffDayService staffDays, ILogger<PlanningApplicationLink> logger)
        {
            _scopes = scopes; _planning = planning; _staffing = staffing; _staffDays = staffDays; _logger = logger;
        }

        /// <summary>Planeringens senaste ansökan som inte dragits tillbaka, eller null.</summary>
        public CompetitionApplication? ForPlanning(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.FirstOrDefault<CompetitionApplication>(
                "SELECT TOP 1 * FROM CompetitionApplication WHERE PlanningId = @0 AND Status <> @1 ORDER BY Id DESC",
                planningId, CompetitionApplicationStatus.Aterkallad);
        }

        /// <summary>
        /// Kopplar tävlingen som just skapats ur ansökan till planeringen. Returnerar ett besked när det inte gick
        /// (planeringen är redan kopplad, tävlingen har redan en planering) — tävlingen är skapad ändå, och det ska
        /// sägas i stället för att tigas om.
        /// </summary>
        public string? LinkCompetition(int planningId, int competitionId, int byMemberId)
        {
            if (_planning.Get(planningId) == null) return "Planeringen som ansökan kom ifrån finns inte längre.";
            var err = _planning.LinkToCompetition(planningId, competitionId, byMemberId);
            if (err != null) return err;
            try
            {
                _staffing.SyncCompetitionMirrors(competitionId);
                _staffDays.AddCompetitionDays(new HpskSite.Models.Staffing.StaffOwner(planningId, competitionId), byMemberId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planering {Id}: bemanningens speglingar efter kopplingen ur ansökan till tävling {Comp} misslyckades.", planningId, competitionId);
            }
            return null;
        }
    }
}
