using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;
using HpskSite.Models;

namespace HpskSite.Services
{
    /// <summary>
    /// Builds and persists the auto-generated start list for direktplacering / Egenbokning
    /// competitions. Extracted from CompetitionController so other controllers (RegistrationAdmin
    /// late registrations / edits, future bulk imports) can keep the start list in sync without
    /// duplicating the rendering logic.
    /// </summary>
    public class DirektplaceringStartListService
    {
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly ILogger<DirektplaceringStartListService> _logger;

        public DirektplaceringStartListService(
            IContentService contentService,
            ClubService clubService,
            ILogger<DirektplaceringStartListService> logger)
        {
            _contentService = contentService;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>
        /// Recomputes the precisionStartList document under the competition based on the
        /// current set of registrations. Best-effort — failures are logged but do not throw,
        /// because the calling write (registration save) has already succeeded.
        /// </summary>
        public void Regenerate(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return;

                var dpConfig = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"));
                if (dpConfig == null) return;

                Regenerate(competitionId, competition, dpConfig);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to regenerate direktplacering start list for {CompId}", competitionId);
            }
        }

        public void Regenerate(int competitionId, IContent competition, DirektplaceringConfig dpConfig)
        {
            var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub"
                || c.Name.Contains("Anmälningar")
                || c.Name.Contains("Registration"));

            var registrationDocs = registrationsHub != null
                ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration").ToList()
                : new List<IContent>();

            var shootersByTeam = new Dictionary<int, List<ShooterRow>>();
            foreach (var team in dpConfig.Teams)
                shootersByTeam[team.TeamNumber] = new List<ShooterRow>();

