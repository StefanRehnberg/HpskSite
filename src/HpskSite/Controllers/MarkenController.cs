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
        private readonly MarkenCompetitionSeriesSync _compSeriesSync;
        private readonly MarkenCompetitionService _compService;
        private readonly MarkenStormastarService _stormastarService;
        private readonly StandardMedalLedgerService _standardMedals;
        private readonly MarkenOrderListService _orderList;
        private readonly StandardMedalProofStorage _proofStorage;
        private readonly ITimeLimitedDataProtector _verifyProtector;

        /// <summary>
        /// How long a QR verify link stays valid. The shooter shows the code to a functionary who is
        /// standing next to them, so minutes is the real-world window — and once the club turns on
        /// <c>markenRequireOnSiteWitness</c> this token IS the proof of on-site witnessing, so an
        /// unbounded one (what <c>CreateProtector</c> alone gives) would let a code be redeemed from
        /// the sofa a year later. Expiry is not a lockout: the club validation queue still works.
        /// </summary>
        private static readonly TimeSpan VerifyTokenLifetime = TimeSpan.FromMinutes(30);

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
            MarkenCompetitionSeriesSync compSeriesSync,
            MarkenCompetitionService compService,
            MarkenStormastarService stormastarService,
            StandardMedalLedgerService standardMedals,
            MarkenOrderListService orderList,
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
            _compSeriesSync = compSeriesSync;
            _compService = compService;
            _stormastarService = stormastarService;
            _standardMedals = standardMedals;
            _orderList = orderList;
            _proofStorage = proofStorage;
            _verifyProtector = dataProtectionProvider.CreateProtector("Marken.SeriesVerify.v1").ToTimeLimitedDataProtector();
        }

        private const string Family = Marken.FamilyPistolskytte;

        /// <summary>Integrity rule: nobody (incl. site admins) may validate their own evidence.</summary>
        private const string SelfValidateMsg = "Du kan inte validera din egen inrapportering — be en annan funktionär.";

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
            await RecomputeMastarAsync(member.Id);
            var families = await BuildFamilySummariesAsync(member.Id, y);
            var mastar = await MastarSummaryAsync(member.Id);
            var stormastar = await StormastarSummaryAsync(member.Id);
            return Json(new { success = true, year = y, pistolskytte, families, mastar, stormastar });
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
            public int? Total { get; set; }               // snabbpistol speed series (scored 0–50)
            public string? PhotoRef { get; set; }
            public string? Notes { get; set; }

            /// <summary>
            /// The day the series was actually SHOT ("yyyy-MM-dd"). Empty = today. Until 2026-08-28
            /// this was always the submission day, so a series entered the morning after read as
            /// having been shot that morning — and the functionary validating it in the queue had no
            /// date on screen at all to judge it by.
            /// </summary>
            public string? SeriesDate { get; set; }
        }

        /// <summary>
        /// How far back a shooter may date their own series. Bounded because the date decides which
        /// year's Guldfodring the series counts toward, and because older series belong in the
        /// functionary-gated klubbliggare import (<see cref="AddBacklogSeries"/>) where someone with
        /// sign-off authority vouches for them. Wide enough to cover "shot in December, submitted in
        /// January", which is a real case and must not be pushed into the backlog flow.
        /// </summary>
        private const int MaxSeriesBackdatingDays = 60;

        /// <summary>
        /// Resolves the shot-date for a self-submitted series. Returns null + a message when the date
        /// is unusable, rather than silently falling back to today: a series stamped with the wrong
        /// year lands in the wrong Guldfodring, and nothing downstream would ever question it.
        /// </summary>
        private static (DateTime? Date, string? Error) ResolveSeriesDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return (DateTime.Now, null);
            if (!DateTime.TryParse(raw, out var parsed)) return (null, "Ogiltigt datum.");

            var day = parsed.Date;
            if (day > DateTime.Now.Date) return (null, "Datumet ligger i framtiden.");
            if (day < DateTime.Now.Date.AddDays(-MaxSeriesBackdatingDays))
                return (null, $"Datumet är mer än {MaxSeriesBackdatingDays} dagar tillbaka. "
                            + "Äldre serier registreras av en funktionär under Historiska serier från klubbliggare.");

            // Keep the time-of-day when the shooter dated the series today, so several series shot the
            // same day still sort in the order they were entered.
            return (day == DateTime.Now.Date ? DateTime.Now : day, null);
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

            var (seriesDate, dateError) = ResolveSeriesDate(request.SeriesDate);
            if (seriesDate == null) return Json(new { success = false, message = dateError });

            // The YEAR follows the shot-date, not the submission date — a series shot on 28 December
            // and submitted on 3 January belongs to the old year's Guldfodring.
            int year = seriesDate.Value.Year;
            int birthYear = _candidates.GetBirthYear(member.Id, year);

            var series = new MarkenSeries
            {
                MemberId = member.Id,
                ClubId = request.ClubId,
                BadgeFamily = Family,
                Year = year,
                SeriesDate = seriesDate.Value,
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
                series.SeriesType = Marken.SeriesTypeSpeed;
                series.Target = request.Target;

                if (request.Target == Marken.SpeedTargetSnabbpistol)
                {
                    // Snabbpistoltavla — scored 0–50 (Elit/Mästar). Level by Elit per-series thresholds.
                    int total = request.Total ?? 0;
                    if (total <= 0 || total > 50)
                        return Json(new { success = false, message = "Ange seriens poäng (0–50)." });
                    series.Total = total;
                    series.ClaimedLevel = total >= 49 ? Marken.LevelGuld : total >= 48 ? Marken.LevelSilver : total >= 45 ? Marken.LevelBrons : "";
                    series.Qualifies = total >= 45;
                }
                else
                {
                    // Tillämpning (B100/C30) — hits-in-time, pass/fail per valör (Pistolskytte).
                    if (Marken.LevelOrdinal(request.ClaimedLevel) == 0)
                        return Json(new { success = false, message = "Välj valör (brons/silver/guld)." });
                    series.ClaimedLevel = request.ClaimedLevel;
                    series.Qualifies = true; // self-declared pass; validator confirms hits-in-time
                }
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

            // ── Probable duplicate ──
            // Checked BEFORE inserting, and reported as a WARNING rather than a refusal. The commonest
            // duplicate is a series shot in a klubbtävling that the shooter also submits by hand; the
            // competition copy is materialised by MarkenCompetitionSeriesSync, so it is already here to
            // be found. Not a hard block, because two genuinely different series can share the signature
            // (47 twice in the same weapon group on the same day is ordinary in a 10-series competition)
            // — the shooter is told, and a functionary decides.
            string? duplicateWarning = null;
            if (series.SeriesType == Marken.SeriesTypePrecision)
            {
                try
                {
                    await _compSeriesSync.SyncMemberYearAsync(member.Id, series.Year);
                    var dupes = await _ledger.FindProbableDuplicatesAsync(
                        member.Id, series.WeaponGroup, series.SeriesDate, series.Total);

                    // Same day, same weapon group, same total AND the identical shot sequence: that is
                    // the same physical series, not a coincidence. Those are recorded but NOT counted,
                    // so the double count cannot arise in the first place — and a functionary can turn
                    // the counting on if it really was a second series.
                    var exact = dupes.FirstOrDefault(d => SameShots(d.Shots, series.Shots));
                    if (exact != null)
                    {
                        series.CountsTowardGuldfodring = false;
                        duplicateWarning = exact.SourceResultId.HasValue
                            ? $"Den här serien finns redan från {exact.Notes ?? "en tävling"} — samma dag, samma skott. "
                              + "Den är sparad men räknas inte en andra gång mot guldfodringen. Sköt du verkligen två "
                              + "likadana serier kan en funktionär räkna med den."
                            : "Du har redan skickat in en serie med samma skott samma dag. Den är sparad men räknas inte "
                              + "en andra gång. Var det en annan serie kan en funktionär räkna med den.";
                    }
                    else if (dupes.Count > 0)
                    {
                        // Same score, different shots. A shooter who fires 48 twice in weapon group C on
                        // one day is ordinary in a 10-series competition, so this only points it out.
                        var d = dupes[0];
                        duplicateWarning = d.SourceResultId.HasValue
                            ? $"Obs: en serie med samma poäng ({d.Total}) i vapengrupp {d.WeaponGroup} finns redan samma dag "
                              + $"från {d.Notes ?? "en tävling"}. Skickar du in samma serie två gånger — säg till en "
                              + "funktionär, så räknas den bara en gång."
                            : $"Obs: du har redan en serie med samma poäng ({d.Total}) i vapengrupp {d.WeaponGroup} samma dag.";
                    }
                }
                catch { /* a warning is never worth failing the submit over */ }
            }

            var id = await _ledger.InsertSeriesAsync(series);
            var token = ProtectVerifyToken("series:" + id);

            return Json(new
            {
                success = true,
                id,
                qualifies = series.Qualifies,
                total = series.Total,
                threshold = series.Threshold,
                verifyToken = token,
                verifyUrl = $"{Request.Scheme}://{Request.Host}/marken/verifiera?t={Uri.EscapeDataString(token)}",
                // The shooter must know BEFORE they walk away from the range whether the queue is a
                // fallback or whether the QR code is the only way this series can ever be approved.
                requiresOnSiteWitness = RequireOnSiteWitness(series.ClubId),
                seriesDate = series.SeriesDate,
                duplicateWarning,
                message = series.SeriesType == Marken.SeriesTypePrecision && !series.Qualifies
                    ? "Sparad. Obs: serien når inte guldkravet — den räknas inte mot guldfodringen men kan ändå valideras."
                    : "Sparad och skickad för validering."
            });
        }

        /// <summary>
        /// Whether the current member's birth year is derivable from their <c>personNumber</c>. The
        /// quick-submit flow uses this to decide whether to ask for the personnummer before submitting
        /// a precision Guldserie — age drives the reduced Guld krav (−1/serie from the year after 55,
        /// silverkrav from the year after 65). GET /umbraco/surface/Marken/GetMyBirthYearStatus
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyBirthYearStatus()
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            int birthYear = _candidates.GetBirthYear(member.Id, DateTime.Now.Year);
            return Json(new { success = true, hasBirthYear = birthYear > 0, birthYear });
        }

        public class SetPersonNumberRequest { public string? PersonNumber { get; set; } }

        /// <summary>
        /// Persist the current member's personnummer to the existing <c>personNumber</c> member property
        /// (the canonical age source used across the site) when it isn't already on file, so the Guld-krav
        /// age concession applies here and in every other age-dependent rule. Only fills the gap — never
        /// overwrites a personnummer that already yields a valid birth year. POST /umbraco/surface/Marken/SetMyPersonNumber
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMyPersonNumber([FromBody] SetPersonNumberRequest request)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });

            int currentYear = DateTime.Now.Year;
            var pn = request?.PersonNumber?.Trim() ?? "";
            int birthYear = Marken.BirthYearFromPersonNumber(pn, currentYear);
            if (birthYear <= 0)
                return Json(new { success = false, message = "Ange ett giltigt personnummer (ÅÅÅÅMMDD-XXXX)." });

            var entity = _memberService.GetById(member.Id);
            if (entity == null) return Json(new { success = false, message = "Medlem hittades inte." });

            // Don't clobber an existing valid personnummer — only fill the gap.
            var existing = entity.GetValue("personNumber")?.ToString();
            if (Marken.BirthYearFromPersonNumber(existing, currentYear) <= 0)
            {
                entity.SetValue("personNumber", pn);
                _memberService.Save(entity);
            }
            return Json(new { success = true, birthYear });
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
        /// Delete one of the current member's OWN submissions from "Mina inskickade serier" — only
        /// when it was <b>rejected</b> (so a shooter can clear a declined entry). Handles both a
        /// MarkenSeries (kind "series") and a self-reported competition result (kind "comp").
        /// POST /umbraco/surface/Marken/DeleteMyEvidence  { kind, id }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMyEvidence([FromBody] EvidenceActionRequest request)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });

            if (request?.Kind == "comp")
            {
                var r = await _compService.GetSelfReportedAsync(request.Id);
                if (r == null) return Json(new { success = false, message = "Hittades inte." });
                if (r.MemberId != member.Id) return Json(new { success = false, message = "Åtkomst nekad." });
                if (r.Status != Marken.StatusRejected) return Json(new { success = false, message = "Bara avvisade inskick kan tas bort." });
                var (cok, cmsg) = await _compService.DeleteSelfReportedAsync(r.Id);
                return Json(new { success = cok, message = cok ? "Borttagen." : cmsg });
            }

            var s = await _ledger.GetSeriesAsync(request?.Id ?? 0);
            if (s == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (s.MemberId != member.Id) return Json(new { success = false, message = "Åtkomst nekad." });
            if (s.Status != Marken.StatusRejected) return Json(new { success = false, message = "Bara avvisade serier kan tas bort." });
            var (ok, msg) = await _ledger.DeleteSeriesAsync(s.Id);
            return Json(new { success = ok, message = ok ? "Borttagen." : msg });
        }

        public class SeriesCountsRequest { public int Id { get; set; } public bool Counts { get; set; } }

        /// <summary>
        /// Include or exclude one guldserie from the Guldfodring, without deleting it.
        /// <para>
        /// This is how a duplicate is resolved — the shooter really did shoot the series, so the record
        /// stays and only the counting changes. It is also the ONLY thing that works for a
        /// competition-sourced series: deleting one is futile because the next reconciliation
        /// materialises it again from the result row.
        /// </para>
        /// POST /umbraco/surface/Marken/SetSeriesCountsToward  { id, counts }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetSeriesCountsToward([FromBody] SeriesCountsRequest request)
        {
            var s = await _ledger.GetSeriesAsync(request?.Id ?? 0);
            if (s == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (!await CanSignOffForMemberAsync(s.MemberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var (ok, msg) = await _ledger.SetSeriesCountsTowardAsync(s.Id, request!.Counts);
            if (!ok) return Json(new { success = false, message = msg });

            // The Guldfodring may complete or un-complete as a direct result.
            int actingId = await GetCurrentMemberIdAsync();
            await RecomputeYearlyQualificationAsync(s.MemberId, s.Year, actingId);
            await RecomputeSeriesProofFamiliesAsync(s.MemberId);

            return Json(new
            {
                success = true,
                message = request.Counts
                    ? "Serien räknas mot guldfodringen igen."
                    : "Serien räknas inte längre mot guldfodringen. Den ligger kvar i historiken."
            });
        }

        /// <summary>
        /// Delete a guldserie a functionary judges to have been entered by mistake.
        /// <para>
        /// ⚠️ Refuses a competition-sourced series and says why: it is derived from a result row, so the
        /// next reconciliation would recreate it. Excluding it (<see cref="SetSeriesCountsToward"/>) is
        /// the operation that lasts — and if the RESULT is what is wrong, the result is what to correct.
        /// </para>
        /// POST /umbraco/surface/Marken/DeleteSeriesAsFunctionary  { id }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSeriesAsFunctionary([FromBody] IdRequest request)
        {
            var s = await _ledger.GetSeriesAsync(request?.Id ?? 0);
            if (s == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (!await CanSignOffForMemberAsync(s.MemberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (s.SourceResultId.HasValue)
                return Json(new
                {
                    success = false,
                    message = "Serien kommer från ett inmatat tävlingsresultat och skapas om automatiskt. "
                            + "Välj \"Räkna inte\" i stället, eller rätta resultatet i tävlingen."
                });

            int memberId = s.MemberId, year = s.Year;
            var (ok, msg) = await _ledger.DeleteSeriesAsync(s.Id);
            if (!ok) return Json(new { success = false, message = msg });

            int actingId = await GetCurrentMemberIdAsync();
            await RecomputeYearlyQualificationAsync(memberId, year, actingId);
            await RecomputeSeriesProofFamiliesAsync(memberId);
            return Json(new { success = true, message = "Serien borttagen." });
        }

        /// <summary>
        /// Mints a FRESH QR verify link for one of the current member's own <b>pending</b> series.
        /// <para>
        /// Required, not a convenience: verify tokens expire (<see cref="VerifyTokenLifetime"/>), and on
        /// a club that requires on-site witnessing the QR code is the ONLY way a series can be
        /// approved. Without a way to show a new code, an expired token would strand the series in the
        /// queue permanently — approvable by nobody, and deletable only by rejecting it first.
        /// </para>
        /// GET /umbraco/surface/Marken/GetMyVerifyLink?id=123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyVerifyLink(int id)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });

            var s = await _ledger.GetSeriesAsync(id);
            if (s == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (s.MemberId != member.Id) return Json(new { success = false, message = "Åtkomst nekad." });
            if (s.Status != Marken.SeriesStatusPending)
                return Json(new { success = false, message = "Serien är redan avgjord." });

            var token = ProtectVerifyToken("series:" + s.Id);
            return Json(new
            {
                success = true,
                id = s.Id,
                verifyToken = token,
                verifyUrl = $"{Request.Scheme}://{Request.Host}/marken/verifiera?t={Uri.EscapeDataString(token)}",
                requiresOnSiteWitness = RequireOnSiteWitness(s.ClubId),
                qualifies = s.Qualifies,
                total = s.Total,
                threshold = s.Threshold,
                message = "Visa koden för en styrelsemedlem eller skjutledare."
            });
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
            var token = ProtectVerifyToken("comp:" + id);

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
            var token = ProtectVerifyToken("series:" + id);

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

        /// <summary>
        /// Pending evidence the current user can validate. By default scoped to the user's own
        /// functionary clubs (board + enabled Skjutledare) — even for site admins, so the personal
        /// queue isn't a site-wide firehose. Site admins can pass allClubs=true to see every club.
        /// Always excludes the viewer's OWN submissions (nobody validates their own).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPendingSeries(bool allClubs = false)
        {
            var me = await GetCurrentMemberAsync();
            if (me == null) return Json(new { success = false, message = "Inte inloggad." });

            bool isSiteAdmin = await _auth.IsCurrentUserAdminAsync();
            bool showAll = isSiteAdmin && allClubs;

            IEnumerable<int>? ids;
            if (showAll)
            {
                ids = null; // all clubs
            }
            else
            {
                var funcClubs = await GetFunctionaryClubsAsync();
                if (funcClubs.Count == 0)
                    return Json(new { success = true, items = Array.Empty<object>(), canViewAllClubs = isSiteAdmin, allClubs = false });
                ids = funcClubs;
            }

            var series = await _ledger.GetPendingSeriesAsync(ids);
            var comps = await _compService.GetPendingSelfReportedAsync(ids);
            var storm = await _stormastarService.GetPendingAsync(ids);
            var items = series.Where(s => s.MemberId != me.Id).Select(SerieDto)
                .Concat(comps.Where(c => c.MemberId != me.Id).Select(CompResultDto))
                .Concat(storm.Where(e => e.MemberId != me.Id).Select(StormastarDto)).ToList();
            return Json(new { success = true, items, canViewAllClubs = isSiteAdmin, allClubs = showAll });
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
                if (r.MemberId == me.Id) return Json(new { success = false, message = SelfValidateMsg });
                if (!await CanSignOffForClubAsync(r.ClubId))
                    return Json(new { success = false, message = "Du har inte behörighet att validera för den här klubben." });
                return Json(new { success = true, serie = CompResultDto(r) });
            }

            if (kind == "stormastar")
            {
                var e = await _stormastarService.GetAsync(id);
                if (e == null) return Json(new { success = false, message = "Inteckningen hittades inte." });
                if (e.MemberId == me.Id) return Json(new { success = false, message = SelfValidateMsg });
                if (!await CanSignOffForClubAsync(e.ClubId))
                    return Json(new { success = false, message = "Du har inte behörighet att validera för den här klubben." });
                return Json(new { success = true, serie = StormastarDto(e) });
            }

            var series = await _ledger.GetSeriesAsync(id);
            if (series == null) return Json(new { success = false, message = "Serien hittades inte." });
            if (series.MemberId == me.Id) return Json(new { success = false, message = SelfValidateMsg });
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

        public class EvidenceActionRequest
        {
            public string Kind { get; set; } = "series";
            public int Id { get; set; }

            /// <summary>
            /// The QR verify token, present when the validator came in through the scan page. Only
            /// consulted by clubs that require on-site witnessing (<see cref="RequireOnSiteWitness"/>).
            /// </summary>
            public string? Token { get; set; }
        }

        /// <summary>Unified validate — dispatches to series or competition-result by kind.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyEvidence([FromBody] EvidenceActionRequest request)
            => request?.Kind switch
            {
                "comp" => await SetCompResultStatus(request.Id, Marken.StatusVerified),
                "stormastar" => await SetStormastarStatus(request.Id, Marken.StatusVerified),
                _ => await SetSeriesStatus(request?.Id ?? 0, Marken.StatusVerified, request?.Token)
            };

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectEvidence([FromBody] EvidenceActionRequest request)
            => request?.Kind switch
            {
                "comp" => await SetCompResultStatus(request.Id, Marken.StatusRejected),
                "stormastar" => await SetStormastarStatus(request.Id, Marken.StatusRejected),
                _ => await SetSeriesStatus(request?.Id ?? 0, Marken.StatusRejected)
            };

        private async Task<IActionResult> SetCompResultStatus(int id, string status)
        {
            var r = await _compService.GetSelfReportedAsync(id);
            if (r == null) return Json(new { success = false, message = "Resultatet hittades inte." });
            int actingId = await GetCurrentMemberIdAsync();
            if (r.MemberId == actingId) return Json(new { success = false, message = SelfValidateMsg });
            if (!await CanSignOffForClubAsync(r.ClubId)) return Json(new { success = false, message = "Åtkomst nekad." });
            var (ok, msg) = await _compService.SetSelfReportedStatusAsync(id, status, actingId);
            if (ok && status == Marken.StatusVerified) await RecomputeCompetitionFamiliesAsync(r.MemberId);
            return Json(new { success = ok, message = ok ? (status == Marken.StatusVerified ? "Godkänd." : "Avvisad.") : msg });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifySeries([FromBody] IdRequest request) => await SetSeriesStatus(request?.Id ?? 0, Marken.StatusVerified);

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectSeries([FromBody] IdRequest request) => await SetSeriesStatus(request?.Id ?? 0, Marken.StatusRejected);

        private async Task<IActionResult> SetSeriesStatus(int id, string status, string? verifyToken = null)
        {
            var series = await _ledger.GetSeriesAsync(id);
            if (series == null) return Json(new { success = false, message = "Serien hittades inte." });
            int validatorId = await GetCurrentMemberIdAsync();
            if (series.MemberId == validatorId) return Json(new { success = false, message = SelfValidateMsg });
            if (!await CanValidateSeriesAsync(series))
                return Json(new { success = false, message = "Åtkomst nekad." });

            // ── On-site witnessing (opt-in per club) ──
            // Approving then requires a LIVE verify token minted for THIS series, which only the
            // shooter's own screen can produce — that is what makes "bevittnad på plats" a fact
            // rather than a claim. Rejecting is always allowed: a club that demands witnessing must
            // still be able to clear its queue of series nobody witnessed, or the queue jams.
            // Series only — a self-reported championship result is a paper result list, not
            // something a functionary stands and watches.
            if (status == Marken.StatusVerified
                && RequireOnSiteWitness(series.ClubId)
                && !IsLiveVerifyToken(verifyToken, "series:" + series.Id))
            {
                return Json(new
                {
                    success = false,
                    message = "Klubben kräver att serier bevittnas på plats. Godkänn genom att skanna "
                            + "skyttens QR-kod vid banan. Har koden gått ut visar skytten en ny."
                });
            }

            var (ok, msg) = await _ledger.SetSeriesStatusAsync(id, status, validatorId);

            // No separate sign-off: validating a series may complete (or un-complete) a yearly badge
            // automatically. A precision series feeds Pistolskytte AND Elit, so recompute both the
            // Guldfodring and the series-proof families on every series validation.
            if (ok)
            {
                await RecomputeYearlyQualificationAsync(series.MemberId, series.Year, validatorId);
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

            int meId = await GetCurrentMemberIdAsync(); // never list the viewer's own submissions
            var series = await _ledger.GetPendingSeriesAsync(new[] { clubId });
            var comps = await _compService.GetPendingSelfReportedAsync(new[] { clubId });
            var storm = await _stormastarService.GetPendingAsync(new[] { clubId });
            var items = series.Where(s => s.MemberId != meId).Select(SerieDto)
                .Concat(comps.Where(c => c.MemberId != meId).Select(CompResultDto))
                .Concat(storm.Where(e => e.MemberId != meId).Select(StormastarDto)).ToList();
            return Json(new { success = true, canValidate = await CanSignOffForClubAsync(clubId), items });
        }

        // ── Backlog entry (migrate a paper ledger) ─────────────────────

        public class BacklogSeriesEntry
        {
            public int MemberId { get; set; }
            public string SeriesType { get; set; } = Marken.SeriesTypePrecision; // Precision | Speed
            public string SeriesDate { get; set; } = "";                          // yyyy-MM-dd
            public string WeaponGroup { get; set; } = "C";
            public int? Total { get; set; }                                       // precision score, or snabbpistol score
            public string? Target { get; set; }                                   // speed target
            public string? ClaimedLevel { get; set; }                             // speed tillämpning valör
        }

        public class AddBacklogSeriesRequest
        {
            public int ClubId { get; set; }
            public List<BacklogSeriesEntry> Entries { get; set; } = new();
        }

        /// <summary>
        /// Club-admin bulk entry of historical Guldserier / Snabbserier from a paper ledger. Each row
        /// lands directly Verified (the entering functionary is the validator), then the yearly
        /// Guldfodring + series-proof families are recomputed. Authority = club sign-off (board /
        /// Skjutledare-if-enabled / site admin). POST /umbraco/surface/Marken/AddBacklogSeries
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddBacklogSeries([FromBody] AddBacklogSeriesRequest request)
        {
            if (request == null || request.ClubId <= 0) return Json(new { success = false, message = "Ogiltig begäran." });
            if (request.Entries == null || request.Entries.Count == 0) return Json(new { success = false, message = "Inga serier angivna." });

            int actingId = await GetCurrentMemberIdAsync();
            if (actingId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });
            if (!await CanSignOffForClubAsync(request.ClubId))
                return Json(new { success = false, message = "Du har inte behörighet att registrera serier för den här klubben." });

            int inserted = 0, skipped = 0;
            var errors = new List<string>();
            var affectedYears = new HashSet<(int Member, int Year)>();
            var affectedMembers = new HashSet<int>();

            for (int i = 0; i < request.Entries.Count; i++)
            {
                var e = request.Entries[i];
                string row = $"Rad {i + 1}";

                var member = e.MemberId > 0 ? _memberService.GetById(e.MemberId) : null;
                if (member == null) { errors.Add($"{row}: medlem saknas."); skipped++; continue; }
                if (!MemberBelongsToClub(member, request.ClubId)) { errors.Add($"{row}: medlemmen tillhör inte klubben."); skipped++; continue; }
                if (!DateTime.TryParse(e.SeriesDate, out var date)) { errors.Add($"{row}: ogiltigt datum."); skipped++; continue; }
                if (date.Date > DateTime.Now.Date) { errors.Add($"{row}: datumet ligger i framtiden."); skipped++; continue; }
                int year = date.Year;

                var group = Marken.WeaponGroup(e.WeaponGroup);
                if (group == null) { errors.Add($"{row}: ogiltig vapengrupp."); skipped++; continue; }

                int birthYear = _candidates.GetBirthYear(member.Id, year);

                var series = new MarkenSeries
                {
                    MemberId = member.Id,
                    ClubId = request.ClubId,
                    BadgeFamily = Family,
                    Year = year,
                    SeriesDate = date,
                    WeaponGroup = group,
                    Status = Marken.StatusVerified,
                    ValidatedByMemberId = actingId,
                    ValidatedDate = DateTime.Now,
                    EnteredByMemberId = actingId,
                    Notes = "Historisk inmatning från klubbliggare."
                };

                if (e.SeriesType == Marken.SeriesTypeSpeed)
                {
                    if (!Marken.IsValidSpeedTarget(e.Target)) { errors.Add($"{row}: välj ett giltigt mål för snabbserien."); skipped++; continue; }
                    series.SeriesType = Marken.SeriesTypeSpeed;
                    series.Target = e.Target;
                    if (e.Target == Marken.SpeedTargetSnabbpistol)
                    {
                        int total = e.Total ?? 0;
                        if (total <= 0 || total > 50) { errors.Add($"{row}: ange snabbpistolseriens poäng (0–50)."); skipped++; continue; }
                        series.Total = total;
                        series.ClaimedLevel = total >= 49 ? Marken.LevelGuld : total >= 48 ? Marken.LevelSilver : total >= 45 ? Marken.LevelBrons : "";
                        series.Qualifies = total >= 45;
                    }
                    else
                    {
                        var level = string.IsNullOrWhiteSpace(e.ClaimedLevel) ? Marken.LevelGuld : e.ClaimedLevel!;
                        if (Marken.LevelOrdinal(level) == 0) { errors.Add($"{row}: välj valör."); skipped++; continue; }
                        series.ClaimedLevel = level;
                        series.Qualifies = true; // tillämpning är pass/fail per valör
                    }
                }
                else
                {
                    int total = e.Total ?? 0;
                    if (total <= 0 || total > 50) { errors.Add($"{row}: ange seriens poäng (0–50)."); skipped++; continue; }
                    int threshold = Marken.PrecisionThreshold(group, year, birthYear);
                    series.SeriesType = Marken.SeriesTypePrecision;
                    series.ClaimedLevel = Marken.LevelGuld;
                    series.Shots = "[]"; // backlog records the total only, not shot-by-shot
                    series.Total = total;
                    series.Threshold = threshold;
                    series.Qualifies = total >= threshold;
                }

                await _ledger.InsertSeriesAsync(series);
                inserted++;
                affectedYears.Add((member.Id, year));
                affectedMembers.Add(member.Id);
            }

            foreach (var (m, y) in affectedYears) await RecomputeYearlyQualificationAsync(m, y, actingId);
            foreach (var m in affectedMembers) await RecomputeSeriesProofFamiliesAsync(m);

            return Json(new
            {
                success = inserted > 0,
                inserted,
                skipped,
                errors,
                message = inserted > 0
                    ? $"{inserted} serie(r) registrerade och validerade." + (skipped > 0 ? $" {skipped} hoppades över." : "")
                    : "Inga serier registrerades." + (errors.Count > 0 ? " " + string.Join(" ", errors) : "")
            });
        }

        // ── Club Guldserie-ligan (friendly leaderboard, Medlemmar tab) ─

        private static int CountX(string? shotsJson)
        {
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shotsJson ?? "") ?? new();
                return list.Count(v => string.Equals(v?.Trim(), "X", StringComparison.OrdinalIgnoreCase));
            }
            catch { return 0; }
        }

        /// <summary>
        /// Per-member leaderboard of approved guldserier for a club — antal, perfekta (50p), bästa serie.
        /// Counts Verified precision-discipline series validated for the club. Logged-in members only
        /// (the Medlemmar tab is members-facing). year &gt; 0 filters to that year; 0/absent = all-time.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubGuldserieLeaderboard(int clubId, int? year)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (await GetCurrentMemberAsync() == null) return Json(new { success = false, message = "Inte inloggad." });

            // The liga is member-based (Stefan's call): it lists the club's members' guldserier wherever
            // they were shot. Competition series only exist here once materialised, so reconcile first —
            // otherwise the liga is exactly as blind to them as before.
            await EnsureCompetitionSeriesSyncedAsync(year is > 0 ? year.Value : DateTime.Now.Year);

            List<MarkenSeries> all;
            try { all = await _ledger.GetVerifiedSeriesForClubAsync(clubId); }
            catch { all = new(); }

            // Only Pistolskyttemärkets PRECISION series belong in the Guldserie-ligan.
            // ⚠️ The discipline filter alone is NOT enough: Marken.SeriesDiscipline returns
            // DisciplinePrecision as its FALLBACK, so anything that is neither Luftpistol-family nor
            // SeriesTypeSpeed falls through into it. An Elit precision proof series (SubmitProofSeries,
            // BadgeFamily = Elit) is judged against Elit brons (45) and not the guldkrav, yet it was
            // counted here as a guldserie. Gate on the family too — a "guldserie" is a Pistolskyttemärke
            // concept. Duell/snabbserier and luftpistol were already excluded by the discipline test.
            var prec = all.Where(s => s.BadgeFamily == Family
                                      && Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplinePrecision);
            if (year is > 0) prec = prec.Where(s => s.Year == year);

            var rows = prec.GroupBy(s => s.MemberId)
                .Select(g =>
                {
                    var list = g.ToList();
                    var best = list.OrderByDescending(s => s.Total).ThenByDescending(s => CountX(s.Shots)).First();
                    return new
                    {
                        memberId = g.Key,
                        name = _memberService.GetById(g.Key)?.Name ?? $"Medlem {g.Key}",
                        count = list.Count(s => s.Qualifies),
                        perfect = list.Count(s => s.Total >= 50),
                        best = best.Total,
                        bestX = CountX(best.Shots)
                    };
                })
                .Where(r => r.count > 0)
                .OrderByDescending(r => r.count).ThenByDescending(r => r.perfect).ThenByDescending(r => r.best)
                .ToList();

            return Json(new { success = true, year = year ?? 0, rows });
        }

        // ── Club Märken settings (sign-off authority) ─────────────────

        /// <summary>Read the club's Märken sign-off setting (whether Skjutledare may validate). Club admins only.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMarkenClubSettings(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _auth.IsClubAdminForClub(clubId)) return Json(new { success = false, message = "Åtkomst nekad." });
            var club = _contentService.GetById(clubId);
            bool hasProp = club != null && club.HasProperty("markenSignoffSkjutledare");
            bool hasWitnessProp = club != null && club.HasProperty("markenRequireOnSiteWitness");
            return Json(new
            {
                success = true,
                skjutledareSignoff = hasProp && club!.GetValue<bool>("markenSignoffSkjutledare"),
                propertyExists = hasProp,
                requireOnSiteWitness = hasWitnessProp && club!.GetValue<bool>("markenRequireOnSiteWitness"),
                witnessPropertyExists = hasWitnessProp
            });
        }

        public class MarkenClubSettingsRequest
        {
            public int ClubId { get; set; }
            public bool SkjutledareSignoff { get; set; }

            /// <summary>
            /// Nullable so a client that only knows about the older switch cannot silently turn the
            /// witnessing requirement off by omitting it — a missing field means "leave it alone",
            /// not "false".
            /// </summary>
            public bool? RequireOnSiteWitness { get; set; }
        }

        /// <summary>Set whether the club's Skjutledare may validate märken. Club admins only.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMarkenClubSettings([FromBody] MarkenClubSettingsRequest request)
        {
            int clubId = request?.ClubId ?? 0;
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _auth.IsClubAdminForClub(clubId)) return Json(new { success = false, message = "Åtkomst nekad." });

            var club = _contentService.GetById(clubId);
            if (club == null) return Json(new { success = false, message = "Klubben hittades inte." });
            if (!club.HasProperty("markenSignoffSkjutledare"))
                return Json(new { success = false, message = "Egenskapen 'markenSignoffSkjutledare' saknas på klubbtypen — be en administratör lägga till den i Umbraco." });

            club.SetValue("markenSignoffSkjutledare", request!.SkjutledareSignoff);

            // Refuse rather than no-op when the property is missing. `SetValue` on an absent property
            // is silently ignored, so the switch would report success, flip back on the next load,
            // and the club would believe it had a requirement it does not have.
            if (request.RequireOnSiteWitness.HasValue)
            {
                if (!club.HasProperty("markenRequireOnSiteWitness"))
                    return Json(new { success = false, message = "Egenskapen 'markenRequireOnSiteWitness' saknas på klubbtypen — be en administratör lägga till den i Umbraco." });
                club.SetValue("markenRequireOnSiteWitness", request.RequireOnSiteWitness.Value);
            }

            _contentService.Save(club);
            _contentService.Publish(club, new[] { "*" }, -1);
            _onSiteWitnessCache.Remove(clubId);

            var messages = new List<string>
            {
                request.SkjutledareSignoff
                    ? "Skjutledare kan nu validera märken för klubben."
                    : "Endast styrelsemedlemmar kan validera märken för klubben."
            };
            if (request.RequireOnSiteWitness == true)
                messages.Add("Serier måste nu bevittnas på plats — de godkänns genom att skanna skyttens QR-kod.");
            else if (request.RequireOnSiteWitness == false)
                messages.Add("Serier kan godkännas i valideringskön i efterhand.");

            return Json(new { success = true, message = string.Join(" ", messages) });
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

            // Materialise competition series before reading, so a member whose only guldserier were shot
            // in competitions shows up here at all.
            await EnsureCompetitionSeriesSyncedAsync(y);

            // Members to show = badge/Guldfodring holders in this club PLUS anyone with verified
            // series recorded at this club this year (so backlog/self-submitted series are visible
            // even before a Guldfodring completes).
            var clubSeries = (await _ledger.GetVerifiedSeriesForClubAsync(clubId))
                .Where(s => s.Year == y && s.BadgeFamily == Family).ToList();
            var seriesByMember = clubSeries.GroupBy(s => s.MemberId).ToDictionary(g => g.Key, g => g.ToList());

            var memberIds = new HashSet<int>(seriesByMember.Keys);
            foreach (var mid in await _ledger.GetAllActiveMemberIdsAsync())
            {
                var m = _memberService.GetById(mid);
                if (m != null && int.TryParse(m.GetValue("primaryClubId")?.ToString(), out var pc) && pc == clubId)
                    memberIds.Add(mid);
            }

            var rows = new List<MarkenSummaryRow>();
            foreach (var mid in memberIds)
            {
                var member = _memberService.GetById(mid);
                if (member == null) continue;

                var badges = await _ledger.GetBadgesForMemberAsync(mid, Family);
                var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3).OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var guld = badges.FirstOrDefault(b => b.Level == Marken.LevelGuld);
                var ladder = await _ledger.GetArtalsmarkeStatusAsync(mid, Family, includeUnverified: false);
                var pending = await _ledger.GetPendingCountAsync(mid, Family);
                var thisYearQ = await _ledger.GetQualificationForYearAsync(mid, Family, y);

                var ms = seriesByMember.GetValueOrDefault(mid) ?? new List<MarkenSeries>();
                int part1 = ms.Count(s => s.SeriesType == Marken.SeriesTypePrecision && s.Qualifies && s.ClaimedLevel == Marken.LevelGuld);
                int part2 = ms.Count(s => s.SeriesType == Marken.SeriesTypeSpeed && s.ClaimedLevel == Marken.LevelGuld);

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
                    ThisYearFulfilled = thisYearQ?.Fulfilled ?? false,
                    QualifyingSeries = part1,
                    SpeedSeries = part2
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
                    thisYearFulfilled = r.ThisYearFulfilled,
                    qualifyingSeries = r.QualifyingSeries,
                    speedSeries = r.SpeedSeries
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

            // Other badge families (Elit, Fält, Precision, Milsnabb, Nat.helmatch, Luftpistol, Mästar,
            // Stormästar) so functionaries see the member's full standing, not just Pistolskyttemärket.
            // Recompute first (mirrors the member's own view) so auto-awarded families are current.
            await RecomputeCompetitionFamiliesAsync(memberId);
            await RecomputeSeriesProofFamiliesAsync(memberId);
            var families = await BuildFamilySummariesAsync(memberId, y);
            var mastar = await MastarSummaryAsync(memberId);
            var stormastar = await StormastarSummaryAsync(memberId);

            // Add the acting user's sign-off capability so the UI can show/hide the buttons.
            return Json(new
            {
                success = true,
                canSignOff = await CanSignOffForMemberAsync(memberId),
                detail = payload,
                families,
                mastar,
                stormastar
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

        /// <summary>
        /// Functionary manual award of an "Andra märken" family valör — the competition-achievement
        /// (Elit*/Fält/Precision/Milsnabb/Nationell helmatch) and series-proof (Luftpistol/Elit)
        /// families surfaced read-only in the secretary detail. For badges earned before the system /
        /// off pistol.nu, where reconstructing the underlying series/competition evidence isn't feasible.
        /// Upserts one MemberBadge per (member, family, level), Source=Admin, Verified. The lazy
        /// auto-derive (RecomputeSeriesProofFamiliesAsync / comp auto-award) is insert-missing-level-only
        /// and never downgrades, so a manual award sticks even when the evidence doesn't (yet) support it.
        /// Family must be a known MarkenFamilies key (excludes Pistolskytte/Mästar/Stormästar, which have
        /// their own award surfaces). POST /umbraco/surface/Marken/AwardFamilyBadge { memberId, family, level, year, note? }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AwardFamilyBadge([FromBody] AwardFamilyBadgeRequest request)
        {
            int memberId = request?.MemberId ?? 0;
            string family = (request?.Family ?? "").Trim();
            string level = (request?.Level ?? "").Trim();
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });

            var def = MarkenFamilies.Get(family);
            if (def == null) return Json(new { success = false, message = "Ogiltig märkesfamilj." });
            if (Marken.LevelOrdinal(level) is < 1 or > 3)
                return Json(new { success = false, message = "Ogiltig valör." });
            if (!await CanSignOffForMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet att tilldela märken för den här medlemmen." });

            int actingId = await GetCurrentMemberIdAsync();
            int y = request?.Year is > 0 ? request!.Year : DateTime.Now.Year;

            // Upsert: one badge per (member, family, level). Re-awarding updates date/year.
            var existing = (await _ledger.GetBadgesForMemberAsync(memberId, family, includeRejected: true))
                .FirstOrDefault(b => b.Level == level);

            if (existing == null)
            {
                await _ledger.InsertBadgeAsync(new MemberBadge
                {
                    MemberId = memberId,
                    BadgeFamily = family,
                    Level = level,
                    LevelOrdinal = Marken.LevelOrdinal(level),
                    AchievedYear = y,
                    AchievedDate = DateTime.Now,
                    Source = Marken.SourceAdmin,
                    Status = Marken.StatusVerified,
                    SignedOffByMemberId = actingId,
                    SignedOffDate = DateTime.Now,
                    Notes = string.IsNullOrWhiteSpace(request?.Note) ? null : request!.Note!.Trim(),
                    EnteredByMemberId = actingId
                });
            }
            else
            {
                existing.AchievedYear = y;
                existing.AchievedDate ??= DateTime.Now;
                existing.Status = Marken.StatusVerified;
                existing.SignedOffByMemberId = actingId;
                existing.SignedOffDate = DateTime.Now;
                if (!string.IsNullOrWhiteSpace(request?.Note)) existing.Notes = request!.Note!.Trim();
                await _ledger.UpdateBadgeAsync(existing);
            }

            return Json(new { success = true, message = $"{def.DisplayName} i {level} tilldelat." });
        }

        /// <summary>Set/replace the national registration number AND/OR the achieved year on a member's
        /// Guld badge. The year matters for the Elitmärke timing gate (proofs count from the year after).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBadgeUniqueNumber([FromBody] UniqueNumberRequest request)
        {
            var badge = await _ledger.GetBadgeAsync(request?.BadgeId ?? 0);
            if (badge == null) return Json(new { success = false, message = "Märket hittades inte." });
            if (!await CanSignOffForMemberAsync(badge.MemberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (badge.Level == Marken.LevelGuld)
                badge.UniqueNumber = string.IsNullOrWhiteSpace(request?.UniqueNumber) ? null : request!.UniqueNumber!.Trim();
            if (request?.Year is > 0)
                badge.AchievedYear = request.Year!.Value;
            await _ledger.UpdateBadgeAsync(badge);
            return Json(new { success = true, message = "Sparat." });
        }

        public class GuldfodringYearRequest { public int MemberId { get; set; } public int Year { get; set; } public bool Fulfilled { get; set; } }

        /// <summary>
        /// Functionary override: mark/unmark a fulfilled Guldfodring year for Pistolskyttemärket — for
        /// årtalsmärke history earned before the system / off pistol.nu. Each fulfilled year is one step
        /// of 3 toward the next årtalsmärke. Manually-asserted years are stamped so the lazy yearly
        /// recompute never downgrades them. POST /umbraco/surface/Marken/SetGuldfodringYear
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetGuldfodringYear([FromBody] GuldfodringYearRequest request)
        {
            int memberId = request?.MemberId ?? 0;
            int year = request?.Year ?? 0;
            if (memberId <= 0 || year < 1900 || year > DateTime.Now.Year + 1)
                return Json(new { success = false, message = "Ogiltigt år." });
            if (!await CanSignOffForMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet att registrera guldfodringar för den här medlemmen." });

            int actingId = await GetCurrentMemberIdAsync();
            if (request!.Fulfilled)
                await _ledger.EnsureManualFulfilledYearAsync(memberId, Family, year, actingId);
            else
            {
                var q = await _ledger.GetQualificationForYearAsync(memberId, Family, year);
                if (q != null) await _ledger.DeleteQualificationAsync(q.Id);
            }
            return Json(new { success = true, message = "Sparat." });
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

        /// <summary>
        /// Remove ALL of a member's badges + årtalsmärke qualifications for one family — for clearing
        /// an erroneously/leniently auto-awarded family märke (e.g. Elit). Note: derived families
        /// (competition / series-proof) re-materialize on next read if the underlying evidence still
        /// qualifies, so the lasting fix is to remove the underlying series/results first.
        /// POST /umbraco/surface/Marken/DeleteFamilyBadges { memberId, family }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteFamilyBadges([FromBody] FamilyMemberRequest request)
        {
            if (request == null || request.MemberId <= 0 || string.IsNullOrWhiteSpace(request.Family))
                return Json(new { success = false, message = "Ogiltig begäran." });
            if (!await CanSignOffForMemberAsync(request.MemberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            foreach (var b in await _ledger.GetBadgesForMemberAsync(request.MemberId, request.Family, includeRejected: true))
                await _ledger.DeleteBadgeAsync(b.Id);
            foreach (var q in await _ledger.GetQualificationsForMemberAsync(request.MemberId, request.Family))
                await _ledger.DeleteQualificationAsync(q.Id);

            return Json(new { success = true, message = "Märket borttaget." });
        }

        // ── Member profile surface (member admin edit form) ───────────
        // Lets a functionary record a member's Pistolskyttemärket grundvalör + Guld
        // registration number directly on the member edit form — for old-timers who
        // took their märke long ago and never used Skyttetrappan. Backed by the same
        // MemberBadge ledger the Skyttetrappan link and Medaljer & Märken read.

        /// <summary>Read a member's current Pistolskyttemärket grundvalör + Guld number for the edit form.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberPistolskytte(int memberId)
        {
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });
            if (!await CanViewMemberAsync(memberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var badges = await _ledger.GetBadgesForMemberAsync(memberId, Family);
            var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3)
                            .OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
            var guld = badges.FirstOrDefault(b => b.Level == Marken.LevelGuld);

            return Json(new
            {
                success = true,
                level = top?.Level ?? "",
                source = top?.Source,
                sourceLabel = top == null ? null : Marken.SourceDisplay(top.Source),
                guldNumber = guld?.UniqueNumber ?? "",
                canEdit = await CanSignOffForMemberAsync(memberId)
            });
        }

        /// <summary>
        /// Set a member's Pistolskyttemärket grundvalör (and Guld number) from the member edit form.
        /// Reconciles the ledger so the member's highest base valör equals the chosen level:
        /// ensures a badge at that level, removes any higher base badge (downgrade/clear), and
        /// for Guld stores the national registration number. Level "" clears all base valörer.
        /// POST /umbraco/surface/Marken/SetMemberPistolskytte  { memberId, level, guldNumber? }
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMemberPistolskytte([FromBody] SetMemberPistolskytteRequest request)
        {
            int memberId = request?.MemberId ?? 0;
            string level = (request?.Level ?? "").Trim();
            if (memberId <= 0) return Json(new { success = false, message = "Ogiltigt medlems-ID." });
            if (!await CanSignOffForMemberAsync(memberId))
                return Json(new { success = false, message = "Du har inte behörighet att registrera märken för den här medlemmen." });

            int targetOrd = string.IsNullOrEmpty(level) ? 0 : Marken.LevelOrdinal(level);
            if (!string.IsNullOrEmpty(level) && targetOrd == 0)
                return Json(new { success = false, message = "Ogiltig valör." });

            int actingId = await GetCurrentMemberIdAsync();
            var baseBadges = (await _ledger.GetBadgesForMemberAsync(memberId, Family, includeRejected: true))
                .Where(b => b.LevelOrdinal is >= 1 and <= 3).ToList();

            // Remove any base valör above the chosen one (downgrade or clear).
            foreach (var b in baseBadges.Where(b => b.LevelOrdinal > targetOrd))
                await _ledger.DeleteBadgeAsync(b.Id);

            if (targetOrd >= 1)
            {
                var existing = baseBadges.FirstOrDefault(b => b.Level == level);
                string? guldNr = level == Marken.LevelGuld && !string.IsNullOrWhiteSpace(request?.GuldNumber)
                    ? request!.GuldNumber!.Trim() : null;

                if (existing == null)
                {
                    await _ledger.InsertBadgeAsync(new MemberBadge
                    {
                        MemberId = memberId,
                        BadgeFamily = Family,
                        Level = level,
                        LevelOrdinal = targetOrd,
                        AchievedYear = DateTime.Now.Year,
                        AchievedDate = DateTime.Now,
                        Source = Marken.SourceAdmin,
                        Status = Marken.StatusVerified,
                        SignedOffByMemberId = actingId,
                        SignedOffDate = DateTime.Now,
                        UniqueNumber = guldNr,
                        EnteredByMemberId = actingId
                    });
                }
                else if (level == Marken.LevelGuld && guldNr != null)
                {
                    await _ledger.SetUniqueNumberAsync(existing.Id, guldNr);
                }
            }

            return Json(new { success = true, message = "Pistolskyttemärket sparat." });
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

            string FamilyLabel(string fam) => fam switch
            {
                Marken.FamilyPistolskytte => "Pistolskyttemärket",
                Marken.FamilyMastar => "Mästarmärket",
                Marken.FamilyStormastar => "Stormästarmärket",
                _ => MarkenFamilies.DisplayName(fam)
            };

            var sb = new System.Text.StringBuilder();
            // Year achievements across ALL families — what the club reports to SPSF for the year:
            // new badges (grundmärken + family valörer earned this year) and fulfilled Guldfodringar.
            sb.AppendLine($"Medlem;Födelseår;Familj;Märke i år;Guldnummer;Guldfodring {y} uppfylld;Årtalsmärke");

            // One sortable row per (member, family) achievement.
            var lines = new List<(string Name, string Fam, string Row)>();
            foreach (var mid in activeIds)
            {
                var member = _memberService.GetById(mid);
                if (member == null) continue;
                if (!int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc) || pc != clubId) continue;

                int birthYear = _candidates.GetBirthYear(mid, y);
                var name = member.Name ?? $"Medlem {mid}";
                var allBadges = await _ledger.GetBadgesForMemberAsync(mid, null);

                void AddRow(string famKey, string marke, string guldNr, bool guldfodring, string artalsmarke)
                {
                    lines.Add((name, FamilyLabel(famKey), string.Join(";", new[]
                    {
                        Csv(name),
                        birthYear > 0 ? birthYear.ToString() : "",
                        Csv(FamilyLabel(famKey)),
                        Csv(marke),
                        Csv(guldNr),
                        guldfodring ? "Ja" : "",
                        Csv(artalsmarke)
                    })));
                }

                // Pistolskyttemärket — new grundmärke this year and/or a fulfilled Guldfodring.
                var pNew = allBadges.Where(b => b.BadgeFamily == Family && b.AchievedYear == y && b.LevelOrdinal is >= 1 and <= 3)
                    .OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var thisYearQ = await _ledger.GetQualificationForYearAsync(mid, Family, y);
                bool guldfodringMet = thisYearQ?.Fulfilled ?? false;
                if (pNew != null || guldfodringMet)
                {
                    var guld = allBadges.FirstOrDefault(b => b.BadgeFamily == Family && b.Level == Marken.LevelGuld);
                    var pLadder = await _ledger.GetArtalsmarkeStatusAsync(mid, Family, includeUnverified: false);
                    AddRow(Family, pNew?.Level ?? "", pNew?.Level == Marken.LevelGuld ? (guld?.UniqueNumber ?? "") : "", guldfodringMet, pLadder.CurrentName);
                }

                // Every other family with a badge earned this year (Elit, Fält, Precision, Milsnabb,
                // Nat.helmatch, Luftpistol, Mästar).
                foreach (var fg in allBadges.Where(b => b.BadgeFamily != Family && b.AchievedYear == y && b.LevelOrdinal is >= 1 and <= 3)
                                            .GroupBy(b => b.BadgeFamily))
                {
                    var topThisYear = fg.OrderByDescending(x => x.LevelOrdinal).First();
                    var fLadder = await _ledger.GetArtalsmarkeStatusAsync(mid, fg.Key, includeUnverified: false);
                    AddRow(fg.Key, topThisYear.Level, "", false, fLadder.CurrentName);
                }
            }

            var sv = StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false);
            foreach (var (_, _, row) in lines.OrderBy(l => l.Name, sv).ThenBy(l => l.Fam, sv))
                sb.AppendLine(row);

            return CsvFile(sb.ToString(), $"marken-arsrapport-{clubId}-{y}.csv");
        }

        /// <summary>
        /// Årets beställnings- och utdelningslista för en klubb: antal per valör att beställa från
        /// förbundet, plus vad varje medlem ska få. <b>Standardmedaljer ingår INTE</b> — de summeras
        /// per medlem på sin egen flik (Stefan 2026-08-31).
        /// GET /umbraco/surface/Marken/GetClubOrderList?clubId=1098&amp;year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubOrderList(int clubId, int? year)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            int y = year ?? DateTime.Now.Year;

            // Same reason the club summary does it: a member whose only guldserier were shot in
            // competitions must be materialised into the ledger before the year is counted, or the
            // order list is short by exactly those people.
            await EnsureCompetitionSeriesSyncedAsync(y);

            var list = await _orderList.BuildAsync(clubId, y);

            return Json(new
            {
                success = true,
                year = list.Year,
                clubName = list.ClubName,
                totalItems = list.TotalItems,
                unverifiedItems = list.UnverifiedItems,
                warnings = list.Warnings,
                order = list.Order.Select(l => new { group = l.Group, item = l.Item, count = l.Count, note = l.Note }),
                handout = list.Handout.Select(h => new
                {
                    memberId = h.MemberId,
                    name = h.Name,
                    items = h.Items.Select(i => new
                    {
                        group = i.Group,
                        item = i.Item,
                        detail = i.Detail,
                        orderable = i.Orderable,
                        unverified = i.Unverified
                    })
                })
            });
        }

        /// <summary>
        /// CSV of either half of the order list. <paramref name="list"/> = "order" (antal per valör,
        /// för beställningen) or "handout" (en rad per medlem och sak, för utdelningen).
        /// GET /umbraco/surface/Marken/ExportClubOrderList?clubId=1098&amp;year=2026&amp;list=order
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportClubOrderList(int clubId, int? year, string? list)
        {
            if (clubId <= 0) return Content("Ogiltigt klubb-ID.");
            if (!await _auth.IsClubAdminForClub(clubId)) return Content("Åtkomst nekad.");

            int y = year ?? DateTime.Now.Year;
            await EnsureCompetitionSeriesSyncedAsync(y);
            var data = await _orderList.BuildAsync(clubId, y);

            var sb = new System.Text.StringBuilder();

            if (string.Equals(list, "handout", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine("Medlem;Grupp;Artikel;Detalj;Att beställa;Granskad");
                foreach (var h in data.Handout)
                    foreach (var i in h.Items)
                        sb.AppendLine(string.Join(";", new[]
                        {
                            Csv(h.Name), Csv(i.Group), Csv(i.Item), Csv(i.Detail),
                            i.Orderable ? "Ja" : "Nej",
                            i.Unverified ? "Nej" : "Ja"
                        }));

                return CsvFile(sb.ToString(), $"marken-utdelningslista-{clubId}-{y}.csv");
            }

            sb.AppendLine("Grupp;Artikel;Antal;Notering");
            foreach (var l in data.Order)
                sb.AppendLine(string.Join(";", new[] { Csv(l.Group), Csv(l.Item), l.Count.ToString(), Csv(l.Note) }));
            sb.AppendLine();
            sb.AppendLine($"Totalt;;{data.TotalItems};");

            return CsvFile(sb.ToString(), $"marken-bestallningslista-{clubId}-{y}.csv");
        }

        /// <summary>
        /// Printable version of both halves on one page — the beställningslista for the förbundet and
        /// the utdelningslista to read from at the utdelningen. Self-contained HTML, no Umbraco node.
        /// GET /umbraco/surface/Marken/PrintClubOrderList?clubId=1098&amp;year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PrintClubOrderList(int clubId, int? year)
        {
            if (clubId <= 0) return Content("Ogiltigt klubb-ID.");
            if (!await _auth.IsClubAdminForClub(clubId)) return Content("Åtkomst nekad.");

            int y = year ?? DateTime.Now.Year;
            await EnsureCompetitionSeriesSyncedAsync(y);
            var data = await _orderList.BuildAsync(clubId, y);

            return Content(BuildOrderListPrintHtml(data), "text/html; charset=utf-8");
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

            // Precision series of the year, including any a functionary excluded from the count.
            var yearPrecisionSeries = (await _ledger.GetSeriesForMemberAsync(memberId, year))
                .Where(s => s.SeriesType == Marken.SeriesTypePrecision)
                .OrderByDescending(s => s.Total).ThenBy(s => s.SeriesDate)
                .ToList();

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
                    nextAtYears = ladder.NextAtYears,
                    // Per-year breakdown so a functionary can add/remove historical Guldfodring-years.
                    // manual = functionary-asserted (removable); otherwise system-derived from validated series.
                    yearList = quals.Where(q => q.Fulfilled && q.Status == Marken.StatusVerified)
                        .OrderBy(q => q.Year)
                        .Select(q => new { year = q.Year, manual = q.Part1Source == Marken.PartSourceManualAttest })
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
                        id = s.Id,
                        date = s.Date,
                        weaponGroup = s.WeaponGroup,
                        score = s.Score,
                        threshold = s.Threshold,
                        source = s.Source,
                        label = s.Label
                    }),
                    // EVERY precision series of the year, counted or not — the counted ones are gone from
                    // cand.QualifyingSeries by definition, so without this an excluded series would be
                    // invisible and impossible to put back.
                    allPrecisionSeries = yearPrecisionSeries.Select(s => new
                    {
                        id = s.Id,
                        date = s.SeriesDate,
                        weaponGroup = s.WeaponGroup,
                        score = s.Total,
                        threshold = s.Threshold,
                        qualifies = s.Qualifies,
                        status = s.Status,
                        counts = s.CountsTowardGuldfodring,
                        fromCompetition = s.IsFromCompetition,
                        competitionName = s.IsFromCompetition ? s.Notes : null
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
                    int earnedYear = a.EarnedYear > 0 ? a.EarnedYear : DateTime.Now.Year;
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
        private async Task<(string? Earned, int EarnedYear, List<int> GuldYears, List<MarkenSeries> ThisYear)>
            AnalyzeSeriesProofAsync(int memberId, MarkenFamilyDef def, int displayYear)
        {
            // Read by DISCIPLINE, not family: Elit's precision series come from the Guldserie button
            // (family Pistolskytte) and its snabb series from the Snabbserie button (snabbpistol target).
            List<MarkenSeries> series;
            if (def.Key == MarkenFamilies.Elit)
            {
                series = (await _ledger.GetAllVerifiedSeriesAsync(memberId))
                    .Where(s =>
                    {
                        var d = Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target);
                        return d == Marken.DisciplinePrecision || d == Marken.DisciplineSnabbpistol;
                    })
                    // Competition series ARE valid elitprov (confirmed with Stefan 2026-08-28, from SHB
                    // 5.4: "skjutningarna får göras under både tränings- och tävlingsskjutning som
                    // anordnats enligt förbundets bestämmelser"), so materialised series count here too.
                    // The gates that keep this honest are elsewhere and unchanged: a held Guldmärke, only
                    // years after it, 5 precision AND 5 snabb in the SAME calendar year, one valör per
                    // year, in order.
                    // ⚠️ The PRECISION half is what competitions feed today. The snabb half still comes
                    // only from human submissions — a Duell competition's series are not materialised,
                    // because whether they are shot on the snabbpistoltavla at 25 m with 3 s/shot is a
                    // question about the discipline, not about this code. Until that is answered, a
                    // shooter cannot complete Elit from competition results alone.
                    .ToList();

                // SHB 5.4.2: "Prov för elitmärke får avläggas första gången året efter det guldmärket
                // erövrats." Elit requires a held Pistolskyttemärket Guld, and only series from the year
                // AFTER that grundmärke's year count.
                var guld = (await _ledger.GetBadgesForMemberAsync(memberId, Marken.FamilyPistolskytte))
                    .FirstOrDefault(b => b.Level == Marken.LevelGuld);
                if (guld == null)
                    return (null, 0, new List<int>(), new List<MarkenSeries>());
                int firstEligibleYear = guld.AchievedYear + 1;
                series = series.Where(s => s.Year >= firstEligibleYear).ToList();
            }
            else
            {
                // Luftpistol = Air discipline (its own family).
                series = await _ledger.GetVerifiedSeriesByFamilyAsync(memberId, def.Key);
            }

            // SHB progression: one valör/year, sequential (Brons→Silver→Guld).
            var perYear = series.GroupBy(s => s.Year)
                .Select(g => (g.Key, Marken.LevelOrdinal(SeriesProofLevel(def, g.ToList()))));
            var (held, heldYear, guldYears) = Marken.ApplyValorProgression(perYear);
            return (Marken.LevelFromOrdinal(held), heldYear, guldYears, series.Where(s => s.Year == displayYear).ToList());
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

        /// <summary>Human-readable krav (requirement thresholds) for a family, for the "Visa krav" section.</summary>
        private static List<string> FamilyKravLines(MarkenFamilyDef def)
        {
            var lines = new List<string>();
            if (def.Pattern == MarkenPattern.CompetitionAchievement && def.CompLevels != null)
            {
                string unit = def.HitBased ? "stn" : "ser";
                lines.Add($"{(def.HitBased ? "Antal träff" : "Poäng")}/tävling (Brons/Silver/Guld) — {def.CompetitionsRequired} tävlingar (krets+):");
                foreach (var (group, byDim) in def.CompLevels)
                {
                    var parts = byDim.OrderBy(kv => kv.Key).Select(kv =>
                        kv.Key == 0 ? $"{kv.Value[0]}/{kv.Value[1]}/{kv.Value[2]}"
                                    : $"{kv.Key} {unit}: {kv.Value[0]}/{kv.Value[1]}/{kv.Value[2]}");
                    lines.Add($"Vapengrupp {group}: {string.Join(" · ", parts)}");
                }
            }
            else if (def.Pattern == MarkenPattern.SeriesProof && def.SeriesThreshold != null)
            {
                lines.Add($"Per serie (Brons/Silver/Guld): {def.SeriesThreshold[0]}/{def.SeriesThreshold[1]}/{def.SeriesThreshold[2]}");
                lines.Add(def.RequiresSpeedSeriesToo
                    ? $"{def.SeriesRequired} precisionsserier + {def.SeriesRequired} snabbserier (snabbpistoltavla)"
                    : $"{def.SeriesRequired} serier");
            }
            return lines;
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
                (string? Earned, int EarnedYear, List<int> GuldYears, List<MarkenSeries> ThisYear) tuple;
                try { tuple = await AnalyzeSeriesProofAsync(memberId, fam, DateTime.Now.Year); }
                catch { continue; }
                if (!string.IsNullOrEmpty(tuple.Earned))
                    await _ledger.EnsureBadgeAsync(memberId, fam.Key, tuple.Earned!,
                        tuple.EarnedYear > 0 ? tuple.EarnedYear : DateTime.Now.Year, Marken.SourceAuto);
                foreach (var gy in tuple.GuldYears.Skip(1))
                    await _ledger.EnsureFulfilledYearAsync(memberId, fam.Key, gy);
            }
        }

        /// <summary>Read-only per-family summaries (competition + series-proof families) for the member view.</summary>
        private async Task<List<object>> BuildFamilySummariesAsync(int memberId, int year)
        {
            var pistolBadges = await _ledger.GetBadgesForMemberAsync(memberId, Marken.FamilyPistolskytte);
            int pistolTop = pistolBadges.Where(b => b.LevelOrdinal is >= 1 and <= 3)
                .Select(b => b.LevelOrdinal).DefaultIfEmpty(0).Max();
            int pistolGuldYear = pistolBadges.FirstOrDefault(b => b.Level == Marken.LevelGuld)?.AchievedYear ?? 0;

            var list = new List<object>();

            // Competition-driven families — one section each, always shown.
            foreach (var fam in MarkenFamilies.CompetitionFamilies)
            {
                CompFamilyAnalysis a;
                try { a = await _compService.AnalyzeAsync(memberId, fam.Key, year); }
                catch { a = new CompFamilyAnalysis { Family = fam.Key, CompetitionsRequired = fam.CompetitionsRequired }; }

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
                    int needMore = Math.Max(0, a.CompetitionsRequired - atNext);
                    status = $"För {next.ToLowerInvariant()}: {atNext}/{a.CompetitionsRequired} tävlingar i år"
                           + (needMore > 0 ? $" (saknar {needMore})" : "")
                           + " — krets-/landsdels-/riks-/nationell tävling.";
                }

                list.Add(new
                {
                    family = fam.Key,
                    displayName = fam.DisplayName,
                    pattern = "comp",
                    earnedLevel = earned,
                    nextLevel = next,
                    statusText = status,
                    kravLines = FamilyKravLines(fam),
                    earnedSource = top?.Source,
                    compsRequired = a.CompetitionsRequired,
                    thisYearComps = a.ThisYear.Select(e => new { name = e.CompetitionName, group = e.WeaponGroup, total = e.Total, level = e.ReachedLevel, source = e.Source }),
                    artalsmarke = new { current = ladder.CurrentName, fulfilledYears = ladder.FulfilledYears, next = ladder.NextName, nextAtYears = ladder.NextAtYears },
                    prereqText = prereqOk ? null : fam.PrereqText
                });
            }

            // Series-proof families (Luftpistol / Elit) — one section each, always shown.
            foreach (var fam in MarkenFamilies.SeriesProofFamilies)
            {
                (string? Earned, int EarnedYear, List<int> GuldYears, List<MarkenSeries> ThisYear) sp;
                try { sp = await AnalyzeSeriesProofAsync(memberId, fam, year); }
                catch { sp = (null, 0, new List<int>(), new List<MarkenSeries>()); }

                var badges = await _ledger.GetBadgesForMemberAsync(memberId, fam.Key);
                var top = badges.Where(b => b.LevelOrdinal is >= 1 and <= 3).OrderByDescending(b => b.LevelOrdinal).FirstOrDefault();
                var ladder = await _ledger.GetArtalsmarkeStatusAsync(memberId, fam.Key, includeUnverified: false);
                var earned = top?.Level ?? sp.Earned;
                bool prereqOk = fam.PrereqPistolskytteLevel == null || pistolTop >= Marken.LevelOrdinal(fam.PrereqPistolskytteLevel);

                // Elit timing gate (SHB 5.4.2): proofs may first be done the year after the Guldmärke.
                string? prereqNote = prereqOk ? null : fam.PrereqText;
                if (fam.Key == MarkenFamilies.Elit && prereqOk && pistolGuldYear > 0 && year <= pistolGuldYear)
                    prereqNote = $"Elitprov får avläggas först {pistolGuldYear + 1} (året efter att guldmärket erövrades {pistolGuldYear}).";

                var next = NextLevel(earned);
                string status;
                if (next == null)
                    status = ladder.FulfilledYears > 0 ? $"Guldmärket uppnått · {ladder.CurrentName}" : "Guldmärket uppnått.";
                else if (fam.RequiresSpeedSeriesToo && fam.SeriesThreshold != null)
                {
                    // Elit: show both halves so it's clear precision series are counting even when the
                    // snabb half is still empty (the badge needs the lesser of the two to reach the target).
                    int thr = fam.SeriesThreshold[Marken.LevelOrdinal(next) - 1];
                    int prec = sp.ThisYear.Count(s => s.SeriesType == Marken.SeriesTypePrecision && s.Total >= thr);
                    int speed = sp.ThisYear.Count(s => s.SeriesType == Marken.SeriesTypeSpeed && s.Total >= thr);
                    status = $"För {next.ToLowerInvariant()} (≥{thr} p/serie): precision {prec}/{fam.SeriesRequired} · snabb {speed}/{fam.SeriesRequired} (snabbpistoltavla), i år.";
                }
                else
                {
                    int atNext = SeriesProofCount(fam, sp.ThisYear, next);
                    int needMore = Math.Max(0, fam.SeriesRequired - atNext);
                    status = $"För {next.ToLowerInvariant()}: {atNext}/{fam.SeriesRequired} serier på nivå i år"
                           + (needMore > 0 ? $" (saknar {needMore})" : "") + ".";
                }

                list.Add(new
                {
                    family = fam.Key,
                    displayName = fam.DisplayName,
                    pattern = "series",
                    earnedLevel = earned,
                    nextLevel = next,
                    statusText = status,
                    kravLines = FamilyKravLines(fam),
                    earnedSource = top?.Source,
                    seriesRequired = fam.SeriesRequired,
                    requiresSpeedSeriesToo = fam.RequiresSpeedSeriesToo,
                    artalsmarke = new { current = ladder.CurrentName, fulfilledYears = ladder.FulfilledYears, next = ladder.NextName, nextAtYears = ladder.NextAtYears },
                    prereqText = prereqNote
                });
            }
            return list;
        }

        // ── Mästarmärket (5.2) — bespoke, year-count → valör (Route 1) ──

        /// <summary>
        /// Auto-derive Route 1 qualifying years (a standardmedalj i SILVER in BOTH fält and precision the
        /// same year, SHB 5.2 alt. 1) from the medal ledger, ensure each as a fulfilled Mästar year, then
        /// award the base valör for the accumulated count. Manual-override years (added by a functionary
        /// for pre-system medals) share the same table and are preserved. Lenient/add-only like the
        /// competition families. Best-effort; lazy on read.
        /// </summary>
        private async Task RecomputeMastarAsync(int memberId)
        {
            try
            {
                var awards = await _standardMedals.GetAwardsForMemberAsync(memberId);
                foreach (var yg in awards.Where(a => a.MedalType == StandardMedals.Silver).GroupBy(a => a.Year))
                {
                    bool falt = yg.Any(a => string.Equals(a.Discipline, StandardMedals.Faltskytte, StringComparison.OrdinalIgnoreCase));
                    bool prec = yg.Any(a => string.Equals(a.Discipline, StandardMedals.Precision, StringComparison.OrdinalIgnoreCase));
                    if (falt && prec)
                        await _ledger.EnsureFulfilledYearAsync(memberId, Marken.FamilyMastar, yg.Key);
                }

                int years = (await _ledger.GetQualificationsForMemberAsync(memberId, Marken.FamilyMastar))
                    .Count(q => q.Fulfilled && q.Status == Marken.StatusVerified);
                var lvl = Marken.MastarLevel(years);
                if (lvl != null)
                    await _ledger.EnsureBadgeAsync(memberId, Marken.FamilyMastar, lvl, DateTime.Now.Year, Marken.SourceAuto);
            }
            catch { /* best-effort */ }
        }

        private async Task<object> MastarSummaryAsync(int memberId)
        {
            List<int> qualYears = new();
            try
            {
                qualYears = (await _ledger.GetQualificationsForMemberAsync(memberId, Marken.FamilyMastar))
                    .Where(q => q.Fulfilled && q.Status == Marken.StatusVerified)
                    .Select(q => q.Year).Distinct().OrderBy(y => y).ToList();
            }
            catch { }
            int years = qualYears.Count;

            var pistolBadges = await _ledger.GetBadgesForMemberAsync(memberId, Marken.FamilyPistolskytte);
            bool hasGuld = pistolBadges.Any(b => b.Level == Marken.LevelGuld);

            var earned = Marken.MastarLevel(years);
            var levelDisplay = Marken.MastarLevelDisplay(years);
            int nextAt = Marken.MastarYearsToNext(years);

            string status;
            if (earned == null)
                status = $"För brons krävs 3 kvalificerande år — du har {years}.";
            else if (nextAt > 0)
            {
                string nextLabel = nextAt <= 6 ? "Silver" : nextAt <= 9 ? "Guld" : "Nästa stjärna";
                status = $"Innehar {levelDisplay}. {nextLabel} vid {nextAt} kvalificerande år (du har {years}).";
            }
            else
                status = $"Innehar {levelDisplay} — högsta valören uppnådd.";

            return new
            {
                family = Marken.FamilyMastar,
                displayName = Marken.FamilyDisplayName(Marken.FamilyMastar),
                earnedLevel = earned,
                levelDisplay,
                qualifyingYears = years,
                qualifyingYearList = qualYears,
                statusText = status,
                kravLines = new List<string>
                {
                    "Kräver pistolskyttemärke i guld. Brons/silver/guld vid 3/6/9 kvalificerande år; guld med ★/★★/★★★ vid 14/19/24.",
                    "Alternativ 1 (kvalificerande år): standardmedalj i silver i BÅDE fält och precision samma år.",
                    Marken.MastarRoute2Note
                },
                prereqText = hasGuld ? null : "Kräver pistolskyttemärke i guld."
            };
        }

        public class MastarYearRequest { public int MemberId { get; set; } public int Year { get; set; } public bool Qualified { get; set; } }

        /// <summary>
        /// Functionary override: mark/unmark a Mästar Route-1 qualifying year for a member (for medals
        /// earned before the system / off pistol.nu). POST /umbraco/surface/Marken/SetMastarQualifyingYear
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMastarQualifyingYear([FromBody] MastarYearRequest request)
        {
            int memberId = request?.MemberId ?? 0;
            int year = request?.Year ?? 0;
            if (memberId <= 0 || year < 1900 || year > DateTime.Now.Year + 1)
                return Json(new { success = false, message = "Ogiltigt år." });
            if (!await CanSignOffForMemberAsync(memberId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            if (request!.Qualified)
                await _ledger.EnsureFulfilledYearAsync(memberId, Marken.FamilyMastar, year);
            else
            {
                var q = await _ledger.GetQualificationForYearAsync(memberId, Marken.FamilyMastar, year);
                if (q != null) await _ledger.DeleteQualificationAsync(q.Id);
            }
            await RecomputeMastarAsync(memberId);
            return Json(new { success = true, message = "Sparat." });
        }

        /// <summary>Mästar summary + edit rights for a member, for the secretary detail panel.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberMastar(int memberId)
        {
            if (memberId <= 0) return Json(new { success = false });
            if (!await CanViewMemberAsync(memberId)) return Json(new { success = false, message = "Åtkomst nekad." });
            await RecomputeMastarAsync(memberId);
            return Json(new { success = true, mastar = await MastarSummaryAsync(memberId), canEdit = await CanSignOffForMemberAsync(memberId) });
        }

        // ── Stormästarmärket (5.3) — career inteckningspoäng ──────────

        public class SubmitStormastarRequest
        {
            public int ClubId { get; set; }
            public int Year { get; set; }
            public string Scope { get; set; } = "";
            public int Participants { get; set; }
            public int Place { get; set; }
            public string? Discipline { get; set; }
            public string? CompetitionName { get; set; }
            public string? Notes { get; set; }
            public string? PhotoRef { get; set; }
        }

        /// <summary>Submit one championship result toward Stormästarmärket. Lands Pending; returns a QR token.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitStormastarEntry([FromBody] SubmitStormastarRequest req)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false, message = "Inte inloggad." });
            if (req == null) return Json(new { success = false, message = "Ogiltig begäran." });
            if (!MemberBelongsToClub(member, req.ClubId))
                return Json(new { success = false, message = "Välj en klubb du är medlem i." });
            if (req.Scope is not (Marken.SmScopeKrets or Marken.SmScopeLandsdel or Marken.SmScopeSvenskt))
                return Json(new { success = false, message = "Välj mästerskapsnivå." });
            if (req.Participants < 1) return Json(new { success = false, message = "Ange antal deltagare." });
            if (req.Place < 1) return Json(new { success = false, message = "Ange placering." });

            int year = req.Year > 1900 ? req.Year : DateTime.Now.Year;
            int points = Marken.StormastarPoints(req.Scope, req.Participants, req.Place);

            var e = new MarkenStormastarEntry
            {
                MemberId = member.Id,
                ClubId = req.ClubId,
                Year = year,
                Scope = req.Scope,
                Participants = req.Participants,
                Place = req.Place,
                Points = points,
                Discipline = string.IsNullOrWhiteSpace(req.Discipline) ? null : req.Discipline!.Trim(),
                CompetitionName = string.IsNullOrWhiteSpace(req.CompetitionName) ? null : req.CompetitionName!.Trim(),
                Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes!.Trim(),
                ProofFileRef = string.IsNullOrWhiteSpace(req.PhotoRef) ? null : req.PhotoRef,
                Status = Marken.SeriesStatusPending,
                EnteredByMemberId = member.Id
            };
            var id = await _stormastarService.InsertAsync(e);
            var token = ProtectVerifyToken("stormastar:" + id);
            return Json(new
            {
                success = true,
                id,
                points,
                verifyToken = token,
                verifyUrl = $"{Request.Scheme}://{Request.Host}/marken/verifiera?t={Uri.EscapeDataString(token)}",
                message = points > 0
                    ? $"Sparat ({points} inteckningspoäng) och skickat för validering."
                    : "Sparat. Resultatet ger 0 inteckningspoäng men kan ändå valideras."
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteStormastarEntry([FromBody] IdRequest request)
        {
            var e = await _stormastarService.GetAsync(request?.Id ?? 0);
            if (e == null) return Json(new { success = false, message = "Hittades inte." });
            var me = await GetCurrentMemberAsync();
            bool owner = me != null && me.Id == e.MemberId && e.Status == Marken.SeriesStatusPending;
            if (!owner && !await CanSignOffForClubAsync(e.ClubId))
                return Json(new { success = false, message = "Åtkomst nekad." });
            var (ok, msg) = await _stormastarService.DeleteAsync(e.Id);
            return Json(new { success = ok, message = ok ? "Borttaget." : msg });
        }

        /// <summary>The member's own Stormästar entries.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyStormastarEntries()
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false });
            var list = await _stormastarService.GetForMemberAsync(member.Id);
            return Json(new { success = true, entries = list.Select(StormastarDto) });
        }

        /// <summary>Poll status of the member's own Stormästar entry (QR live-update).</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyStormastarStatus(int id)
        {
            var member = await GetCurrentMemberAsync();
            if (member == null) return Json(new { success = false });
            var e = await _stormastarService.GetAsync(id);
            if (e == null || e.MemberId != member.Id) return Json(new { success = false });
            return Json(new { success = true, status = e.Status });
        }

        private async Task<object> StormastarSummaryAsync(int memberId)
        {
            var list = await _stormastarService.GetForMemberAsync(memberId);
            int verified = list.Where(e => e.Status == Marken.StatusVerified).Sum(e => e.Points);
            int pending = list.Where(e => e.Status == Marken.SeriesStatusPending).Sum(e => e.Points);
            return new
            {
                family = Marken.FamilyStormastar,
                displayName = Marken.FamilyDisplayName(Marken.FamilyStormastar),
                verifiedPoints = verified,
                pendingPoints = pending,
                eligibleAt = Marken.StormastarEligibleAt,
                eligible = verified >= Marken.StormastarEligibleAt,
                entries = list.Select(StormastarDto)
            };
        }

        private object StormastarDto(MarkenStormastarEntry e) => new
        {
            kind = "stormastar",
            id = e.Id,
            memberId = e.MemberId,
            memberName = _memberService.GetById(e.MemberId)?.Name,
            clubId = e.ClubId,
            clubName = _clubService.GetClubNameById(e.ClubId),
            year = e.Year,
            scope = e.Scope,
            scopeName = Marken.StormastarScopeDisplay(e.Scope),
            participants = e.Participants,
            place = e.Place,
            points = e.Points,
            discipline = e.Discipline,
            competitionName = e.CompetitionName,
            notes = e.Notes,
            status = e.Status,
            hasPhoto = !string.IsNullOrEmpty(e.ProofFileRef),
            validatedDate = e.ValidatedDate
        };

        /// <summary>Stream a Stormästar entry's proof photo — owner, an authorized validator, or site admin.</summary>
        [HttpGet]
        public async Task<IActionResult> GetStormastarPhoto(int id)
        {
            var e = await _stormastarService.GetAsync(id);
            if (e == null || string.IsNullOrEmpty(e.ProofFileRef)) return NotFound();
            var viewer = await GetCurrentMemberAsync();
            if (viewer == null) return Unauthorized();
            bool ok = viewer.Id == e.MemberId || await _auth.IsCurrentUserAdminAsync() || await CanSignOffForClubAsync(e.ClubId);
            if (!ok) return Forbid();
            var path = _proofStorage.GetFilePath(e.ProofFileRef);
            if (path == null) return NotFound();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, StandardMedalProofStorage.ContentTypeFor(e.ProofFileRef));
        }

        private async Task<IActionResult> SetStormastarStatus(int id, string status)
        {
            var e = await _stormastarService.GetAsync(id);
            if (e == null) return Json(new { success = false, message = "Hittades inte." });
            int actingId = await GetCurrentMemberIdAsync();
            if (e.MemberId == actingId) return Json(new { success = false, message = SelfValidateMsg });
            if (!await CanSignOffForClubAsync(e.ClubId)) return Json(new { success = false, message = "Åtkomst nekad." });
            var (ok, msg) = await _stormastarService.SetStatusAsync(id, status, actingId);
            return Json(new { success = ok, message = ok ? (status == Marken.StatusVerified ? "Godkänd." : "Avvisad.") : msg });
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
            // Materialise the member's competition series FIRST. The analyser reads the ledger only, so
            // without this a series shot at a hosted competition would not count at all — and a result
            // corrected days after the competition has to move the guldserie with it.
            await _compSeriesSync.SyncMemberYearAsync(memberId, year);

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
            else if (existing != null && existing.Part1Source != Marken.PartSourceManualAttest)
            {
                // No longer complete — reflect current parts; drops out of the årtalsmärke count.
                // A functionary-asserted historical year (PartSourceManualAttest) is authoritative and
                // has no validated series to re-derive from, so it is left untouched here.
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
        /// <summary>Clubs where the current user is a functionary who may validate märken —
        /// board members always, Skjutledare only where the club enabled it. (No site-admin
        /// short-circuit: site admins validate any club via that club's admin tab / QR / the
        /// "alla klubbar" toggle, not by flooding their personal queue.)</summary>
        private async Task<HashSet<int>> GetFunctionaryClubsAsync()
        {
            var clubs = new HashSet<int>();
            int actingId = await GetCurrentMemberIdAsync();
            if (actingId <= 0) return clubs;

            // Board memberships (member-scoped).
            foreach (var c in _boardRoles.GetClubIdsWhereBoardMember(actingId)) clubs.Add(c);

            // Skjutledare clubs from the member's OWN roles. We must NOT use
            // _auth.GetSkjutledareClubIds() here: it returns EVERY club for a site admin, which would
            // re-introduce the site-wide firehose this scoping is meant to remove (the personal queue
            // would show all clubs that have markenSignoffSkjutledare on). Read the Skjutledare_{id}
            // roles directly so a site admin only gets the clubs they're actually a Skjutledare of.
            foreach (var role in _memberService.GetAllRoles(actingId))
            {
                if (role.StartsWith("Skjutledare_", StringComparison.Ordinal)
                    && int.TryParse(role.Substring("Skjutledare_".Length), out var clubId)
                    && SkjutledareSignoffEnabled(clubId))
                    clubs.Add(clubId);
            }
            return clubs;
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

        /// <summary>
        /// True when two stored shot arrays are the same sequence. Compared as SEQUENCES, not as sets or
        /// sums: the total is already part of the duplicate signature, so what this adds is the order —
        /// which is what separates the same series entered twice from two series that happen to score
        /// the same. An "X" and a "10" are kept distinct, so a shooter who typed 10 where the result
        /// sheet says X gets the softer warning rather than a silent exclusion.
        /// </summary>
        private static bool SameShots(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                var xa = System.Text.Json.JsonSerializer.Deserialize<List<string>>(a);
                var xb = System.Text.Json.JsonSerializer.Deserialize<List<string>>(b);
                if (xa == null || xb == null || xa.Count == 0 || xa.Count != xb.Count) return false;
                for (int i = 0; i < xa.Count; i++)
                    if (!string.Equals((xa[i] ?? "").Trim(), (xb[i] ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }
            catch { return false; }
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
                discipline = Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target),
                disciplineName = Marken.DisciplineDisplay(Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target)),
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
                validatedDate = s.ValidatedDate,
                notes = s.Notes,
                counts = s.CountsTowardGuldfodring,
                fromCompetition = s.IsFromCompetition,
                competitionName = s.IsFromCompetition ? s.Notes : null,
                // Per ITEM, not per queue: the Min sida queue spans several clubs, and only some of
                // them may demand on-site witnessing.
                requiresOnSiteWitness = RequireOnSiteWitness(s.ClubId),
                // The validator IS the witness on a series approved through the QR flow, so this is
                // what "vem har bevittnat serien" resolves to once it has been approved.
                validatedByName = s.ValidatedByMemberId is int vid && vid > 0
                    ? _memberService.GetById(vid)?.Name
                    : null
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

        /// <summary>Mints a QR verify token that expires (see <see cref="VerifyTokenLifetime"/>).</summary>
        private string ProtectVerifyToken(string payload) => _verifyProtector.Protect(payload, VerifyTokenLifetime);

        /// <summary>
        /// Makes sure the year's hosted-competition guldserier are materialised into the ledger before a
        /// CLUB-WIDE surface reads it, cached so the cost is paid once per year per 10 minutes rather
        /// than on every tab load. A member's own page reconciles unconditionally (and cheaply, one
        /// member) in <c>RecomputeYearlyQualificationAsync</c>, so a shooter never sees stale data about
        /// themselves because of this cache.
        /// </summary>
        private async Task EnsureCompetitionSeriesSyncedAsync(int year)
        {
            var key = $"marken_compsync_year_{year}";
            if (AppCaches.RuntimeCache.Get(key) != null) return;
            await _compSeriesSync.SyncYearFromResultsAsync(year);
            AppCaches.RuntimeCache.Insert(key, () => (object)DateTime.Now, TimeSpan.FromMinutes(10));
        }

        /// <summary>
        /// Reads the per-club <c>markenRequireOnSiteWitness</c> toggle (default false). When on, a
        /// series may only be approved by scanning the shooter's live QR code — the club has decided
        /// that a functionary must have witnessed the shooting, as SHB kap 5 assumes. Default is off
        /// because a hard requirement is unusable where there is no coverage at the range.
        /// </summary>
        private bool RequireOnSiteWitness(int clubId)
        {
            if (clubId <= 0) return false;
            // Memoized: SerieDto asks per item, so an unmemoized read would be one content lookup per
            // row in the validation queue.
            if (_onSiteWitnessCache.TryGetValue(clubId, out var cached)) return cached;
            bool required;
            try
            {
                var club = _contentService.GetById(clubId);
                required = club != null
                    && club.HasProperty("markenRequireOnSiteWitness")
                    && club.GetValue<bool>("markenRequireOnSiteWitness");
            }
            catch { required = false; }
            _onSiteWitnessCache[clubId] = required;
            return required;
        }

        private readonly Dictionary<int, bool> _onSiteWitnessCache = new();

        /// <summary>
        /// True when <paramref name="token"/> is a live (unexpired) verify token minted for exactly
        /// <paramref name="expectedPayload"/> — i.e. the validator really scanned THIS evidence's QR
        /// code rather than pasting some other one they had lying around.
        /// </summary>
        private bool IsLiveVerifyToken(string? token, string expectedPayload)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            try { return _verifyProtector.Unprotect(token) == expectedPayload; }
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

        /// <summary>
        /// Both halves of the order list on one printable page. Beställningslistan first (that is the
        /// thing with a January deadline), utdelningslistan below it with a tick column, because at the
        /// utdelning someone stands with a pen and needs to mark off what has actually been handed over.
        /// </summary>
        private static string BuildOrderListPrintHtml(MarkenOrderList data)
        {
            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var sb = new System.Text.StringBuilder();

            sb.Append("<!DOCTYPE html><html lang='sv'><head><meta charset='utf-8'>");
            sb.Append("<title>Märken ").Append(data.Year).Append(" – ").Append(Enc(data.ClubName)).Append("</title>");
            sb.Append("<style>body{font-family:Arial,Helvetica,sans-serif;margin:2rem;color:#222}");
            sb.Append("h1{font-size:1.4rem;margin-bottom:.2rem}h2{font-size:1.1rem;margin-top:1.8rem}");
            sb.Append("table{border-collapse:collapse;width:100%;margin:.5rem 0 1rem}");
            sb.Append("th,td{border:1px solid #ccc;padding:.35rem .6rem;text-align:left;font-size:.88rem;vertical-align:top}");
            sb.Append("th{background:#f3f3f3}td.num{text-align:right;font-variant-numeric:tabular-nums}");
            sb.Append(".muted{color:#666;font-size:.82rem}.warn{border-left:4px solid #d19b00;background:#fff8e6;padding:.5rem .7rem;margin:.6rem 0;font-size:.85rem}");
            sb.Append(".tick{width:1.6rem;text-align:center}.grp{color:#555}");
            sb.Append("tr.total td{font-weight:bold;background:#f9f9f9}");
            sb.Append("@media print{button{display:none}h2{page-break-after:avoid}tr{page-break-inside:avoid}}");
            sb.Append("</style></head><body>");
            sb.Append("<button onclick='window.print()'>Skriv ut</button>");

            sb.Append("<h1>Märken ").Append(data.Year).Append("</h1>");
            sb.Append("<p class='muted'>").Append(Enc(data.ClubName))
              .Append(" · årets förvärvade märken · utskriven ")
              .Append(DateTime.Now.ToString("yyyy-MM-dd")).Append("</p>");

            foreach (var w in data.Warnings)
                sb.Append("<div class='warn'>").Append(Enc(w)).Append("</div>");

            sb.Append("<h2>Att beställa</h2>");
            if (data.Order.Count == 0)
            {
                sb.Append("<p class='muted'>Inga märken att beställa för ").Append(data.Year)
                  .Append(". Står det ändå namn under <em>Att dela ut</em> är det årsprestationer utan föremål — se noteringen på raden.</p>");
            }
            else
            {
                sb.Append("<table><tr><th>Grupp</th><th>Artikel</th><th style='width:5rem'>Antal</th><th>Notering</th></tr>");
                foreach (var l in data.Order)
                    sb.Append("<tr><td class='grp'>").Append(Enc(l.Group))
                      .Append("</td><td>").Append(Enc(l.Item))
                      .Append("</td><td class='num'>").Append(l.Count)
                      .Append("</td><td class='muted'>").Append(Enc(l.Note)).Append("</td></tr>");
                sb.Append("<tr class='total'><td>Totalt</td><td></td><td class='num'>").Append(data.TotalItems).Append("</td><td></td></tr>");
                sb.Append("</table>");
            }

            sb.Append("<h2>Att dela ut</h2>");
            if (data.Handout.Count == 0)
            {
                sb.Append("<p class='muted'>Ingen medlem tog märke eller medalj under ").Append(data.Year).Append(".</p>");
            }
            else
            {
                sb.Append("<table><tr><th class='tick'>&#10003;</th><th style='width:14rem'>Medlem</th><th>Utmärkelse</th><th>Detalj</th></tr>");
                foreach (var h in data.Handout)
                {
                    bool first = true;
                    foreach (var i in h.Items)
                    {
                        sb.Append("<tr><td class='tick'>&#9744;</td><td>")
                          .Append(first ? Enc(h.Name) : "").Append("</td><td>");
                        // Samma regel som i kortet: hoppa över gruppprefixet när posten redan
                        // börjar med det, annars blir det "Guldfodring Guldfodring 2026 uppfylld".
                        if (!i.Item.StartsWith(i.Group, StringComparison.OrdinalIgnoreCase))
                            sb.Append("<span class='grp'>").Append(Enc(i.Group)).Append("</span> ");
                        sb.Append(Enc(i.Item));
                        if (!i.Orderable) sb.Append(" <span class='muted'>(inget märke att beställa)</span>");
                        if (i.Unverified) sb.Append(" <span class='muted'>(ej granskad)</span>");
                        sb.Append("</td><td class='muted'>").Append(Enc(i.Detail)).Append("</td></tr>");
                        first = false;
                    }
                }
                sb.Append("</table>");
            }

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
        public class AwardFamilyBadgeRequest { public int MemberId { get; set; } public string Family { get; set; } = ""; public string Level { get; set; } = ""; public int Year { get; set; } public string? Note { get; set; } }
        public class UniqueNumberRequest { public int BadgeId { get; set; } public string? UniqueNumber { get; set; } public int? Year { get; set; } }
        public class SetMemberPistolskytteRequest { public int MemberId { get; set; } public string Level { get; set; } = ""; public string? GuldNumber { get; set; } }
        public class FamilyMemberRequest { public int MemberId { get; set; } public string Family { get; set; } = ""; }

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
            public int QualifyingSeries { get; set; }   // verified qualifying Guld precision series this year (Part 1)
            public int SpeedSeries { get; set; }        // verified Guld snabbserier this year (Part 2)
        }
    }
}
