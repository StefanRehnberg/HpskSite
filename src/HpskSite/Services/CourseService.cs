using HpskSite.Models;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Data layer for the Utbildning course catalog (Courses + CourseModules +
    /// CoursePrerequisites). Pure CRUD over the NPoco tables — access/eligibility
    /// decisions live in the controllers (they hold CertificationService /
    /// AdminAuthorizationService / MarkenLedgerService). See COURSE_SYSTEM.md.
    /// </summary>
    public class CourseService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<CourseService> _logger;

        public CourseService(IUmbracoDatabaseFactory databaseFactory, ILogger<CourseService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Courses ──────────────────────────────────────────────────────────

        public async Task<List<Course>> GetAllCoursesAsync()
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<Course>("ORDER BY SortOrder, Title");
        }

        public async Task<Course?> GetCourseAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<Course>("WHERE Id = @0", id);
        }

        public async Task<Course?> GetCourseByKeyAsync(string courseKey)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<Course>("WHERE CourseKey = @0", courseKey);
        }

        public async Task<(bool Success, int CourseId, string? Message)> CreateCourseAsync(Course course)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Courses WHERE CourseKey = @0", course.CourseKey) > 0)
                return (false, 0, "En kurs med samma nyckel finns redan.");

            course.CreatedAt = DateTime.Now;
            course.UpdatedAt = null;
            var newId = Convert.ToInt32(await db.InsertAsync(course));
            return (true, newId, null);
        }

        public async Task<(bool Success, string? Message)> UpdateCourseAsync(Course course)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<Course>("WHERE Id = @0", course.Id);
            if (existing == null) return (false, "Kursen hittades inte.");

            // Guard the unique key against collisions with a different course.
            if (!string.Equals(existing.CourseKey, course.CourseKey, StringComparison.OrdinalIgnoreCase) &&
                await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Courses WHERE CourseKey = @0 AND Id <> @1", course.CourseKey, course.Id) > 0)
                return (false, "En annan kurs använder redan den nyckeln.");

            course.CreatedAt = existing.CreatedAt; // preserve
            course.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(course);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteCourseAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            // Modules + prerequisites + test versions cascade via FK ON DELETE CASCADE.
            var rows = await db.ExecuteAsync("DELETE FROM Courses WHERE Id = @0", id);
            return rows > 0 ? (true, null) : (false, "Kursen hittades inte.");
        }

        // ── Modules ──────────────────────────────────────────────────────────

        public async Task<List<CourseModule>> GetModulesAsync(int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CourseModule>("WHERE CourseId = @0 ORDER BY SortOrder, Id", courseId);
        }

        public async Task<CourseModule?> GetModuleAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<CourseModule>("WHERE Id = @0", id);
        }

        public async Task<(bool Success, int ModuleId, string? Message)> CreateModuleAsync(CourseModule module)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Courses WHERE Id = @0", module.CourseId) == 0)
                return (false, 0, "Kursen hittades inte.");

            if (module.SortOrder == 0)
                module.SortOrder = (await db.ExecuteScalarAsync<int?>("SELECT MAX(SortOrder) FROM CourseModules WHERE CourseId = @0", module.CourseId) ?? 0) + 1;

            module.CreatedAt = DateTime.Now;
            module.UpdatedAt = null;
            var newId = Convert.ToInt32(await db.InsertAsync(module));
            return (true, newId, null);
        }

        public async Task<(bool Success, string? Message)> UpdateModuleAsync(CourseModule module)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<CourseModule>("WHERE Id = @0", module.Id);
            if (existing == null) return (false, "Modulen hittades inte.");

            module.CourseId = existing.CourseId; // immutable
            module.CreatedAt = existing.CreatedAt;
            module.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(module);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteModuleAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            var rows = await db.ExecuteAsync("DELETE FROM CourseModules WHERE Id = @0", id);
            return rows > 0 ? (true, null) : (false, "Modulen hittades inte.");
        }

        /// <summary>Persist a new module order (array index → SortOrder).</summary>
        public async Task ReorderModulesAsync(int courseId, IList<int> orderedModuleIds)
        {
            using var db = _databaseFactory.CreateDatabase();
            for (var i = 0; i < orderedModuleIds.Count; i++)
                await db.ExecuteAsync("UPDATE CourseModules SET SortOrder = @0 WHERE Id = @1 AND CourseId = @2",
                    i + 1, orderedModuleIds[i], courseId);
        }

        // ── Prerequisites ──────────────────────────────────────────────────────

        public async Task<List<CoursePrerequisite>> GetPrerequisitesAsync(int courseId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CoursePrerequisite>("WHERE CourseId = @0 ORDER BY Id", courseId);
        }

        public async Task<int> AddPrerequisiteAsync(CoursePrerequisite prereq)
        {
            using var db = _databaseFactory.CreateDatabase();
            return Convert.ToInt32(await db.InsertAsync(prereq));
        }

        public async Task<bool> DeletePrerequisiteAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteAsync("DELETE FROM CoursePrerequisites WHERE Id = @0", id) > 0;
        }
    }
}
