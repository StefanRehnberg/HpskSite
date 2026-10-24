using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Planning;
using HpskSite.Services.Competitions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Banan i planeringen (Stefan 2026-10-24, <c>notes/tavlingsplanering-banan-2026-10-24.md</c>): planeringen äger banans
    /// fältkonfiguration innan tävlingen finns, föreningarna bygger sina stationer i varsin delkonfiguration (områdena), och
    /// "Sätt ihop banan" lägger ihop delarna (<see cref="CourseAssembly"/>). När planeringen kopplas till en fälttävling
    /// ansluts banan till tävlingen — en kopia, som när man ansluter en konfiguration i Redigera tävling.
    ///
    /// <para><b>⚠️ Banan tillhör familjens HUVUDPLANERING</b> (stationsnumren gäller hela familjen): en delplanering läser
    /// och sätter ihop samma bana.</para>
    /// </summary>
    public class PlanningCourseService
    {
        private readonly PlanningService _planning;
        private readonly FaltskytteConfigurationService _configs;
        private readonly ICompetitionStore _competitions;
        private readonly ILogger<PlanningCourseService> _logger;

        public PlanningCourseService(PlanningService planning, FaltskytteConfigurationService configs, ICompetitionStore competitions,
            ILogger<PlanningCourseService> logger)
        {
            _planning = planning; _configs = configs; _competitions = competitions; _logger = logger;
        }

        public static bool IsFalt(string? type) =>
            HpskSite.Models.Staffing.FunctionaryRoles.FaltFamily.Contains(type ?? "", StringComparer.OrdinalIgnoreCase);

        /// <summary>Familjens fälttävling (planeringens koppling, annars huvudplaneringens), eller null.</summary>
        public HpskSite.Models.Competitions.CompetitionInfo? FaltCompetition(int planningId)
        {
            var link = _planning.LinkFor(planningId);
            if (link == null)
            {
                var root = _planning.FamilyRootId(planningId);
                if (root > 0 && root != planningId) link = _planning.LinkFor(root);
            }
            var comp = link == null ? null : _competitions.GetSaved(link.TargetId);
            return comp != null && IsFalt(comp.CompetitionType) ? comp : null;
        }

        public async Task<FaltskytteConfiguration?> CourseConfigAsync(int planningId)
        {
            var id = _planning.CourseConfigId(planningId);
            return id > 0 ? await _configs.GetByIdAsync(id.Value) : null;
        }

        /// <summary>Banans antal stationer: tävlingens, områdenas högsta och banans egen — det största.</summary>
        public int StationCount(int planningId, string? courseBlob)
        {
            var comp = FaltCompetition(planningId);
            var fromCourse = 0;
            try
            {
                var o = string.IsNullOrWhiteSpace(courseBlob) ? null : JObject.Parse(courseBlob);
                fromCourse = Math.Max(o?.Value<int?>(CourseAssembly.StationCountKey) ?? 0, FaltskytteAttachedConfigState.StationCount(courseBlob));
            }
            catch { /* en oläsbar bana räknas som tom */ }
            return Math.Max(Math.Max(comp?.NumberOfSeriesOrStations ?? 0, _planning.MaxAreaStation(planningId)), fromCourse);
        }

        /// <summary>
        /// Skapar banans konfiguration. Har familjens fälttävling redan stationer kopieras de; annars börjar banan tom med
        /// <paramref name="stationCount"/> stationer (konfiguratorn läser <c>_stationCount</c>).
        /// </summary>
        public async Task<(FaltskytteConfiguration? Config, string? Error)> CreateCourseAsync(PlanningRow p, int memberId, int stationCount)
        {
            var existing = await CourseConfigAsync(p.Id);
            if (existing != null) return (existing, null);
            var root = _planning.Get(_planning.FamilyRootId(p.Id)) ?? p;
            var comp = FaltCompetition(p.Id);
            string? source = null;
            var attached = FaltskytteAttachedConfigState.AttachedId(comp?.StationConfig);
            if (attached > 0)
            {
                var src = await _configs.GetByIdAsync(attached.Value);
                if (src != null && await _configs.CanViewAsync(src, memberId)) source = src.JsonBlob;
            }
            if (source == null && FaltskytteAttachedConfigState.StationCount(comp?.StationConfig) > 0) source = comp!.StationConfig;
            JObject blob;
            try { blob = string.IsNullOrWhiteSpace(source) ? new JObject() : JObject.Parse(source); }
            catch { blob = new JObject(); }
            blob.Remove(FaltskytteAttachedConfigState.AttachedKey);
            var count = Math.Max(stationCount, StationCount(p.Id, null));
            if (count > 0 && FaltskytteAttachedConfigState.StationCount(blob.ToString()) == 0) blob[CourseAssembly.StationCountKey] = count;
            if (blob["_scoringMode"] == null && !string.IsNullOrWhiteSpace(comp?.ScoringMode)) blob["_scoringMode"] = comp!.ScoringMode;
            var name = $"{root.Name} – banan";
            var (ok, msg, created) = await _configs.CreateAsync(new CreateFaltskytteConfigurationRequest
            {
                Name = name.Length > 200 ? name[..200] : name,
                Description = $"Banans fältkonfiguration i planeringen {root.Name}. Föreningarnas delar läggs ihop hit med Sätt ihop banan.",
                Visibility = "Private",
                JsonBlob = blob.ToString(Newtonsoft.Json.Formatting.None),
            }, memberId);
            if (!ok || created == null) return (null, msg ?? "Banans fältkonfiguration kunde inte skapas.");
            var linkError = _planning.LinkCourseConfig(p.Id, created.Id, memberId);
            if (linkError != null) { await _configs.DeleteAsync(created.Id); return (null, linkError); }
            return (created, null);
        }

        public sealed record AssembleOutcome(CourseAssemblyResult? Result, bool Changed, int ConfigId, string? Error);

        /// <summary>
        /// "Sätt ihop banan": banans konfiguration (skapas om den saknas) får delarnas stationer. En godkänd bana vägras av
        /// konfigurationens lås — "Begär ändring" först. Ändrades ingenting sparas ingenting.
        /// </summary>
        public async Task<AssembleOutcome> AssembleAsync(PlanningRow p, int memberId)
        {
            var (course, err) = await CreateCourseAsync(p, memberId, 0);
            if (course == null) return new AssembleOutcome(null, false, 0, err);
            var parts = new List<CoursePart>();
            foreach (var l in _planning.AreaConfigLinks(p.Id))
            {
                var cfg = await _configs.GetByIdAsync(l.ConfigId);
                if (cfg != null) parts.Add(new CoursePart(cfg.Id, cfg.Name, cfg.JsonBlob));
            }
            var result = CourseAssembly.Assemble(course.JsonBlob, parts, StationCount(p.Id, course.JsonBlob), DateTime.Now);
            if (CourseAssembly.Checksum(result.Blob) == CourseAssembly.Checksum(course.JsonBlob))
                return new AssembleOutcome(result, false, course.Id, null);
            var (ok, msg, _) = await _configs.UpdateWithVersionAsync(new UpdateFaltskytteConfigurationRequest { Id = course.Id, JsonBlob = result.Blob });
            return ok ? new AssembleOutcome(result, true, course.Id, null) : new AssembleOutcome(result, false, course.Id, msg);
        }

        /// <summary>
        /// Ansluter banan till tävlingen: en kopia i tävlingens stationskonfiguration med <c>_attachedConfigId</c>, tävlingstypen
        /// och antalet stationer — samma som att ansluta i Redigera tävling. <paramref name="onlyIfEmpty"/>: bara när tävlingen
        /// saknar stationer (vid kopplingen; en tävling med en egen bana skrivs aldrig över utan att någon valt det).
        /// Returnerar null när det gjordes eller inte behövdes, annars skälet.
        /// </summary>
        public async Task<string?> AttachToCompetitionAsync(int planningId, int competitionId, bool onlyIfEmpty)
        {
            var comp = _competitions.GetSaved(competitionId);
            if (comp == null || !IsFalt(comp.CompetitionType)) return onlyIfEmpty ? null : "Tävlingen är ingen fälttävling.";
            var course = await CourseConfigAsync(planningId);
            if (course == null) return onlyIfEmpty ? null : "Planeringen har ingen fältkonfiguration för banan.";
            var courseStations = FaltskytteAttachedConfigState.StationCount(course.JsonBlob);
            if (courseStations == 0) return onlyIfEmpty ? null : "Banan har inga stationer än.";
            if (onlyIfEmpty && FaltskytteAttachedConfigState.StationCount(comp.StationConfig) > 0) return null;
            JObject blob;
            try { blob = JObject.Parse(course.JsonBlob ?? "{}"); }
            catch { return "Banans fältkonfiguration går inte att läsa."; }
            blob[FaltskytteAttachedConfigState.AttachedKey] = course.Id;
            var mode = blob.Value<string>("_scoringMode");
            var saved = _competitions.Update(competitionId, null, c =>
            {
                c.StationConfig = blob.ToString(Newtonsoft.Json.Formatting.None);
                c.NumberOfSeriesOrStations = courseStations;
                if (!string.IsNullOrWhiteSpace(mode)) c.ScoringMode = mode;
            });
            if (saved.Outcome != CompetitionUpdateOutcome.Saved)
            {
                _logger.LogWarning("Planering {Id}: banan kunde inte anslutas till tävling {Comp} ({Outcome}).", planningId, competitionId, saved.Outcome);
                return "Tävlingen kunde inte sparas. Försök igen.";
            }
            if (saved.PublishProblem != null) return "Banan är ansluten men tävlingen kunde inte publiceras om: " + saved.PublishProblem;
            return null;
        }

        /// <summary>Delarna och deras läge mot banan: med, ändrad sedan ihopläggningen eller inte med än.</summary>
        public sealed record PartState(int ConfigId, string Name, string Status, List<int> Stations, string State);

        public async Task<List<PartState>> PartsAsync(int planningId, string? courseBlob)
        {
            var assembled = CourseAssembly.AssembledFrom(courseBlob).ToDictionary(a => a.ConfigId);
            var list = new List<PartState>();
            foreach (var l in _planning.AreaConfigLinks(planningId))
            {
                var cfg = await _configs.GetByIdAsync(l.ConfigId);
                if (cfg == null) continue;
                var state = !assembled.TryGetValue(cfg.Id, out var a) ? "new"
                    : CourseAssembly.Checksum(cfg.JsonBlob) != a.Checksum ? "changed" : "current";
                list.Add(new PartState(cfg.Id, cfg.Name, cfg.ApprovalStatus ?? "Draft", AreaConfigBlob.CourseStations(cfg.JsonBlob), state));
            }
            return list;
        }
    }
}