            // The current list's order is kept for everyone who already stands in the same
            // skjutlag: a position is a firing point, and regenerating must not move shooters to
            // another lane because someone else registered or withdrew. Newcomers are appended in
            // registration order. This also carries over an order made by the ordinary generator
            // when Egenbokning is switched on afterwards.
            var existingPlacements = ReadExistingPlacements(
                competitionChildren.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList"));

            // Process registrations in the order they were created so that the position a
            // shooter picked at registration is preserved (first-come, first-served within a
            // team). Sorting by name here would re-shuffle everyone whenever a new shooter
            // with an alphabetically-earlier name registers — the Egenbokning bug.
            registrationDocs = registrationDocs.OrderBy(c => c.CreateDate).ThenBy(c => c.Id).ToList();
            var sortSeq = 0;

            foreach (var reg in registrationDocs)
            {
                var classesJson = reg.GetValue<string>("shootingClasses");
                if (string.IsNullOrWhiteSpace(classesJson)) continue;
                var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(classesJson);
                var memberName = reg.GetValue<string>("memberName") ?? "Okänd";
                var memberId = reg.GetValue<int>("memberId");
                var clubName = "Okänd förening";
                var clubId = reg.GetValue<int>("clubId");
                if (clubId > 0) clubName = _clubService.GetClubNameById(clubId) ?? "Okänd förening";

                foreach (var entry in classes)
                {
                    if (!entry.TeamNumber.HasValue) continue;
                    if (!shootersByTeam.ContainsKey(entry.TeamNumber.Value))
                        shootersByTeam[entry.TeamNumber.Value] = new List<ShooterRow>();
                    var kept = existingPlacements.TryGetValue(PlacementKey(memberId, entry.Class), out var prev)
                               && prev.TeamNumber == entry.TeamNumber.Value
                        ? prev.Position
                        : int.MaxValue;
                    shootersByTeam[entry.TeamNumber.Value].Add(new ShooterRow
                    {
                        Name = memberName,
                        Club = clubName,
                        WeaponClass = entry.Class,
                        MemberId = memberId,
                        KeptPosition = kept,
                        SortOrder = sortSeq++
                    });
                }
            }

            var teams = dpConfig.Teams.Select(team =>
            {
                var shooters = shootersByTeam.TryGetValue(team.TeamNumber, out var s) ? s : new List<ShooterRow>();
                var pos = 0;
                return new
                {
                    TeamNumber = team.TeamNumber,
                    StartTime = team.StartTime,
                    EndTime = team.EndTime,
                    ShooterCount = shooters.Count,
                    WeaponClasses = shooters.Select(sh => sh.WeaponClass).Distinct().OrderBy(c => c).ToList(),
                    Shooters = shooters.OrderBy(sh => sh.KeptPosition).ThenBy(sh => sh.SortOrder).Select(sh =>
                    {
                        pos++;
                        return new { Position = pos, sh.Name, sh.Club, sh.WeaponClass, sh.MemberId };
                    }).ToList()
                };
            }).ToList();

            var config = new
            {
                Settings = new
                {
                    Format = dpConfig.AllowMixedClasses ? "Mixade Skjutlag" : "En vapengrupp per Skjutlag",
                    MaxShootersPerTeam = dpConfig.Teams.Any() ? dpConfig.Teams.Max(t => t.Positions) : 30,
                    FirstStartTime = dpConfig.Teams.FirstOrDefault()?.StartTime ?? "09:00",
                    Generated = DateTime.Now
                },
                Teams = teams
            };

            var configJson = System.Text.Json.JsonSerializer.Serialize(config);

            var existingStartList = competitionChildren.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");
            IContent startList = existingStartList ?? _contentService.Create("Startlista", competition.Id, "precisionStartList");

            var html = new System.Text.StringBuilder();
            html.AppendLine("<div class='start-list-content'>");
            html.AppendLine($"<h3 class='competition-title'>{System.Net.WebUtility.HtmlEncode(competition.Name ?? "")}</h3>");
            foreach (var team in teams)
            {
                var timeStr = !string.IsNullOrEmpty(team.EndTime) ? $"{team.StartTime}-{team.EndTime}" : team.StartTime;
                html.AppendLine($"<h3>Skjutlag: {team.TeamNumber} Tid (ca): {timeStr} ({team.ShooterCount} st)</h3>");
                html.AppendLine("<table class='table table-striped'>");
                html.AppendLine("<thead><tr><th>Plats</th><th>Namn</th><th>Förening</th><th>Vapengrupp</th></tr></thead>");
                html.AppendLine("<tbody>");
                foreach (var shooter in team.Shooters)
                {
                    html.AppendLine($"<tr><td>{shooter.Position}</td><td>{System.Net.WebUtility.HtmlEncode(shooter.Name)}</td><td>{System.Net.WebUtility.HtmlEncode(shooter.Club)}</td><td>{System.Net.WebUtility.HtmlEncode(HpskSite.Models.ShootingClasses.DisplayName(shooter.WeaponClass))}</td></tr>");
                }
                html.AppendLine("</tbody></table><br>");
            }
            html.AppendLine("</div>");

            startList.SetValue("competitionId", competitionId);
            startList.SetValue("teamFormat", dpConfig.AllowMixedClasses ? "Mixade Skjutlag" : "En vapengrupp per Skjutlag");
            startList.SetValue("generatedDate", DateTime.Now);
            startList.SetValue("generatedBy", "Egenbokning (auto)");
            startList.SetValue("notes", "Autogenererad vid anmälan");
            startList.SetValue("isOfficialStartList", true);
            startList.SetValue("configurationData", configJson);
            startList.SetValue("startListContent", html.ToString());

            try { _contentService.Save(startList); }
            catch (Exception ex) when (IsDocumentUrlTimeout(ex))
            {
                _logger.LogWarning("Start list saved but URL segment rebuild timed out (non-critical)");
            }

            try { _contentService.Publish(startList, new[] { "*" }, -1); }
            catch { /* publish is non-critical */ }

            _logger.LogInformation("Auto-updated Egenbokning start list for competition {CompId} with {Teams} teams",
                competitionId, teams.Count);
        }

        /// <summary>
        /// Compute remaining capacity per team across the competition's registrations. Used
        /// by walk-in / edit endpoints to refuse over-booking before the JSON is written.
        /// Optionally exclude one registration's contribution (when re-saving an existing
        /// registration, its old assignments shouldn't double-count against the new ones).
        /// </summary>
        public Dictionary<int, int> GetTeamUsage(int competitionId, int? excludeRegistrationId = null)
        {
            var usage = new Dictionary<int, int>();

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return usage;

            var competitionChildren = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            if (registrationsHub == null) return usage;

            var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration");

            foreach (var reg in registrations)
            {
                if (excludeRegistrationId.HasValue && reg.Id == excludeRegistrationId.Value) continue;
                var json = reg.GetValue<string>("shootingClasses");
                if (string.IsNullOrWhiteSpace(json)) continue;
                var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(json);
                foreach (var entry in classes)
                {
                    if (!entry.TeamNumber.HasValue) continue;
                    var t = entry.TeamNumber.Value;
                    usage[t] = usage.GetValueOrDefault(t) + 1;
                }
            }

            return usage;
        }

