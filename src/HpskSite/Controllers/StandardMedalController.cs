using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Endpoints for Standardmedalj proof files: members upload a PDF/photo of their result
    /// list or diploma; the file is stored under App_Data and only streamed back to the owning
    /// member, a club admin for that member's club, or a site admin.
    /// (Award + Guldmedalj management endpoints come with the secretary tab in Phase B.)
    /// </summary>
    public class StandardMedalController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly StandardMedalProofStorage _proofStorage;
        private readonly StandardMedalLedgerService _ledger;
        private readonly AdminAuthorizationService _authorizationService;

        public StandardMedalController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            StandardMedalProofStorage proofStorage,
            StandardMedalLedgerService ledger,
            AdminAuthorizationService authorizationService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _proofStorage = proofStorage;
            _ledger = ledger;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Upload a proof file. Returns an opaque reference the caller then submits with the
        /// result/medal so the StandardMedalAward can be created with ProofFileRef set.
        /// POST /umbraco/surface/StandardMedal/UploadProof  (multipart/form-data, field "file")
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadProof(IFormFile? file)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Du måste vara inloggad för att ladda upp bevis." });

            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "Ingen fil mottogs." });

            var (ok, error) = _proofStorage.Validate(file.FileName, file.Length);
            if (!ok)
                return Json(new { success = false, message = error });

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

        /// <summary>
        /// The current member's Standardmedalj progress: per-discipline klass-3 qualification
        /// for the given year (default current year) and lifetime Guldmedalj status. Counts
        /// non-rejected awards (incl. unverified self-reported) so the member sees full progress.
        /// GET /umbraco/surface/StandardMedal/GetMyMedalSummary?year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyMedalSummary(int? year)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad." });

            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (member == null)
                return Json(new { success = false, message = "Medlem hittades inte." });

            int y = year ?? DateTime.Now.Year;
            var qualification = await _ledger.GetQualificationAsync(member.Id, y, verifiedOnly: false);
            var gold = await _ledger.GetGoldStatusAsync(member.Id, verifiedOnly: false);

            return Json(new
            {
                success = true,
                year = y,
                qualificationThreshold = Models.StandardMedals.QualificationThreshold,
                goldThreshold = Models.StandardMedals.GoldThreshold,
                qualification = qualification.Select(q => new
                {
                    discipline = q.Discipline,
                    displayName = q.DisplayName,
                    points = q.Points,
                    silver = q.SilverCount,
                    brons = q.BronsCount,
                    qualified = q.Qualified
                }),
                gold = new
                {
                    lifetimePoints = gold.LifetimePoints,
                    available = gold.AvailablePoints,
                    consumed = gold.ConsumedPoints,
                    goldsAwarded = gold.GoldsAwarded,
                    canApply = gold.CanApplyForGold,
                    toNext = gold.PointsToNextGold
                }
            });
        }

        /// <summary>
        /// The linked Standardmedalj award for one of the current member's self-entered results —
        /// so the edit modal can show current proof status and offer to add/replace it.
        /// GET /umbraco/surface/StandardMedal/GetMedalForTrainingScore?trainingScoreId=123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMedalForTrainingScore(int trainingScoreId)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad." });

            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (member == null)
                return Json(new { success = false, message = "Medlem hittades inte." });

            var award = await _ledger.GetByTrainingScoreAsync(trainingScoreId);
            if (award == null || award.MemberId != member.Id)
                return Json(new { success = true, hasAward = false });

            return Json(new
            {
                success = true,
                hasAward = true,
                awardId = award.Id,
                medalType = award.MedalType,
                status = award.Status,
                hasProofFile = award.ProofType == Models.StandardMedals.ProofFile && !string.IsNullOrEmpty(award.ProofFileRef),
                inGold = award.GoldApplicationId.HasValue
            });
        }

        /// <summary>
        /// The current member's previously-uploaded proof files, so a single result list can be
        /// reused across several results from the same competition without re-uploading. One entry
        /// per distinct file, newest first.
        /// GET /umbraco/surface/StandardMedal/GetMyReusableProofs
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyReusableProofs()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Json(new { success = false, message = "Inte inloggad." });

            var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (member == null)
                return Json(new { success = false, message = "Medlem hittades inte." });

            var awards = await _ledger.GetAwardsForMemberAsync(member.Id, includeRejected: false);
            var proofs = awards
                .Where(a => a.ProofType == Models.StandardMedals.ProofFile && !string.IsNullOrEmpty(a.ProofFileRef))
                .GroupBy(a => a.ProofFileRef!)
                .Select(g => g.OrderByDescending(a => a.CompetitionDate ?? DateTime.MinValue).First())
                .OrderByDescending(a => a.CompetitionDate ?? DateTime.MinValue)
                .Take(25)
                .Select(a => new
                {
                    proofRef = a.ProofFileRef,
                    awardId = a.Id,
                    competitionName = a.CompetitionName,
                    competitionDate = a.CompetitionDate,
                    medalType = a.MedalType
                });

            return Json(new { success = true, proofs });
        }

        // ── Club secretary endpoints ──────────────────────────────────

        /// <summary>
        /// Members of a club (by primary club) who won Standard medals in a year, with point
        /// totals, medal counts, and how many self-reported awards still need verifying.
        /// GET /umbraco/surface/StandardMedal/GetClubMedalSummary?clubId=1098&year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubMedalSummary(int clubId, int? year)
        {
            if (clubId <= 0)
                return Json(new { success = false, message = "Ogiltigt klubb-ID." });
            if (!await _authorizationService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            int y = year ?? DateTime.Now.Year;
            var awards = await _ledger.GetAwardsForYearAsync(y);

            var rows = new List<(int MemberId, string Name, int Points, int Silver, int Brons, int Unverified, int MedalCount)>();
            foreach (var g in awards.GroupBy(a => a.MemberId))
            {
                var member = _memberService.GetById(g.Key);
                if (member == null) continue;
                if (!int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var pc) || pc != clubId) continue;

                var list = g.ToList();
                rows.Add((
                    g.Key,
                    member.Name ?? $"Medlem {g.Key}",
                    list.Sum(a => a.Points),
                    list.Count(a => a.MedalType == Models.StandardMedals.Silver),
                    list.Count(a => a.MedalType == Models.StandardMedals.Brons),
                    list.Count(a => a.Status == Models.StandardMedals.StatusReported),
                    list.Count));
            }

            var ordered = rows
                .OrderByDescending(r => r.Points)
                .ThenBy(r => r.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false))
                .Select(r => new
                {
                    memberId = r.MemberId,
                    name = r.Name,
                    points = r.Points,
                    silver = r.Silver,
                    brons = r.Brons,
                    unverified = r.Unverified,
                    medalCount = r.MedalCount
                });

            return Json(new { success = true, year = y, members = ordered });
        }

        /// <summary>
        /// A single member's awards for a year (incl. rejected, so the admin sees full history).
        /// GET /umbraco/surface/StandardMedal/GetMemberMedalDetail?memberId=2043&year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberMedalDetail(int memberId, int? year)
        {
            var clubId = GetPrimaryClubId(memberId);
            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId));
            if (!ok)
                return Json(new { success = false, message = "Åtkomst nekad." });

            int y = year ?? DateTime.Now.Year;
            var awards = await _ledger.GetAwardsForMemberAsync(memberId, y, includeRejected: true);
            var member = _memberService.GetById(memberId);

            var items = awards.Select(a => new
            {
                id = a.Id,
                discipline = Models.StandardMedals.DisciplineDisplayName(a.Discipline),
                medal = Models.StandardMedals.MedalDisplayName(a.MedalType),
                medalType = a.MedalType,
                points = a.Points,
                competitionName = a.CompetitionName,
                competitionDate = a.CompetitionDate,
                location = a.Location,
                shootingClass = a.ShootingClass,
                source = a.Source,
                status = a.Status,
                proofType = a.ProofType,
                hasProofFile = a.ProofType == Models.StandardMedals.ProofFile && !string.IsNullOrEmpty(a.ProofFileRef),
                inGold = a.GoldApplicationId.HasValue,
                // The medal type is correctable until it's locked in: OnSite awards follow the
                // result list, Gold-consumed awards back a submitted application, and a Verified
                // award is the club's confirmed record — all three show a fixed badge, not a select.
                editable = a.Source != Models.StandardMedals.SourceOnSite
                           && !a.GoldApplicationId.HasValue
                           && a.Status != Models.StandardMedals.StatusVerified
            });

            return Json(new { success = true, memberId, memberName = member?.Name, year = y, awards = items });
        }

        public class AwardActionRequest
        {
            public int AwardId { get; set; }
        }

        /// <summary>Mark a self-reported award as Verified. Club admin (member's club) or site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyAward([FromBody] AwardActionRequest request)
            => await SetAwardStatus(request?.AwardId ?? 0, Models.StandardMedals.StatusVerified);

        /// <summary>Reject a self-reported award (drops it from counts). Club admin or site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectAward([FromBody] AwardActionRequest request)
            => await SetAwardStatus(request?.AwardId ?? 0, Models.StandardMedals.StatusRejected);

        public class AwardMedalRequest
        {
            public int AwardId { get; set; }
            public string MedalType { get; set; } = "";
        }

        /// <summary>
        /// Correct a self-reported/admin award's medal type (S↔B). Not allowed for OnSite awards
        /// (those follow the result list) or awards locked into a Gold application.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAwardMedal([FromBody] AwardMedalRequest request)
        {
            var awardId = request?.AwardId ?? 0;
            if (awardId <= 0)
                return Json(new { success = false, message = "Ogiltigt medalj-ID." });

            var award = await _ledger.GetAwardAsync(awardId);
            if (award == null)
                return Json(new { success = false, message = "Medaljen hittades inte." });

            if (award.Source == Models.StandardMedals.SourceOnSite)
                return Json(new { success = false, message = "Medaljer från pistol.nu-tävlingar ändras i resultatlistan, inte här." });

            if (award.Status == Models.StandardMedals.StatusVerified)
                return Json(new { success = false, message = "Medaljen är verifierad och kan inte ändras. Avvisa den först om den behöver korrigeras." });

            var clubId = GetPrimaryClubId(award.MemberId);
            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId));
            if (!ok)
                return Json(new { success = false, message = "Åtkomst nekad." });

            var (success, message) = await _ledger.SetAwardMedalAsync(awardId, request!.MedalType);
            return Json(new { success, message });
        }

        private async Task<IActionResult> SetAwardStatus(int awardId, string status)
        {
            if (awardId <= 0)
                return Json(new { success = false, message = "Ogiltigt medalj-ID." });

            var award = await _ledger.GetAwardAsync(awardId);
            if (award == null)
                return Json(new { success = false, message = "Medaljen hittades inte." });

            var clubId = GetPrimaryClubId(award.MemberId);
            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId));
            if (!ok)
                return Json(new { success = false, message = "Åtkomst nekad." });

            var actingId = await GetCurrentMemberIdAsync();
            var (success, message) = await _ledger.SetAwardStatusAsync(awardId, status, actingId);
            return Json(new { success, message });
        }

        private int GetPrimaryClubId(int memberId)
        {
            var member = _memberService.GetById(memberId);
            if (member != null && int.TryParse(member.GetValue("primaryClubId")?.ToString(), out var clubId))
                return clubId;
            return 0;
        }

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            var member = _memberService.GetByEmail(current.Email ?? string.Empty);
            return member?.Id ?? 0;
        }

        // ── Exports (CSV) ─────────────────────────────────────────────
        // Format-agnostic groundwork: a semicolon-separated, UTF-8-BOM CSV that opens cleanly in
        // Swedish Excel. Adapting to the eventual SPSF form is a column-mapping change here only.

        /// <summary>
        /// CSV of every Standard medal won by the club's members in a year (one row per medal).
        /// GET /umbraco/surface/StandardMedal/ExportClubMedals?clubId=1098&year=2026
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportClubMedals(int clubId, int? year)
        {
            if (clubId <= 0)
                return Content("Ogiltigt klubb-ID.");
            if (!await _authorizationService.IsClubAdminForClub(clubId))
                return Content("Åtkomst nekad.");

            int y = year ?? DateTime.Now.Year;
            var awards = await _ledger.GetAwardsForYearAsync(y);

            // Filter to this club's members and resolve names once.
            var nameById = new Dictionary<int, string?>();
            var rows = new List<(string Name, Models.StandardMedalAward A)>();
            foreach (var a in awards)
            {
                if (!nameById.TryGetValue(a.MemberId, out var name))
                {
                    var member = _memberService.GetById(a.MemberId);
                    int pc = 0;
                    if (member != null) int.TryParse(member.GetValue("primaryClubId")?.ToString(), out pc);
                    name = pc == clubId ? (member?.Name ?? $"Medlem {a.MemberId}") : null; // null = not this club
                    nameById[a.MemberId] = name;
                }
                if (name != null) rows.Add((name, a));
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Medlem;År;Gren;Tävling;Datum;Ort;Klass;Medalj;Poäng;Källa;Status");
            foreach (var (name, a) in rows
                .OrderBy(r => r.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false))
                .ThenBy(r => r.A.CompetitionDate))
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    Csv(name),
                    a.Year.ToString(),
                    Csv(Models.StandardMedals.DisciplineDisplayName(a.Discipline)),
                    Csv(a.CompetitionName),
                    a.CompetitionDate?.ToString("yyyy-MM-dd") ?? "",
                    Csv(a.Location),
                    Csv(a.ShootingClass),
                    Csv(Models.StandardMedals.MedalDisplayName(a.MedalType)),
                    a.Points.ToString(),
                    Csv(SourceDisplay(a.Source)),
                    Csv(StatusDisplay(a.Status))
                }));
            }

            return CsvFile(sb.ToString(), $"standardmedaljer-{clubId}-{y}.csv");
        }

        /// <summary>
        /// CSV of the award bundle forming a Guldmedalj application (the result lists to attach).
        /// GET /umbraco/surface/StandardMedal/ExportGoldApplication?applicationId=12
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportGoldApplication(int applicationId)
        {
            var app = await _ledger.GetGoldApplicationAsync(applicationId);
            if (app == null)
                return Content("Ansökan hittades inte.");
            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (app.ClubId > 0 && await _authorizationService.IsClubAdminForClub(app.ClubId));
            if (!ok)
                return Content("Åtkomst nekad.");

            var ids = new List<int>();
            if (!string.IsNullOrWhiteSpace(app.AwardIdsJson))
            {
                try { ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(app.AwardIdsJson) ?? new(); }
                catch { ids = new(); }
            }
            var awards = await _ledger.GetAwardsByIdsAsync(ids);
            var member = _memberService.GetById(app.MemberId);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Guldmedaljsansökan #{app.SequenceNumber} – {Csv(member?.Name)}");
            sb.AppendLine($"Status;{Csv(StatusDisplay(app.Status))}");
            sb.AppendLine($"Poäng;{app.PointsConsumed}");
            sb.AppendLine("");
            sb.AppendLine("Gren;Tävling;Datum;Ort;Klass;Medalj;Poäng;Källa");
            foreach (var a in awards)
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    Csv(Models.StandardMedals.DisciplineDisplayName(a.Discipline)),
                    Csv(a.CompetitionName),
                    a.CompetitionDate?.ToString("yyyy-MM-dd") ?? "",
                    Csv(a.Location),
                    Csv(a.ShootingClass),
                    Csv(Models.StandardMedals.MedalDisplayName(a.MedalType)),
                    a.Points.ToString(),
                    Csv(SourceDisplay(a.Source))
                }));
            }

            return CsvFile(sb.ToString(), $"guldmedalj-ansokan-{applicationId}.csv");
        }

        private static string SourceDisplay(string? source) => source switch
        {
            Models.StandardMedals.SourceOnSite => "pistol.nu",
            Models.StandardMedals.SourceSelfReported => "Egenrapporterad",
            Models.StandardMedals.SourceAdminEntered => "Admin",
            _ => source ?? ""
        };

        private static string StatusDisplay(string? status) => status switch
        {
            Models.StandardMedals.StatusReported => "Ej verifierad",
            Models.StandardMedals.StatusVerified => "Verifierad",
            Models.StandardMedals.StatusRejected => "Avvisad",
            Models.StandardMedals.GoldStatusApplied => "Inskickad",
            Models.StandardMedals.GoldStatusApproved => "Godkänd",
            _ => status ?? ""
        };

        private static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            // Quote when the value contains the delimiter, quotes, or newlines.
            if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private FileContentResult CsvFile(string content, string fileName)
        {
            // UTF-8 BOM so Swedish Excel renders åäö correctly.
            var bom = System.Text.Encoding.UTF8.GetPreamble();
            var body = System.Text.Encoding.UTF8.GetBytes(content);
            var bytes = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
            Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
            return File(bytes, "text/csv", fileName);
        }

        // ── Guldmedalj applications ───────────────────────────────────

        /// <summary>A member's Guldmedalj status (verified points, available, reserved) + applications.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberGoldStatus(int memberId)
        {
            var clubId = GetPrimaryClubId(memberId);
            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId));
            if (!ok)
                return Json(new { success = false, message = "Åtkomst nekad." });

            var status = await _ledger.GetGoldStatusAsync(memberId, verifiedOnly: true);
            var apps = await _ledger.GetGoldApplicationsForMemberAsync(memberId);

            return Json(new
            {
                success = true,
                memberId,
                gold = new
                {
                    verifiedPoints = status.LifetimePoints,
                    available = status.AvailablePoints,
                    reserved = status.ConsumedPoints,
                    goldsAwarded = status.GoldsAwarded,
                    canApply = status.CanApplyForGold,
                    toNext = status.PointsToNextGold
                },
                applications = apps.Select(a => new
                {
                    id = a.Id,
                    sequenceNumber = a.SequenceNumber,
                    status = a.Status,
                    points = a.PointsConsumed,
                    appliedAt = a.AppliedAt,
                    approvedAt = a.ApprovedAt
                })
            });
        }

        public class GoldApplicationRequest { public int MemberId { get; set; } }
        public class GoldActionRequest { public int ApplicationId { get; set; } }

        /// <summary>Create a Guldmedalj application (reserves 50 verified points). Club admin / site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateGoldApplication([FromBody] GoldApplicationRequest request)
        {
            var memberId = request?.MemberId ?? 0;
            if (memberId <= 0)
                return Json(new { success = false, message = "Ogiltigt medlems-ID." });

            var clubId = GetPrimaryClubId(memberId);
            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId));
            if (!ok)
                return Json(new { success = false, message = "Åtkomst nekad." });

            var actingId = await GetCurrentMemberIdAsync();
            var (success, message, appId) = await _ledger.CreateGoldApplicationAsync(memberId, clubId, actingId);
            return Json(new { success, message, applicationId = appId });
        }

        /// <summary>Mark a Guldmedalj application as approved (SPSF granted it). Club admin / site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveGoldApplication([FromBody] GoldActionRequest request)
            => await GoldAction(request?.ApplicationId ?? 0, approve: true);

        /// <summary>Reject/cancel a Guldmedalj application, releasing its reserved awards. Club admin / site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectGoldApplication([FromBody] GoldActionRequest request)
            => await GoldAction(request?.ApplicationId ?? 0, approve: false);

        private async Task<IActionResult> GoldAction(int applicationId, bool approve)
        {
            if (applicationId <= 0)
                return Json(new { success = false, message = "Ogiltigt ansöknings-ID." });

            var app = await _ledger.GetGoldApplicationAsync(applicationId);
            if (app == null)
                return Json(new { success = false, message = "Ansökan hittades inte." });

            bool ok = await _authorizationService.IsCurrentUserAdminAsync()
                      || (app.ClubId > 0 && await _authorizationService.IsClubAdminForClub(app.ClubId));
            if (!ok)
                return Json(new { success = false, message = "Åtkomst nekad." });

            var actingId = await GetCurrentMemberIdAsync();
            var (success, message) = approve
                ? await _ledger.ApproveGoldApplicationAsync(applicationId, actingId)
                : await _ledger.RejectGoldApplicationAsync(applicationId);

            // Once a Gold application is approved, its proof has served its purpose (it was bundled
            // into the SPSF application) — delete the stored proof files to limit personal-data retention.
            if (approve && success)
                await CleanupApprovedGoldProofAsync(applicationId);

            return Json(new { success, message });
        }

        private async Task CleanupApprovedGoldProofAsync(int applicationId)
        {
            try
            {
                var app = await _ledger.GetGoldApplicationAsync(applicationId);
                if (app == null || string.IsNullOrWhiteSpace(app.AwardIdsJson)) return;

                List<int> ids;
                try { ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(app.AwardIdsJson) ?? new(); }
                catch { return; }

                var awards = await _ledger.GetAwardsByIdsAsync(ids);
                foreach (var a in awards)
                {
                    var proofRef = a.ProofFileRef;
                    if (string.IsNullOrEmpty(proofRef)) continue;
                    // Clear this award's reference first, then delete the file only if no other
                    // award still references it (the same list may back several medals).
                    await _ledger.ClearAwardProofRefAsync(a.Id);
                    if (await _ledger.CountAwardsUsingProofAsync(proofRef) == 0)
                        _proofStorage.Delete(proofRef);
                }
            }
            catch
            {
                // Best-effort cleanup — never fail the approval over proof deletion.
            }
        }

        /// <summary>
        /// Stream a medal's proof file. Authorized to the owning member, a club admin for that
        /// member's primary club, or a site admin.
        /// GET /umbraco/surface/StandardMedal/GetProof?awardId=123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetProof(int awardId)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Unauthorized();

            var award = await _ledger.GetAwardAsync(awardId);
            if (award == null || string.IsNullOrEmpty(award.ProofFileRef))
                return NotFound();

            // Authorization: owner, club admin for the award member's club, or site admin.
            bool authorized = false;
            var viewer = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (viewer != null && viewer.Id == award.MemberId)
            {
                authorized = true;
            }
            else if (await _authorizationService.IsCurrentUserAdminAsync())
            {
                authorized = true;
            }
            else
            {
                var awardMember = _memberService.GetById(award.MemberId);
                if (awardMember != null
                    && int.TryParse(awardMember.GetValue("primaryClubId")?.ToString(), out var clubId)
                    && clubId > 0
                    && await _authorizationService.IsClubAdminForClub(clubId))
                {
                    authorized = true;
                }
            }

            if (!authorized)
                return Forbid();

            var path = _proofStorage.GetFilePath(award.ProofFileRef);
            if (path == null)
                return NotFound();

            var contentType = StandardMedalProofStorage.ContentTypeFor(award.ProofFileRef);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(stream, contentType);
        }
    }
}
