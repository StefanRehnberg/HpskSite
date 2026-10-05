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
            /// <summary>Gäller för kursens deltagare: obligatoriskt för kursen ELLER för alla på träningen.</summary>
            public bool IsMandatory { get; set; }
            /// <summary>Kursens eget krav (gruppens standard eller tillfällets avvikelse).</summary>
            public bool MandatoryForCourse { get; set; }
            /// <summary>Träningens egen flagga — gäller alla som deltar, inte bara kursen.</summary>
            public bool MandatoryForAll { get; set; }
            public bool RegistrationRequired { get; set; }
            public string? AttendanceOverride { get; set; }
            public string? RegistrationOverride { get; set; }
            public string? Note { get; set; }
            public bool IsToday { get; set; }
            public bool IsPast { get; set; }
            public string? SkjutledareName { get; set; }
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
                // ⚠️ Två skilda krav (Stefan 2026-10-05): kursens, och träningens eget som gäller alla.
                // Ytan måste kunna säga vilket — men för en kursdeltagare räknas båda som obligatoriska.
                var forCourse = TrainingCourseRules.IsMandatory(course.DefaultAttendance, l.Attendance);
                course.Occasions.Add(new Occasion
                {
                    TrainingId = t.Id,
                    Date = t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    StartTime = t.StartTime, EndTime = t.EndTime, Name = t.Name, IsCancelled = t.IsCancelled,
                    IsMandatory = forCourse || t.IsMandatory,
                    MandatoryForCourse = forCourse,
                    MandatoryForAll = t.IsMandatory,
                    RegistrationRequired = TrainingCourseRules.RegistrationRequired(course.DefaultRegistration, l.Registration),
                    AttendanceOverride = TrainingCourseRules.NormaliseAttendance(l.Attendance),
                    RegistrationOverride = TrainingCourseRules.NormaliseRegistration(l.Registration),
                    Note = l.Note,
                    IsToday = t.Date.Date == today.Date,
                    IsPast = t.Date.Date < today.Date,
                    SkjutledareName = t.SkjutledareMemberId is > 0 ? Name(t.SkjutledareMemberId.Value) : null
                });
            }

            FillCells(db, course, trainings);
            course.Badges = BadgeStatusFor(db, course.Participants.Select(p => p.MemberId).ToList(), today.Year);
            return course;
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
    }
}