        private sealed class ShooterRow
        {
            public string Name { get; set; } = "";
            public string Club { get; set; } = "";
            public string WeaponClass { get; set; } = "";
            public int MemberId { get; set; }
            public int KeptPosition { get; set; } = int.MaxValue;
            public int SortOrder { get; set; }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────
        // Switching Egenbokning ON after registrations (or a start list) already exist.
        //
        // Egenbokning keeps the skjutlag ON THE REGISTRATION (ShootingClassEntry.TeamNumber) and
        // derives both the availability counter and the list from it. A registration made before
        // the switch has no TeamNumber, so it was invisible to both: the overview said 0/30 while
        // an ordinary list showed five shooters (competition 3468, 2026-09-30), new bookers could
        // be sold the same places, and the next regeneration would have dropped everyone who had
        // not booked. Plan() says what will happen, Apply() does it — the organiser confirms in
        // between.
        // ─────────────────────────────────────────────────────────────────────────────────────

        public sealed class AssignmentRow
        {
            public int RegistrationId { get; set; }
            public string MemberName { get; set; } = "";
            public string ShootingClass { get; set; } = "";
            /// <summary>Null = no skjutlag fits; the class stays without a place.</summary>
            public int? TeamNumber { get; set; }
            /// <summary>True when the shooter already stood in that skjutlag on the current list.</summary>
            public bool FromExistingList { get; set; }
        }

        public sealed class AssignmentPlan
        {
            public List<AssignmentRow> Rows { get; set; } = new();
            public bool HasStartList { get; set; }
            /// <summary>The current list was made by the ordinary generator, not by Egenbokning.</summary>
            public bool StartListIsManual { get; set; }
            public bool StartListIsPublished { get; set; }
            /// <summary>Rows on the current list that match no registration — they will disappear.</summary>
            public List<string> DroppedFromList { get; set; } = new();
            public bool NeedsConfirmation => Rows.Count > 0 || (StartListIsManual && HasStartList);
        }

        public AssignmentPlan Plan(IContent competition, DirektplaceringConfig dpConfig)
        {
            var plan = new AssignmentPlan();
            var children = _contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList();
            var startList = children.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");
            var existing = ReadExistingPlacements(startList);

            if (startList != null)
            {
                plan.HasStartList = existing.Count > 0;
                plan.StartListIsManual = !string.Equals(
                    startList.GetValue<string>("generatedBy"), "Egenbokning (auto)", StringComparison.Ordinal);
                plan.StartListIsPublished = startList.Published;
            }

            var registrations = RegistrationDocs(children);
            var teams = dpConfig.Teams.OrderBy(t => t.TeamNumber).ToList();
            var used = teams.ToDictionary(t => t.TeamNumber, _ => 0);
            var registeredKeys = new HashSet<string>();

            foreach (var reg in registrations)
            {
                var memberId = reg.GetValue<int>("memberId");
                foreach (var entry in CompetitionRegistrationDocument.DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? ""))
                {
                    registeredKeys.Add(PlacementKey(memberId, entry.Class));
                    if (entry.TeamNumber.HasValue && used.ContainsKey(entry.TeamNumber.Value))
                        used[entry.TeamNumber.Value]++;
                }
            }

            // Unbooked classes, in the order they should be seated: the current list's order
            // first (so nobody changes skjutlag or lane needlessly), then registration order.
            var pending = new List<(IContent Reg, ShootingClassEntry Entry, int MemberId, Placement? Existing, int Seq)>();
            var seq = 0;
            foreach (var reg in registrations)
            {
                var memberId = reg.GetValue<int>("memberId");
                foreach (var entry in CompetitionRegistrationDocument.DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? ""))
                {
                    if (entry.TeamNumber.HasValue) continue;
                    existing.TryGetValue(PlacementKey(memberId, entry.Class), out var ex);
                    pending.Add((reg, entry, memberId, ex, seq++));
                }
            }

