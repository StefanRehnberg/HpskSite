using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Board governance: the Årshjul (annual cycle checklist) and Valberedning (nominations).
    /// Club/region-scoped via OwnerType/OwnerId. See BOARD_WORK_PHASE3_GOVERNANCE.md.
    /// </summary>
    public class BoardGovernanceService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly BoardRoleService _boardRoleService;
        private readonly ShootingRangeService _ranges;
        private readonly ILogger<BoardGovernanceService> _logger;

        public BoardGovernanceService(IScopeProvider scopeProvider, IMemberService memberService, BoardRoleService boardRoleService,
            ShootingRangeService ranges, ILogger<BoardGovernanceService> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _boardRoleService = boardRoleService;
            _ranges = ranges;
            _logger = logger;
        }

        /// <summary>
        /// Banornas krav för en klubb som skrivskyddade tillfällen (B2). Bara klubbar — en krets har inga banor.
        /// Läses UTANFÖR årshjulets scope (egen anslutning i ShootingRangeService). Ett fel här får aldrig fälla
        /// årshjulet; det loggas och kraven utelämnas.
        /// </summary>
        private List<WheelOccurrence> RangeOccurrences(int ownerType, int ownerId)
        {
            if (ownerType != DocumentOwnerType.Club) return new();
            try { return BoardWheelPlanner.FromRangeRequirements(_ranges.GetClubRangeRequirements(ownerId)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Banornas krav kunde inte läsas för klubb {ClubId}s årshjul.", ownerId);
                return new();
            }
        }

        // ---- Årshjul --------------------------------------------------------

        // Reglerna och hur tillfällena räknas fram bor i BoardWheelPlanner (rena funktioner). Den här delen
        // hämtar och sparar. ⚠️ Läsning skriver ingenting utom EN gång per ägare: standardpunkterna som regler
        // (EnsureRules). Ett tillfälle sparas först när någon bockar av, ändrar eller hoppar över det.

        /// <summary>Årshjulet för ett år, så som det visas.</summary>
        public List<WheelOccurrence> GetYearWheel(int ownerType, int ownerId, int year)
        {
            List<WheelOccurrence> own;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                EnsureRules(db, ownerType, ownerId);
                own = BoardWheelPlanner.ForYear(Rules(db, ownerType, ownerId), SavedRows(db, ownerType, ownerId, year, year), year);
            }
            return BoardWheelPlanner.Merge(own, RangeOccurrences(ownerType, ownerId).Where(o => o.Year == year));
        }

        /// <summary>
        /// Ej avklarade tillfällen med datum senast <paramref name="days"/> dagar fram, försenade inräknade —
        /// över årsskiftet: i december ska januaris punkter synas.
        /// </summary>
        public List<WheelOccurrence> GetDueSoon(int ownerType, int ownerId, DateTime today, int days)
        {
            var all = new List<WheelOccurrence>();
            var lastYear = today.Date.AddDays(days).Year;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                EnsureRules(db, ownerType, ownerId);
                var rules = Rules(db, ownerType, ownerId);
                var saved = SavedRows(db, ownerType, ownerId, today.Year, lastYear);
                for (var y = today.Year; y <= lastYear; y++) all.AddRange(BoardWheelPlanner.ForYear(rules, saved, y));
            }
            // Banans krav: även ett passerat från ett tidigare år (en besiktning som inte gjorts försvinner inte vid nyår).
            all.AddRange(RangeOccurrences(ownerType, ownerId).Where(o => o.Year <= lastYear));
            return BoardWheelPlanner.DueSoon(all, today, days);
        }

        /// <summary>
        /// Föreningens EGNA tillfällen (regler och engångspunkter, inga krav från banor) för ett antal år — för
        /// påminnelsesvepet. ⚠️ Skriver ingenting: standardpunkterna läggs inte in här (en bakgrundstjänst ska inte
        /// skapa regler åt föreningar som aldrig öppnat sitt årshjul).
        /// </summary>
        public List<WheelOccurrence> GetOwnOccurrences(int ownerType, int ownerId, int fromYear, int toYear)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rules = Rules(db, ownerType, ownerId);
            var saved = SavedRows(db, ownerType, ownerId, fromYear, toYear);
            var all = new List<WheelOccurrence>();
            for (var y = fromYear; y <= toYear; y++) all.AddRange(BoardWheelPlanner.ForYear(rules, saved, y));
            return all;
        }

        /// <summary>Föreningar som kan ha något att påminna om: en regel med påminnelse, eller en ej avklarad
        /// engångspunkt med datum de här åren.</summary>
        public List<(int OwnerType, int OwnerId)> WheelReminderOwners(int fromYear, int toYear)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<OwnerRow>(
                @"SELECT DISTINCT OwnerType, OwnerId FROM BoardWheelRule WHERE IsActive = 1 AND Remind = 1
                  UNION
                  SELECT DISTINCT OwnerType, OwnerId FROM BoardYearWheelItems
                   WHERE RuleId IS NULL AND IsActive = 1 AND Done = 0 AND TargetDate IS NOT NULL AND Year BETWEEN @0 AND @1",
                fromYear, toYear).Select(r => (r.OwnerType, r.OwnerId)).ToList();
        }

        private class OwnerRow { public int OwnerType { get; set; } public int OwnerId { get; set; } }

        /// <summary>Slå på eller av mejlpåminnelsen för det återkommande tillfällets regel (alla år).</summary>
        public string? SetWheelRemind(WheelKey key, bool remind)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var (rule, _, _, error) = ResolveOccurrence(db, key);
            if (error != null) return error;
            if (rule == null) return "En uppgift som inte återkommer påminns det alltid om.";
            rule.Remind = remind;
            db.Update(rule);
            return null;
        }

        /// <summary>"Kommande längre fram" efter det visade året (se <see cref="BoardWheelPlanner.Upcoming"/>).</summary>
        public List<WheelOccurrence> GetUpcoming(int ownerType, int ownerId, int afterYear)
        {
            List<WheelOccurrence> own;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                EnsureRules(db, ownerType, ownerId);
                own = BoardWheelPlanner.Upcoming(Rules(db, ownerType, ownerId), SavedRows(db, ownerType, ownerId, afterYear + 1, 2999), afterYear);
            }
            return own.Concat(RangeOccurrences(ownerType, ownerId).Where(o => o.Year > afterYear))
                .OrderBy(o => o.Year).ThenBy(o => o.TargetDate ?? DateTime.MaxValue).Take(10).ToList();
        }

        /// <summary>
        /// År att erbjuda i väljaren: år med sparade uppgifter, plus nästa tillfälle (från i år) för varje regel
        /// med intervall ≥ 2 och varje kommande engångspunkt — så att 2030 års besiktning går att välja.
        /// </summary>
        public List<int> GetWheelYears(int ownerType, int ownerId)
        {
            List<int> years;
            var thisYear = DateTime.Today.Year;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                years = db.Fetch<int>(
                    "SELECT DISTINCT Year FROM BoardYearWheelItems WHERE OwnerType=@0 AND OwnerId=@1 AND IsActive=1",
                    ownerType, ownerId);
                years.AddRange(BoardWheelPlanner.Upcoming(Rules(db, ownerType, ownerId),
                    SavedRows(db, ownerType, ownerId, thisYear, 2999), thisYear - 1, max: 50).Select(o => o.Year));
            }
            years.AddRange(RangeOccurrences(ownerType, ownerId).Select(o => o.Year).Where(y => y >= thisYear - 1));
            return years.Distinct().OrderByDescending(y => y).ToList();
        }

        /// <summary>Vilken förening ett tillfälle tillhör — för behörighetskontrollen. Null = finns inte.</summary>
        public (int OwnerType, int OwnerId)? ResolveWheelOwner(WheelKey key)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            if (key.ItemId.HasValue)
            {
                var it = db.SingleOrDefaultById<BoardYearWheelItem>(key.ItemId.Value);
                return it == null ? null : (it.OwnerType, it.OwnerId);
            }
            var rule = db.SingleOrDefaultById<BoardWheelRule>(key.RuleId!.Value);
            return rule == null ? null : (rule.OwnerType, rule.OwnerId);
        }

        /// <summary>Bocka av eller ångra. Ett framräknat tillfälle sparas. Returnerar det sparade id:t, eller ett fel.</summary>
        public (int ItemId, string? Error) SetWheelDone(WheelKey key, bool done)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            return Change(db, key, row =>
            {
                row.Done = done;
                row.DoneDate = done ? DateTime.Now : null;
                return null;
            });
        }

        /// <summary>
        /// Lägg till en punkt. <b>Året kommer ur datumet</b>, aldrig ur det år som visas (buggen 2026-10-14: en
        /// punkt daterad 2030 hamnade på 2027). Utan datum: det år som visas.
        /// <paramref name="intervalYears"/> 0 = en gång, 1 = varje år, N = vart N:e år (en regel som börjar det året).
        /// </summary>
        public (int Year, string? Error) AddWheelItem(int ownerType, int ownerId, int shownYear, string title,
            DateTime? targetDate, int intervalYears, int? createdBy)
        {
            if (intervalYears < 0 || intervalYears > BoardWheelPlanner.MaxIntervalYears)
                return (0, $"Intervallet måste vara mellan 1 och {BoardWheelPlanner.MaxIntervalYears} år.");
            var year = targetDate?.Year ?? shownYear;

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            if (intervalYears == 0)
            {
                db.Insert(new BoardYearWheelItem
                {
                    OwnerType = ownerType, OwnerId = ownerId, Year = year, Title = title,
                    TargetDate = targetDate, SortOrder = 100, IsActive = true
                });
                return (year, null);
            }

            // Standardpunkterna först — spärren för dem är att någon regel finns, och en egen regel får inte
            // ta deras plats.
            EnsureRules(db, ownerType, ownerId);
            db.Insert(new BoardWheelRule
            {
                OwnerType = ownerType, OwnerId = ownerId, Title = title,
                Month = targetDate?.Month, Day = targetDate?.Day,
                IntervalYears = intervalYears, FirstYear = year, SortOrder = 100,
                Remind = BoardWheelRule.DefaultRemind(null, intervalYears),
                IsActive = true, CreatedDate = DateTime.Now, CreatedBy = createdBy
            });
            return (year, null);
        }

        /// <summary>
        /// Ändra titel och datum. En engångspunkt FLYTTAR till datumets år. Ett tillfälle av en regel ändras bara
        /// för sitt år och sparas — datumet måste då ligga i samma år.
        /// </summary>
        public (int Year, string? Error) UpdateWheelItem(WheelKey key, string title, DateTime? targetDate)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            if (key.ItemId.HasValue)
            {
                var it = db.SingleOrDefaultById<BoardYearWheelItem>(key.ItemId.Value);
                if (it == null || !it.IsActive) return (0, "Uppgiften finns inte längre.");
                if (!it.RuleId.HasValue)
                {
                    it.Title = title;
                    it.TargetDate = targetDate;
                    if (targetDate.HasValue) it.Year = targetDate.Value.Year;
                    db.Update(it);
                    return (it.Year, null);
                }
            }

            var year = 0;
            var (_, error) = Change(db, key, row =>
            {
                if (targetDate.HasValue && targetDate.Value.Year != row.Year)
                    return $"Datumet måste ligga i {row.Year}. Det här är en återkommande uppgift, och ändringen gäller bara {row.Year}.";
                row.Title = title;
                row.TargetDate = targetDate;
                year = row.Year;
                return null;
            });
            return (year, error);
        }

        /// <summary>
        /// Ta bort. En engångspunkt göms. Ett tillfälle av en regel blir en gravsten för sitt år — regeln gäller
        /// fortfarande övriga år (samma som förut: en borttagen standardpunkt försvann bara det året).
        /// </summary>
        public string? RemoveWheelItem(WheelKey key)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var (_, error) = Change(db, key, row => { row.IsActive = false; return null; }, allowInactive: true);
            return error;
        }

        /// <summary>
        /// "Det här och kommande": ändringen gäller från tillfällets år och framåt; tidigare år behåller sin betydelse.
        /// <list type="bullet">
        /// <item>Är året regelns första ändras regeln själv. Annars <b>delas regeln</b>: den gamla slutar året före
        /// och en ny börjar med de nya värdena (samma standardnyckel, påminnelse och ordning).</item>
        /// <item><paramref name="intervalYears"/> 0 = uppgiften återkommer inte längre: regeln slutar året före och
        /// året blir en engångspunkt.</item>
        /// <item>För en engångspunkt med intervall ≥ 1: punkten blir en regel som börjar sitt år.</item>
        /// <item>Sparade år efter ändringen följer med till den nya regeln när den slår till det året; annars tas de
        /// bort — utom avbockade, som är historik.</item>
        /// </list>
        /// </summary>
        public (int Year, string? Error) UpdateWheelFollowing(WheelKey key, string title, DateTime? targetDate, int intervalYears, int? by)
        {
            if (intervalYears < 0 || intervalYears > BoardWheelPlanner.MaxIntervalYears)
                return (0, $"Intervallet måste vara mellan 1 och {BoardWheelPlanner.MaxIntervalYears} år.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var (rule, row, year, error) = ResolveOccurrence(db, key);
            if (error != null) return (0, error);

            if (rule == null)
            {
                // Engångspunkt.
                if (intervalYears == 0)
                {
                    row!.Title = title;
                    row.TargetDate = targetDate;
                    if (targetDate.HasValue) row.Year = targetDate.Value.Year;
                    db.Update(row);
                    return (row.Year, null);
                }
                var first = targetDate?.Year ?? row!.Year;
                var made = new BoardWheelRule
                {
                    OwnerType = row!.OwnerType, OwnerId = row.OwnerId, Title = title,
                    Month = targetDate?.Month, Day = targetDate?.Day, IntervalYears = intervalYears, FirstYear = first,
                    SortOrder = row.SortOrder, Remind = BoardWheelRule.DefaultRemind(null, intervalYears),
                    IsActive = true, CreatedDate = DateTime.Now, CreatedBy = by
                };
                db.Insert(made);
                row.RuleId = made.Id; row.Year = first; row.Title = title; row.TargetDate = targetDate;
                db.Update(row);
                return (first, null);
            }

            if (targetDate.HasValue && targetDate.Value.Year != year)
                return (0, $"Datumet måste ligga i {year} — ändringen gäller från {year} och framåt.");

            var laterRows = db.Fetch<BoardYearWheelItem>(
                "SELECT * FROM BoardYearWheelItems WITH (UPDLOCK, HOLDLOCK) WHERE RuleId=@0 AND Year >= @1", rule.Id, year);

            if (intervalYears == 0)
            {
                EndRuleBefore(db, rule, year);
                var thisRow = laterRows.FirstOrDefault(r => r.Year == year);
                foreach (var r in laterRows.Where(r => r.Year > year && !(r.Done && r.IsActive))) db.Delete(r);
                if (thisRow != null)
                {
                    thisRow.RuleId = null; thisRow.Title = title; thisRow.TargetDate = targetDate; thisRow.IsActive = true;
                    db.Update(thisRow);
                }
                else db.Insert(new BoardYearWheelItem
                {
                    OwnerType = rule.OwnerType, OwnerId = rule.OwnerId, Year = year, Title = title,
                    TargetDate = targetDate, SortOrder = rule.SortOrder, IsActive = true
                });
                return (year, null);
            }

            BoardWheelRule target;
            if (year == rule.FirstYear)
            {
                rule.Title = title; rule.Month = targetDate?.Month; rule.Day = targetDate?.Day; rule.IntervalYears = intervalYears;
                db.Update(rule);
                target = rule;
            }
            else
            {
                target = new BoardWheelRule
                {
                    OwnerType = rule.OwnerType, OwnerId = rule.OwnerId, Title = title,
                    Month = targetDate?.Month, Day = targetDate?.Day, IntervalYears = intervalYears,
                    FirstYear = year, LastYear = rule.LastYear, TemplateKey = rule.TemplateKey, SortOrder = rule.SortOrder,
                    Remind = rule.Remind, IsActive = true, CreatedDate = DateTime.Now, CreatedBy = by
                };
                db.Insert(target);
                rule.LastYear = year - 1;
                db.Update(rule);
            }

            foreach (var r in laterRows)
            {
                if (BoardWheelPlanner.Occurs(target, r.Year))
                {
                    r.RuleId = target.Id;
                    r.Title = title;
                    r.TargetDate = r.Year == year ? targetDate : BoardWheelPlanner.DateFor(target.Month, target.Day, r.Year);
                    db.Update(r);
                }
                else if (!(r.Done && r.IsActive)) db.Delete(r);
            }
            return (year, null);
        }

        /// <summary>
        /// "Det här och kommande" vid borttagning: regeln slutar året före (har den inga tidigare år blir den
        /// inaktiv). Årets sparade tillfälle tas bort, senare likaså utom avbockade (historik).
        /// För en engångspunkt: samma som <see cref="RemoveWheelItem"/>.
        /// </summary>
        public string? RemoveWheelFollowing(WheelKey key)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var (rule, row, year, error) = ResolveOccurrence(db, key);
            if (error != null) return error;
            if (rule == null)
            {
                row!.IsActive = false;
                db.Update(row);
                return null;
            }
            EndRuleBefore(db, rule, year);
            foreach (var r in db.Fetch<BoardYearWheelItem>(
                         "SELECT * FROM BoardYearWheelItems WITH (UPDLOCK, HOLDLOCK) WHERE RuleId=@0 AND Year >= @1", rule.Id, year))
                if (r.Year == year || !(r.Done && r.IsActive)) db.Delete(r);
            return null;
        }

        /// <summary>
        /// Bocka av kallelsen till årsmötet. Går på standardpunktens nyckel, inte titeln — en klubb som döpt om
        /// punkten ska inte tappa avbockningen. Äldre sparade år (rader utan regel) matchas på titeln som förut.
        /// </summary>
        public bool TickKallelse(int ownerType, int ownerId, int year)
        {
            var target = BoardWheelPlanner.KallelseTarget(GetYearWheel(ownerType, ownerId, year));
            if (target == null) return false;
            var key = WheelKey.Parse(target.Key);
            return key != null && SetWheelDone(key, true).Error == null;
        }

        /// <summary>Saknade tabeller och kolumner för årshjulets regler (startkontrollen).</summary>
        public List<string> MissingWheelSchema()
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var missing = new List<string>();
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.BoardWheelRule', 'U') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("BoardWheelRule");
            if (db.ExecuteScalar<int>("SELECT CASE WHEN COL_LENGTH('dbo.BoardYearWheelItems', 'RuleId') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("BoardYearWheelItems.RuleId");
            return missing;
        }

        // ---- Årshjul: hjälpare ---------------------------------------------

        /// <summary>
        /// Nyckeln som (regel, sparad rad, år). En engångspunkt ger regel null och raden; ett tillfälle av en regel
        /// ger regeln, ev. den sparade raden och året.
        /// </summary>
        private static (BoardWheelRule? Rule, BoardYearWheelItem? Row, int Year, string? Error) ResolveOccurrence(IUmbracoDatabase db, WheelKey key)
        {
            if (key.ItemId.HasValue)
            {
                var row = db.SingleOrDefaultById<BoardYearWheelItem>(key.ItemId.Value);
                if (row == null || !row.IsActive) return (null, null, 0, "Uppgiften finns inte längre.");
                if (!row.RuleId.HasValue) return (null, row, row.Year, null);
                var owning = db.SingleOrDefaultById<BoardWheelRule>(row.RuleId.Value);
                return owning == null ? (null, row, row.Year, null) : (owning, row, row.Year, null);
            }
            var rule = db.SingleOrDefaultById<BoardWheelRule>(key.RuleId!.Value);
            if (rule == null || !rule.IsActive || !BoardWheelPlanner.Occurs(rule, key.Year))
                return (null, null, 0, "Uppgiften finns inte det året.");
            return (rule, null, key.Year, null);
        }

        /// <summary>Regeln slutar året före <paramref name="year"/>; har den inga tidigare år blir den inaktiv.</summary>
        private static void EndRuleBefore(IUmbracoDatabase db, BoardWheelRule rule, int year)
        {
            if (year <= rule.FirstYear) rule.IsActive = false;
            else rule.LastYear = year - 1;
            db.Update(rule);
        }

        private static List<BoardWheelRule> Rules(IUmbracoDatabase db, int ownerType, int ownerId) =>
            db.Fetch<BoardWheelRule>("SELECT * FROM BoardWheelRule WHERE OwnerType=@0 AND OwnerId=@1", ownerType, ownerId);

        // Även gravstenar (IsActive = 0) — de tar bort ett framräknat tillfälle.
        private static List<BoardYearWheelItem> SavedRows(IUmbracoDatabase db, int ownerType, int ownerId, int fromYear, int toYear) =>
            db.Fetch<BoardYearWheelItem>(
                "SELECT * FROM BoardYearWheelItems WHERE OwnerType=@0 AND OwnerId=@1 AND Year BETWEEN @2 AND @3",
                ownerType, ownerId, fromYear, toYear);

        /// <summary>
        /// Standardpunkterna som regler, en gång per ägare. ⚠️ Spärren är att det finns NÅGON regelrad, även
        /// inaktiv — annars kommer borttagna standardpunkter tillbaka. Läses under UPDLOCK/HOLDLOCK så två
        /// samtidiga första läsningar inte ger dubbla regler.
        /// </summary>
        private static void EnsureRules(IUmbracoDatabase db, int ownerType, int ownerId)
        {
            var any = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM BoardWheelRule WITH (UPDLOCK, HOLDLOCK) WHERE OwnerType=@0 AND OwnerId=@1",
                ownerType, ownerId);
            if (any > 0) return;
            var maxYear = db.ExecuteScalar<int?>(
                "SELECT MAX(Year) FROM BoardYearWheelItems WHERE OwnerType=@0 AND OwnerId=@1", ownerType, ownerId);
            var first = BoardWheelPlanner.SeedFirstYear(maxYear, DateTime.Today.Year);
            foreach (var rule in BoardWheelPlanner.TemplateRules(ownerType, ownerId, first, DateTime.Now))
                db.Insert(rule);
        }

        /// <summary>
        /// Gemensam väg för allt som ändrar ETT tillfälle. En sparad rad ändras direkt; ett framräknat tillfälle
        /// sparas först (upsert under UPDLOCK/HOLDLOCK mot det unika indexet (RuleId, Year)).
        /// <paramref name="change"/> returnerar ett felmeddelande eller null.
        /// </summary>
        private static (int ItemId, string? Error) Change(IUmbracoDatabase db, WheelKey key,
            Func<BoardYearWheelItem, string?> change, bool allowInactive = false)
        {
            BoardYearWheelItem? row;
            if (key.ItemId.HasValue)
            {
                row = db.SingleOrDefaultById<BoardYearWheelItem>(key.ItemId.Value);
                if (row == null || (!row.IsActive && !allowInactive)) return (0, "Uppgiften finns inte längre.");
                if (!row.IsActive) return (row.Id, null); // redan borttagen
                var err = change(row);
                if (err != null) return (0, err);
                db.Update(row);
                return (row.Id, null);
            }

            var rule = db.SingleOrDefaultById<BoardWheelRule>(key.RuleId!.Value);
            if (rule == null) return (0, "Uppgiften finns inte längre.");
            row = db.FirstOrDefault<BoardYearWheelItem>(
                "SELECT * FROM BoardYearWheelItems WITH (UPDLOCK, HOLDLOCK) WHERE RuleId=@0 AND Year=@1",
                rule.Id, key.Year);
            if (row == null)
            {
                if (!rule.IsActive || !BoardWheelPlanner.Occurs(rule, key.Year))
                    return (0, "Uppgiften finns inte det året.");
                row = new BoardYearWheelItem
                {
                    OwnerType = rule.OwnerType, OwnerId = rule.OwnerId, Year = key.Year, RuleId = rule.Id,
                    Title = rule.Title, TargetDate = BoardWheelPlanner.DateFor(rule.Month, rule.Day, key.Year),
                    SortOrder = rule.SortOrder, IsActive = true
                };
                var err = change(row);
                if (err != null) return (0, err);
                db.Insert(row);
                return (row.Id, null);
            }
            if (!row.IsActive) return allowInactive ? (row.Id, null) : (0, "Uppgiften är borttagen det året.");
            var err2 = change(row);
            if (err2 != null) return (0, err2);
            db.Update(row);
            return (row.Id, null);
        }

        // ---- Valberedning ---------------------------------------------------

        public List<BoardNomination> GetNominations(int ownerType, int ownerId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<BoardNomination>(
                "SELECT * FROM BoardNominations WHERE OwnerType=@0 AND OwnerId=@1 AND Year=@2 AND IsActive=1 ORDER BY SortOrder, Id",
                ownerType, ownerId, year);
        }

        public List<int> GetNominationYears(int ownerType, int ownerId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT DISTINCT Year FROM BoardNominations WHERE OwnerType=@0 AND OwnerId=@1 AND IsActive=1 ORDER BY Year DESC",
                ownerType, ownerId);
        }

        public BoardNomination AddNomination(int ownerType, int ownerId, int year, string? postKey, string postLabel,
            string candidateName, int? candidateMemberId, string status, string? notes, int createdByMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = new BoardNomination
            {
                OwnerType = ownerType, OwnerId = ownerId, Year = year, PostKey = postKey, PostLabel = postLabel,
                CandidateName = candidateName, CandidateMemberId = candidateMemberId,
                Status = string.IsNullOrWhiteSpace(status) ? "Föreslagen" : status, Notes = notes,
                CreatedByMemberId = createdByMemberId, CreatedDate = DateTime.Now, IsActive = true
            };
            db.Insert(n);
            return n;
        }

        public bool UpdateNomination(int id, string postLabel, string candidateName, string status, string? notes)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.SingleOrDefaultById<BoardNomination>(id);
            if (n == null) return false;
            n.PostLabel = postLabel;
            n.CandidateName = candidateName;
            n.Status = status;
            n.Notes = notes;
            db.Update(n);
            return true;
        }

        public bool SetNominationStatus(int id, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.SingleOrDefaultById<BoardNomination>(id);
            if (n == null) return false;
            n.Status = status;
            db.Update(n);
            return true;
        }

        public bool RemoveNomination(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.SingleOrDefaultById<BoardNomination>(id);
            if (n == null) return false;
            n.IsActive = false;
            db.Update(n);
            return true;
        }

        public BoardNomination? GetNomination(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefaultById<BoardNomination>(id);
        }

        /// <summary>Posts whose mandate ends on/before the end of the election year (drives "poster på val").</summary>
        public List<BoardRole> GetPostsUpForElection(int ownerType, int ownerId, int year)
        {
            return _boardRoleService.GetExpiringRoles(ownerType, ownerId, new DateTime(year, 12, 31).AddDays(1));
        }
    }
}
