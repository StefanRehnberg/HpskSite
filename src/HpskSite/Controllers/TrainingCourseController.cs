using HpskSite.Models.Training;
using HpskSite.Services;
using HpskSite.Services.Training;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fas D: kursen (träningsgruppen med kopplade träningar). Behörigheten är gruppens befintliga
    /// <see cref="TrainingGroupService.CanManageTrainingGroup"/> — sajtadmin, klubbadmin, skjutledare
    /// och gruppens tränare. Föreningsinstruktören får ingen egen rätt (beslut 2026-10-02): klubben gör
    /// utbildaren till tränare i gruppen.
    /// </summary>
    public class TrainingCourseController : SurfaceController
    {
        private readonly TrainingCourseService _courses;
        private readonly TrainingGroupService _groups;
        private readonly TrainingCourseSeriesService _series;
        private readonly ClubTrainingService _trainings;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;

        public TrainingCourseController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            TrainingCourseService courses,
            TrainingGroupService groups,
            AdminAuthorizationService auth,
            IMemberManager memberManager,
            IMemberService memberService,
            TrainingCourseSeriesService series,
            ClubTrainingService trainings)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _courses = courses;
            _groups = groups;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
            _series = series;
            _trainings = trainings;
        }

        private const string Denied = "Du har inte behörighet till den här kursen.";

        [HttpGet]
        public async Task<IActionResult> MyCourses()
        {
            var me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Logga in för att se dina kurser." });
            var clubs = (await _auth.GetManagedClubIds()).Concat(await _auth.GetSkjutledareClubIds());
            return Json(new { success = true, courses = _courses.CoursesFor(me, clubs) });
        }

        [HttpGet]
        public async Task<IActionResult> Get(int groupId)
        {
            if (!await _groups.CanManageTrainingGroup(groupId)) return Json(new { success = false, message = Denied });
            var c = _courses.Get(groupId, DateTime.Today);
            if (c == null) return Json(new { success = false, message = "Kursen finns inte." });
            return Json(new { success = true, course = c, notes = _courses.Notes(groupId) });
        }

        [HttpGet]
        public async Task<IActionResult> Candidates(int groupId)
        {
            if (!await _groups.CanManageTrainingGroup(groupId)) return Json(new { success = false, message = Denied });
            return Json(new
            {
                success = true,
                trainings = _courses.Candidates(groupId, DateTime.Today).Select(x => new
                {
                    id = x.Training.Id,
                    date = x.Training.Date.ToString("yyyy-MM-dd"),
                    startTime = x.Training.StartTime,
                    name = x.Training.Name,
                    isCancelled = x.Training.IsCancelled,
                    linked = x.Linked
                })
            });
        }

        public class LinkRequest
        {
            public int GroupId { get; set; }
            public List<int>? TrainingIds { get; set; }
            public int TrainingId { get; set; }
            public string? Attendance { get; set; }
            public string? Registration { get; set; }
            public string? Note { get; set; }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Link([FromBody] LinkRequest req)
        {
            if (req == null || !await _groups.CanManageTrainingGroup(req.GroupId)) return Json(new { success = false, message = Denied });
            var n = _courses.Link(req.GroupId, req.TrainingIds ?? new(), await CurrentMemberIdAsync());
            return Json(new { success = true, linked = n, message = n == 1 ? "1 träning kopplad till kursen." : $"{n} träningar kopplade till kursen." });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Unlink([FromBody] LinkRequest req)
        {
            if (req == null || !await _groups.CanManageTrainingGroup(req.GroupId)) return Json(new { success = false, message = Denied });
            var ok = _courses.Unlink(req.GroupId, req.TrainingId);
            return Json(new { success = ok, message = ok ? "Träningen är inte längre kopplad till kursen. Den finns kvar som klubbträning." : "Träningen var inte kopplad." });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetLink([FromBody] LinkRequest req)
        {
            if (req == null || !await _groups.CanManageTrainingGroup(req.GroupId)) return Json(new { success = false, message = Denied });
            var err = _courses.SetLinkRules(req.GroupId, req.TrainingId, req.Attendance, req.Registration, req.Note);
            return Json(new { success = err == null, message = err ?? "Sparat." });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SetDefaults([FromBody] LinkRequest req)
        {
            if (req == null || !await _groups.CanManageTrainingGroup(req.GroupId)) return Json(new { success = false, message = Denied });
            _courses.SetDefaults(req.GroupId, req.Attendance, req.Registration);
            return Json(new { success = true, message = "Kursens standard är sparad." });
        }

        public class NoteRequest
        {
            public int GroupId { get; set; }
            public int TrainingId { get; set; }
            public int MemberId { get; set; }
            public string? Note { get; set; }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveNote([FromBody] NoteRequest req)
        {
            if (req == null || !await _groups.CanManageTrainingGroup(req.GroupId)) return Json(new { success = false, message = Denied });
            // Anteckningen hör till en DELTAGARE på ett KOPPLAT tillfälle — annars kan den hänga på vad som helst.
            if (!_courses.IsLinked(req.GroupId, req.TrainingId)) return Json(new { success = false, message = "Träningen är inte kopplad till kursen." });
            if (!_courses.IsParticipant(req.GroupId, req.MemberId)) return Json(new { success = false, message = "Personen är inte med i kursen." });
            _courses.SaveNote(req.GroupId, req.TrainingId, req.MemberId, req.Note, await CurrentMemberIdAsync());
            return Json(new { success = true, message = string.IsNullOrWhiteSpace(req.Note) ? "Anteckningen är borttagen." : "Anteckningen är sparad." });
        }

        // ── Serier ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Får den inloggade registrera serier i kursens klubb? Klubbadmin eller skjutledare (sajtadmin
        /// alltid) — samma folk som får signera märken. En tränare utan den rätten ser serierna men får
        /// en förklaring i stället för knappen (beslut 2026-10-02: klubben gör utbildaren till skjutledare).
        /// </summary>
        private async Task<bool> CanRecordSeriesAsync(int clubId)
            => await _auth.IsCurrentUserAdminAsync()
               || await _auth.IsClubAdminForClub(clubId)
               || await _auth.IsSkjutledareForClub(clubId);

        [HttpGet]
        public async Task<IActionResult> Series(int groupId, int trainingId)
        {
            if (!await _groups.CanManageTrainingGroup(groupId)) return Json(new { success = false, message = Denied });
            var clubId = _groups.GetTrainingGroupClubId(groupId);
            var year = _trainings.Get(trainingId)?.Date.Year ?? DateTime.Today.Year;
            var course = _courses.Get(groupId, DateTime.Today);
            return Json(new
            {
                success = true,
                canRecord = await CanRecordSeriesAsync(clubId),
                cannotRecordReason = "Serier registreras av klubbens skjutledare eller klubbadmin — be klubben göra dig till skjutledare.",
                series = _series.ForOccasion(groupId, trainingId),
                thresholds = (course?.Participants ?? new()).ToDictionary(p => p.MemberId, p =>
                {
                    var t = _series.Thresholds(p.MemberId, year);
                    return new { brons = t.Brons, silver = t.Silver, guld = t.Guld };
                })
            });
        }

        public class SeriesRequest
        {
            public int GroupId { get; set; }
            public int TrainingId { get; set; }
            public int MemberId { get; set; }
            public List<string>? Shots { get; set; }
            public int? Total { get; set; }
            public int SeriesId { get; set; }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RecordSeries([FromBody] SeriesRequest req)
        {
            if (req == null) return Json(new { success = false, message = Denied });
            var clubId = _groups.GetTrainingGroupClubId(req.GroupId);
            if (clubId <= 0 || !await CanRecordSeriesAsync(clubId))
                return Json(new { success = false, message = "Serier registreras av klubbens skjutledare eller klubbadmin." });
            if (!_courses.IsLinked(req.GroupId, req.TrainingId)) return Json(new { success = false, message = "Träningen är inte kopplad till kursen." });
            if (!_courses.IsParticipant(req.GroupId, req.MemberId)) return Json(new { success = false, message = "Personen är inte med i kursen." });
            var training = _trainings.Get(req.TrainingId);
            if (training == null) return Json(new { success = false, message = "Träningen finns inte." });

            var (series, error) = await _series.RecordAsync(req.GroupId, clubId, training, req.MemberId, req.Shots, req.Total, await CurrentMemberIdAsync());
            if (error != null) return Json(new { success = false, message = error });
            var msg = series!.Valor != null
                ? $"Serie {series.SeriesNumber}: {series.Total} p — {series.Valor.ToLowerInvariant()}. Sparad i träningsloggen och som märkesserie."
                : $"Serie {series.SeriesNumber}: {series.Total} p — under brons. Sparad i träningsloggen.";
            return Json(new { success = true, message = msg, series });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSeries([FromBody] SeriesRequest req)
        {
            if (req == null) return Json(new { success = false, message = Denied });
            var clubId = _groups.GetTrainingGroupClubId(req.GroupId);
            if (clubId <= 0 || !await CanRecordSeriesAsync(clubId))
                return Json(new { success = false, message = "Serier tas bort av klubbens skjutledare eller klubbadmin." });
            var err = await _series.DeleteAsync(req.GroupId, req.SeriesId);
            return Json(new { success = err == null, message = err ?? "Serien är borttagen — ur träningsloggen och, om den nådde brons, ur märkesserierna." });
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }
    }
}
