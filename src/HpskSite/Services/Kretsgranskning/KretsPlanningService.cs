using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models.Kretsgranskning;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Fas 4, resten: Förbundets stomprogram och kretsens arrangörschecklista.
    ///
    /// <para><b>Stomprogrammet</b> läses av kretskalendern (<see cref="KretsCalendarService"/>) som
    /// bakgrund och krockkälla. Sajtadmin för in det.</para>
    ///
    /// <para><b>Arrangörschecklistan</b> skrivs av kretsen och läggs in i en tävlings förberedelser
    /// när arrangören väljer det. ⚠️ Ingenting skrivs när förberedelserna bara LÄSES: en
    /// sidladdning som tyst lade in uppgifter vore omöjlig att ångra. Varje punkt arrangören tar
    /// ställning till (lagt in eller valt bort) registreras i <c>RegionChecklistApplied</c>, så att
    /// en uppgift arrangören tagit bort inte erbjuds igen.</para>
    /// </summary>
    public class KretsPlanningService
    {
        public const string ChecklistScopeType = "KretsChecklist";

        private readonly IScopeProvider _scopeProvider;
        private readonly KretsCalendarService _calendar;
        private readonly IContentService _content;
        private readonly ILogger<KretsPlanningService> _logger;

        public KretsPlanningService(IScopeProvider scopeProvider, KretsCalendarService calendar, IContentService content,
            ILogger<KretsPlanningService> logger)
        {
            _scopeProvider = scopeProvider;
            _calendar = calendar;
            _content = content;
            _logger = logger;
        }

        public bool TablesExist() => SchemaProblem() == null;

        /// <summary>Null när schemat finns, annars vad som saknas (för startkontrollen).</summary>
        public string? SchemaProblem()
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var missing = new[] { "StomprogramItem", "RegionOrganiserChecklistItem", "RegionChecklistApplied" }
                    .Where(t => scope.Database.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + t) == 0)
                    .ToList();
                return missing.Count == 0 ? null : "tabellen " + string.Join(", ", missing);
            }
            catch (Exception ex) { return "schemat kunde inte läsas: " + ex.Message; }
        }

        // ── Arrangörschecklistan: kretsens sida ───────────────────────────────────────────

        public List<RegionOrganiserChecklistItem> Checklist(int regionId, bool activeOnly = true)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<RegionOrganiserChecklistItem>(
                "SELECT * FROM RegionOrganiserChecklistItem WHERE RegionId = @0" + (activeOnly ? " AND IsActive = 1" : "") + " ORDER BY SortOrder, Id",
                regionId);
        }

        public (RegionOrganiserChecklistItem? Item, string? Error) SaveChecklistItem(int regionId, int id, string? title, string? description,
            int? daysBeforeComp, string? appliesTo, int actorId)
        {
            title = (title ?? "").Trim();
            if (title.Length == 0) return (null, "Skriv vad arrangören ska göra.");
            if (title.Length > 300) title = title[..300];
            description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
            if (description?.Length > 2000) description = description[..2000];
            if (daysBeforeComp is < -365 or > 730) return (null, "Antalet dagar ska ligga mellan 365 dagar efter och två år före tävlingen.");

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            RegionOrganiserChecklistItem it;
            if (id > 0)
            {
                it = db.SingleOrDefaultById<RegionOrganiserChecklistItem>(id);
                if (it == null || it.RegionId != regionId || !it.IsActive) return (null, "Punkten finns inte.");
            }
            else
            {
                it = new RegionOrganiserChecklistItem
                {
                    RegionId = regionId, CreatedAt = DateTime.Now, CreatedByMemberId = actorId, IsActive = true,
                    SortOrder = db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM RegionOrganiserChecklistItem WHERE RegionId = @0", regionId)
                };
            }
            it.Title = title;
            it.Description = description;
            it.DaysBeforeComp = daysBeforeComp;
            it.AppliesTo = OrganiserChecklistScope.Normalize(appliesTo);
            it.UpdatedAt = DateTime.Now;
            if (it.Id > 0) db.Update(it); else db.Insert(it);
            scope.Complete();
            return (it, null);
        }

        /// <summary>
        /// Tar bort punkten ur kretsens lista. ⚠️ MJUKT: redan inlagda uppgifter i tävlingarnas
        /// förberedelser är arrangörens och rörs inte, och spärrraderna behöver sin punkt.
        /// </summary>
        public bool RemoveChecklistItem(int regionId, int id)
        {
            using var scope = _scopeProvider.CreateScope();
            var n = scope.Database.Execute("UPDATE RegionOrganiserChecklistItem SET IsActive = 0, UpdatedAt = @2 WHERE Id = @0 AND RegionId = @1", id, regionId, DateTime.Now);
            scope.Complete();
            return n > 0;
        }

        public bool MoveChecklistItem(int regionId, int id, int direction)
        {
            var list = Checklist(regionId);
            var i = list.FindIndex(x => x.Id == id);
            var j = i + Math.Sign(direction);
            if (i < 0 || j < 0 || j >= list.Count) return false;
            (list[i], list[j]) = (list[j], list[i]);
            using var scope = _scopeProvider.CreateScope();
            for (var k = 0; k < list.Count; k++)
                scope.Database.Execute("UPDATE RegionOrganiserChecklistItem SET SortOrder = @1 WHERE Id = @0", list[k].Id, k + 1);
            scope.Complete();
            return true;
        }

        // ── Arrangörschecklistan: tävlingens sida ─────────────────────────────────────────

        public record CompetitionChecklist(int RegionId, string RegionName, List<RegionOrganiserChecklistItem> Pending, int Added, int Dismissed);

        /// <summary>
        /// Kretsens punkter som gäller tävlingen och som arrangören ännu inte tagit ställning till.
        /// Null när tävlingen inte har någon krets eller kretsen ingen checklista.
        /// </summary>
        public CompetitionChecklist? ForCompetition(int competitionId)
        {
            try
            {
                if (!TablesExist()) return null;
                var cr = _calendar.CompetitionRegion(competitionId);
                if (cr == null) return null;
                var regionId = cr.Value.Region.Id;
                var items = Checklist(regionId);
                if (items.Count == 0) return null;

                var comp = _content.GetById(competitionId);
                if (comp == null) return null;
                var level = CompetitionLevel.Normalize(comp.GetValue<string>("competitionLevel"));
                var scopeText = ChampionshipCategory.NormalizeScope(comp.GetValue<string>("competitionScope"));
                var isClubOnly = comp.GetValue<bool>("isClubOnly");
                var kretsOrAbove = CompetitionLevel.IsKretsOrAbove(level)
                    || scopeText is CompetitionScopeHelper.Kretsmasterskap or CompetitionScopeHelper.Landsdelsmasterskap or CompetitionScopeHelper.SvensktMasterskap;
                var isKm = scopeText == CompetitionScopeHelper.Kretsmasterskap;
                var applies = items.Where(i => OrganiserChecklistScope.Applies(i.AppliesTo, isClubOnly, kretsOrAbove, isKm)).ToList();

                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var handled = scope.Database.Fetch<AppliedRow>("SELECT ChecklistItemId, Added FROM RegionChecklistApplied WHERE CompetitionId = @0", competitionId);
                var handledIds = handled.Select(h => h.ChecklistItemId).ToHashSet();
                return new CompetitionChecklist(regionId, cr.Value.Region.Name,
                    applies.Where(i => !handledIds.Contains(i.Id)).ToList(),
                    handled.Count(h => h.Added), handled.Count(h => !h.Added));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kretsens checklista kunde inte läsas för tävling {Id}.", competitionId);
                return null;
            }
        }

        private class AppliedRow { public int ChecklistItemId { get; set; } public bool Added { get; set; } }

        /// <summary>
        /// Lägger in de valda punkterna i tävlingens förberedelser, under ett område med kretsens
        /// namn, och registrerar <paramref name="dismissIds"/> som bortvalda (de erbjuds inte igen).
        /// Bara VÄNTANDE punkter som gäller tävlingen rörs — ett id ur en annan krets eller en redan
        /// hanterad punkt ignoreras. Idempotent: en punkt läggs aldrig in två gånger.
        /// </summary>
        public (int Added, int Dismissed) Apply(int competitionId, IEnumerable<int> addIds, IEnumerable<int> dismissIds, int actorId)
        {
            var cl = ForCompetition(competitionId);
            if (cl == null) return (0, 0);
            var add = addIds.ToHashSet();
            var dismiss = dismissIds.Where(i => !add.Contains(i)).ToHashSet();
            var toAdd = cl.Pending.Where(p => add.Contains(p.Id)).ToList();
            var toDismiss = cl.Pending.Where(p => dismiss.Contains(p.Id)).ToList();
            if (toAdd.Count == 0 && toDismiss.Count == 0) return (0, 0);

            var compDate = _content.GetById(competitionId)?.GetValue<DateTime?>("competitionDate");
            if (compDate is { Year: <= 1900 }) compDate = null;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            int added = 0;
            if (toAdd.Count > 0)
            {
                var areaName = "Kretsens checklista — " + cl.RegionName;
                var area = db.FirstOrDefault<WorkArea>("SELECT * FROM WorkArea WHERE CompetitionId = @0 AND Name = @1", competitionId, areaName);
                if (area == null)
                {
                    area = new WorkArea
                    {
                        CompetitionId = competitionId, Name = areaName, CreatedByMemberId = actorId, CreatedDate = DateTime.Now,
                        SortOrder = db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) + 1 FROM WorkArea WHERE CompetitionId = @0", competitionId)
                    };
                    db.Insert(area);
                }
                var existing = db.Fetch<string>("SELECT ScopeKey FROM WorkItem WHERE CompetitionId = @0 AND ScopeType = @1", competitionId, ChecklistScopeType).ToHashSet();
                var sort = db.ExecuteScalar<int>("SELECT ISNULL(MAX(SortOrder), 0) FROM WorkItem WHERE WorkAreaId = @0", area.Id);
                foreach (var p in toAdd)
                {
                    if (!existing.Contains(p.Id.ToString()))
                    {
                        db.Insert(new WorkItem
                        {
                            CompetitionId = competitionId, WorkAreaId = area.Id, Title = p.Title, Description = p.Description,
                            DueDate = compDate != null && p.DaysBeforeComp != null ? compDate.Value.Date.AddDays(-p.DaysBeforeComp.Value) : null,
                            Status = WorkItemStatus.Planerad, ScopeType = ChecklistScopeType, ScopeKey = p.Id.ToString(),
                            SortOrder = ++sort, CreatedByMemberId = actorId, CreatedDate = DateTime.Now, ModifiedDate = DateTime.Now
                        });
                        added++;
                    }
                    Mark(db, competitionId, p.Id, true, actorId);
                }
            }
            foreach (var p in toDismiss) Mark(db, competitionId, p.Id, false, actorId);
            scope.Complete();
            return (added, toDismiss.Count);
        }

        private static void Mark(IDatabase db, int competitionId, int itemId, bool added, int actorId) =>
            db.Execute(@"IF NOT EXISTS (SELECT 1 FROM RegionChecklistApplied WHERE CompetitionId = @0 AND ChecklistItemId = @1)
                         INSERT INTO RegionChecklistApplied (CompetitionId, ChecklistItemId, Added, AppliedAt, AppliedByMemberId) VALUES (@0, @1, @2, @3, @4)",
                competitionId, itemId, added, DateTime.Now, actorId);
    }
}
