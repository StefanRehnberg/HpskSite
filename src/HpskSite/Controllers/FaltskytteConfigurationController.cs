using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// API surface for standalone Fältskytte station configurations.
    /// All endpoints require a logged-in member; view/edit/delete checks
    /// flow through FaltskytteConfigurationService.
    /// </summary>
    public class FaltskytteConfigurationController : SurfaceController
    {
        private readonly FaltskytteConfigurationService _configService;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly ILogger<FaltskytteConfigurationController> _logger;
        private readonly HpskSite.Services.MemberDirectoryService _directory;
        private readonly HpskSite.CompetitionTypes.Faltskytte.Services.StationPrintOptionService _printOptions;

        public FaltskytteConfigurationController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FaltskytteConfigurationService configService,
            IMemberService memberService,
            ClubService clubService,
            ILogger<FaltskytteConfigurationController> logger,
            HpskSite.Services.MemberDirectoryService directory,
            HpskSite.CompetitionTypes.Faltskytte.Services.StationPrintOptionService printOptions)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _printOptions = printOptions;
            _directory = directory;
            _configService = configService;
            _memberService = memberService;
            _clubService = clubService;
            _logger = logger;
        }

        // ── Stationskort ur editorn ──────────────────────────────────────

        public sealed class StationCardFromEditorRequest
        {
            /// <summary>Editorns aktuella konfiguration (faltCfgData som JSON), även osparade ändringar.</summary>
            public string? Json { get; set; }
            public int Station { get; set; }
            public string? WeaponClass { get; set; }
            public int? CompetitionId { get; set; }
            /// <summary>Tävlingstypen när konfigurationen själv inte bär `_scoringMode`.</summary>
            public string? ScoringMode { get; set; }
            /// <summary>Konfigurationens id när den är sparad (fristående editor eller ansluten); bär Full/Kompakt per station.</summary>
            public int? ConfigId { get; set; }
        }

        public sealed class StationPrintModeRequest
        {
            public int ConfigId { get; set; }
            public int Station { get; set; }
            public string? Mode { get; set; }
        }

        /// <summary>Full eller Kompakt för en station. Den som får ändra konfigurationen får välja; valet rör aldrig konfigurationens blob.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("GetByIdAsync + CanEditAsync: bara den som får ändra konfigurationen")]
        public async Task<IActionResult> SetStationPrintMode([FromBody] StationPrintModeRequest request)
        {
            var viewerId = await _configService.GetCurrentMemberIdAsync();
            if (viewerId == null) return StatusCode(401, "Inloggning krävs.");
            if (!HpskSite.CompetitionTypes.Faltskytte.Services.StationPrintMode.IsValid(request.Mode) || request.Station <= 0)
                return BadRequest("Okänt läge eller station.");
            var cfg = await _configService.GetByIdAsync(request.ConfigId);
            if (cfg == null || !await _configService.CanEditAsync(cfg, viewerId))
                return StatusCode(403, "Du får inte ändra den här konfigurationen.");
            try
            {
                _printOptions.Set(cfg.Id, request.Station, request.Mode!, viewerId);
                return Json(new { success = true, mode = request.Mode });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte spara utskriftsläge för konfiguration {ConfigId} station {Station}", cfg.Id, request.Station);
                return StatusCode(500, "Valet kunde inte sparas.");
            }
        }

        /// <summary>
        /// Magnum-stationernas anmärkningar (SHB Del E: stationsantal, poängmål, Hands-up, särskjutningsstation) för det editorn har just nu.
        /// Reglerna finns bara i <c>MagnumStationRules</c>; konfiguratorn visar svaret. Ger bara tillbaka anmärkningar för det som
        /// skickades in.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("GetCurrentMemberIdAsync: inloggad medlem; räknar bara på det som skickades in")]
        public async Task<IActionResult> MagnumNotes([FromBody] StationCardFromEditorRequest request)
        {
            var viewerId = await _configService.GetCurrentMemberIdAsync();
            if (viewerId == null) return StatusCode(401, "Inloggning krävs.");
            if (string.IsNullOrWhiteSpace(request.Json) || request.Json.Length > 4_000_000)
                return BadRequest("Konfigurationen saknas eller är för stor.");
            return Json(new { success = true, notes = HpskSite.Services.Kretsgranskning.CourseReviewChecklist.MagnumNotesOf(request.Json) });
        }

        /// <summary>
        /// Konfiguratorns "Skriv ut stationskort": samma renderare som tävlingens kort (StationCardRenderer), men ur
        /// det editorn har just nu. Ger bara tillbaka kortet för det som skickades in, så den som är inloggad kan
        /// inte läsa något hen inte redan hade. Texten HTML-kodas i renderaren.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [HpskSite.Services.Security.InCodeAuth("GetCurrentMemberIdAsync: inloggad medlem; ger bara tillbaka det som skickades in")]
        public async Task<IActionResult> StationCardHtml([FromBody] StationCardFromEditorRequest request)
        {
            var viewerId = await _configService.GetCurrentMemberIdAsync();
            if (viewerId == null) return StatusCode(401, "Inloggning krävs.");
            if (string.IsNullOrWhiteSpace(request.Json) || request.Json.Length > 4_000_000)
                return BadRequest("Konfigurationen saknas eller är för stor.");

            var config = FaltskytteConfigParser.Parse(request.Json);
            var meta = HpskSite.CompetitionTypes.Faltskytte.Services.StationCardRenderer.ReadMeta(request.Json);
            var byWc = new Dictionary<string, FaltskytteStationConfig>();
            foreach (var kv in config.WeaponConfigs)
            {
                var st = kv.Value.Stations.FirstOrDefault(s => s.Station == request.Station);
                if (st != null) byWc[kv.Key] = st;
            }
            if (byWc.Count == 0) return NotFound("Stationen finns inte.");

            var item = new StationPrintItem { Station = request.Station, StationsByWeaponClass = byWc };
            // Full/Kompakt ligger utanför blobben: läs det sparade valet för den här konfigurationen.
            var printMode = HpskSite.CompetitionTypes.Faltskytte.Services.StationPrintMode.Full;
            var canSetMode = false;
            var cfgId = request.ConfigId ?? 0;
            if (cfgId > 0)
            {
                item.Compact = _printOptions.CompactStations(cfgId).Contains(request.Station);
                if (item.Compact) printMode = HpskSite.CompetitionTypes.Faltskytte.Services.StationPrintMode.Kompakt;
                var cfg = await _configService.GetByIdAsync(cfgId);
                canSetMode = cfg != null && await _configService.CanEditAsync(cfg, viewerId);
            }
            if (request.CompetitionId is int compId && compId > 0)
            {
                var entryUrl = $"{Request.PublicBase()}/station?c={compId}&s={request.Station}";
                item.Qr1Url = $"/umbraco/surface/Faltskytte/GetStationInfoQr?competitionId={compId}&stationNumber={request.Station}";
                item.Qr2Url = $"/umbraco/surface/Faltskytte/GenerateQrCode?url={Uri.EscapeDataString(entryUrl)}";
            }

            var blocks = HpskSite.CompetitionTypes.Faltskytte.Services.StationCardRenderer.BuildBlocks(item, meta.Strict, meta.CourseStations, request.WeaponClass);
            var isPoang = FaltskytteScoringMode.IsPoang(config, request.ScoringMode);
            var title = "Station " + request.Station + (blocks.Count == 1 && blocks[0].ShowClassesInHeader ? " — " + string.Join(", ", blocks[0].Classes) : "");
            // Förhandsvisning med verktygsfält (Full/Kompakt, sidräknare, Skriv ut) i stället för att skrivardialogen öppnas direkt.
            var toolbar = new HpskSite.CompetitionTypes.Faltskytte.Services.StationCardToolbar { Station = request.Station, WeaponClass = request.WeaponClass, Mode = printMode, CanSetMode = canSetMode };
            var html = HpskSite.CompetitionTypes.Faltskytte.Services.StationCardRenderer.RenderDocument(title, null, blocks, isPoang, autoPrint: false, toolbar: toolbar);
            return Content(html, "text/html; charset=utf-8");
        }

        // ── Reads ────────────────────────────────────────────────────────

        /// <summary>Returns every configuration the current user can view. JSON blob omitted.</summary>
        [HttpGet]
        public async Task<IActionResult> ListAccessible()
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var configs = await _configService.ListAccessibleAsync(viewerId);
                var views = new List<FaltskytteConfigurationView>();
                foreach (var cfg in configs)
                {
                    views.Add(await _configService.BuildViewAsync(cfg, viewerId, includeJson: false));
                }
                var viewerCanApprove = await _configService.CanApproveAsync(viewerId);
                return Json(new { success = true, configurations = views, viewerCanApprove });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing Fältskytte configurations");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Passar konfigurationen tävlingens typ? (Magnum-omtaget 2026-11-22.) En Magnumfält-tävling kräver vapengrupp M, vanlig
        /// fält kräver att det inte bara finns M. Svaret är meningar att visa; det spärrar ingenting.
        /// </summary>
        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("GetCurrentMemberIdAsync + CanViewAsync: bara den som får se konfigurationen")]
        public async Task<IActionResult> CompatibilityCheck(int id, string? competitionType)
        {
            var viewerId = await _configService.GetCurrentMemberIdAsync();
            if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
            var config = await _configService.GetByIdAsync(id);
            if (config == null || !await _configService.CanViewAsync(config, viewerId))
                return Json(new { success = false, message = "Konfigurationen hittades inte." });
            return Json(new
            {
                success = true,
                weaponGroups = HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteConfigCompatibility.WeaponGroupsOf(config.JsonBlob),
                problems = HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteConfigCompatibility.Problems(config.JsonBlob, competitionType)
            });
        }

        /// <summary>Returns a single configuration, full view including JSON blob. Auth via CanViewAsync.</summary>
        [HttpGet]
        public async Task<IActionResult> Get(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanViewAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att visa denna konfiguration." });

                var view = await _configService.BuildViewAsync(config, viewerId, includeJson: true);
                return Json(new { success = true, configuration = view, planningStations = await PlanningStationsJsonAsync(config, viewerId.Value) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting Fältskytte configuration {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Stefan 2026-10-25: en planerings stationer — vem som är ansvarig för vilken station, vilka den inloggade får
        /// ändra, varje stations kontrollsumma (skickas tillbaka vid sparning) och godkännandet per station. Null när
        /// konfigurationen inte är en planerings stationer.
        /// </summary>
        private async Task<object?> PlanningStationsJsonAsync(FaltskytteConfiguration config, int viewerId)
        {
            var r = await _configService.StationRightsAsync(config, viewerId);
            if (!r.IsCourse) return null;
            var isBanlaggare = await HttpContext.RequestServices.GetRequiredService<CertificationService>()
                .HasActiveCertAsync(viewerId, HpskSite.Models.CertificationTypes.Banlaggare);
            var approvals = Enumerable.Range(1, Math.Max(r.StationCount, 0))
                .Select(n => (n, a: HpskSite.Models.Planning.StationBlob.Current(config.JsonBlob, n)))
                .Where(x => x.a != null)
                .ToDictionary(x => x.n.ToString(), x => (object)new { status = x.a!.Status, comment = x.a.Comment, at = x.a.At });
            return new
            {
                all = r.All,
                mine = r.Stations.OrderBy(n => n),
                responsible = r.Responsible.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                checksums = HpskSite.Models.Planning.StationBlob.Checksums(config.JsonBlob).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                approvals,
                stationCount = r.StationCount,
                canJudge = isBanlaggare && r.All,
            };
        }

        public sealed class UpdateStationsRequest
        {
            public int Id { get; set; }
            public List<int> Stations { get; set; } = new();
            public string JsonBlob { get; set; } = "";
            public Dictionary<string, string>? Expected { get; set; }
        }

        /// <summary>Sparar en eller flera stationer (en planerings stationer) — bara de stationerna, se UpdateStationsAsync.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStations([FromBody] UpdateStationsRequest request)
        {
            var viewerId = await _configService.GetCurrentMemberIdAsync();
            if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
            var expected = request.Expected?.Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);
            var (ok, msg, sums) = await _configService.UpdateStationsAsync(request.Id, viewerId.Value, request.Stations, request.JsonBlob, expected);
            return Json(new { success = ok, message = msg, checksums = sums?.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value) });
        }

        public sealed class StationApprovalRequest
        {
            public int ConfigId { get; set; }
            public int Station { get; set; }
            public string Action { get; set; } = "";
            public string? Comment { get; set; }
        }

        /// <summary>Godkännandet av EN station: request / approve / return / reopen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StationApproval([FromBody] StationApprovalRequest request)
        {
            var viewerId = await _configService.GetCurrentMemberIdAsync();
            if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
            var (ok, msg) = await _configService.SetStationApprovalAsync(request.ConfigId, viewerId.Value, request.Station, request.Action, request.Comment);
            return Json(new { success = ok, message = msg });
        }

        /// <summary>
        /// Vilka andra konfigurationer som länkar till stationer i den här — läses av
        /// redigeraren innan en station flyttas eller tas bort, eftersom länkarna pekar
        /// på stationsNUMRET. Kräver redigeringsrätt: frågan ställs bara av den som
        /// kan numrera om.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetLinkers(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                var linkers = await _configService.GetLinkersAsync(id, viewerId);
                return Json(new { success = true, linkers });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading linkers for config {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Lightweight station-summary view used by the "Importera station från en annan konfiguration"
        /// picker. Returns the stations array (figures + målgrupps) but skips the full faltCfgData blob.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStationsForImport(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanViewAsync(config, viewerId))
                    return Json(new { success = false, message = "Åtkomst nekad." });

                return Json(new
                {
                    success = true,
                    id = config.Id,
                    name = config.Name,
                    jsonBlob = config.JsonBlob
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stations for import from config {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Writes ───────────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromBody] CreateFaltskytteConfigurationRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var (success, message, created) = await _configService.CreateAsync(request, viewerId.Value);
                if (!success || created == null) return Json(new { success = false, message });

                return Json(new { success = true, configurationId = created.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Fältskytte configuration");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update([FromBody] UpdateFaltskytteConfigurationRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(request.Id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra denna konfiguration." });

                // Omgång 3 (K9): svaret bär den nya versionen, som editorn skickar med nästa gång.
                var (success, message, version) = await _configService.UpdateWithVersionAsync(request);
                return Json(new { success, message, version });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Fältskytte configuration {Id}", request.Id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(id);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanDeleteAsync(config, viewerId))
                    return Json(new { success = false, message = "Endast ägare eller administratör kan ta bort konfigurationen." });

                var (success, message) = await _configService.DeleteAsync(id);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting Fältskytte configuration {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Duplicate(int id, string? newName = null)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var source = await _configService.GetByIdAsync(id);
                if (source == null) return Json(new { success = false, message = "Källkonfigurationen hittades inte." });

                // Must be able to view source in order to copy it.
                if (!await _configService.CanViewAsync(source, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att kopiera denna konfiguration." });

                var (success, message, created) = await _configService.DuplicateAsync(id, viewerId.Value, newName);
                if (!success || created == null) return Json(new { success = false, message });
                return Json(new { success = true, configurationId = created.Id });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error duplicating Fältskytte configuration {Id}", id);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Collaborators ────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddCollaborator([FromBody] AddCollaboratorRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(request.ConfigId);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra delning." });

                var (success, message) = await _configService.AddCollaboratorAsync(request.ConfigId, request.MemberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding collaborator to config {Id}", request.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveCollaborator([FromBody] RemoveCollaboratorRequest request)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(request.ConfigId);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra delning." });

                var (success, message) = await _configService.RemoveCollaboratorAsync(request.ConfigId, request.MemberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing collaborator from config {Id}", request.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Approval workflow ───────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestApproval([FromBody] RequestApprovalRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.RequestApprovalAsync(
                    request.ConfigId, viewerId.Value, request.RequestedApproverMemberId);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error requesting approval for config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Returns every active Banläggare cert holder (name + club) for the request-approval picker.</summary>
        [HttpGet]
        public async Task<IActionResult> GetBanlaggareCandidates()
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var candidates = await _configService.GetBanlaggareCandidatesAsync();
                return Json(new { success = true, candidates });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading Banläggare candidates");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve([FromBody] ApprovalActionRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.ApproveAsync(request.ConfigId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error approving config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Unapprove([FromBody] ApprovalActionRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.UnapproveAsync(request.ConfigId, viewerId.Value);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unapproving config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>Omgång 3 (3.3): banläggaren skickar tillbaka konfigurationen med en kommentar (åter till utkast).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReturnForChanges([FromBody] ApprovalActionRequest request)
        {
            try
            {
                if (request == null) return Json(new { success = false, message = "Ogiltig förfrågan." });
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });
                var (success, message) = await _configService.ReturnAsync(request.ConfigId, viewerId.Value, request.Comment);
                return Json(new { success, message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error returning config {Id}", request?.ConfigId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Member search (for the collaborator picker) ─────────────────

        /// <summary>
        /// Member search restricted to any logged-in user. Returns up to 20 matches.
        /// Distinct from TrainingGroupController's SearchMembers (which is admin-gated)
        /// because any user can need to add a collaborator to their own configuration.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SearchMembers(string query)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                    return Json(new { success = true, members = new List<object>() });

                // Sökningen i SQL (skalplanen B) — förut byggdes hela registret för att svara med 20 rader.
                var members = _directory.Search(query, 20).Select(m => new
                {
                    memberId = m.MemberId,
                    memberName = m.DisplayName,
                    clubName = m.PrimaryClubId is int clubId ? _clubService.GetClubNameById(clubId) : null
                }).ToList();

                return Json(new { success = true, members });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching members for collaborator picker");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        // ── Station images ───────────────────────────────────────────────
        // The configurator's photo buttons used to post to
        // Faltskytte/UploadTargetGroupImage, which authorizes on a competition
        // id. A standalone configuration has no competition, so every upload
        // came back success=false and the picture silently never appeared.
        // These two endpoints cover the configurator's other two hosts:
        // a saved configuration (edit rights decide) and the competition
        // wizard, where nothing is saved yet and the image is parked in the
        // uploading member's own draft folder.

        /// <summary>Stores a station/figure photo for a standalone configuration.</summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UploadStationImage(IFormFile file, int configurationId, string weaponClass, int stationNumber, int groupNumber)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                var config = await _configService.GetByIdAsync(configurationId);
                if (config == null) return Json(new { success = false, message = "Konfigurationen hittades inte." });

                if (!await _configService.CanEditAsync(config, viewerId))
                    return Json(new { success = false, message = "Du har inte rättighet att ändra denna konfiguration." });

                return await SaveStationImageAsync(file, configurationId.ToString(),
                    weaponClass, stationNumber, groupNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading station image for configuration {Id}", configurationId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Stores a station/figure photo while the competition wizard is still
        /// running, i.e. before there is a competition or configuration to hang
        /// it on. Scoped to the uploading member so two users drafting at the
        /// same time cannot overwrite each other.
        /// </summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> UploadDraftStationImage(IFormFile file, string weaponClass, int stationNumber, int groupNumber)
        {
            try
            {
                var viewerId = await _configService.GetCurrentMemberIdAsync();
                if (viewerId == null) return Json(new { success = false, message = "Inloggning krävs." });

                return await SaveStationImageAsync(file, "utkast/" + viewerId.Value,
                    weaponClass, stationNumber, groupNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading draft station image");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Shared validation + write for the two upload endpoints above.
        /// <paramref name="subDir"/> is the folder inside the file store's "faltkonfig" area
        /// (wwwroot/images/faltkonfig on disk; served at /images/faltkonfig/… either way).
        /// Callers must have checked authorization first.
        /// </summary>
        private async Task<IActionResult> SaveStationImageAsync(IFormFile file, string subDir, string weaponClass, int stationNumber, int groupNumber)
        {
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "Ingen fil vald." });

            if (file.Length > 5 * 1024 * 1024)
                return Json(new { success = false, message = "Filen är för stor (max 5 MB)." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                return Json(new { success = false, message = "Endast JPG, PNG eller WebP." });

            var wc = SanitizeForFileName(weaponClass);
            var fileName = $"st{stationNumber}_{wc}_tg{groupNumber}{ext}";

            var name = subDir + "/" + fileName;
            await using (var stream = file.OpenReadStream())
                await HttpContext.RequestServices.GetRequiredService<HpskSite.Services.Storage.IFileStore>().SaveAsync(HpskSite.Services.Storage.FileStoreAreas.Faltkonfig, name, stream);

            var imageUrl = HpskSite.Services.Storage.FileStoreAreas.PublicUrl(HpskSite.Services.Storage.FileStoreAreas.Faltkonfig, name);
            _logger.LogInformation("Uploaded station image: {Url}", imageUrl);
            return Json(new { success = true, imageUrl });
        }

        /// <summary>Keeps a client-supplied weapon class from escaping the image folder.</summary>
        private static string SanitizeForFileName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "x";
            var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
            return cleaned.Length == 0 ? "x" : cleaned;
        }
    }
}
