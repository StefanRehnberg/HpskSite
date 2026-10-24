using HpskSite.Models.Staffing;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// P2b: bemanningen följer planeringens koppling till en tävling. Körs i anroparens transaktion
    /// (PlanningService: skapa tävlingens planering, koppla, koppla loss) — en halv flytt får aldrig synas.
    ///
    /// <para><b>Adopt</b>: tävlingens rader som ännu saknar planering tas in i planeringen. Där ett unikt
    /// villkor skulle krocka (samma dag, samma behov, samma anmälan, samma källa, samma roll) vinner
    /// planeringens rad och tävlingens tas bort — planeringen är den som planerats i.</para>
    /// <para><b>Attach</b>: planeringens rader får tävlingens id, så alla som läser per tävling (Mitt schema,
    /// startsidans kort, appbehörigheten, raderingsspärren) ser dem.</para>
    /// <para><b>Detach</b>: planeringens rader tappar tävlingens id. Appbehörigheten till tävlingen upphör
    /// därmed (den läses per tävling), och speglingarna räknas om av anroparen efteråt.</para>
    /// <para>Speglingarna till tävlingen (tävlingsansvariga, stationschefer) skrivs INTE här — de går genom
    /// tävlingslagringen och får inte ligga i en annan tjänsts transaktion. Anroparen kör
    /// <see cref="StaffingService.SyncCompetitionMirrors"/> efteråt.</para>
    /// </summary>
    public static class StaffingPlanningLink
    {
        private static readonly string[] Plain = { "StaffAssignment", "StaffAvailability", "StaffPass", "StaffHelpSlot", "StaffRequestLog" };

        /// <summary>Tabellerna med ett unikt villkor, och kolumnerna det gäller utöver ägaren.</summary>
        private static readonly (string Table, string[] Keys)[] Unique =
        {
            ("StaffDay", new[] { "DayDate" }),
            ("StaffCrewNeed", new[] { "ScopeKind", "RoleKey", "DayDate", "ScopeKey", "PassId" }),
            ("StaffHelpSignup", new[] { "MemberId" }),
            ("StaffingSourceScope", new[] { "ScopeType", "ScopeKey" }),
        };

        public static readonly string[] AllTables =
            { "StaffAssignment", "StaffAvailability", "StaffDay", "StaffPass", "StaffCrewNeed", "StaffHelpSlot", "StaffHelpSignup", "StaffingSourceScope", "StaffRequestLog" };

        public static void Adopt(IUmbracoDatabase db, int planningId, int competitionId)
        {
            if (planningId <= 0 || competitionId <= 0) return;
            foreach (var (t, keys) in Unique)
            {
                // Null-säker: behovets DayDate är null för "alla dagar".
                var match = string.Join(" AND ", keys.Select(k => $"(x.{k} = d.{k} OR (x.{k} IS NULL AND d.{k} IS NULL))"));
                db.Execute($@"DELETE d FROM {t} d
                    WHERE d.CompetitionId = @1 AND d.PlanningId IS NULL
                      AND EXISTS (SELECT 1 FROM {t} x WHERE x.PlanningId = @0 AND {match})", planningId, competitionId);
                db.Execute($"UPDATE {t} SET PlanningId = @0 WHERE CompetitionId = @1 AND PlanningId IS NULL", planningId, competitionId);
            }
            foreach (var t in Plain)
                db.Execute($"UPDATE {t} SET PlanningId = @0 WHERE CompetitionId = @1 AND PlanningId IS NULL", planningId, competitionId);

            // Rollerna: tävlingens egna blir planeringens.
            db.Execute(@"DELETE r FROM StaffRole r
                WHERE r.OwnerType = @2 AND r.OwnerId = @1
                  AND EXISTS (SELECT 1 FROM StaffRole x WHERE x.OwnerType = @3 AND x.OwnerId = @0 AND x.RoleKey = r.RoleKey)",
                planningId, competitionId, RoleOwnerType.Competition, RoleOwnerType.Planning);
            db.Execute("UPDATE StaffRole SET OwnerType = @2, OwnerId = @0 WHERE OwnerType = @3 AND OwnerId = @1",
                planningId, competitionId, RoleOwnerType.Planning, RoleOwnerType.Competition);
        }

        public static void Attach(IUmbracoDatabase db, int planningId, int competitionId)
        {
            if (planningId <= 0 || competitionId <= 0) return;
            Adopt(db, planningId, competitionId);
            RecomputeFamily(db, planningId);
        }

        public static void Detach(IUmbracoDatabase db, int planningId, int competitionId)
        {
            if (planningId <= 0) return;
            if (competitionId > 0) Adopt(db, planningId, competitionId);
            RecomputeFamily(db, planningId);
        }

        private sealed class LinkPair { public int PlanningId { get; set; } public int TargetId { get; set; } }

        /// <summary>
        /// Omgång 3: varje planering i familjen får den tävling dess bemanning gäller (<see cref="PlanningFamilyRules.StaffingCompetition"/>)
        /// på alla sina rader. EN väg — koppla, koppla loss, "Bemannar för" och borttagning går hit, så att familjens
        /// planeringar aldrig kan säga olika saker om vilken tävling de bemannar. Familjen är högst några tiotal planeringar.
        /// </summary>
        public static void RecomputeFamily(IUmbracoDatabase db, int planningId)
        {
            var p = db.SingleOrDefault<HpskSite.Models.Planning.PlanningRow>("SELECT * FROM Planning WHERE Id = @0", planningId);
            if (p == null) return;
            var root = p.ParentPlanningId > 0 ? p.ParentPlanningId!.Value : p.Id;
            var family = db.Fetch<HpskSite.Models.Planning.PlanningRow>(
                "SELECT * FROM Planning WHERE (Id = @0 OR ParentPlanningId = @0) AND DeletedAt IS NULL", root);
            if (family.Count == 0) return;
            var ids = string.Join(",", family.Select(f => f.Id));
            var links = db.Fetch<LinkPair>($"SELECT PlanningId, TargetId FROM PlanningLink WHERE TargetKind = N'competition' AND PlanningId IN ({ids})")
                .GroupBy(l => l.PlanningId).ToDictionary(g => g.Key, g => g.First().TargetId);
            foreach (var f in family)
            {
                var comp = HpskSite.Models.Planning.PlanningFamilyRules.StaffingCompetition(f, links);
                foreach (var t in AllTables)
                {
                    if (comp is int c) db.Execute($"UPDATE {t} SET CompetitionId = @1 WHERE PlanningId = @0 AND (CompetitionId IS NULL OR CompetitionId <> @1)", f.Id, c);
                    else db.Execute($"UPDATE {t} SET CompetitionId = NULL WHERE PlanningId = @0 AND CompetitionId IS NOT NULL", f.Id);
                }
            }
        }
    }
}
