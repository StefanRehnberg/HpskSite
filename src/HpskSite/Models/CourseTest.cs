using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One of the 2-3 variants of a course's test. Phase 2. See COURSE_SYSTEM.md §7.
    /// </summary>
    [TableName("CourseTestVersions")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CourseTestVersion
    {
        public int Id { get; set; }
        public int CourseId { get; set; }
        public string VersionLabel { get; set; } = "";
        public bool IsActive { get; set; } = true;

        /// <summary>Questions JSON (online auto-scored) or a file/printable reference.</summary>
        public string? ContentRef { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// A trainer/admin grant enabling an eligible (prerequisite-holding) participant to take a
    /// course's test. The ONLY participant-facing course capability. Phase 2.
    /// </summary>
    [TableName("CourseTestAccess")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CourseTestAccess
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int CourseId { get; set; }
        public int EnabledByMemberId { get; set; }
        public DateTime EnabledAt { get; set; }

        /// <summary>Enabled | Revoked | Used.</summary>
        public string Status { get; set; } = "Enabled";
    }

    /// <summary>
    /// A recorded test result — the system's ONLY per-participant tracking. Phase 2.
    /// Online results are auto-scored; Paper results are recorded by an instructor.
    /// </summary>
    [TableName("CourseTestResults")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CourseTestResult
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int CourseId { get; set; }
        public int? TestVersionId { get; set; }

        /// <summary>Online | Paper.</summary>
        public string Mode { get; set; } = "Online";

        public int? Score { get; set; }
        public int? MaxScore { get; set; }
        public bool Passed { get; set; }
        public int AttemptNumber { get; set; } = 1;
        public DateTime TakenAt { get; set; }

        /// <summary>Who recorded a Paper result (the instructor). Null for self-taken Online tests.</summary>
        public int? AdministeredByMemberId { get; set; }
        public string? Notes { get; set; }
    }

    // ── Online test content (stored as JSON in CourseTestVersion.ContentRef) ──
    public class CourseTestContent
    {
        public List<CourseTestQuestion> Questions { get; set; } = new();
    }

    public class CourseTestQuestion
    {
        public string Q { get; set; } = "";
        public List<string> Options { get; set; } = new();
        /// <summary>Index into Options of the correct answer (server-side only; never sent to the taker).</summary>
        public int Correct { get; set; }
    }

    public static class CourseTestModes
    {
        public const string Online = "Online";
        public const string Paper = "Paper";
    }

    public static class CourseTestAccessStatus
    {
        public const string Enabled = "Enabled";
        public const string Revoked = "Revoked";
        public const string Used = "Used";
    }
}
