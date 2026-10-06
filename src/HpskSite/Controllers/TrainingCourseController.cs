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
        private readonly HpskSite.Services.Firearms.FirearmBookingService _bookings;
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
            ClubTrainingService trainings,
            HpskSite.Services.Firearms.FirearmBookingService bookings)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _courses = courses;
            _groups = groups;
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
            _series = series;
            _trainings = trainings;
            _bookings = bookings;
        }

        private const string Denied = "Du har inte behörighet till den här kursen.";

        [HttpGet]
        public async Task<IActionResult> MyCourses()
        {
            var me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Logga in för att se dina kurser." });
            var clubs = (await _auth.GetManagedClubIds()).Concat(await _auth.GetSkjutledareClubIds());
            // role: "trainer" (leder kursen), "manager" (klubbens kurs, via klubbadmin/skjutledare)
            // eller "participant" (går kursen — får deltagarens läsvy, UX-omgången 2026-10-06).
            var list = _courses.CoursesFor(me, clubs)
                .Select(c => new { c.GroupId, c.Name, c.ClubId, c.IsTrainer, role = c.IsTrainer ? "trainer" : "manager" })
                .ToList();
            foreach (var p in _courses.CoursesOfMember(me).Where(p => p.Role == TrainingCourseService.RoleParticipant))
            {
                // En klubbadmin som själv går en kurs i sin klubb ser den redan som "manager" —
                // deltagarens vy är då inte vad hen letar efter i första hand, men den ska finnas.
                if (list.Any(x => x.GroupId == p.GroupId && x.role != "manager")) continue;
                list.RemoveAll(x => x.GroupId == p.GroupId);
                list.Add(new { p.GroupId, p.Name, p.ClubId, IsTrainer = false, role = "participant" });
            }
            return Json(new { success = true, courses = list });
        }

        /// <summary>
        /// Deltagarens egen vy av kursen. Bara för den som går kursen; visar bara hens egna rader.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Mine(int groupId)
        {
            var me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Logga in för att se din kurs." });
            if (!_courses.IsParticipant(groupId, me)) return Json(new { success = false, message = "Du går inte den här kursen." });
            var v = _courses.GetForParticipant(groupId, me, DateTime.Today);
            if (v == null) return Json(new { success = false, message = "Kursen finns inte." });
            return Json(new { success = true, course = v });
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
                    linked = x.Linked,
                    // Bulkvalet i dialogen: "hela schemat" grupperar på det här.
                    scheduleId = x.Training.ScheduleId ?? 0
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

        // ── Lånevapen till kursens tillfälle (D7) ─────────────────────────────────────────────────

        /// <summary>
        /// Prövar att träningen hör till kursen och till samma klubb. Utan klubbkontrollen kunde en
        /// tränare boka en annan klubbs vapen genom att koppla en främmande träning.
        /// </summary>
        private async Task<(ClubTraining? Training, int ClubId, string? Error)> LoanContextAsync(int groupId, int trainingId)
        {
            if (!await _groups.CanManageTrainingGroup(groupId)) return (null, 0, Denied);
            if (!_courses.IsLinked(groupId, trainingId)) return (null, 0, "Träningen är inte kopplad till kursen.");
            var t = _trainings.Get(trainingId);
            var clubId = _groups.GetTrainingGroupClubId(groupId);
            if (t == null || clubId <= 0 || t.ClubId != clubId) return (null, 0, "Träningen hör inte till kursens klubb.");
            return (t, clubId, null);
        }

        /// <summary>Vilka deltagare har ett lånevapen bokat till tillfället? Läser bara.</summary>
        [HttpGet]
        public async Task<IActionResult> LoanWeapons(int groupId, int trainingId)
        {
            var (t, clubId, error) = await LoanContextAsync(groupId, trainingId);
            if (error != null) return Json(new { success = false, message = error });
            var booked = _bookings.GetForOccasion(clubId, HpskSite.Services.Firearms.FirearmOccasionKind.Training, trainingId)
                .Where(b => b.IsActive).Select(b => b.MemberId).ToHashSet();
            var participants = _groups.GetGroupMemberIds(groupId).Select(id =>
            {
                var m = _memberService.GetById(id);
                var name = m == null ? $"Medlem {id}" : $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                return new { memberId = id, name, booked = booked.Contains(id) };
            }).OrderBy(p => p.name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true)).ToList();
            return Json(new { success = true, cancelled = t!.IsCancelled, participants });
        }

        public class LoanRequest
        {
            public int GroupId { get; set; }
            public int TrainingId { get; set; }
            public List<int>? MemberIds { get; set; }
        }

        /// <summary>
        /// Bokar en lånevapenplats per vald deltagare. Samma form som klubbens kurstilldelning:
        /// <c>Source = Tilldelad</c> (går förbi klubbens horisont — en kurs planeras i förväg) och
        /// platsbokningar utan nummer, eftersom valvet avgör vilket vapen var och en får.
        ///
        /// <para>⚠️ Bara kursens DELTAGARE kan bokas; ett id utanför kursen vägras per rad. Och svaret
        /// säger per person vad som hände — tränaren måste veta vem som blev utan vapen.</para>
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignLoanWeapons([FromBody] LoanRequest req)
        {
            var (t, clubId, error) = await LoanContextAsync(req.GroupId, req.TrainingId);
            if (error != null) return Json(new { success = false, message = error });
            if (t!.IsCancelled) return Json(new { success = false, message = "Träningen är inställd." });

            var participants = _groups.GetGroupMemberIds(req.GroupId).ToHashSet();
            var wanted = (req.MemberIds ?? new()).Distinct().ToList();
            if (wanted.Count == 0) return Json(new { success = false, message = "Välj minst en deltagare." });

            var day = t.Date.Date;
            var results = new List<object>();
            int created = 0;
            foreach (var id in wanted)
            {
                var m = _memberService.GetById(id);
                var name = m == null ? $"Medlem {id}" : $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                if (!participants.Contains(id))
                {
                    results.Add(new { memberId = id, name, ok = false, message = "Inte deltagare i kursen." });
                    continue;
                }
                var (bookingId, err) = _bookings.Create(new HpskSite.Services.Firearms.FirearmBookingRequest
                {
                    MemberId = id,
                    ClubId = clubId,
                    FirearmId = null,
                    OccasionKind = HpskSite.Services.Firearms.FirearmOccasionKind.Training,
                    OccasionId = t.Id,
                    OccasionLabel = t.Name,
                    From = day,
                    To = day.AddDays(1).AddSeconds(-1),
                    Source = HpskSite.Services.Firearms.FirearmBookingSource.Tilldelad,
                });
                if (err == null) created++;
                results.Add(new { memberId = id, name, ok = err == null, bookingId, message = err });
            }
            var failed = wanted.Count - created;
            return Json(new
            {
                success = created > 0,
                created,
                failed,
                message = created == 0 ? "Ingen kunde få ett lånevapen."
                        : failed == 0 ? (created == 1 ? "Ett lånevapen är bokat åt en deltagare." : $"Lånevapen är bokat åt {created} deltagare.")
                        : $"Lånevapen är bokat åt {created} deltagare, {failed} kunde inte bokas.",
                results,
            });
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
        /// Får den inloggade registrera serier för kursens deltagare? Samma svar som att få hantera
        /// gruppen: sajtadmin, klubbadmin, klubbens skjutledare — och **kursens tränare** (Stefans beslut
        /// 2026-10-05, ersätter 2026-10-02). Kursledaren är ofta inte dagens skjutledare: nybörjarna
        /// skjuter bland klubbens övriga skyttar, och skjutledaren är den som har banan. Rätten följer
        /// därför kursen, aldrig skjutledarrollen. Deltagare och kopplad träning prövas per anrop.
        /// </summary>
        private Task<bool> CanRecordSeriesAsync(int groupId) => _groups.CanManageTrainingGroup(groupId);

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
                canRecord = await CanRecordSeriesAsync(groupId),
                cannotRecordReason = "Serier registreras av kursens tränare eller klubbadmin.",
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
            if (clubId <= 0 || !await CanRecordSeriesAsync(req.GroupId))
                return Json(new { success = false, message = "Serier registreras av kursens tränare eller klubbadmin." });
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
            if (clubId <= 0 || !await CanRecordSeriesAsync(req.GroupId))
                return Json(new { success = false, message = "Serier tas bort av kursens tränare eller klubbadmin." });
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
