using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Controllers
{
    /// <summary>
    /// P4 (Stefan 2026-10-24, fråga 15): överskottets fördelning — ett underlag, ingen bokföring. Föreningarna och talen
    /// (personer, uppdrag, dagar) ur bemanningen i planeringen och dess delplaneringar, "Ställer upp för" avgör föreningen.
    /// Bara den som kan ändra planeringen (det är pengar).
    /// </summary>
    public partial class PlanningController
    {
        private sealed record OrgCount(string Key, string Name, HashSet<string> People, int Assignments, HashSet<string> Days);

        /// <summary>Föreningarna i bemanningen och deras tal. En person räknas en gång även om hen finns i flera planeringar.</summary>
        private List<OrgCount> SurplusCounts(int planningId)
        {
            var people = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Staffing.CompetitionPeopleService>();
            var owners = HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Staffing.StaffOwnerResolver>();
            var map = new Dictionary<string, OrgCount>();
            var seenAssignments = new HashSet<int>();
            foreach (var pid in _planning.FamilyIds(planningId))
            {
                var owner = owners.ForPlanning(pid);
                if (owner == null) continue;
                HpskSite.Models.Staffing.CompetitionPeopleResponse resp;
                try { resp = people.Build(owner, null, false, includePrep: false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Överskottet: bemanningen i planering {Id} kunde inte läsas.", pid); continue; }
                foreach (var row in resp.People.Where(r => r.Assignments.Count > 0))
                {
                    var key = row.RepresentsId > 0 ? row.RepresentsType + ":" + row.RepresentsId : "none";
                    var name = row.RepresentsId > 0 && !string.IsNullOrEmpty(row.RepresentsName) ? row.RepresentsName! : "Ingen förening";
                    if (!map.TryGetValue(key, out var oc)) map[key] = oc = new OrgCount(key, name, new HashSet<string>(), 0, new HashSet<string>());
                    var added = row.Assignments.Count(a => a.Id <= 0 || seenAssignments.Add(a.Id));
                    map[key] = oc = oc with { Assignments = oc.Assignments + added };
                    oc.People.Add(row.Key);
                    foreach (var a in row.Assignments) oc.Days.Add(row.Key + "|" + (a.DateKey ?? ""));
                }
            }
            return map.Values.OrderBy(o => o.Key == "none" ? 1 : 0).ThenByDescending(o => o.People.Count).ThenBy(o => o.Name, StringComparer.Create(Sv, true)).ToList();
        }

        private object SurplusJson(int planningId, SurplusData data)
        {
            var counts = SurplusCounts(planningId);
            var choice = (data.Orgs ?? new()).ToDictionary(o => o.Key, o => o);
            var orgs = counts.Select(c =>
            {
                choice.TryGetValue(c.Key, out var ch);
                return new SurplusOrg(c.Key, c.Name, c.People.Count, c.Assignments, c.Days.Count, ch?.Include ?? c.Key != "none", ch?.Weight ?? 0, false);
            }).ToList();
            // Föreningar som lagts till för hand (finns inte i bemanningen) — till exempel en krets som bidragit på annat sätt.
            foreach (var ch in (data.Orgs ?? new()).Where(o => o.Key.StartsWith("text:") && counts.All(c => c.Key != o.Key)))
                orgs.Add(new SurplusOrg(ch.Key, ch.Name ?? ch.Key[5..], 0, 0, 0, ch.Include, ch.Weight, true));
            var (shares, remaining, err) = PlanningSurplusRules.Distribute(data.Amount, data.Fixed ?? new(), data.Key, orgs);
            var byKey = shares.ToDictionary(s => s.Key);
            return new
            {
                success = true,
                amount = data.Amount, key = SurplusKey.Normalize(data.Key), note = data.Note ?? "",
                keys = SurplusKey.All.Select(k => new { value = k, label = SurplusKey.Label(k) }),
                @fixed = (data.Fixed ?? new()).Select(f => new { label = f.Label, amount = f.Amount }),
                remaining, error = err,
                orgs = orgs.Select(o => new
                {
                    key = o.Key, name = o.Name, people = o.People, assignments = o.Assignments, days = o.Days, include = o.Include,
                    weight = o.Weight, manual = o.Manual, basis = byKey.TryGetValue(o.Key, out var sh) ? sh.Basis : 0,
                    share = byKey.TryGetValue(o.Key, out var s2) ? s2.Amount : 0,
                }),
                total = shares.Sum(s => s.Amount),
            };
        }

        private static SurplusData? ParseSurplus(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return new SurplusData();
            try { return Newtonsoft.Json.JsonConvert.DeserializeObject<SurplusData>(data); }
            catch { return null; }
        }

        [HttpGet]
        public async Task<IActionResult> Surplus(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Överskottets fördelning görs av den som kan ändra planeringen.");
            return Json(SurplusJson(id, _planning.Surplus(id) ?? new SurplusData()));
        }

        /// <summary>Räknar om utan att spara (när arrangören skriver).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CalculateSurplus(int id, string? data)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Överskottets fördelning görs av den som kan ändra planeringen.");
            var d = ParseSurplus(data);
            return d == null ? No("Underlaget gick inte att läsa.") : Json(SurplusJson(id, d));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSurplus(int id, string? data)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Överskottets fördelning görs av den som kan ändra planeringen.");
            var d = ParseSurplus(data);
            if (d == null) return No("Underlaget gick inte att läsa.");
            _planning.SaveSurplus(id, d, x.MemberId);
            return Json(SurplusJson(id, _planning.Surplus(id) ?? new SurplusData()));
        }
    }
}