            foreach (var p in pending
                .OrderBy(p => p.Existing?.TeamNumber ?? int.MaxValue)
                .ThenBy(p => p.Existing?.Position ?? int.MaxValue)
                .ThenBy(p => p.Seq))
            {
                // Two classes of one registration never share a skjutlag — the booking form
                // refuses that too.
                var taken = CompetitionRegistrationDocument.DeserializeShootingClasses(p.Reg.GetValue<string>("shootingClasses") ?? "")
                    .Where(c => c.TeamNumber.HasValue).Select(c => c.TeamNumber!.Value)
                    .Concat(plan.Rows.Where(r => r.RegistrationId == p.Reg.Id && r.TeamNumber.HasValue).Select(r => r.TeamNumber!.Value))
                    .ToHashSet();

                bool Fits(DirektplaceringTeam t) =>
                    used[t.TeamNumber] < t.Positions
                    && !taken.Contains(t.TeamNumber)
                    && ClassAllowed(dpConfig, t, p.Entry.Class);

                DirektplaceringTeam? chosen = null;
                var fromList = false;
                if (p.Existing != null)
                {
                    chosen = teams.FirstOrDefault(t => t.TeamNumber == p.Existing.TeamNumber && Fits(t));
                    fromList = chosen != null;
                }
                chosen ??= teams.FirstOrDefault(Fits);
                if (chosen != null) used[chosen.TeamNumber]++;

                plan.Rows.Add(new AssignmentRow
                {
                    RegistrationId = p.Reg.Id,
                    MemberName = p.Reg.GetValue<string>("memberName") ?? "Okänd",
                    ShootingClass = p.Entry.Class,
                    TeamNumber = chosen?.TeamNumber,
                    FromExistingList = fromList
                });
            }

            if (plan.StartListIsManual)
            {
                plan.DroppedFromList = existing
                    .Where(kv => !registeredKeys.Contains(kv.Key))
                    .Select(kv => kv.Value.Name)
                    .Distinct()
                    .ToList();
            }

            return plan;
        }

        /// <summary>
        /// Writes the planned skjutlag onto the registrations and rebuilds the list. Returns the
        /// plan that was applied so the caller can report it.
        /// </summary>
        public AssignmentPlan Apply(int competitionId)
        {
            var competition = _contentService.GetById(competitionId)
                ?? throw new InvalidOperationException("Tävlingen hittades inte.");
            var dpConfig = DirektplaceringConfig.Parse(competition.GetValue<string>("direktplaceringConfig"))
                ?? throw new InvalidOperationException("Egenbokning är inte påslagen.");

            var plan = Plan(competition, dpConfig);

            // ⚠️ Read the registrations through the paged query, NOT GetById: GetById can hand back
            // a cached instance whose version is no longer current (a registration touched by the
            // eager-invoice path moments earlier), and Save then throws "Cannot save a non-current
            // version" — measured in dev on the third of three registrations.
            var fresh = RegistrationDocs(_contentService.GetPagedChildren(competition.Id, 0, 100, out _).ToList())
                .ToDictionary(r => r.Id);

            foreach (var group in plan.Rows.Where(r => r.TeamNumber.HasValue).GroupBy(r => r.RegistrationId))
            {
                if (!fresh.TryGetValue(group.Key, out var reg)) continue;
                var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "");
                foreach (var row in group)
                {
                    var entry = classes.FirstOrDefault(c => !c.TeamNumber.HasValue
                        && ShootingClasses.NormalizeKey(c.Class) == ShootingClasses.NormalizeKey(row.ShootingClass));
                    if (entry != null) entry.TeamNumber = row.TeamNumber;
                }
                reg.SetValue("shootingClasses", CompetitionRegistrationDocument.SerializeShootingClasses(classes));
                try
                {
                    // Registrations are saved unpublished everywhere else too.
                    _contentService.Save(reg);
                }
                catch (Exception ex)
                {
                    // One failed registration must not leave the others half-done or skip the
                    // rebuild. It is reported as unplaced, which is the truth: it has no skjutlag.
                    _logger.LogError(ex, "Egenbokning: could not save skjutlag on registration {RegId}", reg.Id);
                    foreach (var row in group) row.TeamNumber = null;
                }
            }

