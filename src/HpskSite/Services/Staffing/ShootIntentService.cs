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
    /// Omgång 3 steg 9 (Stefan 2026-10-24): "de som anmäler sig som funktionärer meddelar vilka tävlingar de tänker anmäla sig
    /// till, så kan hänsyn tas till det i planeringen långt innan anmälningstiden öppnar". Avsikterna per familj (veckan), och
    /// krockarna över hela familjen: uppdrag mot uppdrag i en annan planering, mot egna starter och mot avsikter
    /// (<see cref="FamilyClashRules"/>).
    /// </summary>
    public class ShootIntentService
    {
        private readonly IScopeProvider _scopes;
        private readonly PlanningService _planning;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly MyScheduleService _schedule;
        private readonly StaffOwnerResolver _owners;
        private readonly RoleCatalogService _roles;
        private readonly ILogger<ShootIntentService> _logger;

        /// <summary>En start utan sluttid antas ta så här lång tid (fältets rundor saknar sluttid i schemat).</summary>
        public static readonly TimeSpan EstimatedStart = TimeSpan.FromHours(2);

        public ShootIntentService(IScopeProvider scopes, PlanningService planning, HpskSite.Services.Competitions.ICompetitionStore competitions,
            MyScheduleService schedule, StaffOwnerResolver owners, RoleCatalogService roles, ILogger<ShootIntentService> logger)
        {
            _scopes = scopes; _planning = planning; _competitions = competitions; _schedule = schedule; _owners = owners; _roles = roles; _logger = logger;
        }

        /// <summary>En tävling i veckan: en planering i familjen, med den kopplade tävlingens namn och datum när den finns.</summary>
        public sealed record Target(int PlanningId, string Name, string PlanningName, int CompetitionId, DateTime? From, DateTime? To);

        /// <summary>
        /// Veckans tävlingar. En planering i familjen är en tävling när den är kopplad till en, när den är en delplanering
        /// MED EN GREN, eller när den står ensam. En okopplad huvudplanering med delplaneringar är veckan själv — ingen
        /// tävling. Stefan 2026-10-25 (Expertgruppen): en delplanering utan gren och utan tävling är en del av arbetet
        /// ("Servering", en förening som sköter något) — ingen tävling i veckoprogrammet.
        /// </summary>
        public List<Target> Targets(int planningId)
        {
            var family = _planning.Family(planningId);
            if (family.Count == 0) return new();
            var links = _planning.FamilyLinks(family);
            var root = family[0];
            var hasChildren = family.Count > 1;
            var result = new List<Target>();
            foreach (var f in family)
            {
                var linked = links.TryGetValue(f.Id, out var cid);
                if (f.Id == root.Id && hasChildren && !linked) continue;
                if (f.Id != root.Id && !linked && string.IsNullOrWhiteSpace(f.DisciplineKey)) continue;
                var comp = linked ? _competitions.GetSaved(cid) : null;
                var from = comp?.CompetitionDate ?? f.TargetStart;
                var to = comp?.CompetitionEndDate ?? f.TargetEnd ?? from;
                result.Add(new Target(f.Id, comp?.DisplayName ?? f.Name, f.Name, comp?.Id ?? 0, from?.Date, to?.Date));
            }
            return result.OrderBy(t => t.From ?? DateTime.MaxValue).ThenBy(t => t.Name).ToList();
        }

        private int Root(int planningId) => _planning.Family(planningId).FirstOrDefault()?.Id ?? 0;

        public List<PlanningShootIntent> ForFamily(int planningId)
        {
            var root = Root(planningId);
            if (root <= 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningShootIntent>("SELECT * FROM PlanningShootIntent WHERE RootPlanningId = @0 ORDER BY DisplayName", root);
        }

        /// <summary>
        /// Skriver om en persons avsikter i familjen (alla veckans tävlingar på en gång). <paramref name="personKey"/> är
        /// bemanningens nyckel ("m:{id}" eller "n:{namn}"). Bara veckans tävlingar godtas.
        /// </summary>
        public string? Save(int planningId, string personKey, string displayName, IEnumerable<(int TargetPlanningId, string? Classes)> picks,
            string source, int byMemberId)
        {
            var root = Root(planningId);
            if (root <= 0) return "Planeringen finns inte.";
            int? memberId = null;
            if (personKey.StartsWith("m:") && int.TryParse(personKey[2..], out var mid) && mid > 0) memberId = mid;
            else if (!personKey.StartsWith("n:")) return "Personen gick inte att läsa.";
            var name = (displayName ?? "").Trim();
            if (name.Length == 0) return "Personen saknar namn.";
            if (name.Length > 200) name = name[..200];
            var valid = Targets(planningId).Select(t => t.PlanningId).ToHashSet();
            var list = picks.GroupBy(p => p.TargetPlanningId).Select(g => g.Last()).ToList();
            if (list.Any(p => !valid.Contains(p.TargetPlanningId))) return "En av tävlingarna hör inte till veckan.";
            var now = DateTime.Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var existing = db.Fetch<PlanningShootIntent>("SELECT * FROM PlanningShootIntent WHERE RootPlanningId = @0", root)
                .Where(i => i.PersonKey == (memberId is int m ? "m:" + m : "n:" + name.ToLowerInvariant())).ToList();
            foreach (var e in existing.Where(e => !list.Any(p => p.TargetPlanningId == e.TargetPlanningId)))
                db.Execute("DELETE FROM PlanningShootIntent WHERE Id = @0", e.Id);
            foreach (var (target, classesRaw) in list)
            {
                var classes = string.IsNullOrWhiteSpace(classesRaw) ? null : classesRaw.Trim();
                if (classes?.Length > 200) classes = classes[..200];
                var row = existing.FirstOrDefault(e => e.TargetPlanningId == target);
                if (row == null)
                    db.Insert(new PlanningShootIntent
                    {
                        RootPlanningId = root, TargetPlanningId = target, MemberId = memberId, DisplayName = name, Classes = classes,
                        Source = source, CreatedAt = now, UpdatedAt = now, UpdatedBy = byMemberId,
                    });
                else if (row.Classes != classes || row.DisplayName != name)
                    db.Execute("UPDATE PlanningShootIntent SET Classes = @0, DisplayName = @1, Source = @2, UpdatedAt = @3, UpdatedBy = @4 WHERE Id = @5",
                        classes, name, source, now, byMemberId, row.Id);
            }
            return null;
        }

        /// <summary>Krockarna över hela familjen (veckan) — se <see cref="FamilyClashRules"/>.</summary>
        public List<FamilyClashRules.Clash> Clashes(int planningId)
        {
            var family = _planning.Family(planningId);
            if (family.Count == 0) return new();
            var ids = string.Join(",", family.Select(f => f.Id));
            var names = family.ToDictionary(f => f.Id, f => f.Name);
            List<StaffAssignment> rows; List<StaffPass> passes; List<PlanningShootIntent> intents;
            using (var s = _scopes.CreateScope(autoComplete: true))
            {
                var db = s.Database;
                rows = db.Fetch<StaffAssignment>($"SELECT * FROM StaffAssignment WHERE PlanningId IN ({ids})");
                passes = db.Fetch<StaffPass>($"SELECT * FROM StaffPass WHERE PlanningId IN ({ids})");
                intents = db.Fetch<PlanningShootIntent>("SELECT * FROM PlanningShootIntent WHERE RootPlanningId = @0", family[0].Id);
            }
            var passById = passes.ToDictionary(p => p.Id);
            var roleNames = new Dictionary<int, Func<string, string>>();
            Func<string, string> RoleNameFor(int pid)
            {
                if (roleNames.TryGetValue(pid, out var f)) return f;
                var owner = _owners.ForPlanning(pid);
                var disc = owner == null ? null : _owners.Discipline(owner);
                f = key => owner == null ? key : _roles.NameFor(owner, disc, key);
                return roleNames[pid] = f;
            }

            var slots = new List<FamilyClashRules.Slot>();
            foreach (var a in rows.Where(a => a.PlanningId is int && !string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase)
                                             && !string.IsNullOrWhiteSpace(a.DisplayName)))
            {
                var pid = a.PlanningId!.Value;
                StaffPass? pass = a.PassId is int ap && passById.TryGetValue(ap, out var pp) ? pp : null;
                var date = a.StartsAt?.Date ?? a.DayDate?.Date ?? pass?.PassDate.Date;
                if (date == null) continue;
                (TimeSpan, TimeSpan)? span = a.StartsAt is DateTime st && a.EndsAt is DateTime en && en > st ? (st.TimeOfDay, en.TimeOfDay)
                    : pass != null ? StaffTimeCoverage.ParseRange(string.IsNullOrEmpty(pass.StartTime) || string.IsNullOrEmpty(pass.EndTime) ? null : pass.StartTime + "-" + pass.EndTime)
                    : null;
                var time = span is { } sp ? $" {sp.Item1:hh\\:mm}–{sp.Item2:hh\\:mm}" : " hela dagen";
                var key = a.MemberId is int m && m > 0 ? "m:" + m : "n:" + a.DisplayName.Trim().ToLowerInvariant();
                slots.Add(new FamilyClashRules.Slot(key, a.DisplayName, pid, date.Value, span,
                    $"{RoleNameFor(pid)(a.RoleKey)}{time} ({names.GetValueOrDefault(pid, "")})"));
            }

            var targets = Targets(planningId);
            var starts = new List<FamilyClashRules.Start>();
            foreach (var t in targets.Where(t => t.CompetitionId > 0 && t.From != null))
            {
                // Bara medlemmar som har ett uppdrag under tävlingens dagar — schemat läses per medlem.
                var members = slots.Where(sl => sl.PersonKey.StartsWith("m:") && sl.Date >= t.From!.Value && sl.Date <= (t.To ?? t.From).Value)
                    .Select(sl => int.Parse(sl.PersonKey[2..], CultureInfo.InvariantCulture)).Distinct();
                foreach (var mid in members)
                {
                    try
                    {
                        foreach (var item in _schedule.GetSchedule(mid, t.CompetitionId).Days.SelectMany(d => d.Items)
                                     .Where(i => i.Kind == ScheduleItemKind.Skytte && i.StartsAt != null))
                        {
                            var st = item.StartsAt!.Value;
                            var en = item.EndsAt is DateTime e && e > st ? e : st + EstimatedStart;
                            var guessed = item.EndsAt == null || item.EndsAt <= st;
                            starts.Add(new FamilyClashRules.Start("m:" + mid, t.CompetitionId, st.Date, (st.TimeOfDay, en.TimeOfDay),
                                $"egen start i {t.Name}: {item.Title} {st:HH\\:mm}{(guessed ? " (ca " + (int)EstimatedStart.TotalHours + " h)" : "–" + en.ToString("HH:mm"))}"));
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Krockar: schemat för medlem {Member} i tävling {Comp} gick inte att läsa.", mid, t.CompetitionId); }
                }
            }

            var byTarget = targets.ToDictionary(t => t.PlanningId);
            var intentRecords = intents.Where(i => byTarget.TryGetValue(i.TargetPlanningId, out var t) && t.From != null)
                .Select(i =>
                {
                    var t = byTarget[i.TargetPlanningId];
                    return new FamilyClashRules.Intent(i.PersonKey, i.DisplayName, t.CompetitionId, t.From!.Value, (t.To ?? t.From).Value,
                        $"tänker tävla i {t.Name}{(string.IsNullOrEmpty(i.Classes) ? "" : " (" + i.Classes + ")")}");
                });
            return FamilyClashRules.Find(slots, starts, intentRecords);
        }
    }
}
