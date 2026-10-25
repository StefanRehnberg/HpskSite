using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// FAMILJEN på ett ställe (Stefan 2026-10-25, Expertgruppen K6): en huvudplanering och dess delplaneringar, en nivå.
    /// Regeln stod förut som <c>Id = @0 OR ParentPlanningId = @0</c> på nio ställen. Databasen har den beräknade kolumnen
    /// <c>Planning.FamilyRootId</c> (= <c>COALESCE(ParentPlanningId, Id)</c>, persisted och indexerad), så att varje fråga
    /// om familjen är EN jämförelse — se <see cref="RootSql"/>.
    ///
    /// <para><b>Läsrätten huvud → del</b> (<see cref="ReadsSubViaParentSql"/>): Sammankallande och Kan ändra i
    /// huvudplaneringen läser delplaneringarna — men inte en rad som bara kom till genom tävlingsbehörigheten
    /// (<c>ViaCompetition</c>), annars berodde läsrätten på om man råkat öppna huvudplaneringen först (Expertgruppen R2,
    /// Stefan B3). Huvudplaneringens sammankallande är dessutom SAMMANKALLANDE i varje delplanering
    /// (<c>PlanningService.EnsureParentOwner</c>, Stefan B4) — läsrätten här gäller alltså i praktiken Kan ändra.</para>
    /// </summary>
    public static class PlanningFamily
    {
        /// <summary>SQL: huvudplaneringens id för planeringen @0 (själv om den är en huvudplanering).</summary>
        public const string RootSql = "SELECT FamilyRootId FROM Planning WHERE Id = @0";

        /// <summary>
        /// SQL-villkor på en <c>PlanningMember m</c> i huvudplaneringen: får hen LÄSA delplaneringarna? Aktiv, Sammankallande
        /// eller Kan ändra, och inte en rad som bara finns genom tävlingsbehörigheten.
        /// </summary>
        public const string ReadsSubViaParentSql = "m.RemovedAt IS NULL AND m.Access IN (N'Owner', N'Editor') AND m.ViaCompetition = 0";

        /// <summary>Samma regel som <see cref="ReadsSubViaParentSql"/>, för en rad i minnet (enhetstestad).</summary>
        public static bool ReadsSubViaParent(PlanningMemberRow? m) =>
            m is { IsActive: true, ViaCompetition: false }
            && (m.Access == PlanningAccessLevel.Owner || m.Access == PlanningAccessLevel.Editor);

        /// <summary>Huvudplaneringen (eller planeringen själv), 0 om planeringen inte finns.</summary>
        public static int Root(IUmbracoDatabase db, int planningId) =>
            planningId <= 0 ? 0 : db.ExecuteScalar<int?>(RootSql, planningId) ?? 0;

        /// <summary>Familjens planeringar (inte raderade): huvudplaneringen först, sedan delplaneringarna efter namn.</summary>
        public static List<PlanningRow> Rows(IUmbracoDatabase db, int planningId) =>
            db.Fetch<PlanningRow>(@"SELECT f.* FROM Planning p JOIN Planning f ON f.FamilyRootId = p.FamilyRootId AND f.DeletedAt IS NULL
                WHERE p.Id = @0 ORDER BY CASE WHEN f.ParentPlanningId IS NULL THEN 0 ELSE 1 END, f.Name, f.Id", planningId);

        /// <summary>Familjens id:n i samma ordning som <see cref="Rows"/>.</summary>
        public static List<int> Ids(IUmbracoDatabase db, int planningId) =>
            db.Fetch<int>(@"SELECT f.Id FROM Planning p JOIN Planning f ON f.FamilyRootId = p.FamilyRootId AND f.DeletedAt IS NULL
                WHERE p.Id = @0 ORDER BY CASE WHEN f.ParentPlanningId IS NULL THEN 0 ELSE 1 END, f.Name, f.Id", planningId);

        /// <summary>Hör planeringarna till samma familj (och finns de)?</summary>
        public static bool SameFamily(IUmbracoDatabase db, int a, int b) =>
            a > 0 && b > 0 && db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM Planning x JOIN Planning y ON y.FamilyRootId = x.FamilyRootId
                WHERE x.Id = @0 AND y.Id = @1 AND x.DeletedAt IS NULL AND y.DeletedAt IS NULL", a, b) == 1;

        /// <summary>Id-listan för en IN-sats (minst ett id, så att satsen aldrig blir tom).</summary>
        public static string InList(IEnumerable<int> ids, int fallback) => string.Join(",", ids.DefaultIfEmpty(fallback));
    }
}
