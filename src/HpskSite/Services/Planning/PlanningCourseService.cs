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
    /// Banan i planeringen (Stefan 2026-10-24, omgjord 2026-10-25): planeringen äger stationernas fältkonfiguration innan
    /// tävlingen finns — EN konfiguration, med en ansvarig per station (<c>PlanningStationResponsible</c>). När planeringen
    /// kopplas till en fälttävling ansluts den till tävlingen — en kopia, som när man ansluter en konfiguration i Redigera
    /// tävling.
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
        private readonly FaltskytteProjectService _projects;

        public PlanningCourseService(PlanningService planning, FaltskytteConfigurationService configs, ICompetitionStore competitions,
            ILogger<PlanningCourseService> logger, FaltskytteProjectService projects)
        {
            _planning = planning; _configs = configs; _competitions = competitions; _logger = logger; _projects = projects;
        }

        /// <summary>
        /// Stefan 2026-10-25: konfigurationer som skapas från en planering ligger i ett projekt i Fältkonfiguratorn med samma
        /// namn som (stationernas) planering. Skapar projektet första gången och lägger konfigurationen i det.
        /// </summary>
        public async Task EnsureProjectAsync(int planningId, FaltskytteConfiguration config, int memberId)
        {
            try
            {
                if (config.ProjectId > 0) return;
                var owner = _planning.Get(_planning.StationOwnerId(planningId));
                if (owner == null) return;
                var projectId = _planning.ProjectLinkId(owner.Id);
                if (projectId == null)
                {
                    var (ok, _, created) = await _projects.CreateAsync(new CreateFaltskytteProjectRequest
                    {
                        Name = owner.Name.Length > 200 ? owner.Name[..200] : owner.Name,
                        Description = $"Fältkonfigurationerna i planeringen {owner.Name}.",
                    }, memberId);
                    if (!ok || created == null) return;
                    try { _planning.LinkProject(owner.Id, created.Id, memberId); }
                    catch
                    {
                        // Ett projekt som inte kan kopplas till planeringen blir inte kvar som ett tomt projekt i konfiguratorn.
                        await _projects.DeleteAsync(created.Id, memberId);
                        throw;
                    }
                    projectId = created.Id;
                }
                await _configs.SetProjectForPlanningAsync(config.Id, projectId.Value);
                config.ProjectId = projectId;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Planering {Id}: projektet i Fältkonfiguratorn kunde inte skapas.", planningId); }
        }

        public static bool IsFalt(string? type) =>
            HpskSite.Models.Staffing.FunctionaryRoles.FaltFamily.Contains(type ?? "", StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stationernas fälttävling: stationsplaneringens koppling (Fält-delplaneringen i en SM-vecka), annars planeringens
        /// egen, annars huvudplaneringens — eller null.
        /// </summary>
        public HpskSite.Models.Competitions.CompetitionInfo? FaltCompetition(int planningId)
        {
            var owner = _planning.StationOwnerId(planningId);
            var link = (owner > 0 ? _planning.LinkFor(owner) : null) ?? _planning.LinkFor(planningId);
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
            return Math.Max(Math.Max(comp?.NumberOfSeriesOrStations ?? 0, _planning.MaxAssignedStation(planningId)), fromCourse);
        }

        /// <summary>
        /// Skapar banans konfiguration. Har familjens fälttävling redan stationer kopieras de; annars börjar banan tom med
        /// <paramref name="stationCount"/> stationer (konfiguratorn läser <c>_stationCount</c>).
        /// </summary>
        public async Task<(FaltskytteConfiguration? Config, string? Error)> CreateCourseAsync(PlanningRow p, int memberId, int stationCount)
        {
            var existing = await CourseConfigAsync(p.Id);
            if (existing != null) return (existing, null);
            var root = _planning.Get(_planning.StationOwnerId(p.Id)) ?? p;
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
            var name = $"{root.Name} – stationerna";
            var (ok, msg, created) = await _configs.CreateAsync(new CreateFaltskytteConfigurationRequest
            {
                Name = name.Length > 200 ? name[..200] : name,
                Description = $"Tävlingens stationer i planeringen {root.Name}. Varje station kan ha en ansvarig som ändrar just den.",
                Visibility = "Private",
                JsonBlob = blob.ToString(Newtonsoft.Json.Formatting.None),
            }, memberId);
            if (!ok || created == null) return (null, msg ?? "Stationerna kunde inte skapas.");
            var linkError = _planning.LinkCourseConfig(p.Id, created.Id, memberId);
            if (linkError != null) { await _configs.DeleteAsync(created.Id); return (null, linkError); }
            await EnsureProjectAsync(p.Id, created, memberId);
            return (created, null);
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
            if (course == null) return onlyIfEmpty ? null : "Planeringens stationer är inte skapade än.";
            var courseStations = FaltskytteAttachedConfigState.StationCount(course.JsonBlob);
            if (courseStations == 0) return onlyIfEmpty ? null : "Ingen station är byggd än.";
            if (onlyIfEmpty && FaltskytteAttachedConfigState.StationCount(comp.StationConfig) > 0) return null;
            JObject blob;
            try { blob = JObject.Parse(course.JsonBlob ?? "{}"); }
            catch { return "Stationernas fältkonfiguration går inte att läsa."; }
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
            if (saved.PublishProblem != null) return "Stationerna är anslutna men tävlingen kunde inte publiceras om: " + saved.PublishProblem;
            return null;
        }

        /// <summary>
        /// "Ändra antalet stationer": tävlingens antal i helheten (<c>_stationCount</c>). Färre stationer vägras så länge en
        /// station över gränsen har en ansvarig eller är byggd.
        /// </summary>
        public async Task<string?> SetStationCountAsync(PlanningRow p, int memberId, int count)
        {
            if (count < 1 || count > PlanningStations.Max) return $"Tävlingen kan ha 1–{PlanningStations.Max} stationer.";
            var (course, err) = await CreateCourseAsync(p, memberId, count);
            if (course == null) return err;
            var assignedMax = _planning.MaxAssignedStation(p.Id);
            if (assignedMax > count) return $"Station {assignedMax} har en ansvarig. Ta bort den ansvarige för stationen först.";
            var built = CourseAssembly.BuiltStations(course.JsonBlob);
            if (built.Count > 0 && built.Max > count) return $"Station {built.Max} är byggd. Ta bort den i konfiguratorn först.";
            var comp = FaltCompetition(p.Id);
            if (comp != null && comp.NumberOfSeriesOrStations > count)
                return $"Tävlingen {comp.Name} har {comp.NumberOfSeriesOrStations} stationer. Ändra den i tävlingen först.";
            JObject blob;
            try { blob = string.IsNullOrWhiteSpace(course.JsonBlob) ? new JObject() : JObject.Parse(course.JsonBlob); }
            catch { return "Stationernas fältkonfiguration går inte att läsa."; }
            if (blob.Value<int?>(CourseAssembly.StationCountKey) == count) return null;
            blob[CourseAssembly.StationCountKey] = count;
            var (ok, msg, _) = await _configs.UpdateWithVersionAsync(new UpdateFaltskytteConfigurationRequest { Id = course.Id, JsonBlob = blob.ToString(Newtonsoft.Json.Formatting.None) });
            return ok ? null : msg;
        }
    }
}
