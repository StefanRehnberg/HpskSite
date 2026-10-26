using System.Globalization;
using HpskSite.Models.Planning;
using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using HpskSite.Services.Schedule;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Steg 3 (Expertgruppen D3): <b>personens vecka över hela familjen</b> — dag för dag alla uppdrag i alla delar, egna starter
    /// och avsikter att tävla, med krockarna markerade. Läser samma underlag som <see cref="ShootIntentService.Clashes"/> men
    /// bygger <see cref="PersonWeek"/> (tidsintervallet med heldag, midnatt och platsbyte), så att personvyn, Mitt schema och
    /// påminnelserna får en och samma form. Ren läsning — ingenting skrivs.
    ///
    /// <para><b>Starter kostar.</b> En start läses ur Mitt schema per medlem och tävling. Listan över alla personer räknas därför
    /// utan starter (uppdrag mot uppdrag och avsikter); en enskild persons vecka tar med starterna.</para>
    /// </summary>
    public class FamilyWeekService
    {
        private readonly IScopeProvider _scopes;
        private readonly PlanningService _planning;
        private readonly ShootIntentService _intents;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly StartWindowReader _starts;
        private readonly MyScheduleService _schedule;
        private readonly StaffOwnerResolver _owners;
        private readonly RoleCatalogService _roles;
        private readonly ILogger<FamilyWeekService> _logger;

        public FamilyWeekService(IScopeProvider scopes, PlanningService planning, ShootIntentService intents,
            HpskSite.Services.Competitions.ICompetitionStore competitions, StartWindowReader starts, MyScheduleService schedule,
            StaffOwnerResolver owners, RoleCatalogService roles, ILogger<FamilyWeekService> logger)
        {
            _scopes = scopes; _planning = planning; _intents = intents; _competitions = competitions; _starts = starts; _schedule = schedule;
            _owners = owners; _roles = roles; _logger = logger;
        }

        public sealed record Person(string Key, string Name, int MemberId, bool IsExternal, int Assignments, int Clashes, int Warnings,
            IReadOnlyList<PersonWeekDay> Days);

        /// <summary>
        /// Alla personer med uppdrag eller avsikt i familjen. <paramref name="onlyKey"/> = en persons vecka, med starterna.
        /// <paramref name="withStarts"/> = starterna räknas med för alla i listan. <paramref name="includeUnpublishedStarts"/> = planeraren ser
        /// även en startlista som ännu inte publicerats (aldrig sant för medlemmens egen vecka).
        /// </summary>
        public List<Person> Build(int planningId, string? onlyKey = null, bool withStarts = false, bool includeUnpublishedStarts = false)
        {
            var family = _planning.Family(planningId);
            if (family.Count == 0) return new();
            var ids = string.Join(",", family.Select(f => f.Id));
            var partName = family.ToDictionary(f => f.Id, f => f.Name);
            List<StaffAssignment> rows; List<StaffPass> passes; List<PlanningShootIntent> intents;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                rows = db.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE PlanningId IN ({ids})");
                passes = db.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({ids})");
                intents = _intents.ForFamily(family[0].Id);
            }
            var passById = passes.ToDictionary(p => p.Id);
            var venues = new Dictionary<int, string?>();
            string? Venue(int? competitionId)
            {
                if (competitionId is not > 0) return null;
                if (venues.TryGetValue(competitionId.Value, out var v)) return v;
                try { v = _competitions.GetSaved(competitionId.Value)?.Venue; } catch { v = null; }
                return venues[competitionId.Value] = string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            var roleName = new Dictionary<int, Func<string, string>>();
            Func<string, string> RoleNameFor(int pid)
            {
                if (roleName.TryGetValue(pid, out var f)) return f;
                var owner = _owners.ForPlanning(pid);
                var disc = owner == null ? null : _owners.Discipline(owner);
                return roleName[pid] = key => owner == null ? key : _roles.NameFor(owner, disc, key);
            }

            var entries = new Dictionary<string, List<PersonWeekEntry>>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var memberOf = new Dictionary<string, int>(StringComparer.Ordinal);
            List<PersonWeekEntry> For(string key, string name, int? memberId)
            {
                if (!entries.TryGetValue(key, out var l)) { entries[key] = l = new(); names[key] = name; memberOf[key] = memberId ?? 0; }
                else if (name.Length > names[key].Length) names[key] = name;
                return l;
            }

            foreach (var a in rows.Where(a => a.PlanningId is > 0
                                             && !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase)
                                             && !string.IsNullOrWhiteSpace(a.DisplayName)))
            {
                var key = StaffPersonKey.For(a.MemberId, a.DisplayName);
                if (onlyKey != null && key != onlyKey) continue;
                var pid = a.PlanningId!.Value;
                StaffPass? pass = a.PassId is int ap && passById.TryGetValue(ap, out var pp) ? pp : null;
                var (date, interval) = WhenOf(a, pass);
                if (date == null) continue;
                var time = interval is { AllDay: false } iv ? $" {iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : (interval is { AllDay: true } ? " hela dagen" : "");
                For(key, a.DisplayName.Trim(), a.MemberId).Add(new PersonWeekEntry(
                    "a" + a.Id, PersonWeekKind.Assignment, date.Value, interval, Venue(a.CompetitionId),
                    $"{RoleNameFor(pid)(a.RoleKey)}{time} ({partName.GetValueOrDefault(pid, "")})", pid, a.CompetitionId,
                    RoleNameFor(pid)(a.RoleKey), partName.GetValueOrDefault(pid), a.Status));
            }

            var targets = _intents.Targets(planningId);
            var byTarget = targets.ToDictionary(t => t.PlanningId);
            foreach (var i in intents)
            {
                if (onlyKey != null && i.PersonKey != onlyKey) continue;
                if (!byTarget.TryGetValue(i.TargetPlanningId, out var t) || t.From == null) continue;
                var label = $"tänker tävla i {t.Name}{(string.IsNullOrEmpty(i.Classes) ? "" : " (" + i.Classes + ")")}";
                var list = For(i.PersonKey, i.DisplayName, i.MemberId);
                for (var d = t.From.Value.Date; d <= (t.To ?? t.From).Value.Date; d = d.AddDays(1))
                    list.Add(new PersonWeekEntry($"i{i.Id}:{d:yyyyMMdd}", PersonWeekKind.Intent, d, null, null, label, null, t.CompetitionId > 0 ? t.CompetitionId : null));
            }

            // Egna starter: för en enskild medlem, eller — med withStarts — för alla i listan. Planera dagen (Expertgruppen K2): starterna
            // läses med EN läsning per tävling (StartWindowReader) i stället för en schemaläsning per medlem, så det finns inget tak.
            // Publicerat bara, om inte planeraren ber om mer: Mitt schema ska aldrig visa en startlista som inte släppts.
            var startKeys = onlyKey != null ? (entries.ContainsKey(onlyKey) ? new List<string> { onlyKey } : new List<string>())
                : withStarts ? entries.Keys.ToList() : new List<string>();
            startKeys = startKeys.Where(k => StaffPersonKey.MemberIdOf(k) > 0).ToList();
            if (startKeys.Count > 0)
                foreach (var t in targets.Where(t => t.CompetitionId > 0 && t.From != null))
                {
                    try
                    {
                        // Planera dagen steg 0: skjuttiden hör till grenen (och planeringen), inte en konstant på 2 h.
                        var shoot = _owners.ShootTime(t.PlanningId);
                        if (!_starts.Covers(t.CompetitionId))
                        {
                            // Grenar utanför verktyget (springskytte): som förut, en schemaläsning per medlem — bara det publicerade.
                            foreach (var startKey in startKeys)
                            {
                                var mid = StaffPersonKey.MemberIdOf(startKey);
                                foreach (var item in _schedule.GetSchedule(mid, t.CompetitionId).Days.SelectMany(d => d.Items)
                                             .Where(i => i.Kind == ScheduleItemKind.Skytte && i.StartsAt != null))
                                {
                                    var st = item.StartsAt!.Value;
                                    var guessed = item.EndsAt == null || item.EndsAt <= st;
                                    var en = guessed ? st + shoot.Duration : item.EndsAt!.Value;
                                    entries[startKey].Add(new PersonWeekEntry(
                                        $"s:{t.CompetitionId}:{item.SourceKey}", PersonWeekKind.Start, st.Date, shoot.Window(st, item.EndsAt), Venue(t.CompetitionId),
                                        $"egen start i {t.Name}: {item.Title} {st:HH\\:mm}{(guessed ? " (ca " + ShootTime.Text(shoot.DurationMinutes) + ")" : "–" + en.ToString("HH:mm"))}",
                                        null, t.CompetitionId));
                                }
                            }
                            continue;
                        }
                        var byMember = _starts.ForCompetition(t.CompetitionId, includeUnpublishedStarts).GroupBy(w => w.MemberId).ToDictionary(g => g.Key, g => g.ToList());
                        foreach (var startKey in startKeys)
                            foreach (var w in byMember.GetValueOrDefault(StaffPersonKey.MemberIdOf(startKey)) ?? new List<StartWindow>())
                            {
                                var guessed = w.End == null;
                                var en = w.End ?? w.Start + shoot.Duration;
                                entries[startKey].Add(new PersonWeekEntry(
                                    $"s:{t.CompetitionId}:{w.SourceKey}", PersonWeekKind.Start, w.Start.Date, shoot.Window(w.Start, w.End), Venue(t.CompetitionId),
                                    $"egen start i {t.Name}: {w.Title} {w.Start:HH\\:mm}{(guessed ? " (ca " + ShootTime.Text(shoot.DurationMinutes) + ")" : "–" + en.ToString("HH:mm"))}",
                                    null, t.CompetitionId));
                            }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Veckan: starterna i tävling {Comp} gick inte att läsa.", t.CompetitionId); }
                }

            var sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true);
            var result = new List<Person>();
            foreach (var (key, list) in entries)
            {
                if (list.Count == 0) continue;
                var from = list.Min(e => e.Date); var to = list.Max(e => e.Date);
                var days = PersonWeek.Build(list, from, to);
                var items = days.SelectMany(d => d.Items).ToList();
                result.Add(new Person(key, names[key], memberOf[key], memberOf[key] <= 0,
                    list.Count(e => e.Kind == PersonWeekKind.Assignment),
                    items.Count(i => i.HasClash), items.Count(i => i.HasWarning && !i.HasClash), days));
            }
            return result.OrderByDescending(p => p.Clashes).ThenBy(p => p.Name, sv).ToList();
        }

        /// <summary>
        /// Dagen och tidsintervallet för ett uppdrag — en enda regel för veckan och för ersättarsökningen (steg 6b): starttid → dag →
        /// passets dag; bara ena änden angiven = okänt intervall (null); varken start eller slut = heldag.
        /// </summary>
        public static (DateTime? Date, StaffInterval? Interval) WhenOf(StaffAssignment a, StaffPass? pass)
        {
            var date = a.StartsAt?.Date ?? a.DayDate?.Date ?? pass?.PassDate.Date;
            if (date == null) return (null, null);
            StaffInterval? interval;
            if (a.StartsAt != null)
                interval = StaffInterval.FromMoments(a.StartsAt, a.EndsAt);              // bara ena änden = okänt
            else if (pass != null && StaffTimeCoverage.ParseRange(PassRange(pass)) is { } r)
                interval = StaffInterval.From(date.Value, r.Start, r.End);
            else
                interval = StaffInterval.From(date.Value, null, null);                    // heldag
            return (date, interval);
        }

        private static string? PassRange(StaffPass p) =>
            string.IsNullOrEmpty(p.StartTime) || string.IsNullOrEmpty(p.EndTime) ? null : p.StartTime + "-" + p.EndTime;
    }
}
