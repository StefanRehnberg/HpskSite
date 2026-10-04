using System.Globalization;
using System.Text.Json;
using HpskSite.Models;
using HpskSite.Models.Training;
using HpskSite.Services.Firearms;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Training
{
    /// <summary>
    /// Fas B4: klubbens träningar — lista, skapa ett tillfälle, skapa ett schema, byta skjutledare,
    /// ställa in. Deltagandet (anmälan, upprop, gäster, betalning, QR) är INTE här: det är
    /// <see cref="ClubEventParticipationService"/> med <c>kind = Training</c>, samma kod som för en
    /// händelse.
    ///
    /// <para><b>⚠️ Ett tillfälle med deltagare eller lånevapenbokningar raderas aldrig</b> — det
    /// ställs in. En radering hade lämnat anmälningar som pekar på ingenting (raderna har ingen
    /// främmande nyckel mot träningen), och medlemmen som anmält sig hade aldrig fått veta.</para>
    /// </summary>
    public class ClubTrainingService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly ILogger<ClubTrainingService> _logger;

        public ClubTrainingService(
            IUmbracoDatabaseFactory databaseFactory,
            IMemberService memberService,
            ILogger<ClubTrainingService> logger)
        {
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _logger = logger;
        }

        public class TrainingRow
        {
            public int Id { get; set; }
            public int? ScheduleId { get; set; }
            public string Date { get; set; } = "";
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            public string Name { get; set; } = "";
            public string? Discipline { get; set; }
            public string DisciplineLabel { get; set; } = "";
            public string? Venue { get; set; }
            public string? Description { get; set; }
            public int? SkjutledareMemberId { get; set; }
            public string SkjutledareName { get; set; } = "";
            public bool IsCancelled { get; set; }
            public bool RegistrationRequired { get; set; }
            public int? MaxParticipants { get; set; }
            public bool LoanWeaponsOffered { get; set; }
            public bool IsMandatory { get; set; }
            public int SignedUp { get; set; }
            public int Present { get; set; }
            public int Bookings { get; set; }
        }

        private class CountRow
        {
            public int K { get; set; }
            public int SignedUp { get; set; }
            public int Present { get; set; }
        }

        private class IdCount
        {
            public int K { get; set; }
            public int V { get; set; }
        }

        public ClubTraining? Get(int id)
        {
            if (id <= 0) return null;
            using var db = _databaseFactory.CreateDatabase();
            return db.SingleOrDefault<ClubTraining>("WHERE Id = @0", id);
        }

        /// <summary>Klubbens tillfällen i [from, to], äldst först, med antal anmälda och närvarande.</summary>
        public List<TrainingRow> List(int clubId, DateTime from, DateTime to)
        {
            using var db = _databaseFactory.CreateDatabase();
            var trainings = db.Fetch<ClubTraining>(
                "WHERE ClubId = @0 AND [Date] >= @1 AND [Date] <= @2 ORDER BY [Date], StartTime, Id",
                clubId, from.Date, to.Date);
            if (trainings.Count == 0) return new();

            var ids = trainings.Select(t => t.Id).ToList();
            var counts = new Dictionary<int, CountRow>();
            var bookings = new Dictionary<int, int>();
            // ⚠️ IN (@0) tar slut kring 2100 parametrar — ett helt års träningar ryms, men chunka ändå.
            foreach (var chunk in ids.Chunk(1000))
            {
                foreach (var c in db.Fetch<CountRow>(@"
SELECT EventId AS K,
       SUM(CASE WHEN SignedUpAt IS NOT NULL AND CancelledAt IS NULL THEN 1 ELSE 0 END) AS SignedUp,
       SUM(CASE WHEN AttendanceStatus = @1 THEN 1 ELSE 0 END) AS Present
FROM dbo.ClubEventParticipant
WHERE OccasionKind = @0 AND EventId IN (@2)
GROUP BY EventId", ClubEvents.OccasionTraining, ClubEvents.AttendancePresent, chunk))
                    counts[c.K] = c;
                try
                {
                    foreach (var b in db.Fetch<IdCount>(@"
SELECT OccasionId AS K, COUNT(*) AS V FROM dbo.FirearmBooking
WHERE OccasionKind = @0 AND OccasionId IN (@1) AND Status IN (@2) GROUP BY OccasionId",
                        FirearmOccasionKind.Training, chunk, FirearmBookingStatus.Blocking))
                        bookings[b.K] = b.V;
                }
                catch (Exception ex)
                {
                    // Lånevapnen är en sidouppgift i listan — en saknad tabell får inte ta ner den.
                    _logger.LogDebug(ex, "Lånevapenbokningar kunde inte räknas för träningslistan");
                }
            }

            var names = new Dictionary<int, string>();
            string NameOf(int? memberId)
            {
                if (memberId is not > 0) return "";
                if (names.TryGetValue(memberId.Value, out var n)) return n;
                n = _memberService.GetById(memberId.Value)?.Name ?? $"Medlem {memberId}";
                names[memberId.Value] = n;
                return n;
            }

            return trainings.Select(t => new TrainingRow
            {
                Id = t.Id,
                ScheduleId = t.ScheduleId,
                Date = t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StartTime = t.StartTime,
                EndTime = t.EndTime,
                Name = t.Name,
                Discipline = t.Discipline,
                DisciplineLabel = ActivityDiscipline.Label(t.Discipline),
                Venue = t.Venue,
                Description = t.Description,
                SkjutledareMemberId = t.SkjutledareMemberId,
                SkjutledareName = NameOf(t.SkjutledareMemberId),
                IsCancelled = t.IsCancelled,
                RegistrationRequired = t.RegistrationRequired,
                MaxParticipants = t.MaxParticipants,
                LoanWeaponsOffered = t.LoanWeaponsOffered,
                IsMandatory = t.IsMandatory,
                SignedUp = counts.TryGetValue(t.Id, out var c) ? c.SignedUp : 0,
                Present = counts.TryGetValue(t.Id, out var c2) ? c2.Present : 0,
                Bookings = bookings.GetValueOrDefault(t.Id)
            }).ToList();
        }

        public class TrainingInput
        {
            public int Id { get; set; }
            public int ClubId { get; set; }
            public string? Name { get; set; }
            public string? Date { get; set; }
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            public string? Discipline { get; set; }
            public string? Venue { get; set; }
            public string? Description { get; set; }
            public int? SkjutledareMemberId { get; set; }
            /// <summary>Strängar ("1"/"0"), eftersom "1" inte binder till bool i ASP.NET Core.</summary>
            public string? RegistrationRequired { get; set; }
            public int? MaxParticipants { get; set; }
            public string? LoanWeaponsOffered { get; set; }
            public string? IsMandatory { get; set; }
        }

        /// <summary>Skapar eller ändrar ETT tillfälle. Returnerar id eller ett fel i klartext.</summary>
        public (int Id, string? Error) Save(TrainingInput input, int actingMemberId)
        {
            var name = (input.Name ?? "").Trim();
            if (name.Length == 0) return (0, "Ange ett namn på träningen.");
            if (!TryDate(input.Date, out var date)) return (0, "Ange ett giltigt datum.");
            if (!TryTime(input.StartTime, out var start)) return (0, "Starttiden ska skrivas som tt:mm.");
            if (!TryTime(input.EndTime, out var end)) return (0, "Sluttiden ska skrivas som tt:mm.");
            if (start != null && end != null && string.CompareOrdinal(end, start) <= 0)
                return (0, "Sluttiden måste vara efter starttiden.");
            if (input.MaxParticipants is < 0) return (0, "Antalet platser kan inte vara negativt.");

            using var db = _databaseFactory.CreateDatabase();
            ClubTraining t;
            if (input.Id > 0)
            {
                t = db.SingleOrDefault<ClubTraining>("WHERE Id = @0", input.Id)!;
                if (t == null) return (0, "Träningen finns inte.");
                if (t.ClubId != input.ClubId) return (0, "Träningen hör till en annan klubb.");
            }
            else
            {
                t = new ClubTraining { ClubId = input.ClubId, CreatedByMemberId = actingMemberId, CreatedDate = DateTime.Now };
            }

            t.Name = name;
            t.Date = date;
            t.StartTime = start;
            t.EndTime = end;
            t.Discipline = NormaliseDiscipline(input.Discipline);
            t.Venue = Blank(input.Venue);
            t.Description = Blank(input.Description);
            t.SkjutledareMemberId = input.SkjutledareMemberId is > 0 ? input.SkjutledareMemberId : null;
            t.RegistrationRequired = IsTrue(input.RegistrationRequired);
            t.MaxParticipants = input.MaxParticipants is > 0 ? input.MaxParticipants : null;
            t.LoanWeaponsOffered = IsTrue(input.LoanWeaponsOffered);
            t.IsMandatory = IsTrue(input.IsMandatory);
            t.UpdatedDate = DateTime.Now;

            if (t.Id > 0) db.Update(t); else db.Insert(t);
            return (t.Id, null);
        }

        public class ScheduleInput
        {
            public int ClubId { get; set; }
            public string? Name { get; set; }
            public string? Discipline { get; set; }
            public string? StartTime { get; set; }
            public string? EndTime { get; set; }
            /// <summary>ISO-veckodagar, 1 = måndag.</summary>
            public List<int>? Weekdays { get; set; }
            public string? From { get; set; }
            public string? To { get; set; }
            public List<BreakInput>? Breaks { get; set; }
            public int? SkjutledareMemberId { get; set; }
            /// <summary>Turas om: medlemmarna i den ordning de tar passen. Tom = samma varje gång.</summary>
            public List<int>? Rotation { get; set; }
            public string? Venue { get; set; }
            public string? RegistrationRequired { get; set; }
            public int? MaxParticipants { get; set; }
            public string? LoanWeaponsOffered { get; set; }
            public string? IsMandatory { get; set; }
        }

        public class BreakInput
        {
            public string? Label { get; set; }
            public string? From { get; set; }
            public string? To { get; set; }
        }

        public class SchedulePlan
        {
            public string? Error { get; set; }
            public int Count { get; set; }
            public string Summary { get; set; } = "";
            public string? FirstDate { get; set; }
            public string? LastDate { get; set; }
            public bool Capped { get; set; }
            internal List<TrainingSchedulePlanner.Occasion> Occasions { get; set; } = new();
            internal List<TrainingSchedulePlanner.DateRange> BreakRanges { get; set; } = new();
            internal DateTime From { get; set; }
            internal DateTime To { get; set; }
            internal List<int> Days { get; set; } = new();
            internal string? Start { get; set; }
            internal string? End { get; set; }
        }

        /// <summary>
        /// Räknar ut vad ett schema skulle skapa, utan att skriva något. Samma funktion används av
        /// förhandsvisningen ("Skapar 32 tillfällen …") och av skapandet, så siffran användaren ser
        /// är exakt den som skapas.
        /// </summary>
        public SchedulePlan Plan(ScheduleInput input)
        {
            var plan = new SchedulePlan();
            if (string.IsNullOrWhiteSpace(input.Name)) { plan.Error = "Ange ett namn på schemat."; return plan; }
            var days = (input.Weekdays ?? new()).Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d).ToList();
            if (days.Count == 0) { plan.Error = "Välj minst en veckodag."; return plan; }
            if (!TryDate(input.From, out var from) || !TryDate(input.To, out var to))
            { plan.Error = "Ange från- och till-datum."; return plan; }
            if (to < from) { plan.Error = "Till-datumet måste vara efter från-datumet."; return plan; }
            if (!TryTime(input.StartTime, out var start) || !TryTime(input.EndTime, out var end))
            { plan.Error = "Tiderna ska skrivas som tt:mm."; return plan; }
            if (start != null && end != null && string.CompareOrdinal(end, start) <= 0)
            { plan.Error = "Sluttiden måste vara efter starttiden."; return plan; }

            var breaks = new List<TrainingSchedulePlanner.DateRange>();
            foreach (var b in input.Breaks ?? new())
            {
                if (!TryDate(b.From, out var bf)) continue;
                var bt = TryDate(b.To, out var x) ? x : bf;
                breaks.Add(new TrainingSchedulePlanner.DateRange(bf, bt < bf ? bf : bt));
            }

            var rotation = (input.Rotation ?? new()).Where(m => m > 0).ToList();
            var occasions = TrainingSchedulePlanner.Expand(from, to, days, breaks,
                input.SkjutledareMemberId is > 0 ? input.SkjutledareMemberId : null,
                rotation.Count > 0 ? rotation : null);

            plan.Occasions = occasions;
            plan.BreakRanges = breaks;
            plan.From = from; plan.To = to; plan.Days = days; plan.Start = start; plan.End = end;
            plan.Count = occasions.Count;
            plan.Capped = occasions.Count >= TrainingSchedulePlanner.MaxOccasions;
            plan.FirstDate = occasions.FirstOrDefault()?.Date.ToString("yyyy-MM-dd");
            plan.LastDate = occasions.LastOrDefault()?.Date.ToString("yyyy-MM-dd");
            plan.Summary = Describe(plan, breaks.Count);
            return plan;
        }

        /// <summary>Skapar schemat och alla dess tillfällen i EN transaktion.</summary>
        public (int ScheduleId, int Created, string? Error) CreateSchedule(ScheduleInput input, int actingMemberId)
        {
            var plan = Plan(input);
            if (plan.Error != null) return (0, 0, plan.Error);
            if (plan.Count == 0) return (0, 0, "Schemat ger inga tillfällen — kontrollera veckodagar, period och uppehåll.");

            var rotation = (input.Rotation ?? new()).Where(m => m > 0).ToList();
            var now = DateTime.Now;
            var schedule = new ClubTrainingSchedule
            {
                ClubId = input.ClubId,
                Name = input.Name!.Trim(),
                Discipline = NormaliseDiscipline(input.Discipline),
                Weekdays = string.Join(",", plan.Days),
                StartTime = plan.Start,
                EndTime = plan.End,
                PeriodFrom = plan.From,
                PeriodTo = plan.To,
                Breaks = plan.BreakRanges.Count == 0 ? null : JsonSerializer.Serialize(
                    (input.Breaks ?? new()).Select(b => new { b.Label, b.From, b.To })),
                DefaultSkjutledareMemberId = input.SkjutledareMemberId is > 0 ? input.SkjutledareMemberId : null,
                RotatingSkjutledare = rotation.Count > 0 ? string.Join(",", rotation) : null,
                Venue = Blank(input.Venue),
                RegistrationRequired = IsTrue(input.RegistrationRequired),
                MaxParticipants = input.MaxParticipants is > 0 ? input.MaxParticipants : null,
                LoanWeaponsOffered = IsTrue(input.LoanWeaponsOffered),
                IsActive = true,
                CreatedByMemberId = actingMemberId,
                CreatedDate = now,
                UpdatedDate = now
            };

            using var db = _databaseFactory.CreateDatabase();
            using (var tx = db.GetTransaction())
            {
                db.Insert(schedule);
                foreach (var o in plan.Occasions)
                {
                    db.Insert(new ClubTraining
                    {
                        ClubId = input.ClubId,
                        ScheduleId = schedule.Id,
                        Date = o.Date,
                        StartTime = plan.Start,
                        EndTime = plan.End,
                        Discipline = schedule.Discipline,
                        Name = schedule.Name,
                        Venue = schedule.Venue,
                        SkjutledareMemberId = o.SkjutledareMemberId,
                        RegistrationRequired = schedule.RegistrationRequired,
                        MaxParticipants = schedule.MaxParticipants,
                        LoanWeaponsOffered = schedule.LoanWeaponsOffered,
                        IsMandatory = IsTrue(input.IsMandatory),
                        CreatedByMemberId = actingMemberId,
                        CreatedDate = now,
                        UpdatedDate = now
                    });
                }
                tx.Complete();
            }
            return (schedule.Id, plan.Count, null);
        }

        /// <summary>Byter skjutledare för ett tillfälle. null = ingen utsedd.</summary>
        public string? SetSkjutledare(int trainingId, int? memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var n = db.Execute("UPDATE dbo.ClubTraining SET SkjutledareMemberId = @1, UpdatedDate = GETDATE() WHERE Id = @0",
                trainingId, memberId is > 0 ? memberId : null);
            return n == 1 ? null : "Träningen finns inte.";
        }

        public string? SetCancelled(int trainingId, bool cancelled)
        {
            using var db = _databaseFactory.CreateDatabase();
            var n = db.Execute("UPDATE dbo.ClubTraining SET IsCancelled = @1, UpdatedDate = GETDATE() WHERE Id = @0",
                trainingId, cancelled);
            return n == 1 ? null : "Träningen finns inte.";
        }

        /// <summary>Raderar ett tillfälle — bara när ingen är anmäld och inget vapen bokat.</summary>
        public string? Delete(int trainingId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var people = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.ClubEventParticipant WHERE OccasionKind = @0 AND EventId = @1",
                ClubEvents.OccasionTraining, trainingId);
            var bookings = 0;
            try
            {
                bookings = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM dbo.FirearmBooking WHERE OccasionKind = @0 AND OccasionId = @1",
                    FirearmOccasionKind.Training, trainingId);
            }
            catch { /* tabellen saknas — inga bokningar */ }
            if (people > 0 || bookings > 0)
                return $"Träningen har {people} deltagare och {bookings} lånevapenbokningar och kan inte tas bort. Ställ in den i stället — då får de som anmält sig se det.";
            var n = db.Execute("DELETE FROM dbo.ClubTraining WHERE Id = @0", trainingId);
            return n == 1 ? null : "Träningen finns inte.";
        }

        private static readonly string[] DayNames = { "", "mån", "tis", "ons", "tor", "fre", "lör", "sön" };

        private static string Describe(SchedulePlan p, int breakCount)
        {
            if (p.Count == 0) return "Schemat ger inga tillfällen.";
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            var days = string.Join(" och ", p.Days.Select(d => DayNames[d]));
            var range = $"{p.From.ToString("d MMM", sv)}–{p.To.ToString("d MMM", sv)}";
            var brk = breakCount == 0 ? "" : breakCount == 1 ? ", utan uppehållet" : $", utan {breakCount} uppehåll";
            var cap = p.Capped ? $" (högst {TrainingSchedulePlanner.MaxOccasions} — kortare period?)" : "";
            return $"Skapar {p.Count} tillfällen ({days}, {range}{brk}){cap}.";
        }

        private static string? NormaliseDiscipline(string? raw)
        {
            var c = ActivityDiscipline.Canonical(raw);
            return c.Length == 0 ? null : c;
        }

        private static bool TryDate(string? s, out DateTime d)
            => DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

        /// <summary>Tom = inget klockslag (giltigt). Annars "H:mm"/"HH:mm" → "HH:mm".</summary>
        private static bool TryTime(string? s, out string? time)
        {
            time = null;
            var v = (s ?? "").Trim();
            if (v.Length == 0) return true;
            if (!TimeSpan.TryParseExact(v, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var ts)) return false;
            if (ts.TotalHours >= 24) return false;
            time = ts.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            return true;
        }

        private static bool IsTrue(string? v)
            => v != null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase));

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
