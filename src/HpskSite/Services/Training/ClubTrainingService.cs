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
            /// <summary>Kommande tillfällen i samma serie EFTER det här — styr frågan "bara den här eller alla kommande".</summary>
            public int FollowingInSeries { get; set; }
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

        /// <summary>
        /// Datumen för alla klubbars träningar från <paramref name="from"/>, per klubb — för
        /// statistiken, som räknar träningar intill klubbens händelser. Inställda räknas inte: de
        /// ägde aldrig rum. EN fråga för alla klubbar. Fel ger en tom ordbok — statistiken ska visas
        /// även i en miljö där tabellen saknas.
        /// </summary>
        public Dictionary<int, List<DateTime>> DatesByClub(DateTime from)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.Fetch<ClubTraining>("WHERE [Date] >= @0 AND IsCancelled = 0", from.Date)
                    .GroupBy(t => t.ClubId)
                    .ToDictionary(g => g.Key, g => g.Select(t => t.Date).ToList());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Träningarnas datum kunde inte läsas för statistiken");
                return new();
            }
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

            // Serien = tillfällen med samma ScheduleId. Räknas mot ALLA kommande tillfällen, inte bara
            // de i listans fönster — frågan gäller resten av säsongen.
            var seriesDates = db.Fetch<ClubTraining>(
                "WHERE ClubId = @0 AND ScheduleId IS NOT NULL AND [Date] >= @1", clubId, DateTime.Today)
                .GroupBy(x => x.ScheduleId!.Value).ToDictionary(g => g.Key, g => g.Select(x => x.Date.Date).ToList());
            int Following(ClubTraining t) => t.ScheduleId is int sid && seriesDates.TryGetValue(sid, out var ds)
                ? ds.Count(d => d > t.Date.Date) : 0;

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
                Bookings = bookings.GetValueOrDefault(t.Id),
                FollowingInSeries = Following(t)
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
            /// <summary>"following" = för över ändringen till seriens kommande tillfällen (kontrollern gör det).</summary>
            public string? Scope { get; set; }
        }

        /// <summary>Aktiva lånebara klubbvapen. 0 vid fel — dialogen varnar hellre för mycket än tiger.</summary>
        public int LoanableWeaponCount(int clubId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.Firearm WHERE ScopeKind = 'Club' AND ScopeId = @0 AND IsLoanable = 1 AND IsActive = 1", clubId);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Lånebara vapen kunde inte räknas"); return 0; }
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
            if (IsTrue(input.LoanWeaponsOffered) && !IsTrue(input.RegistrationRequired))
                return (0, "Lånevapen bokas när man anmäler sig — kryssa i Anmälan krävs, eller ta bort Lånevapen erbjuds.");

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

        // ── Kopiera en träning (ersätter "Nytt träningsschema", Stefan 2026-10-05) ──────────────────
        // Man skapar först EN träning och kopierar den sedan: till en period, eller dag för dag i
        // kalendern. Kopiorna hör till samma serie (ScheduleId), så en ändring kan gälla "den här och
        // alla kommande". Ingen ny tabell: en schemarad blir seriens nyckel.

        /// <summary>Ser till att träningen hör till en serie och returnerar seriens id.</summary>
        private int EnsureSeries(IUmbracoDatabase db, ClubTraining source, int actingMemberId)
        {
            if (source.ScheduleId is > 0) return source.ScheduleId.Value;
            var now = DateTime.Now;
            var iso = ((int)source.Date.DayOfWeek + 6) % 7 + 1;
            var schedule = new ClubTrainingSchedule
            {
                ClubId = source.ClubId, Name = source.Name, Discipline = source.Discipline,
                Weekdays = iso.ToString(CultureInfo.InvariantCulture),
                StartTime = source.StartTime, EndTime = source.EndTime,
                PeriodFrom = source.Date.Date, PeriodTo = source.Date.Date,
                DefaultSkjutledareMemberId = source.SkjutledareMemberId, Venue = source.Venue, RangeId = source.RangeId,
                RegistrationRequired = source.RegistrationRequired, MaxParticipants = source.MaxParticipants,
                LoanWeaponsOffered = source.LoanWeaponsOffered, IsActive = true,
                CreatedByMemberId = actingMemberId, CreatedDate = now, UpdatedDate = now
            };
            db.Insert(schedule);
            db.Execute("UPDATE dbo.ClubTraining SET ScheduleId = @1 WHERE Id = @0", source.Id, schedule.Id);
            source.ScheduleId = schedule.Id;
            return schedule.Id;
        }

        public class CopyResult
        {
            public List<int> CreatedIds { get; set; } = new();
            public List<string> CreatedDates { get; set; } = new();
            /// <summary>Datum där serien redan har ett tillfälle — de hoppas över, aldrig dubbleras.</summary>
            public List<string> SkippedDates { get; set; } = new();
            public string? Error { get; set; }
        }

        /// <summary>
        /// Kopierar träningen till de angivna datumen. Allt följer med utom datumet: namn, tider, gren,
        /// plats, skjutledare (om inget annat anges per datum), anmälan, platser, priser, lånevapen och
        /// obligatoriskt. Sista anmälningsdag flyttas lika många dagar som träningen. En transaktion.
        /// </summary>
        public CopyResult CopyTo(int sourceId, IEnumerable<(DateTime Date, int? Skjutledare)> targets, int actingMemberId)
        {
            var result = new CopyResult();
            using var db = _databaseFactory.CreateDatabase();
            var src = db.SingleOrDefault<ClubTraining>("WHERE Id = @0", sourceId);
            if (src == null) { result.Error = "Träningen finns inte."; return result; }
            var list = targets.GroupBy(x => x.Date.Date).Select(g => g.First()).OrderBy(x => x.Date).ToList();
            if (list.Count == 0) { result.Error = "Inga datum valda."; return result; }
            if (list.Count > TrainingSchedulePlanner.MaxOccasions)
            { result.Error = $"Högst {TrainingSchedulePlanner.MaxOccasions} tillfällen åt gången."; return result; }

            using var tx = db.GetTransaction();
            var seriesId = EnsureSeries(db, src, actingMemberId);
            var taken = db.Fetch<DateTime>("SELECT [Date] FROM dbo.ClubTraining WHERE ScheduleId = @0", seriesId)
                .Select(d => d.Date).ToHashSet();
            var now = DateTime.Now;
            foreach (var (date, leader) in list)
            {
                var day = date.Date;
                if (taken.Contains(day)) { result.SkippedDates.Add(day.ToString("yyyy-MM-dd")); continue; }
                var shift = (day - src.Date.Date).Days;
                var copy = new ClubTraining
                {
                    ClubId = src.ClubId, ScheduleId = seriesId, Date = day,
                    StartTime = src.StartTime, EndTime = src.EndTime, Discipline = src.Discipline, Name = src.Name,
                    Venue = src.Venue, RangeId = src.RangeId,
                    SkjutledareMemberId = leader is > 0 ? leader : src.SkjutledareMemberId,
                    Description = src.Description, IsCancelled = false,
                    RegistrationRequired = src.RegistrationRequired, MaxParticipants = src.MaxParticipants,
                    RegistrationDeadline = src.RegistrationDeadline?.AddDays(shift),
                    Prices = src.Prices, SwishNumber = src.SwishNumber, Audience = src.Audience,
                    LoanWeaponsOffered = src.LoanWeaponsOffered, IsMandatory = src.IsMandatory,
                    CreatedByMemberId = actingMemberId, CreatedDate = now, UpdatedDate = now
                };
                db.Insert(copy);
                taken.Add(day);
                result.CreatedIds.Add(copy.Id);
                result.CreatedDates.Add(day.ToString("yyyy-MM-dd"));
            }
            // Seriens period följer tillfällena (informativt — inget läser den som regel).
            db.Execute(@"UPDATE s SET PeriodFrom = x.f, PeriodTo = x.t, UpdatedDate = GETDATE()
FROM dbo.ClubTrainingSchedule s CROSS APPLY (SELECT MIN([Date]) f, MAX([Date]) t FROM dbo.ClubTraining WHERE ScheduleId = s.Id) x
WHERE s.Id = @0", seriesId);
            tx.Complete();
            return result;
        }

        public class CopyPeriodInput
        {
            public int SourceId { get; set; }
            /// <summary>ISO-veckodagar, 1 = måndag.</summary>
            public List<int>? Weekdays { get; set; }
            public string? From { get; set; }
            public string? To { get; set; }
            public List<BreakInput>? Breaks { get; set; }
            /// <summary>Turas om. Tom = förlagans skjutledare varje gång.</summary>
            public List<int>? Rotation { get; set; }
        }

        /// <summary>Räknar ut datumen för "Kopiera till en period" — samma funktion för förhandsvisning och skapande.</summary>
        public (List<TrainingSchedulePlanner.Occasion> Occasions, List<string> AlreadyInSeries, string? Error) PlanCopyPeriod(CopyPeriodInput input)
        {
            var src = Get(input.SourceId);
            if (src == null) return (new(), new(), "Träningen finns inte.");
            var days = (input.Weekdays ?? new()).Where(d => d is >= 1 and <= 7).Distinct().OrderBy(d => d).ToList();
            if (days.Count == 0) return (new(), new(), "Välj minst en veckodag.");
            if (!TryDate(input.From, out var from) || !TryDate(input.To, out var to)) return (new(), new(), "Ange från- och till-datum.");
            if (to < from) return (new(), new(), "Till-datumet måste vara efter från-datumet.");
            var breaks = new List<TrainingSchedulePlanner.DateRange>();
            foreach (var b in input.Breaks ?? new())
            {
                if (!TryDate(b.From, out var bf)) continue;
                var bt = TryDate(b.To, out var x) ? x : bf;
                breaks.Add(new TrainingSchedulePlanner.DateRange(bf, bt < bf ? bf : bt));
            }
            var rotation = (input.Rotation ?? new()).Where(m => m > 0).ToList();
            var occ = TrainingSchedulePlanner.Expand(from, to, days, breaks, src.SkjutledareMemberId, rotation.Count > 0 ? rotation : null);
            var already = new List<string>();
            if (src.ScheduleId is > 0)
            {
                using var db = _databaseFactory.CreateDatabase();
                var taken = db.Fetch<DateTime>("SELECT [Date] FROM dbo.ClubTraining WHERE ScheduleId = @0", src.ScheduleId)
                    .Select(d => d.Date).ToHashSet();
                already = occ.Where(o => taken.Contains(o.Date.Date)).Select(o => o.Date.ToString("yyyy-MM-dd")).ToList();
            }
            else already = occ.Where(o => o.Date.Date == src.Date.Date).Select(o => o.Date.ToString("yyyy-MM-dd")).ToList();
            return (occ, already, null);
        }

        /// <summary>
        /// "Den här och alla kommande": för över det som beskriver träningen till seriens senare,
        /// ännu inte passerade tillfällen. Datum, skjutledare och inställt-läge är per tillfälle och
        /// rörs aldrig; passerade tillfällen rörs aldrig. Returnerar antalet ändrade.
        /// </summary>
        public int ApplyToFollowing(int trainingId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var t = db.SingleOrDefault<ClubTraining>("WHERE Id = @0", trainingId);
            if (t?.ScheduleId is not > 0) return 0;
            return db.Execute(@"UPDATE dbo.ClubTraining SET Name = @2, StartTime = @3, EndTime = @4, Discipline = @5,
    Venue = @6, Description = @7, RegistrationRequired = @8, MaxParticipants = @9, LoanWeaponsOffered = @10,
    IsMandatory = @11, UpdatedDate = GETDATE()
WHERE ScheduleId = @0 AND [Date] > @1 AND [Date] >= CAST(GETDATE() AS date)",
                t.ScheduleId, t.Date.Date, t.Name, t.StartTime, t.EndTime, t.Discipline, t.Venue, t.Description,
                t.RegistrationRequired, t.MaxParticipants, t.LoanWeaponsOffered, t.IsMandatory);
        }

        /// <summary>Klubbens träningar att kopiera från: en rad per serie (senaste tillfället) och varje fristående träning.</summary>
        public List<TrainingRow> CopySources(int clubId)
        {
            var all = List(clubId, DateTime.Today.AddMonths(-6), DateTime.Today.AddYears(2));
            return all.GroupBy(r => r.ScheduleId is > 0 ? "s" + r.ScheduleId : "t" + r.Id)
                .Select(g => g.OrderBy(r => r.Date).Last())
                .OrderBy(r => r.Name, StringComparer.Create(new CultureInfo("sv-SE"), true)).ToList();
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

        public class DeleteManyResult
        {
            public List<int> DeletedIds { get; } = new();
            /// <summary>Tillfällen som inte togs bort, med skälet (anmälda/lånevapen) i klartext.</summary>
            public List<string> Blocked { get; } = new();
        }

        /// <summary>
        /// Tar bort flera träningar i en klubb. Samma regel som <see cref="Delete"/>: ett tillfälle med
        /// deltagare eller lånevapenbokningar tas aldrig bort — det hoppas över och namnges, så att de
        /// andra ändå går. Id:n utanför klubben ignoreras (anroparen har prövat behörigheten för klubben).
        /// En serie som blir tom tas bort med sina tillfällen.
        /// </summary>
        public DeleteManyResult DeleteMany(int clubId, IEnumerable<int> ids)
        {
            var result = new DeleteManyResult();
            var wanted = ids.Where(i => i > 0).Distinct().ToList();
            if (wanted.Count == 0) return result;
            using var db = _databaseFactory.CreateDatabase();
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            var schedules = new HashSet<int>();
            // Chunkat: IN (@0) tar slut kring 2100 parametrar och gör det tyst.
            foreach (var chunk in wanted.Chunk(500))
            {
                var rows = db.Fetch<ClubTraining>("WHERE ClubId = @0 AND Id IN (@1)", clubId, chunk);
                foreach (var t in rows.OrderBy(r => r.Date))
                {
                    var error = Delete(t.Id);
                    if (error == null)
                    {
                        result.DeletedIds.Add(t.Id);
                        if (t.ScheduleId is int sid) schedules.Add(sid);
                    }
                    else result.Blocked.Add($"{t.Date.ToString("ddd d MMM", sv)} {t.Name}: {error}");
                }
            }
            foreach (var sid in schedules)
            {
                try
                {
                    db.Execute(@"DELETE FROM dbo.ClubTrainingSchedule WHERE Id = @0
                                 AND NOT EXISTS (SELECT 1 FROM dbo.ClubTraining WHERE ScheduleId = @0)", sid);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Tom träningsserie {Id} kunde inte tas bort", sid); }
            }
            return result;
        }

        /// <summary>Id:n i träningens serie — alla, eller bara från och med träningen.</summary>
        public List<int> SeriesIds(int trainingId, bool fromThisOn)
        {
            var t = Get(trainingId);
            if (t == null) return new();
            if (t.ScheduleId is not int sid) return new() { t.Id };
            using var db = _databaseFactory.CreateDatabase();
            return fromThisOn
                ? db.Fetch<int>("SELECT Id FROM dbo.ClubTraining WHERE ScheduleId = @0 AND [Date] >= @1", sid, t.Date.Date)
                : db.Fetch<int>("SELECT Id FROM dbo.ClubTraining WHERE ScheduleId = @0", sid);
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
