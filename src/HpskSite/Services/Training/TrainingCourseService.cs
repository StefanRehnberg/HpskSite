using System.Globalization;
using HpskSite.Models;
using HpskSite.Models.Training;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Training
{
    /// <summary>
    /// Fas D: kursen = en träningsgrupp med kopplade träningar. Kopplingar, kursens krav per
    /// tillfälle (härledda, se <see cref="TrainingCourseRules"/>), närvarorutnätet, märkesläget och
    /// anteckningarna per deltagare och tillfälle.
    ///
    /// <para><b>⚠️ Behörigheten avgörs INTE här</b> — controllern frågar
    /// <c>TrainingGroupService.CanManageTrainingGroup</c> innan den anropar. Tjänsten tar id:n som de är.</para>
    ///
    /// <para><b>Närvaron läses, den skrivs inte här.</b> Uppropet går genom
    /// <c>ClubEvent/SetAttendance</c> med <c>kind = Training</c> — samma väg som deltagarsidan, så
    /// det finns EN sanning om vem som var där.</para>
    /// </summary>
    public class TrainingCourseService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly ILogger<TrainingCourseService> _logger;

        public TrainingCourseService(IUmbracoDatabaseFactory databaseFactory, IMemberService memberService,
                                     ILogger<TrainingCourseService> logger)
        {
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _logger = logger;
        }

        // ── Läsmodell ──────────────────────────────────────────────────────────────────────────

        public class Course
        {
            public int GroupId { get; set; }
            public string Name { get; set; } = "";
            public int ClubId { get; set; }
            public string? Description { get; set; }
            public bool IsActive { get; set; }
            public string? DefaultAttendance { get; set; }
            public string? DefaultRegistration { get; set; }
            public List<Person> Participants { get; set; } = new();
            public List<Person> Trainers { get; set; } = new();
            public List<Occasion> Occasions { get; set; } = new();
            /// <summary>"{memberId}:{trainingId}" → cell.</summary>
            public Dictionary<string, Cell> Cells { get; set; } = new();
            public Dictionary<int, BadgeStatus> Badges { get; set; } = new();
        }

        public class Person { public int MemberId { get; set; } public string Name { get; set; } = ""; }

        public class Occasion
        {
            public int TrainingId { get; set; }
            public string Date { get; set; } = "";
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            public string Name { get; set; } = "";
            public bool IsCancelled { get; set; }
            /// <summary>Gäller för kursens deltagare. Är alltid kursens krav (gruppens standard eller
            /// tillfällets avvikelse) — en träning är aldrig obligatorisk för alla (Stefan 2026-10-06).</summary>
            public bool IsMandatory { get; set; }
            /// <summary>Samma som <see cref="IsMandatory"/>; namnet finns kvar för Min kurs-ytan.</summary>
            public bool MandatoryForCourse { get; set; }
            public bool RegistrationRequired { get; set; }
            public string? AttendanceOverride { get; set; }
            public string? RegistrationOverride { get; set; }
            public string? Note { get; set; }
            public bool IsToday { get; set; }
            public bool IsPast { get; set; }
            public string? SkjutledareName { get; set; }
            public string? Venue { get; set; }
            /// <summary>Grenens visningsnamn (Precision, Fältskytte …), eller null.</summary>
            public string? Discipline { get; set; }
            public string? Description { get; set; }
        }

        /// <summary>
        /// En deltagare på ett tillfälle. State: present / absent / excused / checkedin / signedup /
        /// none. checkedin = ingen avprickning, men en QR-incheckning på klubbens bana inom fönstret
        /// (härlett, inte en närvarorad). Missed = obligatoriskt, passerat, och varken närvarande,
        /// incheckad eller giltigt frånvarande.
        /// </summary>
        public class Cell
        {
            public string State { get; set; } = "none";
            public bool Missed { get; set; }
        }

        public class BadgeStatus
        {
            public string? HeldLevel { get; set; }
            public string? TargetLevel { get; set; }
            public int Precision { get; set; }
            public int Tillampning { get; set; }
            public bool ReadyForPistolskyttekort { get; set; }
            public string Text { get; set; } = "";
        }

        private class LinkRow
        {
            public int TrainingId { get; set; }
            public string? Attendance { get; set; }
            public string? Registration { get; set; }
            public string? Note { get; set; }
        }

        private class GroupRow
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int ClubId { get; set; }
            public string? Description { get; set; }
            public bool IsActive { get; set; }
            public string? DefaultAttendance { get; set; }
            public string? DefaultRegistration { get; set; }
        }

        private class MemberRow { public int MemberId { get; set; } public string Role { get; set; } = ""; }
        private class AttendanceRow { public int EventId { get; set; } public int MemberId { get; set; } public string? AttendanceStatus { get; set; } public DateTime? SignedUpAt { get; set; } public DateTime? CancelledAt { get; set; } }
        private class CheckInRow { public int MemberId { get; set; } public DateTime Date { get; set; } public TimeSpan? StartTime { get; set; } }
        private class SeriesRow { public int MemberId { get; set; } public string SeriesType { get; set; } = ""; public string? Target { get; set; } public string? ClaimedLevel { get; set; } public string? BadgeFamily { get; set; } }

        public Course? Get(int groupId, DateTime today)
        {
            using var db = _databaseFactory.CreateDatabase();
            var g = db.SingleOrDefault<GroupRow>(
                "SELECT Id, Name, ClubId, Description, IsActive, DefaultAttendance, DefaultRegistration FROM dbo.TrainingGroups WHERE Id = @0", groupId);
            if (g == null) return null;

            var course = new Course
            {
                GroupId = g.Id, Name = g.Name, ClubId = g.ClubId, Description = g.Description, IsActive = g.IsActive,
                DefaultAttendance = TrainingCourseRules.NormaliseAttendance(g.DefaultAttendance),
                DefaultRegistration = TrainingCourseRules.NormaliseRegistration(g.DefaultRegistration)
            };

            var members = db.Fetch<MemberRow>(
                "SELECT MemberId, Role FROM dbo.TrainingGroupMembers WHERE TrainingGroupId = @0 AND IsActive = 1", groupId);
            var names = new Dictionary<int, string>();
            string Name(int id)
            {
                if (!names.TryGetValue(id, out var n)) names[id] = n = _memberService.GetById(id)?.Name ?? $"Medlem {id}";
                return n;
            }
            var sv = StringComparer.Create(new CultureInfo("sv-SE"), true);
            course.Participants = members.Where(m => m.Role != "Trainer")
                .Select(m => new Person { MemberId = m.MemberId, Name = Name(m.MemberId) }).OrderBy(p => p.Name, sv).ToList();
            course.Trainers = members.Where(m => m.Role == "Trainer")
                .Select(m => new Person { MemberId = m.MemberId, Name = Name(m.MemberId) }).OrderBy(p => p.Name, sv).ToList();

            var links = db.Fetch<LinkRow>(
                "SELECT TrainingId, Attendance, Registration, Note FROM dbo.TrainingGroupTraining WHERE TrainingGroupId = @0", groupId)
                .ToDictionary(l => l.TrainingId);
            var trainings = links.Count == 0 ? new List<ClubTraining>()
                : db.Fetch<ClubTraining>("WHERE Id IN (@0) ORDER BY [Date], StartTime", links.Keys.ToList());

            foreach (var t in trainings)
            {
                var l = links[t.Id];
                // Bara kursens krav. Träningens egen flagga läses inte (Stefan 2026-10-06, efter
                // Luleå PK): obligatoriskt för alla hör till händelser, inte träningar.
                var forCourse = TrainingCourseRules.IsMandatory(course.DefaultAttendance, l.Attendance);
                course.Occasions.Add(new Occasion
                {
                    TrainingId = t.Id,
                    Date = t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StartTime = t.StartTime, EndTime = t.EndTime, Name = t.Name, IsCancelled = t.IsCancelled,
                    IsMandatory = forCourse,
                    MandatoryForCourse = forCourse,
                    RegistrationRequired = TrainingCourseRules.RegistrationRequired(course.DefaultRegistration, l.Registration),
                    AttendanceOverride = TrainingCourseRules.NormaliseAttendance(l.Attendance),
                    RegistrationOverride = TrainingCourseRules.NormaliseRegistration(l.Registration),
                    Note = l.Note,
                    IsToday = t.Date.Date == today.Date,
                    IsPast = t.Date.Date < today.Date,
                    SkjutledareName = t.SkjutledareMemberId is > 0 ? Name(t.SkjutledareMemberId.Value) : null,
                    Venue = t.Venue,
                    Discipline = DisciplineLabel(t.Discipline),
                    Description = string.IsNullOrWhiteSpace(t.Description) ? null : t.Description.Trim()
                });
            }

            FillCells(db, course, trainings);
            course.Badges = BadgeStatusFor(db, course.Participants.Select(p => p.MemberId).ToList(), today.Year);
            return course;
        }

        private static string? DisciplineLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var c = ActivityDiscipline.Canonical(raw);
            return c.Length > 0 ? ActivityDiscipline.Label(c) : raw.Trim();
        }

        private void FillCells(IUmbracoDatabase db, Course course, List<ClubTraining> trainings)
        {
            if (trainings.Count == 0 || course.Participants.Count == 0) return;
            var memberIds = course.Participants.Select(p => p.MemberId).ToList();
            var trainingIds = trainings.Select(t => t.Id).ToList();
            var rows = db.Fetch<AttendanceRow>(@"
SELECT EventId, MemberId, AttendanceStatus, SignedUpAt, CancelledAt FROM dbo.ClubEventParticipant
WHERE OccasionKind = @0 AND EventId IN (@1) AND MemberId IN (@2)", ClubEvents.OccasionTraining, trainingIds, memberIds)
                .ToDictionary(r => $"{r.MemberId}:{r.EventId}");

            // Incheckningar på klubbens banor de dagar kursen tränar — EN fråga.
            var checkIns = new List<CheckInRow>();
            try
            {
                var dates = trainings.Select(t => t.Date.Date).Distinct().ToList();
                checkIns = db.Fetch<CheckInRow>(@"
SELECT s.MemberId, s.[Date], s.StartTime FROM dbo.RangeActivitySession s
WHERE s.MemberId IN (@0) AND CAST(s.[Date] AS date) IN (@1)
  AND s.RangeId IN (SELECT RangeId FROM dbo.ClubRangeLink WHERE ClubId = @2)", memberIds, dates, course.ClubId);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Incheckningar kunde inte läsas för kursen"); }

            foreach (var t in trainings)
            {
                var occ = course.Occasions.First(o => o.TrainingId == t.Id);
                foreach (var m in memberIds)
                {
                    var cell = new Cell();
                    if (rows.TryGetValue($"{m}:{t.Id}", out var r) && r.CancelledAt == null)
                    {
                        cell.State = r.AttendanceStatus switch
                        {
                            ClubEvents.AttendancePresent => "present",
                            ClubEvents.AttendanceAbsent => "absent",
                            ClubEvents.AttendanceExcused => "excused",
                            _ => r.SignedUpAt != null ? "signedup" : "none"
                        };
                    }
                    if (cell.State is "none" or "signedup"
                        && checkIns.Any(c => c.MemberId == m && TrainingCourseRules.CheckInCounts(
                               c.Date.Date.Add(c.StartTime ?? TimeSpan.Zero), t.Date, t.StartTime, t.EndTime)))
                        cell.State = "checkedin";
                    cell.Missed = occ.IsMandatory && occ.IsPast && !t.IsCancelled
                                  && cell.State is not ("present" or "checkedin" or "excused");
                    course.Cells[$"{m}:{t.Id}"] = cell;
                }
            }
        }

        /// <summary>
        /// Märkesläget: hållen grundvalör, nästa valör, och hur många av årets godkända precisions-
        /// respektive tillämpningsserier som når den. "Klar för Pistolskyttekortet" = bronsmärket hållet.
        /// </summary>
        public Dictionary<int, BadgeStatus> BadgeStatusFor(IUmbracoDatabase db, List<int> memberIds, int year)
        {
            var result = new Dictionary<int, BadgeStatus>();
            if (memberIds.Count == 0) return result;
            var held = new Dictionary<int, int>();
            var series = new List<SeriesRow>();
            try
            {
                foreach (var r in db.Fetch<MemberRow>(@"
SELECT MemberId, CAST(MAX(LevelOrdinal) AS nvarchar(10)) AS Role FROM dbo.MemberBadge
WHERE MemberId IN (@0) AND BadgeFamily = @1 AND Status = 'Verified' GROUP BY MemberId", memberIds, Marken.FamilyPistolskytte))
                    held[r.MemberId] = int.TryParse(r.Role, out var o) ? o : 0;
                series = db.Fetch<SeriesRow>(@"
SELECT MemberId, SeriesType, Target, ClaimedLevel, BadgeFamily FROM dbo.MarkenSeries
WHERE MemberId IN (@0) AND [Year] = @1 AND Status = 'Verified' AND CountsTowardGuldfodring = 1", memberIds, year);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Märkesläget kunde inte läsas för kursen"); }

            foreach (var id in memberIds)
            {
                var heldOrd = held.GetValueOrDefault(id);
                var targetOrd = Math.Min(heldOrd + 1, Marken.LevelOrdinal(Marken.LevelGuld));
                var mine = series.Where(s => s.MemberId == id && Marken.LevelOrdinal(s.ClaimedLevel) >= targetOrd).ToList();
                var st = new BadgeStatus
                {
                    HeldLevel = Marken.LevelFromOrdinal(heldOrd),
                    TargetLevel = Marken.LevelFromOrdinal(targetOrd),
                    Precision = mine.Count(s => Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplinePrecision),
                    Tillampning = mine.Count(s => Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplineTillampning),
                    ReadyForPistolskyttekort = heldOrd >= Marken.LevelOrdinal(Marken.LevelBrons)
                };
                var target = (st.TargetLevel ?? "").ToLowerInvariant();
                st.Text = heldOrd >= Marken.LevelOrdinal(Marken.LevelGuld)
                    ? "Guldmärket klart"
                    : (st.ReadyForPistolskyttekort ? $"{st.HeldLevel}märket klart · " : "")
                      + $"Precision {Math.Min(st.Precision, 3)}/3 {target} · tillämpning {Math.Min(st.Tillampning, 3)}/3";
                result[id] = st;
            }
            return result;
        }

        // ── Kopplingar ─────────────────────────────────────────────────────────────────────────

        /// <summary>Kopplar träningar till kursen. Bara träningar i kursens klubb; redan kopplade hoppas över.</summary>
        public int Link(int groupId, IEnumerable<int> trainingIds, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var clubId = db.ExecuteScalar<int>("SELECT ClubId FROM dbo.TrainingGroups WHERE Id = @0", groupId);
            var ids = trainingIds.Where(i => i > 0).Distinct().ToList();
            if (clubId <= 0 || ids.Count == 0) return 0;
            var valid = db.Fetch<int>("SELECT Id FROM dbo.ClubTraining WHERE ClubId = @0 AND Id IN (@1)", clubId, ids);
            var existing = db.Fetch<int>("SELECT TrainingId FROM dbo.TrainingGroupTraining WHERE TrainingGroupId = @0", groupId).ToHashSet();
            var n = 0;
            foreach (var id in valid.Where(v => !existing.Contains(v)))
            {
                db.Insert(new TrainingGroupTraining { TrainingGroupId = groupId, TrainingId = id, CreatedByMemberId = actingMemberId });
                n++;
            }
            return n;
        }

        /// <summary>Klubbens träningar som kan kopplas: kommande (och de senaste 30 dagarna), med markering om de redan är kopplade.</summary>
        public List<(ClubTraining Training, bool Linked)> Candidates(int groupId, DateTime today)
        {
            using var db = _databaseFactory.CreateDatabase();
            var clubId = db.ExecuteScalar<int>("SELECT ClubId FROM dbo.TrainingGroups WHERE Id = @0", groupId);
            var linked = db.Fetch<int>("SELECT TrainingId FROM dbo.TrainingGroupTraining WHERE TrainingGroupId = @0", groupId).ToHashSet();
            return db.Fetch<ClubTraining>("WHERE ClubId = @0 AND [Date] >= @1 ORDER BY [Date], StartTime", clubId, today.AddDays(-30))
                .Select(t => (t, linked.Contains(t.Id))).ToList();
        }

        public bool Unlink(int groupId, int trainingId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Execute("DELETE FROM dbo.TrainingGroupTraining WHERE TrainingGroupId = @0 AND TrainingId = @1", groupId, trainingId) == 1;
        }

        public string? SetLinkRules(int groupId, int trainingId, string? attendance, string? registration, string? note)
        {
            using var db = _databaseFactory.CreateDatabase();
            var n = db.Execute(
                "UPDATE dbo.TrainingGroupTraining SET Attendance = @2, Registration = @3, Note = @4 WHERE TrainingGroupId = @0 AND TrainingId = @1",
                groupId, trainingId, TrainingCourseRules.NormaliseAttendance(attendance),
                TrainingCourseRules.NormaliseRegistration(registration),
                string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)]);
            return n == 1 ? null : "Träningen är inte kopplad till kursen.";
        }

        public void SetDefaults(int groupId, string? attendance, string? registration)
        {
            using var db = _databaseFactory.CreateDatabase();
            db.Execute("UPDATE dbo.TrainingGroups SET DefaultAttendance = @1, DefaultRegistration = @2 WHERE Id = @0",
                groupId, TrainingCourseRules.NormaliseAttendance(attendance), TrainingCourseRules.NormaliseRegistration(registration));
        }

        // ── Anteckningar ───────────────────────────────────────────────────────────────────────

        public class NoteView
        {
            public int MemberId { get; set; }
            public int TrainingId { get; set; }
            public string Note { get; set; } = "";
            public string AuthorName { get; set; } = "";
            public string Date { get; set; } = "";
        }

        /// <summary>Alla kursens anteckningar, nyaste först.</summary>
        public List<NoteView> Notes(int groupId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<TrainingCourseNote>("WHERE TrainingGroupId = @0 ORDER BY UpdatedDate DESC", groupId)
                .Select(n => new NoteView
                {
                    MemberId = n.MemberId, TrainingId = n.TrainingId, Note = n.Note,
                    AuthorName = _memberService.GetById(n.AuthorMemberId)?.Name ?? "",
                    Date = n.UpdatedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                }).ToList();
        }

        /// <summary>Sparar, ändrar eller (med tom text) tar bort anteckningen.</summary>
        public void SaveNote(int groupId, int trainingId, int memberId, string? note, int authorMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var text = (note ?? "").Trim();
            if (text.Length == 0)
            {
                db.Execute("DELETE FROM dbo.TrainingCourseNote WHERE TrainingGroupId=@0 AND TrainingId=@1 AND MemberId=@2", groupId, trainingId, memberId);
                return;
            }
            if (text.Length > 2000) text = text[..2000];
            var row = db.SingleOrDefault<TrainingCourseNote>(
                "WHERE TrainingGroupId=@0 AND TrainingId=@1 AND MemberId=@2", groupId, trainingId, memberId);
            if (row == null)
                db.Insert(new TrainingCourseNote { TrainingGroupId = groupId, TrainingId = trainingId, MemberId = memberId, Note = text, AuthorMemberId = authorMemberId });
            else
            {
                row.Note = text; row.AuthorMemberId = authorMemberId; row.UpdatedDate = DateTime.Now;
                db.Update(row);
            }
        }

        /// <summary>Är träningen kopplad till kursen? Endpointen kontrollerar det innan något skrivs.</summary>
        public bool IsLinked(int groupId, int trainingId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.TrainingGroupTraining WHERE TrainingGroupId=@0 AND TrainingId=@1", groupId, trainingId) > 0;
        }

        public class CourseListItem
        {
            public int GroupId { get; set; }
            public string Name { get; set; } = "";
            public int ClubId { get; set; }
            public bool IsTrainer { get; set; }
        }

        /// <summary>Aktiva kurser där medlemmen är tränare, plus alla aktiva kurser i <paramref name="managedClubIds"/>.</summary>
        public List<CourseListItem> CoursesFor(int memberId, IEnumerable<int> managedClubIds)
        {
            using var db = _databaseFactory.CreateDatabase();
            var clubs = managedClubIds.Where(c => c > 0).Distinct().ToList();
            var rows = db.Fetch<CourseListItem>(@"
SELECT g.Id AS GroupId, g.Name, g.ClubId,
       CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.TrainingGroupMembers m
                              WHERE m.TrainingGroupId = g.Id AND m.MemberId = @0 AND m.Role = 'Trainer' AND m.IsActive = 1)
                 THEN 1 ELSE 0 END AS bit) AS IsTrainer
FROM dbo.TrainingGroups g
WHERE g.IsActive = 1 AND (
      EXISTS (SELECT 1 FROM dbo.TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.MemberId = @0 AND m.Role = 'Trainer' AND m.IsActive = 1)
   " + (clubs.Count > 0 ? "OR g.ClubId IN (@1)" : "") + @")
ORDER BY CASE WHEN EXISTS (SELECT 1 FROM dbo.TrainingGroupMembers m WHERE m.TrainingGroupId = g.Id AND m.MemberId = @0 AND m.Role = 'Trainer' AND m.IsActive = 1) THEN 0 ELSE 1 END, g.Name",
                memberId, clubs.Count > 0 ? clubs : new List<int> { 0 });
            return rows;
        }

        public class MemberCourseOnTraining
        {
            public string CourseName { get; set; } = "";
            public bool Mandatory { get; set; }
            public bool RegistrationRequired { get; set; }
            public string? Note { get; set; }
        }

        /// <summary>
        /// Kurserna medlemmen går (som DELTAGARE, aktiv grupp) som tillfället är kopplat till, med
        /// kursens krav för just det tillfället. Driver träningspanelen, så att nybörjaren själv ser att
        /// tillfället är obligatoriskt för hens kurs och vad det är (Stefan 2026-10-05). Ett fel ger tom
        /// lista — panelen ska visas ändå.
        /// </summary>
        public List<MemberCourseOnTraining> CoursesForMemberOnTraining(int memberId, int trainingId)
        {
            if (memberId <= 0 || trainingId <= 0) return new();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var rows = db.Fetch<CourseLinkRow>(@"
SELECT g.Name, g.DefaultAttendance, g.DefaultRegistration, l.Attendance, l.Registration, l.Note
FROM dbo.TrainingGroupTraining l
JOIN dbo.TrainingGroups g ON g.Id = l.TrainingGroupId AND g.IsActive = 1
JOIN dbo.TrainingGroupMembers m ON m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.MemberId = @0 AND m.Role <> 'Trainer'
WHERE l.TrainingId = @1
ORDER BY g.Name", memberId, trainingId);
                return rows.Select(r => new MemberCourseOnTraining
                {
                    CourseName = r.Name ?? "",
                    Mandatory = TrainingCourseRules.IsMandatory(r.DefaultAttendance, r.Attendance),
                    RegistrationRequired = TrainingCourseRules.RegistrationRequired(r.DefaultRegistration, r.Registration),
                    Note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa kurser för medlem {MemberId} på träning {TrainingId}", memberId, trainingId);
                return new();
            }
        }

        private class CourseLinkRow
        {
            public string? Name { get; set; }
            public string? DefaultAttendance { get; set; }
            public string? DefaultRegistration { get; set; }
            public string? Attendance { get; set; }
            public string? Registration { get; set; }
            public string? Note { get; set; }
        }

        /// <summary>
        /// Är medlemmen tränare i någon aktiv kurs? Styr menyvalet "Min kurs" i sidhuvudet, så frågan
        /// körs vid varje sidladdning för inloggade — därför en enda EXISTS, och ett fel svarar nej
        /// i stället för att ta ner sidhuvudet.
        /// </summary>
        public bool IsTrainerInActiveCourse(int memberId)
        {
            if (memberId <= 0) return false;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.ExecuteScalar<int>(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.TrainingGroupMembers m
                         JOIN dbo.TrainingGroups g ON g.Id = m.TrainingGroupId AND g.IsActive = 1
                         WHERE m.MemberId = @0 AND m.Role = 'Trainer' AND m.IsActive = 1)
            THEN 1 ELSE 0 END", memberId) == 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte avgöra om medlem {MemberId} är kurstränare", memberId);
                return false;
            }
        }

        public bool IsParticipant(int groupId, int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.TrainingGroupMembers WHERE TrainingGroupId=@0 AND MemberId=@1 AND IsActive=1 AND Role <> 'Trainer'", groupId, memberId) > 0;
        }

        // ── Medlemmens egna kurser (UX-omgången 2026-10-06) ───────────────────────────────────
        //
        // ⚠️ Deltagaren hade INGEN kursyta: Min kurs fanns bara för tränare, startsidan nämnde inte
        // kursen och kalendern visade kursens tillfällen som vilka träningar som helst. Allt nedan
        // finns för att nybörjaren ska hitta sin kurs utan att veta var hen ska leta.

        public const string RoleTrainer = "Trainer";
        public const string RoleParticipant = "Participant";

        public class MemberCourse
        {
            public int GroupId { get; set; }
            public string Name { get; set; } = "";
            public int ClubId { get; set; }
            /// <summary><see cref="RoleTrainer"/> eller <see cref="RoleParticipant"/>.</summary>
            public string Role { get; set; } = RoleParticipant;
        }

        /// <summary>
        /// Aktiva kurser medlemmen är med i, som tränare eller deltagare. En rad per kurs; är hen
        /// både och (ska inte hända) vinner tränarrollen.
        /// </summary>
        public List<MemberCourse> CoursesOfMember(int memberId)
        {
            if (memberId <= 0) return new();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.Fetch<MemberCourse>(@"
SELECT g.Id AS GroupId, g.Name, g.ClubId,
       CASE WHEN MAX(CASE WHEN m.Role = 'Trainer' THEN 1 ELSE 0 END) = 1 THEN 'Trainer' ELSE 'Participant' END AS Role
FROM dbo.TrainingGroupMembers m
JOIN dbo.TrainingGroups g ON g.Id = m.TrainingGroupId AND g.IsActive = 1
WHERE m.MemberId = @0 AND m.IsActive = 1
GROUP BY g.Id, g.Name, g.ClubId
ORDER BY g.Name", memberId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa medlem {MemberId}s kurser", memberId);
                return new();
            }
        }

        /// <summary>
        /// Är medlemmen med i någon aktiv kurs, som tränare ELLER deltagare? Styr menyvalet "Min kurs"
        /// — körs vid varje sidladdning för inloggade, därför en EXISTS och nej vid fel.
        /// </summary>
        public bool IsInActiveCourse(int memberId)
        {
            if (memberId <= 0) return false;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.ExecuteScalar<int>(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.TrainingGroupMembers m
                         JOIN dbo.TrainingGroups g ON g.Id = m.TrainingGroupId AND g.IsActive = 1
                         WHERE m.MemberId = @0 AND m.IsActive = 1)
            THEN 1 ELSE 0 END", memberId) == 1;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte avgöra om medlem {MemberId} går en kurs", memberId);
                return false;
            }
        }

        /// <summary>Är medlemmen med i kursen (tränare eller deltagare, aktiv grupp)?</summary>
        public bool IsInCourse(int groupId, int memberId)
        {
            if (groupId <= 0 || memberId <= 0) return false;
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>(@"
SELECT COUNT(*) FROM dbo.TrainingGroupMembers m JOIN dbo.TrainingGroups g ON g.Id = m.TrainingGroupId AND g.IsActive = 1
WHERE m.TrainingGroupId = @0 AND m.MemberId = @1 AND m.IsActive = 1", groupId, memberId) > 0;
        }

        public class MySeries
        {
            public string Date { get; set; } = "";
            public string Occasion { get; set; } = "";
            public int SeriesNumber { get; set; }
            public int Total { get; set; }
            public string? Valor { get; set; }
        }

        /// <summary>
        /// Deltagarens egen bild av kursen. ⚠️ Bara HENS rader: inga andra deltagare, inga tränarens
        /// anteckningar (de är tränarens överlämning, inte till deltagaren).
        /// </summary>
        public class ParticipantView
        {
            public int GroupId { get; set; }
            public string Name { get; set; } = "";
            public string? Description { get; set; }
            public List<string> Trainers { get; set; } = new();
            public List<Occasion> Occasions { get; set; } = new();
            /// <summary>trainingId → min cell.</summary>
            public Dictionary<int, Cell> Mine { get; set; } = new();
            public BadgeStatus? Badge { get; set; }
            public List<MySeries> Series { get; set; } = new();
            /// <summary>
            /// Årets godkända serier som märkesläget räknar — samma urval som <see cref="BadgeStatusFor"/>.
            /// ⚠️ Utan dem säger märkesläget "Precision 3/3" medan Mina serier är tom: kursens serier
            /// är bara en del av underlaget (tävlingsserier och inskickade serier räknas också).
            /// </summary>
            public List<BadgeSeries> BadgeSeries { get; set; } = new();
        }

        public class BadgeSeries
        {
            public string Date { get; set; } = "";
            /// <summary>Precision / Tillämpning / Snabbpistol / Luftpistol.</summary>
            public string Kind { get; set; } = "";
            public string? WeaponGroup { get; set; }
            public int Total { get; set; }
            public string? Valor { get; set; }
            /// <summary>Var serien kom ifrån: tävlingens namn, "Kursen" eller "Inskickad".</summary>
            public string Source { get; set; } = "";
            /// <summary>Räknas mot nästa valör (når målvalören), som märkesläget räknar.</summary>
            public bool CountsTowardTarget { get; set; }
        }

        private class BadgeSeriesRow
        {
            public DateTime SeriesDate { get; set; }
            public string SeriesType { get; set; } = "";
            public string? Target { get; set; }
            public string? BadgeFamily { get; set; }
            public string? WeaponGroup { get; set; }
            public int Total { get; set; }
            public string? ClaimedLevel { get; set; }
            public int? SourceCompetitionId { get; set; }
            public string? CompetitionName { get; set; }
            public string? Notes { get; set; }
        }

        private class MySeriesRow
        {
            public DateTime Date { get; set; }
            public string Name { get; set; } = "";
            public string? Note { get; set; }
            public int SeriesNumber { get; set; }
            public int Total { get; set; }
            public string? Valor { get; set; }
        }

        public ParticipantView? GetForParticipant(int groupId, int memberId, DateTime today)
        {
            var c = Get(groupId, today);
            if (c == null) return null;
            var v = new ParticipantView
            {
                GroupId = c.GroupId, Name = c.Name, Description = c.Description,
                Trainers = c.Trainers.Select(t => t.Name).ToList(),
                Occasions = c.Occasions
            };
            foreach (var o in c.Occasions)
                v.Mine[o.TrainingId] = c.Cells.TryGetValue($"{memberId}:{o.TrainingId}", out var cell) ? cell : new Cell();
            v.Badge = c.Badges.GetValueOrDefault(memberId);
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                v.Series = db.Fetch<MySeriesRow>(@"
SELECT t.[Date], t.Name, l.Note, s.SeriesNumber, s.Total, s.Valor
FROM dbo.TrainingCourseSeries s
JOIN dbo.ClubTraining t ON t.Id = s.TrainingId
LEFT JOIN dbo.TrainingGroupTraining l ON l.TrainingGroupId = s.TrainingGroupId AND l.TrainingId = s.TrainingId
WHERE s.TrainingGroupId = @0 AND s.MemberId = @1
ORDER BY t.[Date] DESC, s.SeriesNumber", groupId, memberId)
                    .Select(r => new MySeries
                    {
                        Date = r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Occasion = string.IsNullOrWhiteSpace(r.Note) ? r.Name : r.Note!,
                        SeriesNumber = r.SeriesNumber, Total = r.Total, Valor = r.Valor
                    }).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Deltagarens serier kunde inte läsas (kurs {Group})", groupId); }

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var targetOrd = Marken.LevelOrdinal(v.Badge?.TargetLevel);
                // Samma urval som BadgeStatusFor — annars kan listan och märkesläget säga olika saker.
                v.BadgeSeries = db.Fetch<BadgeSeriesRow>(@"
SELECT s.SeriesDate, s.SeriesType, s.Target, s.BadgeFamily, s.WeaponGroup, s.Total, s.ClaimedLevel,
       s.SourceCompetitionId, n.[text] AS CompetitionName, s.Notes
FROM dbo.MarkenSeries s
LEFT JOIN dbo.umbracoNode n ON n.id = s.SourceCompetitionId
WHERE s.MemberId = @0 AND s.[Year] = @1 AND s.Status = 'Verified' AND s.CountsTowardGuldfodring = 1
ORDER BY s.SeriesDate DESC, s.Id DESC", memberId, today.Year)
                    .Select(r => new BadgeSeries
                    {
                        Date = r.SeriesDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Kind = Marken.DisciplineDisplay(Marken.SeriesDiscipline(r.BadgeFamily, r.SeriesType, r.Target)),
                        WeaponGroup = r.WeaponGroup,
                        Total = r.Total,
                        Valor = string.IsNullOrWhiteSpace(r.ClaimedLevel) ? null : r.ClaimedLevel,
                        Source = r.SourceCompetitionId is > 0
                            ? (string.IsNullOrWhiteSpace(r.CompetitionName) ? "Tävling" : r.CompetitionName!)
                            : (r.Notes ?? "").StartsWith("Kursserie", StringComparison.Ordinal) ? "Kursen" : "Inskickad",
                        CountsTowardTarget = targetOrd > 0 && Marken.LevelOrdinal(r.ClaimedLevel) >= targetOrd
                    }).ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Märkesserierna kunde inte läsas (medlem {Member})", memberId); }
            return v;
        }

        /// <summary>Startsidans kort: kursen, min roll, nästa tillfälle och (för deltagaren) missade obligatoriska.</summary>
        public class HomeCard
        {
            public int GroupId { get; set; }
            public string Name { get; set; } = "";
            public string Role { get; set; } = RoleParticipant;
            public Occasion? Next { get; set; }
            public int MissedCount { get; set; }
            public int ParticipantCount { get; set; }
        }

        /// <summary>
        /// Ett kort per aktiv kurs som har ett kommande (eller dagens) tillfälle. En kurs utan fler
        /// tillfällen visas inte — det finns inget att göra på startsidan för den.
        /// </summary>
        public List<HomeCard> HomeCardsFor(int memberId, DateTime today)
        {
            var result = new List<HomeCard>();
            foreach (var mc in CoursesOfMember(memberId))
            {
                try
                {
                    var c = Get(mc.GroupId, today);
                    if (c == null) continue;
                    var next = c.Occasions.FirstOrDefault(o => !o.IsCancelled && !o.IsPast);
                    if (next == null) continue;
                    result.Add(new HomeCard
                    {
                        GroupId = c.GroupId, Name = c.Name, Role = mc.Role, Next = next,
                        ParticipantCount = c.Participants.Count,
                        MissedCount = mc.Role == RoleParticipant
                            ? c.Occasions.Count(o => c.Cells.TryGetValue($"{memberId}:{o.TrainingId}", out var x) && x.Missed)
                            : 0
                    });
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Kurskortet kunde inte byggas (kurs {Group})", mc.GroupId); }
            }
            return result.OrderBy(r => r.Next!.Date).ThenBy(r => r.Next!.StartTime).ToList();
        }

        public class CalendarMark
        {
            public string CourseName { get; set; } = "";
            public bool Mandatory { get; set; }
        }

        private class MarkRow
        {
            public int TrainingId { get; set; }
            public string Name { get; set; } = "";
            public string? DefaultAttendance { get; set; }
            public string? Attendance { get; set; }
        }

        /// <summary>
        /// Vilka av träningarna hör till en kurs medlemmen går (eller leder)? Kalendern märker dem
        /// "din kurs" — annars ser kursens tillfällen ut som klubbens vanliga träningar.
        /// </summary>
        public Dictionary<int, CalendarMark> CalendarMarksFor(int memberId, IEnumerable<int> trainingIds)
        {
            var ids = trainingIds.Where(i => i > 0).Distinct().ToList();
            var result = new Dictionary<int, CalendarMark>();
            if (memberId <= 0 || ids.Count == 0) return result;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                foreach (var chunk in ids.Chunk(1000))
                {
                    foreach (var r in db.Fetch<MarkRow>(@"
SELECT l.TrainingId, g.Name, g.DefaultAttendance, l.Attendance
FROM dbo.TrainingGroupTraining l
JOIN dbo.TrainingGroups g ON g.Id = l.TrainingGroupId AND g.IsActive = 1
JOIN dbo.TrainingGroupMembers m ON m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.MemberId = @0
WHERE l.TrainingId IN (@1)", memberId, chunk.ToList()))
                    {
                        var mand = TrainingCourseRules.IsMandatory(r.DefaultAttendance, r.Attendance);
                        if (result.TryGetValue(r.TrainingId, out var existing))
                            existing.Mandatory |= mand;
                        else
                            result[r.TrainingId] = new CalendarMark { CourseName = r.Name, Mandatory = mand };
                    }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Kursmarkeringen i kalendern kunde inte läsas"); }
            return result;
        }

        public int ClubIdOf(int groupId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>("SELECT ClubId FROM dbo.TrainingGroups WHERE Id = @0", groupId);
        }

        public class ClubSummary
        {
            public int GroupId { get; set; }
            public int OccasionCount { get; set; }
            public List<string> Trainers { get; set; } = new();
            public string? NextDate { get; set; }
            public string? NextTime { get; set; }
            public string? NextName { get; set; }
        }

        private class SummaryRow
        {
            public int GroupId { get; set; }
            public int OccasionCount { get; set; }
            public DateTime? NextDate { get; set; }
            public string? NextTime { get; set; }
            public string? NextName { get; set; }
            public string? NextNote { get; set; }
        }

        private class TrainerRow { public int GroupId { get; set; } public int MemberId { get; set; } }

        /// <summary>
        /// Admin → Träningsgrupper: per grupp i klubben antal kopplade tillfällen, nästa (ej
        /// inställda) tillfälle och instruktörernas namn — två frågor för hela listan.
        /// </summary>
        public List<ClubSummary> ClubSummaries(int clubId, DateTime today)
        {
            using var db = _databaseFactory.CreateDatabase();
            var rows = db.Fetch<SummaryRow>(@"
SELECT g.Id AS GroupId,
       (SELECT COUNT(*) FROM dbo.TrainingGroupTraining l WHERE l.TrainingGroupId = g.Id) AS OccasionCount,
       nx.[Date] AS NextDate, nx.StartTime AS NextTime, nx.Name AS NextName, nx.Note AS NextNote
FROM dbo.TrainingGroups g
OUTER APPLY (SELECT TOP 1 t.[Date], t.StartTime, t.Name, l.Note
             FROM dbo.TrainingGroupTraining l
             JOIN dbo.ClubTraining t ON t.Id = l.TrainingId AND t.IsCancelled = 0
             WHERE l.TrainingGroupId = g.Id AND t.[Date] >= @1
             ORDER BY t.[Date], t.StartTime) nx
WHERE g.ClubId = @0", clubId, today.Date);
            var trainers = db.Fetch<TrainerRow>(@"
SELECT m.TrainingGroupId AS GroupId, m.MemberId FROM dbo.TrainingGroupMembers m
JOIN dbo.TrainingGroups g ON g.Id = m.TrainingGroupId
WHERE g.ClubId = @0 AND m.Role = 'Trainer' AND m.IsActive = 1", clubId);
            var names = new Dictionary<int, string>();
            string Name(int id)
            {
                if (!names.TryGetValue(id, out var n)) names[id] = n = _memberService.GetById(id)?.Name ?? $"Medlem {id}";
                return n;
            }
            return rows.Select(r => new ClubSummary
            {
                GroupId = r.GroupId, OccasionCount = r.OccasionCount,
                Trainers = trainers.Where(t => t.GroupId == r.GroupId).Select(t => Name(t.MemberId)).OrderBy(n => n).ToList(),
                NextDate = r.NextDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                NextTime = r.NextTime,
                NextName = string.IsNullOrWhiteSpace(r.NextNote) ? r.NextName : r.NextNote
            }).ToList();
        }

        public class ReminderOccasion
        {
            public int MemberId { get; set; }
            public int GroupId { get; set; }
            public int TrainingId { get; set; }
            public DateTime Date { get; set; }
            public string? StartTime { get; set; }
            public string Name { get; set; } = "";
            public string? Note { get; set; }
            public string? Venue { get; set; }
            public string GroupName { get; set; } = "";
            public string? DefaultAttendance { get; set; }
            public string? Attendance { get; set; }
            public bool Mandatory => TrainingCourseRules.IsMandatory(DefaultAttendance, Attendance);
            public DateTime? StartsAt => TimeSpan.TryParseExact(StartTime ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
                ? Date.Date.Add(ts) : null;
        }

        /// <summary>
        /// Kursernas tillfällen mellan två dagar, en rad per DELTAGARE (inte tränare — de leder
        /// tillfället). Inställda tas inte med. Underlag för påminnelsen dagen innan.
        /// </summary>
        public List<ReminderOccasion> ParticipantOccasionsBetween(DateTime fromDate, DateTime toDate)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<ReminderOccasion>(@"
SELECT m.MemberId, g.Id AS GroupId, t.Id AS TrainingId, t.[Date], t.StartTime, t.Name, l.Note, t.Venue,
       g.Name AS GroupName, g.DefaultAttendance, l.Attendance
FROM dbo.TrainingGroupTraining l
JOIN dbo.ClubTraining t ON t.Id = l.TrainingId AND t.IsCancelled = 0
JOIN dbo.TrainingGroups g ON g.Id = l.TrainingGroupId AND g.IsActive = 1
JOIN dbo.TrainingGroupMembers m ON m.TrainingGroupId = g.Id AND m.IsActive = 1 AND m.Role <> 'Trainer'
WHERE t.[Date] >= @0 AND t.[Date] <= @1", fromDate.Date, toDate.Date);
        }
    }
}
