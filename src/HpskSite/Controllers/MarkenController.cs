using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Models.ViewModels.Training;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Märken (marksmanship proficiency badges, SHB kap 5) — Phase 1: Pistolskyttemärket.
    /// Member-facing progress + on-site sign-off + club-secretary management.
    ///
    /// Sign-off authority (per the locked design): site admins always; <b>board members</b>
    /// (Styrelse, via <see cref="BoardRoleService"/>) of the member's primary club always; and
    /// <b>Skjutledare</b> only when the club enabled it (<c>markenSignoffSkjutledare</c>).
    /// Viewing the secretary tab uses the broader club-admin gate.
    /// </summary>
    public class MarkenController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;
        private readonly BoardRoleService _boardRoles;
        private readonly ClubService _clubService;
        private readonly MarkenLedgerService _ledger;
        private readonly MarkenCandidateService _candidates;
        private readonly MarkenCompetitionService _compService;
        private readonly StandardMedalProofStorage _proofStorage;
        private readonly IDataProtector _verifyProtector;

        public MarkenController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            AdminAuthorizationService auth,
            BoardRoleService boardRoles,
            ClubService clubService,
            MarkenLedgerService ledger,
            MarkenCandidateService candidates,
            MarkenCompetitionService compService,
            StandardMedalProofStorage proofStorage,
            IDataProtectionProvider dataProtectionProvider)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _auth = auth;
            _boardRoles = boardRoles;
            _clubService = clubService;
            _ledger = ledger;
            _candidates = candidates;
            _compService = compService;
            _proofStorage = proofStorage;
            _verifyProtector = dataProtectionProvider.CreateProtector("Marken.SeriesVerify.v1");
        }

        private const string Family = Marken.FamilyPistolskytte;

        // ── Member-facing ─────────────────────────────────────────────

        /// <summary>
        /// The current member's Märken: badges, årtalsmärke ladder status, and this year's
        /// Guldfodring (the persisted row merged with the live candidate analysis).
        /// GET /umbraco/surface/Marken/GetMyMarken?year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyMarken(int? year)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });

            int y = year ?? DateTime.Now.Year;
            var pistolskytte = await BuildMemberPayloadAsync(member.Id, y, includeUnverifiedInLadder: true, isOwnView: true);
            await RecomputeCompetitionFamiliesAsync(member.Id);
            await RecomputeSeriesProofFamiliesAsync(member.Id);
            var families = await BuildFamilySummariesAsync(member.Id, y);
            return Json(new { success = true, year = y, pistolskytte, families });
        }

        /// <summary>The current member's clubs (primary + additional) for the validation-club picker.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyClubsForSeries()
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });

            var clubs = new List<object>();
            var seen = new HashSet<int>();
            void Add(int id)
            {
                if (id <= 0 || !seen.Add(id)) return;
                clubs.Add(new { id, name = _clubService.GetClubNameById(id) ?? $"Klubb {id}" });
            }

            if (int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc)) Add(pc);
            var extra = member.GetValue("memberClubIds")?.ToString();
            if (!string.IsNullOrWhiteSpace(extra))
                foreach (var part in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(part, out var cid)) Add(cid);

            return Json(new { success = true, clubs });
        }

        public class SubmitSeriesRequest
        {
            public string SeriesType { get; set; } = Marken.SeriesTypePrecision;
            public int ClubId { get; set; }
            public string WeaponGroup { get; set; } = "C";
            public List<string>? Shots { get; set; }     // precision (5)
            public string? Target { get; set; }          // speed
            public string ClaimedLevel { get; set; } = Marken.LevelGuld;
            public string? PhotoRef { get; set; }
            public string? Notes { get; set; }
        }

        /// <summary>
        /// Submit a Guldserie (precision, shot-by-shot) or Snabbserie (speed, target + valör). Lands
        /// Pending in the chosen club's validation queue. Returns a QR verify token for on-the-spot
        /// validation. POST /umbraco/surface/Marken/SubmitSeries
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitSeries([FromBody] SubmitSeriesRequest request)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            if (request == null) return Json(new { success = false, message = "Ogiltig begäran." });

            // The chosen validation club must be one the member belongs to.
            if (!MemberBelongsToClub(member, request.ClubId))
                return Json(new { success = false, message = "Välj en klubb du är medlem i." });

            var group = Marken.WeaponGroup(request.WeaponGroup);
            if (group == null) return Json(new { success = false, message = "Ogiltig vapengrupp." });

            int year = DateTime.Now.Year;
            int birthYear = _candidates.GetBirthYear(member.Id, year);

            var series = new MarkenSeries
            {
                MemberId = member.Id,
                ClubId = request.ClubId,
                BadgeFamily = Family,
                Year = year,
                SeriesDate = DateTime.Now,
                WeaponGroup = group,
                Status = Marken.SeriesStatusPending,
                PhotoFileRef = string.IsNullOrWhiteSpace(request.PhotoRef) ? null : request.PhotoRef,
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes!.Trim(),
                EnteredByMemberId = member.Id
            };

            if (request.SeriesType == Marken.SeriesTypeSpeed)
            {
                if (!Marken.IsValidSpeedTarget(request.Target))
                    return Json(new { success = false, message = "Välj ett giltigt mål." });
                if (Marken.LevelOrdinal(request.ClaimedLevel) == 0)
                    return Json(new { success = false, message = "Välj valör (brons/silver/guld)." });
                series.SeriesType = Marken.SeriesTypeSpeed;
                series.Target = request.Target;
                series.ClaimedLevel = request.ClaimedLevel;
                series.Qualifies = true; // self-declared pass; validator confirms hits-in-time
            }
            else
            {
                var shots = request.Shots ?? new List<string>();
                if (shots.Count != 5 || !shots.All(IsValidShot))
                    return Json(new { success = false, message = "Ange exakt 5 giltiga skott (0–10 eller X)." });
                int total = shots.Sum(ShotValue);
                int threshold = Marken.PrecisionThreshold(group, year, birthYear);
                series.SeriesType = Marken.SeriesTypePrecision;
                series.ClaimedLevel = Marken.LevelGuld;
                series.Shots = System.Text.Json.JsonSerializer.Serialize(shots);
                series.Total = total;
                series.Threshold = threshold;
                series.Qualifies = total >= threshold;
            }

            var id = await _ledger.InsertSeriesAsync(series);
            var token = _verifyProtector.Protect("series:" + id);

            return Json(new
            {
                success = true,
                id,
                qualifies = series.Qualifies,
                total = series.Total,
                threshold = series.Threshold,
                verifyToken = token,
                verifyUrl = $"{Request.Scheme}://{Request.Host}/marken/verifiera?t={Uri.EscapeDataString(token)}",
                message = series.SeriesType == Marken.SeriesTypePrecision && !series.Qualifies
                    ? "Sparad. Obs: serien når inte guldkravet — den räknas inte mot guldfodringen men kan ändå valideras."
                    : "Sparad och skickad för validering."
            });
        }

        /// <summary>Upload a target photo for a series. Returns an opaque ref to submit with SubmitSeries.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadSeriePhoto(IFormFile? file)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            if (file == null || file.Length == 0) return Json(new { success = false, message = "Ingen fil mottogs." });

            var (ok, error) = _proofStorage.Validate(file.FileName, file.Length);
            if (!ok) return Json(new { success = false, message = error });

            try
            {
                using var stream = file.OpenReadStream();
                var fileRef = await _proofStorage.SaveAsync(stream, file.FileName);
                return Json(new { success = true, fileRef });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte spara filen: " + ex.Message });
            }
        }

        /// <summary>Stream a series' target photo — owner, an authorized validator for its club, or site admin.</summary>
        [HttpGet]
        public async Task<IActionResult> GetSeriePhoto(int id)
        {
            var series = await _ledger.GetSeriesAsync(id);
            if (series == null || string.IsNullOrEmpty(series.PhotoFileRef)) return NotFound();

            var viewer = await GetCurrentMemberAsync();
            if (viewer == null) return Unauthorized();
            bool ok = viewer.Id == series.MemberId
                      || await _auth.IsCurrentUserAdminAsync()
                      || await CanSignOffForClubAsync(series.ClubId);
            if (!ok) return Forbid();

            var path = _proofStorage.GetFilePath(series.PhotoFileRef);
            if (path == null) return NotFound();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, StandardMedalProofStorage.ContentTypeFor(series.PhotoFileRef));
        }

        /// <summary>The current member's submitted series for a year (their own progress view).</summary>
        [HttpGet]
        public async Task<IActionResult> GetMySeries(int? year)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            int y = year ?? DateTime.Now.Year;
            var series = await _ledger.GetSeriesForMemberAsync(member.Id, y);
            var items = series.Select(SerieDto).ToList();
            foreach (var fam in MarkenFamilies.CompetitionFamilies)
                foreach (var r in await _compService.GetSelfReportedForMemberAsync(member.Id, fam.Key, y))
                    items.Add(CompResultDto(r));
            return Json(new { success = true, year = y, series = items });
        }

        /// <summary>
        /// Lightweight status of one of the current member's own series — polled by the QR modal so
        /// the shooter's screen updates the moment a functionary validates it on another device.
        /// GET /umbraco/surface/Marken/GetMySerieStatus?id=123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMySerieStatus(int id)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false });
            var s = await _ledger.GetSeriesAsync(id);
            if (s == null || s.MemberId != member.Id) return Json(new { success = false });
            return Json(new { success = true, status = s.Status });
        }

        // ── External competition self-report (competition-driven families) ──

        public class SubmitCompResultRequest
        {
            public string Family { get; set; } = "";
            public int ClubId { get; set; }
            public string CompetitionName { get; set; } = "";
            public string? CompetitionDate { get; set; }
            public string? Location { get; set; }
            public string WeaponGroup { get; set; } = "C";
            public int Dim { get; set; }       // series count (precision-shape) or station count (Fält)
            public int Total { get; set; }     // points or hits
            public string? PhotoRef { get; set; }
            public string? Notes { get; set; }
        }

        /// <summary>
        /// Submit a result from an external (non-pistol.nu) competition toward a competition-driven
        /// märke. Lands Pending in the chosen club's queue; returns a QR verify token.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitCompetitionResult([FromBody] SubmitCompResultRequest req)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            if (req == null) return Json(new { success = false, message = "Ogiltig begäran." });

            var def = MarkenFamilies.Get(req.Family);
            if (def == null || def.Pattern != MarkenPattern.CompetitionAchievement)
                return Json(new { success = false, message = "Ogiltig märkestyp." });
            if (!MemberBelongsToClub(member, req.ClubId))
                return Json(new { success = false, message = "Välj en klubb du är medlem i." });
            var group = Marken.WeaponGroup(req.WeaponGroup);
            if (group == null) return Json(new { success = false, message = "Ogiltig vapengrupp." });
            if (req.Total <= 0) return Json(new { success = false, message = "Ange ditt totalresultat." });
            if (string.IsNullOrWhiteSpace(req.CompetitionName))
                return Json(new { success = false, message = "Ange tävlingens namn." });

            DateTime date = DateTime.TryParse(req.CompetitionDate, out var d) ? d : DateTime.Now;
            var reached = def.LevelForCompetition(group, req.Dim, req.Total);

            var r = new MarkenCompetitionResult
            {
                MemberId = member.Id,
                ClubId = req.ClubId,
                BadgeFamily = req.Family,
                Year = date.Year,
                CompetitionDate = date,
                CompetitionName = req.CompetitionName.Trim(),
                Location = string.IsNullOrWhiteSpace(req.Location) ? null : req.Location!.Trim(),
                WeaponGroup = group,
                Dim = req.Dim,
                Total = req.Total,
                ReachedLevel = reached,
                Status = Marken.SeriesStatusPending,
                ProofFileRef = string.IsNullOrWhiteSpace(req.PhotoRef) ? null : req.PhotoRef,
                Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes!.Trim(),
                EnteredByMemberId = member.Id
            };
            var id = await _compService.InsertSelfReportedAsync(r);
            var token = _verifyProtector.Protect("comp:" + id);

            return Json(new
            {
                success = true,
                id,
                reachedLevel = reached,
                qualifies = reached != null,
                verifyToken = token,
                verifyUrl = $"{Request.Scheme}://{Request.Host}/marken/verifiera?t={Uri.EscapeDataString(token)}",
                message = reached == null
                    ? "Sparat. Resultatet når inte märkeskravet men kan ändå valideras."
                    : "Sparat och skickat för validering."
            });
        }

        /// <summary>The current member's self-reported competition results for a family.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyCompetitionResults(string family, int? year)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false });
            var list = await _compService.GetSelfReportedForMemberAsync(member.Id, family, year);
            return Json(new { success = true, results = list.Select(CompResultDto) });
        }

        /// <summary>Poll status of the member's own self-reported result (QR live-update).</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyCompResultStatus(int id)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false });
            var r = await _compService.GetSelfReportedAsync(id);
            if (r == null || r.MemberId != member.Id) return Json(new { success = false });
            return Json(new { success = true, status = r.Status });
        }

        /// <summary>Stream a self-reported result's proof photo — owner, authorized validator, or site admin.</summary>
        [HttpGet]
        public async Task<IActionResult> GetCompResultPhoto(int id)
        {
            var r = await _compService.GetSelfReportedAsync(id);
            if (r == null || string.IsNullOrEmpty(r.ProofFileRef)) return NotFound();
            var viewer = await GetCurrentMemberAsync();
            if (viewer == null) return Unauthorized();
            bool ok = viewer.Id == r.MemberId || await _auth.IsCurrentUserAdminAsync() || await CanSignOffForClubAsync(r.ClubId);
            if (!ok) return Forbid();
            var path = _proofStorage.GetFilePath(r.ProofFileRef);
            if (path == null) return NotFound();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, StandardMedalProofStorage.ContentTypeFor(r.ProofFileRef));
        }

        private object CompResultDto(MarkenCompetitionResult r) => new
        {
            kind = "comp",
            id = r.Id,
            memberId = r.MemberId,
            memberName = _memberService.GetById(r.MemberId)?.Name,
            clubId = r.ClubId,
            clubName = _clubService.GetClubNameById(r.ClubId),
            family = r.BadgeFamily,
            familyName = MarkenFamilies.DisplayName(r.BadgeFamily),
            year = r.Year,
            competitionDate = r.CompetitionDate,
            competitionName = r.CompetitionName,
            location = r.Location,
            weaponGroup = r.WeaponGroup,
            dim = r.Dim,
            total = r.Total,
            reachedLevel = r.ReachedLevel,
            status = r.Status,
            hasPhoto = !string.IsNullOrEmpty(r.ProofFileRef),
            validatedDate = r.ValidatedDate
        };

        // ── Series-proof families (Luftpistol / Elit) — series submission ──

        public class SubmitProofSeriesRequest
        {
            public string Family { get; set; } = "";
            public string SeriesType { get; set; } = Marken.SeriesTypePrecision; // Elit: Precision | Speed
            public int ClubId { get; set; }
            public string WeaponGroup { get; set; } = "";
            public int Total { get; set; }
            public string? PhotoRef { get; set; }
            public string? Notes { get; set; }
        }

        /// <summary>
        /// Submit one series toward a series-proof märke (Luftpistol = 10-shot air series;
        /// Elit = precision/snabb series). The series total maps to the highest valör it meets.
        /// Lands Pending in the chosen club's queue (same kind as Guldserier).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitProofSeries([FromBody] SubmitProofSeriesRequest req)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            if (req == null) return Json(new { success = false, message = "Ogiltig begäran." });

            var def = MarkenFamilies.Get(req.Family);
            if (def == null || def.Pattern != MarkenPattern.SeriesProof)
                return Json(new { success = false, message = "Ogiltig märkestyp." });
            if (!MemberBelongsToClub(member, req.ClubId))
                return Json(new { success = false, message = "Välj en klubb du är medlem i." });
            if (req.Total <= 0) return Json(new { success = false, message = "Ange seriens poäng." });

            var level = def.LevelForSeries(req.Total);
            var seriesType = def.RequiresSpeedSeriesToo && req.SeriesType == Marken.SeriesTypeSpeed
                ? Marken.SeriesTypeSpeed : Marken.SeriesTypePrecision;

            var series = new MarkenSeries
            {
                MemberId = member.Id,
                ClubId = req.ClubId,
                BadgeFamily = req.Family,
                SeriesType = seriesType,
                Year = DateTime.Now.Year,
                SeriesDate = DateTime.Now,
                WeaponGroup = string.IsNullOrWhiteSpace(req.WeaponGroup) ? "" : req.WeaponGroup.Trim(),
                ClaimedLevel = level ?? Marken.LevelBrons,
                Total = req.Total,
                Threshold = def.SeriesThreshold != null ? def.SeriesThreshold[0] : 0,
                Qualifies = level != null,
                Status = Marken.SeriesStatusPending,
                PhotoFileRef = string.IsNullOrWhiteSpace(req.PhotoRef) ? null : req.PhotoRef,
                Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes!.Trim(),
                EnteredByMemberId = member.Id
            };
            var id = await _ledger.InsertSeriesAsync(series);
            var token = _verifyProtector.Protect("series:" + id);

            return Json(new
            {
                success = true,
                id,
                qualifies = series.Qualifies,
                verifyToken = token,
                verifyUrl = $"{Request.Scheme}://{Request.Host}/marken/verifiera?t={Uri.EscapeDataString(token)}",
                message = level == null
                    ? "Sparat. Serien når inte bronsnivån men kan ändå valideras."
                    : "Sparat och skickat för validering."
            });
        }

        // ── Validation queue + verify (board / Skjutledare) ───────────

        /// <summary>Pending series the current user is authorized to validate (their board/Skjutledare clubs).</summary>
        [HttpGet]
        public async Task<IActionResult> GetPendingSeries()
        {
            var me = await GetCurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Inte inloggad." });

            var (all, clubIds) = await GetMarkenSignoffScopeAsync();
            if (!all && clubIds.Count == 0)
                return Json(new { success = true, items = Array.Empty<object>() });

            var ids = all ? null : (IEnumerable<int>)clubIds;
            var series = await _ledger.GetPendingSeriesAsync(ids);
            var comps = await _compService.GetPendingSelfReportedAsync(ids);
            var items = series.Select(SerieDto).Concat(comps.Select(CompResultDto)).ToList();
            return Json(new { success = true, items });
        }

        /// <summary>Evidence detail for the QR verify page — only returned to an authorized validator.</summary>
        [HttpGet]
        public async Task<IActionResult> GetSerieForVerify(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return Json(new { success = false, message = "Ogiltig länk." });
            string raw;
            try { raw = _verifyProtector.Unprotect(token); }
            catch { return Json(new { success = false, message = "Ogiltig eller utgången länk." }); }

            var (kind, id) = ParseEvidenceToken(raw);
            var me = await GetCurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Du måste vara inloggad.", needsLogin = true });

            if (kind == "comp")
            {
                var r = await _compService.GetSelfReportedAsync(id);
                if (r == null) return Json(new { success = false, message = "Resultatet hittades inte." });
                if (!await CanSignOffForClubAsync(r.ClubId))
                    return Json(new { success = false, message = "Du har inte behörighet att validera för den här klubben." });
                return Json(new { success = true, serie = CompResultDto(r) });
            }

            var series = await _ledger.GetSeriesAsync(id);
            if (series == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (!await CanValidateSeriesAsync(series))
                return Json(new { success = false, message = "Du har inte behörighet att validera för den här klubben." });
            return Json(new { success = true, serie = SerieDto(series) });
        }

        private static (string Kind, int Id) ParseEvidenceToken(string raw)
        {
            var parts = raw.Split(':', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var i)) return (parts[0], i);
            return ("series", int.TryParse(raw, out var j) ? j : 0); // legacy plain-id = series
        }

        public class EvidenceActionRequest { public string Kind { get; set; } = "series"; public int Id { get; set; } }

        /// <summary>Unified validate — dispatches to series or competition-result by kind.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyEvidence([FromBody] EvidenceActionRequest request)
            => request?.Kind == "comp" ? await SetCompResultStatus(request.Id, Marken.StatusVerified)
                                       : await SetSeriesStatus(request?.Id ?? 0, Marken.StatusVerified);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectEvidence([FromBody] EvidenceActionRequest request)
            => request?.Kind == "comp" ? await SetCompResultStatus(request.Id, Marken.StatusRejected)
                                       : await SetSeriesStatus(request?.Id ?? 0, Marken.StatusRejected);

        private async Task<IActionResult> SetCompResultStatus(int id, string status)
        {
            var r = await _compService.GetSelfReportedAsync(id);
            if (r == null) return Json(new { success = false, message = "Resultatet hittades inte." });
            if (!await CanSignOffForClubAsync(r.ClubId)) return Json(new { success = false, message = "Åtkomst nekad." });
            var (ok, msg) = await _compService.SetSelfReportedStatusAsync(id, status, await GetCurrentMemberIdAsync());
            if (ok && status == Marken.StatusVerified) await RecomputeCompetitionFamiliesAsync(r.MemberId);
            return Json(new { success = ok, message = ok ? (status == Marken.StatusVerified ? "Godkänd." : "Avvisad.") : msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifySeries([FromBody] IdRequest request) => await SetSeriesStatus(request?.Id ?? 0, Marken.StatusVerified);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectSeries([FromBody] IdRequest request) => await SetSeriesStatus(request?.Id ?? 0, Marken.StatusRejected);

        private async Task<IActionResult> SetSeriesStatus(int id, string status)
        {
            var series = await _ledger.GetSeriesAsync(id);
            if (series == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (!await CanValidateSeriesAsync(series))
                return Json(new { success = false, message = "Åtkomst nekad." });

            int validatorId = await GetCurrentMemberIdAsync();
            var (ok, msg) = await _ledger.SetSeriesStatusAsync(id, status, validatorId);

            // No separate sign-off: validating a series may complete (or un-complete) the member's
            // yearly badge automatically. Dispatch by family.
            if (ok)
            {
                if (series.BadgeFamily == Family)
                    await RecomputeYearlyQualificationAsync(series.MemberId, series.Year, validatorId);
                else if (MarkenFamilies.Get(series.BadgeFamily)?.Pattern == MarkenPattern.SeriesProof)
                    await RecomputeSeriesProofFamiliesAsync(series.MemberId);
            }

            return Json(new { success = ok, message = ok ? (status == Marken.StatusVerified ? "Godkänd." : "Avvisad.") : msg });
        }

        /// <summary>
        /// Pending series for one club, for the club-admin Märken tab. Viewable by club admins;
        /// the buttons are only active for users who may actually validate for the club.
        /// GET /umbraco/surface/Marken/GetClubPendingSeries?clubId=1098
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubPendingSeries(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var series = await _ledger.GetPendingSeriesAsync(new[] { clubId });
            var comps = await _compService.GetPendingSelfReportedAsync(new[] { clubId });
            var items = series.Select(SerieDto).Concat(comps.Select(CompResultDto)).ToList();
            return Json(new { success = true, canValidate = await CanSignOffForClubAsync(clubId), items });
        }

        // ── Club secretary: reads ─────────────────────────────────────

        /// <summary>
        /// Members of a club (by primary club) with any märke activity, plus pending sign-off counts.
        /// GET /umbraco/surface/Marken/GetClubMarkenSummary?clubId=1098&year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubMarkenSummary(int clubId, int? year)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            int y = year ?? DateTime.Now.Year;
            var activeIds = await _ledger.GetAllActiveMemberIdsAsync();

            var rows = new List<MarkenSummaryRow>();
            foreach (var mid in activeIds)
            {
                var member = _memberService.GetById(mid);
                if (member == null) continue;
                if (!int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc) || pc != clubId) continue;

                var badges = await _ledger.GetBadgesForMemberAsync(mid, Family);
                var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3).OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var guld = badges.FirstOrDefault(b => b.Level == Marken.LevelGuld);
                var ladder = await _ledger.GetArtalsmarkeStatusAsync(mid, Family, includeUnverified: false);
                var pending = await _ledger.GetPendingCountAsync(mid, Family);
                var thisYearQ = await _ledger.GetQualificationForYearAsync(mid, Family, y);

                rows.Add(new MarkenSummaryRow
                {
                    MemberId = mid,
                    Name = member.Name ?? $"Medlem {mid}",
                    TopLevel = top?.Level ?? "",
                    GuldNumber = guld?.UniqueNumber ?? "",
                    FulfilledYears = ladder.FulfilledYears,
                    Artalsmarke = ladder.CurrentName,
                    Pending = pending,
                    ThisYearStatus = thisYearQ == null ? "" : Marken.StatusDisplay(thisYearQ.Status),
                    ThisYearFulfilled = thisYearQ?.Fulfilled ?? false
                });
            }

            var members = rows
                .OrderByDescending(r => r.Pending)
                .ThenBy(r => r.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false))
                .Select(r => new
                {
                    memberId = r.MemberId,
                    name = r.Name,
                    topLevel = r.TopLevel,
                    guldNumber = r.GuldNumber,
                    fulfilledYears = r.FulfilledYears,
                    artalsmarke = r.Artalsmarke,
                    pending = r.Pending,
                    thisYearStatus = r.ThisYearStatus,
                    thisYearFulfilled = r.ThisYearFulfilled
                });

            return Json(new { success = true, year = y, members });
        }

        /// <summary>
        /// One member's full Märken detail: badges, qualification history, and this year's live
        /// candidate (so the on-site approver sees what's auto-detected).
        /// GET /umbraco/surface/Marken/GetMemberMarkenDetail?memberId=2043&year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberMarkenDetail(int memberId, int? year)
        {
            if (!await CanViewMemberAsync(memberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            int y = year ?? DateTime.Now.Year;
            var payload = await BuildMemberPayloadAsync(memberId, y, includeUnverifiedInLadder: false, isOwnView: false);
            // Add the acting user's sign-off capability so the UI can show/hide the buttons.
            return Json(new
            {
                success = true,
                canSignOff = await CanSignOffForMemberAsync(memberId),
                detail = payload
            });
        }

        // ── Club secretary: writes ────────────────────────────────────
        // (No manual Guldfodring sign-off — the year completes automatically when its parts are
        //  validated; see RecomputeYearlyQualificationAsync, called whenever a series is verified.)

        /// <summary>
        /// Award (or upgrade to) a base valör (Brons/Silver/Guld) for a member, signed off by the
        /// acting functionary. For Guld, an optional national registration number can be supplied.
        /// POST /umbraco/surface/Marken/AwardBadge  { memberId, level, year, uniqueNumber?, note? }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AwardBadge([FromBody] AwardBadgeRequest request)
        {
            int memberId = request?.MemberId ?? 0;
            string level = (request?.Level ?? "").Trim();
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });
            if (Marken.LevelOrdinal(level) == 0)
                return Json(new { success = false, message = "Ogiltig valör." });
            if (!await CanSignOffForMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet att tilldela märken för den här medlemmen." });

            int actingId = await GetCurrentMemberIdAsync();
            int y = request?.Year is > 0 ? request!.Year : DateTime.Now.Year;

            // Upsert: one badge per (member, family, level). Re-awarding updates date/number.
            var existing = (await _ledger.GetBadgesForMemberAsync(memberId, Family, includeRejected: true))
                .FirstOrDefault(b => b.Level == level);

            if (existing == null)
            {
                var badge = new MemberBadge
                {
                    MemberId = memberId,
                    BadgeFamily = Family,
                    Level = level,
                    LevelOrdinal = Marken.LevelOrdinal(level),
                    AchievedYear = y,
                    AchievedDate = DateTime.Now,
                    Source = Marken.SourceAdmin,
                    Status = Marken.StatusVerified,
                    SignedOffByMemberId = actingId,
                    SignedOffDate = DateTime.Now,
                    UniqueNumber = level == Marken.LevelGuld && !string.IsNullOrWhiteSpace(request?.UniqueNumber)
                        ? request!.UniqueNumber!.Trim() : null,
                    Notes = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim(),
                    EnteredByMemberId = actingId
                };
                await _ledger.InsertBadgeAsync(badge);
            }
            else
            {
                existing.AchievedYear = y;
                existing.AchievedDate ??= DateTime.Now;
                existing.Status = Marken.StatusVerified;
                existing.SignedOffByMemberId = actingId;
                existing.SignedOffDate = DateTime.Now;
                if (level == Marken.LevelGuld && !string.IsNullOrWhiteSpace(request?.UniqueNumber))
                    existing.UniqueNumber = request!.UniqueNumber!.Trim();
                if (!string.IsNullOrWhiteSpace(request?.Note)) existing.Notes = request!.Note!.Trim();
                await _ledger.UpdateBadgeAsync(existing);
            }

            return Json(new { success = true, message = $"{Marken.FamilyDisplayName(Family)} i {level} tilldelat." });
        }

        /// <summary>Set/replace the national registration number on a member's Guld badge.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBadgeUniqueNumber([FromBody] UniqueNumberRequest request)
        {
            var badge = await _ledger.GetBadgeAsync(request?.BadgeId ?? 0);
            if (badge == null) return Json(new { success = false, message = "Märket hittades inte." });
            if (!await CanSignOffForMemberAsync(badge.MemberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var (ok, msg) = await _ledger.SetUniqueNumberAsync(badge.Id, request?.UniqueNumber);
            return Json(new { success = ok, message = ok ? "Registreringsnummer sparat." : msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBadge([FromBody] IdRequest request)
        {
            var badge = await _ledger.GetBadgeAsync(request?.Id ?? 0);
            if (badge == null) return Json(new { success = false, message = "Märket hittades inte." });
            if (!await CanSignOffForMemberAsync(badge.MemberId))
                return Json(new { success = false, message = "Åtkomst nekad." });
            var (ok, msg) = await _ledger.DeleteBadgeAsync(badge.Id);
            return Json(new { success = ok, message = ok ? "Borttaget." : msg });
        }

        // ── Exports & report ──────────────────────────────────────────

        /// <summary>
        /// CSV of the club's members' Pistolskyttemärket status for a year — base valör, Guld number,
        /// fulfilled-year count, current årtalsmärke, and this year's Guldfodring status. For MAP.
        /// GET /umbraco/surface/Marken/ExportClubMarken?clubId=1098&year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportClubMarken(int clubId, int? year)
        {
            if (clubId <= 0) return Content("Ogiltigt klubb-ID.");
            if (!await _auth.IsClubAdminForClub(clubId)) return Content("Åtkomst nekad.");

            int y = year ?? DateTime.Now.Year;
            var activeIds = await _ledger.GetAllActiveMemberIdsAsync();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Medlem;Personnr-födelseår;Högsta valör;Guldnummer;Godkända guldfodringar;Årtalsmärke;Guldfodring " + y);

            var lines = new List<(string Name, string Row)>();
            foreach (var mid in activeIds)
            {
                var member = _memberService.GetById(mid);
                if (member == null) continue;
                if (!int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc) || pc != clubId) continue;

                var badges = await _ledger.GetBadgesForMemberAsync(mid, Family);
                var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3).OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var guld = badges.FirstOrDefault(b => b.Level == Marken.LevelGuld);
                var ladder = await _ledger.GetArtalsmarkeStatusAsync(mid, Family, includeUnverified: false);
                var thisYearQ = await _ledger.GetQualificationForYearAsync(mid, Family, y);
                int birthYear = _candidates.GetBirthYear(mid, y);

                var name = member.Name ?? $"Medlem {mid}";
                lines.Add((name, string.Join(";", new[]
                {
                    Csv(name),
                    birthYear > 0 ? birthYear.ToString() : "",
                    Csv(top?.Level ?? ""),
                    Csv(guld?.UniqueNumber ?? ""),
                    ladder.FulfilledYears.ToString(),
                    Csv(ladder.CurrentName),
                    Csv(thisYearQ == null ? "" : Marken.StatusDisplay(thisYearQ.Status))
                })));
            }

            foreach (var (_, row) in lines.OrderBy(l => l.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false)))
                sb.AppendLine(row);

            return CsvFile(sb.ToString(), $"marken-pistolskytte-{clubId}-{y}.csv");
        }

        /// <summary>
        /// A neutral printable record of a member's Märken + Guldfodringar history. Makes no claim
        /// about what the record is for. Self-contained HTML (print-friendly), no Umbraco node needed.
        /// GET /umbraco/surface/Marken/PrintMemberMarken?memberId=2043
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PrintMemberMarken(int memberId)
        {
            if (!await CanViewMemberAsync(memberId))
                return Content("Åtkomst nekad.");

            var member = _memberService.GetById(memberId);
            var badges = await _ledger.GetBadgesForMemberAsync(memberId, Family);
            var quals = (await _ledger.GetQualificationsForMemberAsync(memberId, Family)).OrderBy(q => q.Year).ToList();
            var ladder = await _ledger.GetArtalsmarkeStatusAsync(memberId, Family, includeUnverified: false);
            var guld = badges.FirstOrDefault(b => b.Level == Marken.LevelGuld);

            string html = BuildPrintHtml(member?.Name ?? $"Medlem {memberId}", badges, quals, ladder, guld);
            return Content(html, "text/html; charset=utf-8");
        }

        // ── Helpers ───────────────────────────────────────────────────

        private async Task<object> BuildMemberPayloadAsync(int memberId, int year, bool includeUnverifiedInLadder, bool isOwnView)
        {
            var member = _memberService.GetById(memberId);

            // Lazy Skyttetrappan → valör materialization: members who completed Nybörjartrappa
            // Brons/Silver/Guld before this link existed get their base badges on first view, with
            // the real completion date + approver name carried from completedTrainingSteps. Idempotent.
            await SyncTrappaForMemberAsync(member);

            // Keep the year's Guldfodring in sync with current validated evidence (covers hosted-comp
            // results / fält medals that change outside a series validation). Lazy, no validator.
            await RecomputeYearlyQualificationAsync(memberId, year, null);

            var badges = await _ledger.GetBadgesForMemberAsync(memberId, Family);
            var quals = await _ledger.GetQualificationsForMemberAsync(memberId, Family);
            var ladder = await _ledger.GetArtalsmarkeStatusAsync(memberId, Family, includeUnverifiedInLadder);
            var cand = await _candidates.AnalyzePistolskytteAsync(memberId, year);
            var thisYearQ = quals.FirstOrDefault(q => q.Year == year);

            return new
            {
                success = true,
                memberId,
                memberName = member?.Name,
                year,
                family = Family,
                familyName = Marken.FamilyDisplayName(Family),
                badges = badges.Select(b => new
                {
                    id = b.Id,
                    level = b.Level,
                    levelOrdinal = b.LevelOrdinal,
                    achievedYear = b.AchievedYear,
                    achievedDate = b.AchievedDate,
                    uniqueNumber = b.UniqueNumber,
                    status = b.Status,
                    statusDisplay = Marken.StatusDisplay(b.Status),
                    source = b.Source,
                    sourceDisplay = Marken.SourceDisplay(b.Source),
                    isGuld = b.Level == Marken.LevelGuld
                }),
                artalsmarke = new
                {
                    fulfilledYears = ladder.FulfilledYears,
                    current = ladder.CurrentName,
                    next = ladder.NextName,
                    nextAtYears = ladder.NextAtYears
                },
                guldfodring = new
                {
                    year,
                    // Live candidate
                    part1Met = cand.Part1Met,
                    part1Note = cand.Part1ThresholdNote,
                    qualifyingSeriesCount = cand.QualifyingSeries.Count,
                    pendingPrecisionCount = cand.PendingPrecisionCount,
                    requiredSeries = cand.RequiredSeries,
                    bestSeries = cand.QualifyingSeries.Take(5).Select(s => new
                    {
                        date = s.Date,
                        weaponGroup = s.WeaponGroup,
                        score = s.Score,
                        threshold = s.Threshold,
                        source = s.Source,
                        label = s.Label
                    }),
                    part2Met = cand.Part2Met,
                    part2Source = cand.Part2Source,
                    part2Detail = cand.Part2Detail,
                    part2ViaFalt = cand.Part2ViaFalt,
                    part2SeriesCount = cand.Part2SeriesCount,
                    pendingSpeedCount = cand.PendingSpeedCount,
                    part2Required = cand.RequiredSpeedSeries,
                    candidateBothMet = cand.BothPartsMet,
                    // Persisted row (sign-off state)
                    persisted = thisYearQ == null ? null : new
                    {
                        id = thisYearQ.Id,
                        status = thisYearQ.Status,
                        statusDisplay = Marken.StatusDisplay(thisYearQ.Status),
                        fulfilled = thisYearQ.Fulfilled,
                        part1Met = thisYearQ.Part1Met,
                        part2Met = thisYearQ.Part2Met,
                        part2Source = thisYearQ.Part2Source,
                        part2Note = thisYearQ.Part2Note,
                        signedOff = thisYearQ.SignedOffByMemberId.HasValue,
                        signedOffDate = thisYearQ.SignedOffDate
                    }
                }
            };
        }

        /// <summary>
        /// Idempotently materialize Pistolskyttemärket base valörer from the member's completed
        /// Skyttetrappan levels (1/2/3). Best-effort — a sync failure must never break a read.
        /// </summary>
        private async Task SyncTrappaForMemberAsync(Umbraco.Cms.Core.Models.IMember? member)
        {
            if (member == null) return;
            try
            {
                var progress = MemberProgress.FromMember(member);
                await _ledger.SyncTrappaBadgesAsync(member.Id, progress.CompletedSteps, null);
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Auto-award competition-driven family valörer + årtalsmärke years from the member's hosted
        /// (and verified self-reported) competition results. Idempotent; runs lazily on read.
        /// </summary>
        private async Task RecomputeCompetitionFamiliesAsync(int memberId)
        {
            foreach (var fam in MarkenFamilies.CompetitionFamilies)
            {
                CompFamilyAnalysis a;
                try { a = await _compService.AnalyzeAsync(memberId, fam.Key, DateTime.Now.Year); }
                catch { continue; }

                if (!string.IsNullOrEmpty(a.EarnedLevel))
                {
                    int earnedYear = a.GuldMetYears.Count > 0 ? a.GuldMetYears[0] : DateTime.Now.Year;
                    await _ledger.EnsureBadgeAsync(memberId, fam.Key, a.EarnedLevel!, earnedYear, Marken.SourceAuto);
                }
                // Årtalsmärke years = guld-met years after the first (the first earns the guld märke).
                foreach (var gy in a.GuldMetYears.Skip(1))
                    await _ledger.EnsureFulfilledYearAsync(memberId, fam.Key, gy);
            }
        }

        /// <summary>
        /// Series-proof analysis (Luftpistol/Elit): per year, the highest valör reached by ≥ required
        /// series at that level (Elit also needs ≥ required snabb series). Returns the highest valör
        /// across years + the guld-met years + this-year counts.
        /// </summary>
        private async Task<(string? Earned, List<int> GuldYears, List<MarkenSeries> ThisYear)>
            AnalyzeSeriesProofAsync(int memberId, MarkenFamilyDef def, int displayYear)
        {
            var series = await _ledger.GetVerifiedSeriesByFamilyAsync(memberId, def.Key);
            string? earned = null;
            var guldYears = new List<int>();
            foreach (var yg in series.GroupBy(s => s.Year))
            {
                var lvl = SeriesProofLevel(def, yg.ToList());
                if (Marken.LevelOrdinal(lvl) > Marken.LevelOrdinal(earned)) earned = lvl;
                if (lvl == Marken.LevelGuld) guldYears.Add(yg.Key);
            }
            guldYears.Sort();
            return (earned, guldYears, series.Where(s => s.Year == displayYear).ToList());
        }

        /// <summary>Count of series qualifying for a level (Elit = min of precision + snabb counts).</summary>
        private static int SeriesProofCount(MarkenFamilyDef def, List<MarkenSeries> series, string level)
        {
            if (def.SeriesThreshold == null) return 0;
            int thr = def.SeriesThreshold[Marken.LevelOrdinal(level) - 1];
            if (def.RequiresSpeedSeriesToo)
            {
                int prec = series.Count(s => s.SeriesType == Marken.SeriesTypePrecision && s.Total >= thr);
                int speed = series.Count(s => s.SeriesType == Marken.SeriesTypeSpeed && s.Total >= thr);
                return Math.Min(prec, speed);
            }
            return series.Count(s => s.Total >= thr);
        }

        /// <summary>Highest valör a set of verified series satisfies for a series-proof family.</summary>
        private static string? SeriesProofLevel(MarkenFamilyDef def, List<MarkenSeries> series)
        {
            foreach (var level in new[] { Marken.LevelGuld, Marken.LevelSilver, Marken.LevelBrons })
                if (SeriesProofCount(def, series, level) >= def.SeriesRequired) return level;
            return null;
        }

        /// <summary>Next valör up from the current one (null current → Brons; Guld → null).</summary>
        private static string? NextLevel(string? earned) => earned switch
        {
            null or "" => Marken.LevelBrons,
            Marken.LevelBrons => Marken.LevelSilver,
            Marken.LevelSilver => Marken.LevelGuld,
            _ => null
        };

        /// <summary>Auto-award series-proof family valörer + årtalsmärke years (lazy on read / on validation).</summary>
        private async Task RecomputeSeriesProofFamiliesAsync(int memberId)
        {
            foreach (var fam in MarkenFamilies.SeriesProofFamilies)
            {
                (string? earned, List<int> guldYears, List<MarkenSeries> thisYear) tuple;
                try { tuple = await AnalyzeSeriesProofAsync(memberId, fam, DateTime.Now.Year); }
                catch { continue; }
                if (!string.IsNullOrEmpty(tuple.earned))
                    await _ledger.EnsureBadgeAsync(memberId, fam.Key, tuple.earned!,
                        tuple.guldYears.Count > 0 ? tuple.guldYears[0] : DateTime.Now.Year, Marken.SourceAuto);
                foreach (var gy in tuple.guldYears.Skip(1))
                    await _ledger.EnsureFulfilledYearAsync(memberId, fam.Key, gy);
            }
        }

        /// <summary>Read-only per-family summaries (competition + series-proof families) for the member view.</summary>
        private async Task<List<object>> BuildFamilySummariesAsync(int memberId, int year)
        {
            var pistolBadges = await _ledger.GetBadgesForMemberAsync(memberId, Marken.FamilyPistolskytte);
            int pistolTop = pistolBadges.Where(b => b.LevelOrdinal is >= 1 and <= 3)
                .Select(b => b.LevelOrdinal).DefaultIfEmpty(0).Max();

            var list = new List<object>();

            // Competition-driven families — one section each, always shown.
            foreach (var fam in MarkenFamilies.CompetitionFamilies)
            {
                CompFamilyAnalysis a;
                try { a = await _compService.AnalyzeAsync(memberId, fam.Key, year); }
                catch { continue; }

                var badges = await _ledger.GetBadgesForMemberAsync(memberId, fam.Key);
                var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3)
                    .OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var ladder = await _ledger.GetArtalsmarkeStatusAsync(memberId, fam.Key, includeUnverified: false);
                var earned = top?.Level ?? a.EarnedLevel;
                bool prereqOk = fam.PrereqPistolskytteLevel == null || pistolTop >= Marken.LevelOrdinal(fam.PrereqPistolskytteLevel);

                var next = NextLevel(earned);
                string status;
                if (next == null)
                    status = ladder.FulfilledYears > 0
                        ? $"Guldmärket uppnått · {ladder.CurrentName}"
                        : "Guldmärket uppnått.";
                else
                {
                    int atNext = a.ThisYear.Count(e => Marken.LevelOrdinal(e.ReachedLevel) >= Marken.LevelOrdinal(next));
                    status = $"Saknar {atNext}/{a.CompetitionsRequired} tävlingar på {next.ToLowerInvariant()}-nivå i år "
                           + $"(har {atNext}) — krets-/landsdels-/riks-/nationell tävling.";
                }

                list.Add(new
                {
                    family = fam.Key,
                    displayName = fam.DisplayName,
                    pattern = "comp",
                    earnedLevel = earned,
                    nextLevel = next,
                    statusText = status,
                    compsRequired = a.CompetitionsRequired,
                    thisYearComps = a.ThisYear.Select(e => new { name = e.CompetitionName, group = e.WeaponGroup, total = e.Total, level = e.ReachedLevel, source = e.Source }),
                    artalsmarke = new { current = ladder.CurrentName, fulfilledYears = ladder.FulfilledYears, next = ladder.NextName, nextAtYears = ladder.NextAtYears },
                    prereqText = prereqOk ? null : fam.PrereqText
                });
            }

            // Series-proof families (Luftpistol / Elit) — one section each, always shown.
            foreach (var fam in MarkenFamilies.SeriesProofFamilies)
            {
                (string? earned, List<int> guldYears, List<MarkenSeries> thisYear) sp;
                try { sp = await AnalyzeSeriesProofAsync(memberId, fam, year); }
                catch { continue; }

                var badges = await _ledger.GetBadgesForMemberAsync(memberId, fam.Key);
                var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3).OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var ladder = await _ledger.GetArtalsmarkeStatusAsync(memberId, fam.Key, includeUnverified: false);
                var earned = top?.Level ?? sp.earned;
                bool prereqOk = fam.PrereqPistolskytteLevel == null || pistolTop >= Marken.LevelOrdinal(fam.PrereqPistolskytteLevel);

                var next = NextLevel(earned);
                string status;
                if (next == null)
                    status = ladder.FulfilledYears > 0 ? $"Guldmärket uppnått · {ladder.CurrentName}" : "Guldmärket uppnått.";
                else
                {
                    int atNext = SeriesProofCount(fam, sp.thisYear, next);
                    status = $"Saknar {Math.Max(0, fam.SeriesRequired - atNext)} av {fam.SeriesRequired} serier på {next.ToLowerInvariant()}-nivå i år (har {atNext})"
                           + (fam.RequiresSpeedSeriesToo ? " — av både precisions- och snabbserier." : ".");
                }

                list.Add(new
                {
                    family = fam.Key,
                    displayName = fam.DisplayName,
                    pattern = "series",
                    earnedLevel = earned,
                    nextLevel = next,
                    statusText = status,
                    seriesRequired = fam.SeriesRequired,
                    requiresSpeedSeriesToo = fam.RequiresSpeedSeriesToo,
                    artalsmarke = new { current = ladder.CurrentName, fulfilledYears = ladder.FulfilledYears, next = ladder.NextName, nextAtYears = ladder.NextAtYears },
                    prereqText = prereqOk ? null : fam.PrereqText
                });
            }
            return list;
        }

        /// <summary>
        /// Recompute a member's yearly Guldfodring from validated evidence and materialize it — no
        /// manual sign-off. When both parts are met the qualification row is written Fulfilled +
        /// Verified (so it counts toward the årtalsmärke ladder); when they're no longer met (e.g. a
        /// series was rejected) any existing row is downgraded. Called after every series validation
        /// and lazily on read. Generic seam for future badge families (dispatches on family).
        /// </summary>
        private async Task RecomputeYearlyQualificationAsync(int memberId, int year, int? validatorId)
        {
            // Phase 1: Pistolskyttemärket. Future families add their own analyzer here.
            var cand = await _candidates.AnalyzePistolskytteAsync(memberId, year);
            var existing = await _ledger.GetQualificationForYearAsync(memberId, Family, year);

            if (cand.BothPartsMet)
            {
                var q = existing ?? new MemberBadgeQualification
                {
                    MemberId = memberId,
                    BadgeFamily = Family,
                    Year = year,
                    EnteredByMemberId = validatorId ?? 0
                };
                q.Part1Met = true;
                q.Part1Source = Marken.PartSourceCompetition; // validated series / hosted comp
                q.Part1Date ??= DateTime.Now;
                q.Part1RefId = cand.QualifyingSeries.FirstOrDefault()?.Id;
                q.Part1Note = cand.Part1ThresholdNote;
                q.Part2Met = true;
                q.Part2Source = cand.Part2Source;
                q.Part2Date ??= DateTime.Now;
                q.Part2Note = cand.Part2Detail;
                q.Fulfilled = true;
                q.Status = Marken.StatusVerified;
                q.SignedOffByMemberId = validatorId ?? q.SignedOffByMemberId;
                q.SignedOffDate ??= DateTime.Now;
                await _ledger.UpsertQualificationAsync(q);
            }
            else if (existing != null)
            {
                // No longer complete — reflect current parts; drops out of the årtalsmärke count.
                existing.Part1Met = cand.Part1Met;
                existing.Part2Met = cand.Part2Met;
                existing.Fulfilled = false;
                existing.Status = Marken.StatusReported;
                existing.SignedOffByMemberId = null;
                existing.SignedOffDate = null;
                await _ledger.UpsertQualificationAsync(existing);
            }
        }

        /// <summary>Sign-off authority for a member's Guldfodring uses their primary club.</summary>
        private async Task<bool> CanSignOffForMemberAsync(int memberId)
            => await CanSignOffForClubAsync(GetPrimaryClubId(memberId));

        /// <summary>Site admin, board member (Styrelse) of the club, or (if the club enabled it) Skjutledare.</summary>
        private async Task<bool> CanSignOffForClubAsync(int clubId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            if (clubId <= 0) return false;

            int actingId = await GetCurrentMemberIdAsync();
            if (actingId <= 0) return false;

            var board = _boardRoles.GetBoardMembers(DocumentOwnerType.Club, clubId, boardOnly: true);
            if (board.Any(r => r.MemberId == actingId)) return true;

            if (SkjutledareSignoffEnabled(clubId) && await _auth.IsSkjutledareForClub(clubId)) return true;
            return false;
        }

        private Task<bool> CanValidateSeriesAsync(MarkenSeries s) => CanSignOffForClubAsync(s.ClubId);

        /// <summary>(All, ClubIds) describing where the current user may validate märke series.</summary>
        private async Task<(bool All, HashSet<int> ClubIds)> GetMarkenSignoffScopeAsync()
        {
            if (await _auth.IsCurrentUserAdminAsync()) return (true, new HashSet<int>());
            var clubs = new HashSet<int>();
            int actingId = await GetCurrentMemberIdAsync();
            if (actingId <= 0) return (false, clubs);

            foreach (var c in _boardRoles.GetClubIdsWhereBoardMember(actingId)) clubs.Add(c);
            foreach (var c in await _auth.GetSkjutledareClubIds())
                if (SkjutledareSignoffEnabled(c)) clubs.Add(c);
            return (false, clubs);
        }

        private static bool MemberBelongsToClub(Umbraco.Cms.Core.Models.IMember member, int clubId)
        {
            if (clubId <= 0) return false;
            if (int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc) && pc == clubId) return true;
            var extra = member.GetValue("memberClubIds")?.ToString();
            if (!string.IsNullOrWhiteSpace(extra))
                foreach (var part in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(part, out var cid) && cid == clubId) return true;
            return false;
        }

        private static bool IsValidShot(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            var v = s.Trim().ToUpperInvariant();
            if (v == "X") return true;
            return int.TryParse(v, out var n) && n >= 0 && n <= 10;
        }

        private static int ShotValue(string s)
        {
            var v = s.Trim().ToUpperInvariant();
            if (v == "X") return 10;
            return int.TryParse(v, out var n) ? n : 0;
        }

        /// <summary>Shared JSON shape for a series across the member view, the queue, and the QR verify page.</summary>
        private object SerieDto(MarkenSeries s)
        {
            List<string> shots;
            try { shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.Shots) ?? new(); }
            catch { shots = new(); }

            return new
            {
                kind = "series",
                id = s.Id,
                memberId = s.MemberId,
                memberName = _memberService.GetById(s.MemberId)?.Name,
                clubId = s.ClubId,
                clubName = _clubService.GetClubNameById(s.ClubId),
                family = s.BadgeFamily,
                familyName = MarkenFamilies.DisplayName(s.BadgeFamily),
                seriesType = s.SeriesType,
                seriesTypeName = Marken.SeriesTypeDisplay(s.SeriesType),
                year = s.Year,
                seriesDate = s.SeriesDate,
                weaponGroup = s.WeaponGroup,
                claimedLevel = s.ClaimedLevel,
                shots,
                total = s.Total,
                threshold = s.Threshold,
                qualifies = s.Qualifies,
                target = s.Target,
                targetName = Marken.SpeedTargetDisplay(s.Target),
                speedRequirement = s.SeriesType == Marken.SeriesTypeSpeed ? Marken.SpeedRequirementText(s.ClaimedLevel) : null,
                status = s.Status,
                hasPhoto = !string.IsNullOrEmpty(s.PhotoFileRef),
                validatedDate = s.ValidatedDate
            };
        }

        /// <summary>Viewing detail uses the broader club-admin gate (site/regional/club admin).</summary>
        private async Task<bool> CanViewMemberAsync(int memberId)
        {
            if (await _auth.IsCurrentUserAdminAsync()) return true;
            // The member themselves can view their own detail.
            var self = await GetCurrentMemberAsync();
            if (self != null && self.Id == memberId) return true;
            int clubId = GetPrimaryClubId(memberId);
            return clubId > 0 && await _auth.IsClubAdminForClub(clubId);
        }

        /// <summary>Reads the per-club <c>markenSignoffSkjutledare</c> toggle (default false = board only).</summary>
        private bool SkjutledareSignoffEnabled(int clubId)
        {
            try
            {
                var club = _contentService.GetById(clubId);
                if (club == null) return false;
                if (!club.HasProperty("markenSignoffSkjutledare")) return false;
                return club.GetValue<bool>("markenSignoffSkjutledare");
            }
            catch { return false; }
        }

        private int GetPrimaryClubId(int memberId)
        {
            var member = _memberService.GetById(memberId);
            if (member != null && int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var clubId))
                return clubId;
            return 0;
        }

        private async Task<Umbraco.Cms.Core.Models.IMember?> GetCurrentMemberAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            return _memberService.GetByEmail(current.Email ?? string.Empty);
        }

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var m = await GetCurrentMemberAsync();
            return m?.Id ?? 0;
        }

        private static string BuildPrintHtml(string memberName, List<MemberBadge> badges,
            List<MemberBadgeQualification> quals, ArtalsmarkeStatus ladder, MemberBadge? guld)
        {
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new System.Text.StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='sv'><head><meta charset='utf-8'>");
            sb.Append("<title>Märkesutskrift – ").Append(Enc(memberName)).Append("</title>");
            sb.Append("<style>body{font-family:Arial,Helvetica,sans-serif;margin:2rem;color:#222}h1{font-size:1.4rem}");
            sb.Append("table{border-collapse:collapse;width:100%;margin:.5rem 0 1.5rem}th,td{border:1px solid #ccc;padding:.4rem .6rem;text-align:left;font-size:.9rem}");
            sb.Append("th{background:#f3f3f3}.muted{color:#666;font-size:.8rem}@media print{button{display:none}}</style></head><body>");
            sb.Append("<button onclick='window.print()'>Skriv ut</button>");
            sb.Append("<h1>Märkesutskrift – Pistolskyttemärket</h1>");
            sb.Append("<p><strong>").Append(Enc(memberName)).Append("</strong></p>");

            sb.Append("<h2>Märken</h2><table><tr><th>Valör</th><th>År</th><th>Registreringsnr</th><th>Status</th></tr>");
            foreach (var b in badges.Where(b => b.LevelOrdinal is >= 1 and <= 3).OrderBy(b => b.LevelOrdinal))
            {
                sb.Append("<tr><td>").Append(Enc(b.Level)).Append("</td><td>").Append(b.AchievedYear)
                  .Append("</td><td>").Append(Enc(b.UniqueNumber)).Append("</td><td>").Append(Enc(Marken.StatusDisplay(b.Status))).Append("</td></tr>");
            }
            sb.Append("</table>");

            sb.Append("<h2>Årtalsmärke</h2><p>Godkända guldfodringar: <strong>").Append(ladder.FulfilledYears)
              .Append("</strong>. Aktuellt årtalsmärke: <strong>").Append(Enc(string.IsNullOrEmpty(ladder.CurrentName) ? "–" : ladder.CurrentName)).Append("</strong>.</p>");

            sb.Append("<h2>Guldfodringar</h2><table><tr><th>År</th><th>Precisionsdel</th><th>Snabbskyttedel</th><th>Status</th></tr>");
            foreach (var q in quals)
            {
                sb.Append("<tr><td>").Append(q.Year).Append("</td><td>")
                  .Append(q.Part1Met ? "Klar" : "–").Append("</td><td>")
                  .Append(q.Part2Met ? Enc(Marken.PartSourceDisplay(q.Part2Source)) : "–").Append("</td><td>")
                  .Append(Enc(Marken.StatusDisplay(q.Status))).Append("</td></tr>");
            }
            sb.Append("</table>");
            sb.Append("<p class='muted'>Utskrift från pistol.nu ").Append(DateTime.Now.ToString("yyyy-MM-dd"))
              .Append(". Underlaget bygger på signerade uppgifter i klubbens märkesregister.</p>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private FileContentResult CsvFile(string content, string fileName)
        {
            var bom = System.Text.Encoding.UTF8.GetPreamble();
            var body = System.Text.Encoding.UTF8.GetBytes(content);
            var bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            return File(bytes, "text/csv", fileName);
        }

        // ── Request DTOs ──────────────────────────────────────────────
        public class YearRequest { public int Year { get; set; } }
        public class IdRequest { public int Id { get; set; } }
        public class AwardBadgeRequest { public int MemberId { get; set; } public string Level { get; set; } = ""; public int Year { get; set; } public string? UniqueNumber { get; set; } public string? Note { get; set; } }
        public class UniqueNumberRequest { public int BadgeId { get; set; } public string? UniqueNumber { get; set; } }

        private class MarkenSummaryRow
        {
            public int MemberId { get; set; }
            public string Name { get; set; } = "";
            public string TopLevel { get; set; } = "";
            public string GuldNumber { get; set; } = "";
            public int FulfilledYears { get; set; }
            public string Artalsmarke { get; set; } = "";
            public int Pending { get; set; }
            public string ThisYearStatus { get; set; } = "";
            public bool ThisYearFulfilled { get; set; }
        }
    }
}
