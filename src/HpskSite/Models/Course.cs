using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A course in the Utbildning system — one per certification path (incl.
    /// Pistolskyttekortet, which is NOT a <see cref="CertificationTypes"/> value;
    /// the DB catalog is what lets it exist as a course). See COURSE_SYSTEM.md.
    /// </summary>
    [TableName("Courses")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class Course
    {
        public int Id { get; set; }

        /// <summary>Unique slug, e.g. "foreningsinstruktor", "pistolskyttekort".</summary>
        public string CourseKey { get; set; } = "";

        public string Title { get; set; } = "";
        public string? Description { get; set; }

        /// <summary>The cert this course leads to — a <see cref="CertificationTypes"/> value,
        /// "Pistolskyttekort", or null. Informational link, not an access gate.</summary>
        public string? TargetCertType { get; set; }

        /// <summary>The cert that DELIVERS this course — the material visibility gate. Holders of
        /// this cert (+ admins) see the material. Null = admins only. E.g. the Föreningsinstruktör
        /// course has EducatorCertType = Kretsinstruktor.</summary>
        public string? EducatorCertType { get; set; }

        /// <summary>How material visibility is decided. Currently always "Educator" (see
        /// <see cref="CourseAccessRules"/>); reserved for future per-course rules.</summary>
        public string AccessRule { get; set; } = CourseAccessRules.Educator;

        public bool IsPublished { get; set; }
        public int SortOrder { get; set; }

        /// <summary>True only for courses with an assessment (today: Pistolskyttekort).</summary>
        public bool HasTest { get; set; }
        public int? TestPassMark { get; set; }
        public int? TestMaxScore { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        [Ignore]
        public string TargetCertDisplay =>
            string.IsNullOrEmpty(TargetCertType) ? "" : CertificationTypes.DisplayName(TargetCertType);

        [Ignore]
        public string EducatorCertDisplay =>
            string.IsNullOrEmpty(EducatorCertType) ? "" : CertificationTypes.DisplayName(EducatorCertType);
    }

    /// <summary>An ordered lesson within a <see cref="Course"/>. The lesson artifact is a
    /// self-contained HTML file on disk (web lesson + slide deck + video film source).</summary>
    [TableName("CourseModules")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CourseModule
    {
        public int Id { get; set; }
        public int CourseId { get; set; }
        public string Slug { get; set; } = "";
        public string Title { get; set; } = "";

        /// <summary>Path under wwwroot, e.g. "utbildning/foreningsinstruktor/01-mota-nya-medlemmar/lektion.html".</summary>
        public string? LessonPath { get; set; }
        public string? VideoUrl { get; set; }

        public int SortOrder { get; set; }

        /// <summary>The sakgranskning gate — an unsigned-off module is preview-only to admins.</summary>
        public bool IsPublished { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    /// <summary>An eligibility edge a participant must satisfy before the trainer can advance
    /// them in this course (read-only consume of existing records). E.g. Pistolskyttekort →
    /// Badge:"Pistolskytte:Brons". Not a material gate (material is educator-only).</summary>
    [TableName("CoursePrerequisites")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CoursePrerequisite
    {
        public int Id { get; set; }
        public int CourseId { get; set; }

        /// <summary>One of <see cref="CoursePrereqTypes"/>.</summary>
        public string PrereqType { get; set; } = "";

        /// <summary>Type-specific key. Badge → "Family:Level" (e.g. "Pistolskytte:Brons");
        /// Certification → a CertificationTypes value or "Pistolskyttekort"; Course → a CourseKey;
        /// SkyttetrappanLevel → a level identifier.</summary>
        public string PrereqKey { get; set; } = "";
    }

    /// <summary>A member the site admin has granted FULL course-material access to (all
    /// courses, all modules incl. unpublished) — for proofreaders/verifiers, independent of
    /// certifications. Checked in UtbildningController.CanAccessCourseAsync.</summary>
    [TableName("CourseReviewers")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CourseReviewer
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public string? MemberName { get; set; }
        public int? GrantedByMemberId { get; set; }
        public string? GrantedByName { get; set; }
        public DateTime GrantedAt { get; set; }
        public string? Note { get; set; }
    }

    public static class CourseAccessRules
    {
        /// <summary>Material visible to holders of the course's EducatorCertType (or higher
        /// in the instructor ladder) + reviewers + admins.</summary>
        public const string Educator = "Educator";
    }

    public static class CoursePrereqTypes
    {
        public const string Badge = "Badge";                       // Märken: PrereqKey = "Family:Level"
        public const string Certification = "Certification";       // CertificationTypes value or "Pistolskyttekort"
        public const string Course = "Course";                     // another CourseKey
        public const string SkyttetrappanLevel = "SkyttetrappanLevel";
    }
}
