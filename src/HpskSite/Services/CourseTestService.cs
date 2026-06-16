using System.Text.Json;
using HpskSite.Models;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// The course test engine (Phase 2): test versions, prerequisite-gated access grants, and
    /// results (online auto-scored + instructor-recorded paper). Today only the Pistolskyttekort
    /// course has a test. See COURSE_SYSTEM.md §7.
    /// </summary>
    public class CourseTestService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly CourseService _courseService;
        private readonly MarkenLedgerService _markenLedger;
        private readonly CertificationService _certificationService;
        private readonly ILogger<CourseTestService> _logger;

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        public CourseTestService(
            IUmbracoDatabaseFactory databaseFactory,
            CourseService courseService,
            MarkenLedgerService markenLedger,
            CertificationService certificationService,
            ILogger<CourseTestService> logger)
        {
            _databaseFactory = databaseFactory;
            _courseService = courseService;
            _markenLedger = markenLedger;
            _certificationService = certificationService;
            _logger = logger;
        }

        // ── Versions ───────────────────────────────────────────────────────────

        public async Task<List<CourseTestVersion>> GetVersionsAsync(int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CourseTestVersion>("WHERE CourseId = @0 ORDER BY Id", courseId);
        }

        public async Task<List<CourseTestVersion>> GetActiveVersionsAsync(int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CourseTestVersion>("WHERE CourseId = @0 AND IsActive = 1 ORDER BY Id", courseId);
        }

        public async Task<CourseTestVersion?> GetVersionAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<CourseTestVersion>("WHERE Id = @0", id);
        }

        public async Task<int> CreateVersionAsync(CourseTestVersion v)
        {
            using var db = _databaseFactory.CreateDatabase();
            v.CreatedAt = DateTime.Now;
            return Convert.ToInt32(await db.InsertAsync(v));
        }

        public async Task<bool> UpdateVersionAsync(CourseTestVersion v)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<CourseTestVersion>("WHERE Id = @0", v.Id);
            if (existing == null) return false;
            v.CourseId = existing.CourseId;
            v.CreatedAt = existing.CreatedAt;
            await db.UpdateAsync(v);
            return true;
        }

        public async Task<bool> DeleteVersionAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteAsync("DELETE FROM CourseTestVersions WHERE Id = @0", id) > 0;
        }

        // ── Eligibility (prerequisites) ─────────────────────────────────────────

        /// <summary>Does the member satisfy all of the course's prerequisites?</summary>
        public async Task<(bool Eligible, List<string> Missing)> CheckEligibilityAsync(int memberId, int courseId)
        {
            var missing = new List<string>();
            foreach (var p in await _courseService.GetPrerequisitesAsync(courseId))
            {
                if (!await PrereqMetAsync(memberId, p))
                    missing.Add(DescribePrereq(p));
            }
            return (missing.Count == 0, missing);
        }

        private async Task<bool> PrereqMetAsync(int memberId, CoursePrerequisite p)
        {
            switch (p.PrereqType)
            {
                case CoursePrereqTypes.Badge:
                {
                    var parts = p.PrereqKey.Split(':', 2);
                    var family = parts[0];
                    var reqOrdinal = parts.Length > 1 ? LevelOrdinal(parts[1]) : 1;
                    var badges = await _markenLedger.GetBadgesForMemberAsync(memberId, family, includeRejected: false);
                    return badges.Any(b => b.Status == "Verified" && b.LevelOrdinal >= reqOrdinal);
                }
                case CoursePrereqTypes.Certification:
                    // Pistolskyttekortet isn't a MemberCertification — it's evidenced by a passed course test.
                    if (string.Equals(p.PrereqKey, "Pistolskyttekort", StringComparison.OrdinalIgnoreCase))
                        return await HasPassedCourseKeyAsync(memberId, "pistolskyttekort");
                    return await _certificationService.HasActiveCertAsync(memberId, p.PrereqKey);
                case CoursePrereqTypes.Course:
                    return await HasPassedCourseKeyAsync(memberId, p.PrereqKey);
                case CoursePrereqTypes.SkyttetrappanLevel:
                    // Brons etc. is materialised as a Märken badge — model it as a Badge prereq instead.
                    // Not separately evaluated here; treat as met to avoid false negatives.
                    return true;
                default:
                    return true;
            }
        }

        private static int LevelOrdinal(string level) => level.Trim().ToLowerInvariant() switch
        {
            "brons" => 1, "silver" => 2, "guld" => 3, _ => 1
        };

        private static string DescribePrereq(CoursePrerequisite p) => p.PrereqType switch
        {
            CoursePrereqTypes.Badge => $"Märke: {p.PrereqKey.Replace(":", " ")}",
            CoursePrereqTypes.Certification => $"Certifiering: {p.PrereqKey}",
            CoursePrereqTypes.Course => $"Genomförd kurs: {p.PrereqKey}",
            _ => p.PrereqKey
        };

        // ── Access grants ───────────────────────────────────────────────────────

        public async Task<CourseTestAccess?> GetActiveAccessAsync(int memberId, int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<CourseTestAccess>(
                "WHERE MemberId = @0 AND CourseId = @1 AND Status = @2", memberId, courseId, CourseTestAccessStatus.Enabled);
        }

        public async Task<List<CourseTestAccess>> GetAccessForCourseAsync(int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CourseTestAccess>("WHERE CourseId = @0 AND Status = @1", courseId, CourseTestAccessStatus.Enabled);
        }

        /// <summary>Enable a member to take a course's test — only if they satisfy the prerequisites.</summary>
        public async Task<(bool Ok, string? Message)> EnableAccessAsync(int memberId, int courseId, int byMemberId)
        {
            var course = await _courseService.GetCourseAsync(courseId);
            if (course == null) return (false, "Kursen hittades inte.");
            if (!course.HasTest) return (false, "Kursen har inget prov.");

            var (eligible, missing) = await CheckEligibilityAsync(memberId, courseId);
            if (!eligible) return (false, "Deltagaren saknar förkunskapskrav: " + string.Join(", ", missing));

            if (await GetActiveAccessAsync(memberId, courseId) != null) return (true, "Åtkomst fanns redan.");

            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(new CourseTestAccess
            {
                MemberId = memberId,
                CourseId = courseId,
                EnabledByMemberId = byMemberId,
                EnabledAt = DateTime.Now,
                Status = CourseTestAccessStatus.Enabled
            });
            return (true, null);
        }

        public async Task<bool> RevokeAccessAsync(int memberId, int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteAsync(
                "UPDATE CourseTestAccess SET Status = @0 WHERE MemberId = @1 AND CourseId = @2 AND Status = @3",
                CourseTestAccessStatus.Revoked, memberId, courseId, CourseTestAccessStatus.Enabled) > 0;
        }

        // ── Results ─────────────────────────────────────────────────────────────

        public async Task<List<CourseTestResult>> GetResultsForMemberAsync(int memberId, int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CourseTestResult>("WHERE MemberId = @0 AND CourseId = @1 ORDER BY TakenAt DESC", memberId, courseId);
        }

        public async Task<List<CourseTestResult>> GetResultsForCourseAsync(int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CourseTestResult>("WHERE CourseId = @0 ORDER BY TakenAt DESC", courseId);
        }

        public async Task<bool> HasPassedAsync(int memberId, int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM CourseTestResults WHERE MemberId = @0 AND CourseId = @1 AND Passed = 1", memberId, courseId) > 0;
        }

        private async Task<bool> HasPassedCourseKeyAsync(int memberId, string courseKey)
        {
            var course = await _courseService.GetCourseByKeyAsync(courseKey);
            return course != null && await HasPassedAsync(memberId, course.Id);
        }

        private async Task<int> NextAttemptAsync(int memberId, int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return (await db.ExecuteScalarAsync<int?>(
                "SELECT MAX(AttemptNumber) FROM CourseTestResults WHERE MemberId = @0 AND CourseId = @1", memberId, courseId) ?? 0) + 1;
        }

        /// <summary>Record an instructor-administered (paper/oral) result.</summary>
        public async Task<(bool Ok, string? Message)> RecordPaperResultAsync(
            int memberId, int courseId, int score, int maxScore, bool passed, int administeredByMemberId, string? notes)
        {
            var course = await _courseService.GetCourseAsync(courseId);
            if (course == null) return (false, "Kursen hittades inte.");

            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(new CourseTestResult
            {
                MemberId = memberId,
                CourseId = courseId,
                TestVersionId = null,
                Mode = CourseTestModes.Paper,
                Score = score,
                MaxScore = maxScore,
                Passed = passed,
                AttemptNumber = await NextAttemptAsync(memberId, courseId),
                TakenAt = DateTime.Now,
                AdministeredByMemberId = administeredByMemberId,
                Notes = notes
            });
            await MarkAccessUsedIfPassed(memberId, courseId, passed);
            return (true, null);
        }

        /// <summary>Pick a stable test version for a member (varies across members; same each time).</summary>
        public async Task<CourseTestVersion?> PickVersionForMemberAsync(int memberId, int courseId)
        {
            var versions = await GetActiveVersionsAsync(courseId);
            if (versions.Count == 0) return null;
            return versions[Math.Abs(memberId) % versions.Count];
        }

        /// <summary>Score an online submission server-side and record the result.</summary>
        public async Task<(bool Ok, CourseTestResult? Result, string? Message)> SubmitOnlineAsync(
            int memberId, int courseId, int versionId, IList<int> answers)
        {
            var course = await _courseService.GetCourseAsync(courseId);
            if (course == null) return (false, null, "Kursen hittades inte.");
            if (await GetActiveAccessAsync(memberId, courseId) == null)
                return (false, null, "Du har inte fått åtkomst till provet.");

            var version = await GetVersionAsync(versionId);
            if (version == null || version.CourseId != courseId) return (false, null, "Provversionen hittades inte.");

            var content = ParseContent(version.ContentRef);
            if (content.Questions.Count == 0) return (false, null, "Provet saknar frågor.");

            var score = 0;
            for (var i = 0; i < content.Questions.Count; i++)
                if (i < answers.Count && answers[i] == content.Questions[i].Correct) score++;

            var max = content.Questions.Count;
            var passMark = course.TestPassMark ?? max; // no mark set → require all correct
            var passed = score >= passMark;

            var result = new CourseTestResult
            {
                MemberId = memberId,
                CourseId = courseId,
                TestVersionId = versionId,
                Mode = CourseTestModes.Online,
                Score = score,
                MaxScore = max,
                Passed = passed,
                AttemptNumber = await NextAttemptAsync(memberId, courseId),
                TakenAt = DateTime.Now,
                AdministeredByMemberId = null,
                Notes = null
            };

            using (var db = _databaseFactory.CreateDatabase())
                result.Id = Convert.ToInt32(await db.InsertAsync(result));

            await MarkAccessUsedIfPassed(memberId, courseId, passed);
            return (true, result, null);
        }

        private async Task MarkAccessUsedIfPassed(int memberId, int courseId, bool passed)
        {
            if (!passed) return;
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync(
                "UPDATE CourseTestAccess SET Status = @0 WHERE MemberId = @1 AND CourseId = @2 AND Status = @3",
                CourseTestAccessStatus.Used, memberId, courseId, CourseTestAccessStatus.Enabled);
        }

        public static CourseTestContent ParseContent(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new CourseTestContent();
            try { return JsonSerializer.Deserialize<CourseTestContent>(json, JsonOpts) ?? new CourseTestContent(); }
            catch { return new CourseTestContent(); }
        }
    }
}