            Regenerate(competitionId, competition, dpConfig);
            _logger.LogInformation("Egenbokning: assigned {Count} unbooked classes on competition {CompId} ({Unplaced} without a fitting skjutlag)",
                plan.Rows.Count(r => r.TeamNumber.HasValue), competitionId, plan.Rows.Count(r => !r.TeamNumber.HasValue));
            return plan;
        }

        private static bool ClassAllowed(DirektplaceringConfig cfg, DirektplaceringTeam team, string shootingClass)
        {
            // Mirrors the booking form: the restriction only applies when classes are not mixed.
            if (cfg.AllowMixedClasses || team.AllowedWeaponClasses == null || team.AllowedWeaponClasses.Count == 0)
                return true;
            var group = ShootingClasses.GetWeaponClassCode(shootingClass);
            return team.AllowedWeaponClasses.Any(a => string.Equals(a, group, StringComparison.OrdinalIgnoreCase));
        }

        private List<IContent> RegistrationDocs(List<IContent> competitionChildren)
        {
            var hub = competitionChildren.FirstOrDefault(c =>
                c.ContentType.Alias == "competitionRegistrationsHub"
                || c.Name.Contains("Anmälningar")
                || c.Name.Contains("Registration"));
            if (hub == null) return new List<IContent>();
            return _contentService.GetPagedChildren(hub.Id, 0, 1000, out _)
                .Where(c => c.ContentType.Alias == "competitionRegistration")
                .OrderBy(c => c.CreateDate).ThenBy(c => c.Id)
                .ToList();
        }

        private sealed class Placement
        {
            public int TeamNumber { get; set; }
            public int Position { get; set; }
            public string Name { get; set; } = "";
        }

        private static string PlacementKey(int memberId, string? shootingClass) =>
            $"{memberId}|{ShootingClasses.NormalizeKey(shootingClass)}";

        /// <summary>
        /// (member, class) → skjutlag + position on the current list. Reads both shapes — the
        /// ordinary generator's and Egenbokning's — through a case-insensitive JSON walk, since
        /// the two serialisers differ in casing.
        /// </summary>
        private Dictionary<string, Placement> ReadExistingPlacements(IContent? startList)
        {
            var result = new Dictionary<string, Placement>();
            var json = startList?.GetValue<string>("configurationData");
            if (string.IsNullOrWhiteSpace(json)) return result;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!TryGet(doc.RootElement, "teams", out var teams) || teams.ValueKind != System.Text.Json.JsonValueKind.Array)
                    return result;
                foreach (var team in teams.EnumerateArray())
                {
                    var teamNumber = TryGet(team, "teamNumber", out var tn) && tn.TryGetInt32(out var t) ? t : 0;
                    if (!TryGet(team, "shooters", out var shooters) || shooters.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                    foreach (var s in shooters.EnumerateArray())
                    {
                        var memberId = TryGet(s, "memberId", out var m) && m.TryGetInt32(out var mi) ? mi : 0;
                        if (memberId <= 0) continue;
                        var cls = TryGet(s, "weaponClass", out var wc) ? wc.GetString() : null;
                        var key = PlacementKey(memberId, cls);
                        if (result.ContainsKey(key)) continue;
                        result[key] = new Placement
                        {
                            TeamNumber = teamNumber,
                            Position = TryGet(s, "position", out var pos) && pos.TryGetInt32(out var pi) ? pi : int.MaxValue,
                            Name = TryGet(s, "name", out var n) ? n.GetString() ?? "" : ""
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read current start list {NodeId}; its order is not kept", startList?.Id);
            }
            return result;
        }

        private static bool TryGet(System.Text.Json.JsonElement el, string name, out System.Text.Json.JsonElement value)
        {
            value = default;
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            foreach (var p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
            }
            return false;
        }

        private static bool IsDocumentUrlTimeout(Exception ex)
        {
            var inner = ex is AggregateException agg ? agg.InnerException : ex;
            if (inner is Microsoft.Data.SqlClient.SqlException sqlEx && sqlEx.Number == -2)
            {
                return ex.ToString().Contains("DocumentUrlRepository") || ex.ToString().Contains("DocumentUrlService");
            }
            return false;
        }
    }
}
