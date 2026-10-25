using System.Globalization;
using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Steg 4 (Expertgruppen K5): <b>medlemmens vecka över en hel planeringsfamilj</b> i Mitt schema, hubben, kalenderfilen och
    /// påminnelserna. Mitt schema är per tävling; en SM-vecka har flera tävlingar och ett gemensamt lager (sekretariat, servering)
    /// utan tävling, som <see cref="MyScheduleService"/> hoppar över. Veckan läser samma <see cref="PersonWeek"/> som personvyn
    /// (steg 3) och blir en vanlig <see cref="MySchedule"/> (<see cref="MySchedule.PlanningId"/> &gt; 0), så att samma ritning,
    /// samma kalenderbyggare och samma hubbkort räcker.
    ///
    /// <para><b>Vilka familjer.</b> Bara en familj med flera delar, eller där medlemmen har ett uppdrag utan tävling. En
    /// ensam planering med en tävling får inget extra kort — den visas som förut, så att allt utanför en familj är oförändrat.
    /// Per-tävlingens kort rörs inte: veckan kommer <i>utöver</i> dem och visar det de inte kan (krockar mellan delarna,
    /// det gemensamma).</para>
    /// </summary>
    public class MyWeekService
    {
        private readonly IScopeProvider _scopes;
        private readonly PlanningService _planning;
        private readonly FamilyWeekService _week;
        private readonly Umbraco.Cms.Core.Services.IMemberService _members;
        private readonly ILogger<MyWeekService> _logger;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        public MyWeekService(IScopeProvider scopes, PlanningService planning, FamilyWeekService week, Umbraco.Cms.Core.Services.IMemberService members,
            ILogger<MyWeekService> logger)
        {
            _scopes = scopes; _planning = planning; _week = week; _members = members; _logger = logger;
        }

        /// <summary>
        /// Huvudplaneringens sammankallande (den som skapade den först) — den man ringer när något krockar, och den veckomejlets svar går till.
        /// Null när ingen kan läsas. Ett fel loggas på Debug och ger null: kontaktuppgiften är en hjälp, aldrig ett villkor.
        /// </summary>
        public DigestContact? ContactFor(int rootId)
        {
            try
            {
                int? memberId;
                using (var s = _scopes.CreateScope(autoComplete: true))
                    memberId = s.Database.ExecuteScalar<int?>(
                        "SELECT TOP 1 MemberId FROM PlanningMember WHERE PlanningId = @0 AND Access = @1 AND RemovedAt IS NULL AND ViaCompetition = 0 AND MemberId > 0 ORDER BY Id",
                        rootId, HpskSite.Models.Planning.PlanningAccessLevel.Owner);
                if (memberId is not > 0) return null;
                var m = _members.GetById(memberId.Value);
                if (m == null) return null;
                var phone = m.HasProperty("phoneNumber") ? m.GetValue<string>("phoneNumber") : null;
                return new DigestContact(m.Name ?? "", string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(), string.IsNullOrWhiteSpace(m.Email) ? null : m.Email);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MyWeekService: kontaktpersonen för planering {Root} gick inte att läsa.", rootId);
                return null;
            }
        }

        /// <summary>
        /// Villkoret för "en familj där veckan tillför något" (alias <c>sa</c> = uppdraget, <c>p</c> = dess planering): familjen har
        /// fler än en del, eller uppdraget saknar tävling. En ensam planering med en tävling hör hit inte — den visas som förut.
        /// Delas av veckan och påminnelsemejlet så att de aldrig menar olika familjer.
        /// </summary>
        public const string WeekFamilySql =
            @"( sa.CompetitionId IS NULL
                OR EXISTS (SELECT 1 FROM Planning f WHERE f.FamilyRootId = p.FamilyRootId AND f.Id <> p.Id AND f.DeletedAt IS NULL) )";

        /// <summary>
        /// Huvudplaneringarna där medlemmen har ett uppdrag och veckan tillför något (<see cref="WeekFamilySql"/>). Avböjda
        /// uppdrag räknas inte. En fråga.
        /// </summary>
        public List<int> RootsFor(int memberId, DateTime? from = null, DateTime? to = null)
        {
            if (memberId <= 0) return new();
            try
            {
                using var s = _scopes.CreateScope(autoComplete: true);
                // Med ett fönster byggs bara familjer där personen har ett uppdrag i fönstret — annars byggdes varje familj hen
                // någonsin haft ett uppdrag i om vid varje startsidesvisning (steg 4–6 C6). Dagen räknas som överallt annars.
                return s.Database.Fetch<int>(@"
                    SELECT DISTINCT p.FamilyRootId
                      FROM StaffAssignment sa
                      JOIN Planning p ON p.Id = sa.PlanningId AND p.DeletedAt IS NULL
                      LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
                     WHERE sa.MemberId = @0 AND sa.PlanningId IS NOT NULL
                       AND (sa.Status IS NULL OR sa.Status <> @1)
                       AND (@2 IS NULL OR COALESCE(CAST(sa.StartsAt AS date), CAST(sa.DayDate AS date), CAST(sp.PassDate AS date)) >= @2)
                       AND (@3 IS NULL OR COALESCE(CAST(sa.StartsAt AS date), CAST(sa.DayDate AS date), CAST(sp.PassDate AS date)) <= @3)
                       AND " + WeekFamilySql,
                    memberId, StaffAssignmentStatus.Declined, from?.Date, to?.Date);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyWeekService: familjerna för medlem {Member} gick inte att läsa.", memberId);
                return new();
            }
        }

        /// <summary>Medlemmens vecka i en familj, eller null när familjen inte finns eller medlemmen inte har något i den.</summary>
        public MySchedule? Get(int memberId, int rootId, DateTime? from = null, DateTime? to = null)
        {
            try { return GetOrThrow(memberId, rootId, from, to); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyWeekService: veckan för medlem {Member} i familj {Root} gick inte att bygga.", memberId, rootId);
                return null;
            }
        }

        /// <summary>
        /// Som <see cref="Get"/> men ett fel kastas. Veckomejlet måste skilja "ingenting den dagen" (null) från "det gick inte att läsa"
        /// (undantag): det senare får inte tolkas som att inget ska skickas och tyst förlora mejlet (steg 4–6 C8).
        /// </summary>
        public MySchedule? GetOrThrow(int memberId, int rootId, DateTime? from = null, DateTime? to = null)
        {
            if (memberId <= 0 || rootId <= 0) return null;
            var family = _planning.Family(rootId);
            if (family.Count == 0) return null;
            var me = _week.Build(rootId, StaffPersonKey.For(memberId, null)).FirstOrDefault();
            if (me == null) return null;
            var s = ToSchedule(rootId, family[0].Name, me.Days, from, to, DateTime.Now);
            if (!s.HasAny) return null;
            if (s.ConflictCount > 0 && ContactFor(rootId) is { } c) { s.ContactName = c.Name; s.ContactPhone = c.Phone; }   // ringer man först när något krockar
            return s;
        }

        /// <summary>Alla medlemmens veckor i fönstret, tidigast först.</summary>
        public List<MySchedule> ForMember(int memberId, DateTime from, DateTime to) =>
            RootsFor(memberId, from, to)
                .Select(r => Get(memberId, r, from, to))
                .Where(s => s != null)
                .Select(s => s!)
                .OrderBy(s => s.CompDate ?? DateTime.MaxValue)
                .ThenBy(s => s.CompName, StringComparer.Create(Sv, false))
                .ToList();

        /// <summary>
        /// Ren funktion: veckans dagar → en <see cref="MySchedule"/>. En rad får bara ett klockslag (<see cref="ScheduleItem.StartsAt"/>)
        /// när uppgifterna pekar ut ett — heldag och okänd tid hittas aldrig på, så kalenderfilen och påminnelserna hoppar över dem
        /// (samma regel som i <see cref="MyScheduleService"/>).
        /// </summary>
        public static MySchedule ToSchedule(int rootId, string name, IEnumerable<PersonWeekDay> days, DateTime? from, DateTime? to, DateTime now)
        {
            var s = new MySchedule { PlanningId = rootId, CompName = name, IsFunctionary = true };
            foreach (var d in days)
            {
                if (from != null && d.Date.Date < from.Value.Date) continue;
                if (to != null && d.Date.Date > to.Value.Date) continue;
                if (d.Items.Count == 0) continue;
                var label = d.Date.ToString("dddd d MMMM", Sv);
                var day = new ScheduleDay
                {
                    DayKey = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DayLabel = char.ToUpper(label[0], Sv) + label[1..],
                    Date = d.Date.Date,
                    IsToday = d.Date.Date == now.Date,
                };
                foreach (var it in d.Items)
                {
                    day.Items.Add(ToItem(it, day));
                    if (it.Entry.CompetitionId is > 0) s.CompetitionIds.Add(it.Entry.CompetitionId.Value);
                }
                s.Days.Add(day);
            }
            if (s.Days.Count > 0)
            {
                s.CompDate = s.Days[0].Date;
                s.CompEndDate = s.Days[^1].Date;
                s.NextItem = s.Days.SelectMany(x => x.Items).FirstOrDefault(i => i.StartsAt != null && i.StartsAt > now);
            }
            return s;
        }

        private static ScheduleItem ToItem(PersonWeekItem it, ScheduleDay day)
        {
            var e = it.Entry;
            var iv = e.Interval;
            var isAssignment = e.Kind == PersonWeekKind.Assignment;
            var invited = isAssignment && string.Equals(e.Status, StaffAssignmentStatus.Invited, StringComparison.OrdinalIgnoreCase);

            string timeLabel;
            if (iv is { AllDay: true }) timeLabel = "Heldag";
            else if (iv is { } v) timeLabel = $"{Hm(v.Start)}–{Hm(v.End)}" + (v.CrossesMidnight ? " (till nästa dag)" : "");
            else timeLabel = e.Kind == PersonWeekKind.Intent ? "Under dagen" : "Tid ej satt";

            var where = string.Join(" · ", new[] { e.Part, e.Place }.Where(x => !string.IsNullOrWhiteSpace(x)));
            string? link = null, linkText = null;
            if (isAssignment)
            {
                link = e.CompetitionId is > 0 ? $"/bemanna?c={e.CompetitionId}" : e.PlanningId is > 0 ? $"/bemanna?p={e.PlanningId}" : null;
                linkText = invited ? "Svara på inbjudan" : "Uppdraget";
            }
            else if (e.Kind == PersonWeekKind.Start && e.CompetitionId is > 0)
            {
                link = $"/startlista/{e.CompetitionId}"; linkText = "Startlistan";
            }

            var item = new ScheduleItem
            {
                Kind = isAssignment ? ScheduleItemKind.Funktionar : ScheduleItemKind.Skytte,
                Title = isAssignment ? (e.Title ?? e.Label) : e.Label,
                Where = string.IsNullOrEmpty(where) ? null : where,
                // Heldag och okänd tid har inget klockslag — ingen påminnelse, ingen kalenderpost.
                StartsAt = iv is { AllDay: false } a ? a.Start : null,
                EndsAt = iv is { AllDay: false } b ? b.End : null,
                TimeLabel = timeLabel,
                DayKey = day.DayKey,
                DayLabel = day.DayLabel,
                Status = e.Status,
                NeedsResponse = invited,
                SortHint = iv is { } w ? (int)w.Start.TimeOfDay.TotalMinutes : 0,
                Link = link,
                LinkText = linkText,
                Icon = e.Kind switch { PersonWeekKind.Assignment => "bi-person-badge", PersonWeekKind.Start => "bi-bullseye", _ => "bi-flag" },
                SourceKey = e.Kind == PersonWeekKind.Assignment && e.SourceKey.StartsWith('a') ? "uppdrag-" + e.SourceKey[1..] : e.SourceKey,
                AssignmentId = isAssignment && e.SourceKey.StartsWith('a') && int.TryParse(e.SourceKey.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var aid) ? aid : 0,
            };
            foreach (var c in it.Clashes.Where(c => c.IsHard)) item.ConflictsWith.Add(c.WithLabel);
            var warn = it.Clashes.Where(c => !c.IsHard)
                .Select(c => c.Kind == ClashKind.TightTravel
                    ? $"{(c.GapMinutes is { } g ? $"bara {g} min mellan platserna" : "kort tid mellan platserna")} till {c.WithLabel}"
                    : $"samma dag som {c.WithLabel}")
                .ToList();
            if (warn.Count > 0) item.WarningText = string.Join("; ", warn);
            return item;
        }

        private static string Hm(DateTime d) => d.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
