using HpskSite.Models;
using HpskSite.Models.Training;
using HpskSite.Services.Firearms;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Training
{
    /// <summary>
    /// Fas B3: flyttar klubbarnas gamla träningshändelser (<c>clubSimpleEvent</c>) till
    /// <see cref="ClubTraining"/> — och tillbaka. Regeln för VAD som flyttas bor i
    /// <see cref="TrainingMigrationRules"/>; här bor hur. Ytan är KLUBBENS: Admin → Träningar
    /// (<c>_TrainingMigrationClub.cshtml</c>) — Stefan 2026-10-07: klubben vet vilka "Träning"-händelser
    /// som egentligen är något annat.
    ///
    /// <para><b>Torrkörning är standard.</b> <see cref="Run"/> med <c>apply = false</c> skriver
    /// ingenting och svarar med exakt vad en riktig körning skulle göra, rad för rad.</para>
    ///
    /// <para><b>⚠️ En skarp körning flyttar bara de händelser sajtadmin valt i förhandsgranskningen,
    /// i EN klubb</b> (<see cref="RunSelected"/>). Ett id som inte längre står som "migrera" i den
    /// klubben vägrar HELA anropet innan något skrivs — det som förhandsgranskades är det som körs,
    /// eller ingenting. (Stefan 2026-10-07: "det kan potentiellt bli stora konsekvenser om detta
    /// blir fel".)</para>
    ///
    /// <para><b>⚠️ Idempotent, och det är vad som gör en avbruten körning ofarlig.</b> En händelse
    /// som redan har en träning (<c>LegacyEventNodeId</c>) skapas aldrig igen — kontrollen görs med
    /// <c>UPDLOCK, HOLDLOCK</c> INNE i transaktionen, så två samtidiga körningar (en dubbelklick, en
    /// timeout följd av ett nytt försök) kan inte båda skapa den. Dessutom släpper <see cref="Gate"/>
    /// bara fram en körning i taget. Står noden fortfarande publicerad avpubliceras den — det är det
    /// enda steget som kan ha fallerat efter att transaktionen gick igenom.</para>
    ///
    /// <para><b>Vad som flyttas, i EN transaktion per händelse:</b> träningsraden, deltagarraderna
    /// (<c>OccasionKind = Training</c>, <c>EventId</c> = träningens id) och lånevapenbokningarna.
    /// Går något av det fel blir ingenting av det kvar — en träning utan sina deltagare, eller
    /// deltagare som pekar på en träning som inte finns, är värre än en händelse som står kvar.</para>
    ///
    /// <para><b>⚠️ Händelser med NÅGON rad i liggaren flyttas INTE</b> — betalning, avgift,
    /// verifikation, utkast, projekt eller projektgrupp, i både den riktiga liggaren (<c>dbo</c>) och
    /// sandlådan (<c>sbx</c>). Verifikationer är oföränderliga och projekten nycklade på nodens id;
    /// att flytta deltagarna men inte pengarna hade gett en avprickningslista som inte hittar sina
    /// betalningar. (Fram till 2026-10-07 kontrollerades bara betalningarna.)</para>
    ///
    /// <para><b>Noderna avpubliceras, raderas aldrig</b> (Stefan 2026-10-03). Därför går flytten att
    /// ångra (<see cref="UndoSelected"/>) så länge träningen inte fått något den gamla händelsen inte
    /// kan bära: en ändring, en kurskoppling, kursanteckningar/-serier, en serie eller bokföring.</para>
    /// </summary>
    public class TrainingMigrationService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly ILogger<TrainingMigrationService> _logger;

        /// <summary>En skarp körning (flytt eller ångra) i taget i hela appen.</summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        public TrainingMigrationService(
            IUmbracoDatabaseFactory databaseFactory,
            IContentService contentService,
            ILogger<TrainingMigrationService> logger)
        {
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _logger = logger;
        }

        public const string ActionMigrate = "migrera";
        public const string ActionMigrated = "migrerad";
        public const string ActionUnpublishOnly = "avpublicera";
        public const string ActionAlreadyDone = "klar";
        public const string ActionSkip = "lämnas";
        public const string ActionFailed = "fel";

        public const string ActionRestore = "återställ";
        public const string ActionRestored = "återställd";
        public const string ActionKeep = "behålls";

        public class Row
        {
            public int EventId { get; set; }
            public int ClubId { get; set; }
            public string ClubName { get; set; } = "";
            public string Name { get; set; } = "";
            public string EventType { get; set; } = "";
            public DateTime? Date { get; set; }
            public string Action { get; set; } = "";
            public string Reason { get; set; } = "";
            public string? Discipline { get; set; }
            public int Participants { get; set; }
            public int Bookings { get; set; }
            public int? TrainingId { get; set; }
            public List<string> Notes { get; set; } = new();
            /// <summary>Händelsens dag är i dag eller senare. Klubbens yta förkryssar bara dessa —
            /// en passerad händelse kan stå kvar som historik utan att något går förlorat.</summary>
            public bool Upcoming => Date.HasValue && Date.Value.Date >= DateTime.Today;
            /// <summary>Uppgifter på händelsens egen sida som träningen inte har plats för.</summary>
            public List<string> NotCarried { get; set; } = new();
        }

        /// <summary>Händelsens fält som träningen inte bär, med namnen klubben känner igen.
        /// Uppgifterna raderas inte — de ligger kvar på den avpublicerade händelsen.</summary>
        private static readonly (string Alias, string Label)[] NotCarriedFields =
        {
            ("contactPerson", "kontaktperson"), ("contactEmail", "kontaktens e-post"), ("contactPhone", "kontaktens telefon"),
            ("eventImage", "bild"), ("equipmentRequired", "utrustning"), ("targetAudience", "målgrupp"),
            ("contentBlocks", "extra innehåll på sidan"), ("quickLinks", "snabblänkar"), ("registrationUrl", "extern anmälningslänk"),
        };

        private static List<string> ReadNotCarried(IContent node)
        {
            var list = new List<string>();
            foreach (var (alias, label) in NotCarriedFields)
            {
                if (!node.HasProperty(alias)) continue;
                var v = node.GetValue(alias)?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(v) && v != "[]" && v != "{}" && v != "null") list.Add(label);
            }
            return list;
        }

        /// <summary>Händelser som INTE är träning och står kvar — visas som en summering per typ,
        /// så att en felklassning åt andra hållet (en träning med en ovanlig typ) syns.</summary>
        public class KeptType
        {
            public int ClubId { get; set; }
            public string ClubName { get; set; } = "";
            public string EventType { get; set; } = "";
            public int Count { get; set; }
        }

        public class Result
        {
            public bool Applied { get; set; }
            /// <summary>Satt när en skarp körning vägrades INNAN något skrevs.</summary>
            public string? Refused { get; set; }
            public List<Row> Rows { get; set; } = new();
            public List<KeptType> Kept { get; set; } = new();
            public int ToMigrate => Rows.Count(r => r.Action is ActionMigrate or ActionMigrated);
            public int Skipped => Rows.Count(r => r.Action == ActionSkip);
            public int Failed => Rows.Count(r => r.Action == ActionFailed);
            public int ParticipantsMoved => Rows.Where(r => r.Action is ActionMigrate or ActionMigrated).Sum(r => r.Participants);
            public int BookingsMoved => Rows.Where(r => r.Action is ActionMigrate or ActionMigrated).Sum(r => r.Bookings);
        }

        public class UndoRow
        {
            public int TrainingId { get; set; }
            public int EventId { get; set; }
            public int ClubId { get; set; }
            public string Name { get; set; } = "";
            public DateTime Date { get; set; }
            public string? StartTime { get; set; }
            public string Action { get; set; } = "";
            public string Reason { get; set; } = "";
            public int Participants { get; set; }
            public int Bookings { get; set; }
        }

        public class UndoResult
        {
            public bool Applied { get; set; }
            public string? Refused { get; set; }
            public List<UndoRow> Rows { get; set; } = new();
        }

        private class IdPair
        {
            public int K { get; set; }
            public int V { get; set; }
        }

        private class EventNodeRow
        {
            public int EventId { get; set; }
            public int OwnerId { get; set; }
            public string OwnerType { get; set; } = "";
        }

        // ══ Flytt ══════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Den skarpa vägen från sajtadminens flik: flyttar EXAKT <paramref name="eventIds"/> i EN
        /// klubb. Förhandsgranskar först; står något valt id inte som "migrera" (eller "avpublicera",
        /// en tidigare körning som inte hann avpublicera) i den klubben vägras hela anropet och
        /// ingenting skrivs.
        /// </summary>
        public Result RunSelected(int actingMemberId, int clubId, IReadOnlyCollection<int> eventIds)
        {
            if (clubId <= 0)
                return new Result { Refused = "Välj en klubb — flytten görs en klubb i taget." };
            var wanted = (eventIds ?? Array.Empty<int>()).Where(id => id > 0).ToHashSet();
            if (wanted.Count == 0)
                return new Result { Refused = "Inga händelser valda — ingenting flyttades." };

            if (!Gate.Wait(0))
                return new Result { Refused = "En flytt eller återställning pågår redan. Vänta tills den är klar och förhandsgranska igen." };
            try
            {
                var plan = Run(apply: false, actingMemberId, clubId);
                var runnable = plan.Rows
                    .Where(r => r.ClubId == clubId && r.Action is ActionMigrate or ActionUnpublishOnly)
                    .Select(r => r.EventId).ToHashSet();
                var stale = wanted.Where(id => !runnable.Contains(id)).ToList();
                if (stale.Count > 0)
                    return new Result
                    {
                        Refused = $"{stale.Count} av de valda händelserna kan inte längre flyttas (läget har ändrats sedan förhandsgranskningen). " +
                                  "Ingenting flyttades — förhandsgranska igen."
                    };
                return Run(apply: true, actingMemberId, clubId, wanted);
            }
            finally { Gate.Release(); }
        }

        /// <summary>
        /// Går igenom klubbarnas händelser. <paramref name="apply"/> = false ⇒ torrkörning, ingenting
        /// skrivs. <paramref name="clubId"/> &gt; 0 begränsar till en klubb. <paramref name="onlyEventIds"/>
        /// begränsar till de händelserna. ⚠️ Skarpa körningar går via <see cref="RunSelected"/>.
        /// </summary>
        public Result Run(bool apply, int actingMemberId, int clubId = 0, IReadOnlySet<int>? onlyEventIds = null)
        {
            var result = new Result { Applied = apply };

            List<EventNodeRow> nodes;
            Dictionary<int, int> migrated;   // legacy node id → training id
            Dictionary<int, int> participants;
            Dictionary<int, int> bookings;
            HashSet<int> withLedger;
            using (var db = _databaseFactory.CreateDatabase())
            {
                nodes = db.Fetch<EventNodeRow>(@"
SELECT n.id AS EventId, n.parentId AS OwnerId, pct.alias AS OwnerType
FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id
JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias = 'clubSimpleEvent'
JOIN umbracoNode pn ON pn.id = n.parentId
JOIN umbracoContent pc ON pc.nodeId = pn.id
JOIN cmsContentType pct ON pct.nodeId = pc.contentTypeId
WHERE n.trashed = 0 AND pct.alias = 'club' AND (@0 = 0 OR n.parentId = @0)", clubId);

                migrated = db.Fetch<IdPair>(
                        "SELECT LegacyEventNodeId AS K, Id AS V FROM dbo.ClubTraining WHERE LegacyEventNodeId IS NOT NULL")
                    .ToDictionary(x => x.K, x => x.V);
                participants = db.Fetch<IdPair>(
                        "SELECT EventId AS K, COUNT(*) AS V FROM dbo.ClubEventParticipant WHERE OccasionKind = @0 GROUP BY EventId",
                        ClubEvents.OccasionEvent)
                    .ToDictionary(x => x.K, x => x.V);
                bookings = CountBookings(db);
                withLedger = SourcesInLedger(db, HpskSite.Models.Ledger.LedgerSourceType.Event);
            }
            if (onlyEventIds != null) nodes = nodes.Where(n => onlyEventIds.Contains(n.EventId)).ToList();

            var clubNames = new Dictionary<int, string>();
            var kept = new Dictionary<(int Club, string Type), int>();

            foreach (var n in nodes.OrderBy(n => n.OwnerId).ThenBy(n => n.EventId))
            {
                var node = _contentService.GetById(n.EventId);
                if (node == null) continue;
                var row = Describe(node, n.OwnerId, clubNames);

                // Redan flyttad: bara avpubliceringen kan saknas.
                if (migrated.TryGetValue(n.EventId, out var existingId))
                {
                    row.TrainingId = existingId;
                    if (node.Published)
                    {
                        row.Action = ActionUnpublishOnly;
                        row.Reason = "Redan flyttad men står kvar publicerad";
                        if (apply) TryUnpublish(node, row, actingMemberId);
                    }
                    else
                    {
                        row.Action = ActionAlreadyDone;
                        row.Reason = "Redan flyttad";
                    }
                    result.Rows.Add(row);
                    continue;
                }

                var cls = TrainingMigrationRules.Classify(row.EventType);
                row.Discipline = cls.Discipline;
                if (!cls.Migrate)
                {
                    // Händelser som inte är träning listas inte rad för rad — de är majoriteten och
                    // inget händer med dem. De summeras per typ (bara publicerade; en opublicerad syns
                    // ingenstans ändå).
                    if (node.Published)
                    {
                        var key = (row.ClubId, row.EventType.Trim().Length > 0 ? row.EventType.Trim() : "(ingen typ)");
                        kept[key] = kept.GetValueOrDefault(key) + 1;
                    }
                    continue;
                }

                row.Participants = participants.GetValueOrDefault(n.EventId);
                row.Bookings = bookings.GetValueOrDefault(n.EventId);
                row.NotCarried = ReadNotCarried(node);

                if (!node.Published)
                {
                    row.Action = ActionSkip;
                    row.Reason = "Opublicerad — syns redan inte, lämnas som den är";
                }
                else if (withLedger.Contains(n.EventId))
                {
                    row.Action = ActionSkip;
                    row.Reason = "Har rader i liggaren (betalning, avgift, verifikation eller projekt) — flyttas inte";
                }
                else if (row.Date == null)
                {
                    row.Action = ActionSkip;
                    row.Reason = "Saknar datum — en träning måste ha en dag";
                }
                else
                {
                    row.Action = ActionMigrate;
                    row.Reason = cls.Reason;
                    // Sägs redan i torrkörningen — det är där klubben ser vilka tillfällen som ska
                    // kopplas till en kurs.
                    if (node.GetValue<bool>(ClubEvents.MandatoryProperty))
                        row.Notes.Add("Var markerad som obligatorisk. Det togs inte med — gäller kravet en kurs, koppla träningen till kursen och sätt närvaron som krävd där.");
                    var end = ClubEvents.RealDate(node.GetValue<DateTime?>("eventEndDate"));
                    if (TrainingMigrationRules.SplitTimes(row.Date.Value, end).EndDropped)
                        row.Notes.Add("Sluttiden ligger på en annan dag och tas inte med");
                    if (apply) Migrate(node, row, actingMemberId);
                }
                result.Rows.Add(row);
            }

            result.Kept = kept
                .Select(k => new KeptType { ClubId = k.Key.Club, ClubName = ClubName(k.Key.Club, clubNames), EventType = k.Key.Type, Count = k.Value })
                .OrderBy(k => k.ClubName).ThenByDescending(k => k.Count).ToList();

            if (apply)
                _logger.LogWarning(
                    "Träningsflytt (klubb {Club}, av medlem {Member}): {Migrated} flyttade, {Failed} fel, {P} deltagarrader, {B} lånevapenbokningar. Händelser: {Events}",
                    clubId, actingMemberId, result.Rows.Count(r => r.Action == ActionMigrated), result.Failed,
                    result.ParticipantsMoved, result.BookingsMoved,
                    string.Join(",", result.Rows.Where(r => r.Action == ActionMigrated).Select(r => $"{r.EventId}→{r.TrainingId}")));
            return result;
        }

        private string ClubName(int clubId, Dictionary<int, string> cache)
        {
            if (cache.TryGetValue(clubId, out var name)) return name;
            name = _contentService.GetById(clubId)?.Name ?? $"Klubb {clubId}";
            cache[clubId] = name;
            return name;
        }

        private Row Describe(IContent node, int clubId, Dictionary<int, string> clubNames)
        {
            var start = ClubEvents.RealDate(node.GetValue<DateTime?>("eventDate"));
            return new Row
            {
                EventId = node.Id,
                ClubId = clubId,
                ClubName = ClubName(clubId, clubNames),
                Name = node.GetValue<string>("eventName") is { Length: > 0 } nm ? nm : (node.Name ?? ""),
                EventType = node.GetValue<string>("eventType") ?? "",
                Date = start
            };
        }

        /// <summary>
        /// Bygger träningsraden ur noden och flyttar allt som pekar på händelsen — i EN transaktion.
        /// Avpubliceringen sker EFTER, eftersom den går genom innehållstjänstens egen scope och inte
        /// får köras inne i en annan anslutnings transaktion.
        /// </summary>
        private void Migrate(IContent node, Row row, int actingMemberId)
        {
            var start = row.Date!.Value;
            var end = ClubEvents.RealDate(node.GetValue<DateTime?>("eventEndDate"));
            var (date, startTime, endTime, _) = TrainingMigrationRules.SplitTimes(start, end);

            var maxParticipants = node.GetValue<int>("maxParticipants");
            var rangeId = node.HasProperty("rangeId") ? node.GetValue<int>("rangeId") : 0;
            var now = DateTime.Now;
            var training = new ClubTraining
            {
                ClubId = row.ClubId,
                Date = date,
                StartTime = startTime,
                EndTime = endTime,
                Discipline = row.Discipline,
                Name = row.Name.Length > 0 ? row.Name : "Träning",
                Venue = Blank(node.GetValue<string>("venue")),
                RangeId = rangeId > 0 ? rangeId : null,
                Description = Blank(node.GetValue<string>("description")),
                IsCancelled = false,
                RegistrationRequired = node.GetValue<bool>("registrationRequired"),
                MaxParticipants = maxParticipants > 0 ? maxParticipants : null,
                RegistrationDeadline = ClubEvents.RealDate(node.GetValue<DateTime?>(ClubEvents.DeadlineProperty)),
                Prices = Blank(node.GetValue<string>(EventPrices.Property)),
                // Bara händelsens EGET nummer — ett tomt fält ärver klubbens, precis som förut.
                SwishNumber = Blank(node.GetValue<string>(ClubEvents.SwishProperty)),
                Audience = Blank(node.GetValue<string>(EventAudience.Property)),
                LoanWeaponsOffered = node.GetValue<bool>(LoanWeaponClubRules.EventOfferedProperty),
                // En träning är aldrig obligatorisk för alla (Stefan 2026-10-06). Flaggan förs inte
                // över; en kurs krav sätts på kursens koppling — se noten i torrkörningen.
                IsMandatory = false,
                LegacyEventNodeId = node.Id,
                CreatedByMemberId = actingMemberId,
                CreatedDate = now,
                UpdatedDate = now
            };

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                using (var tx = db.GetTransaction())
                {
                    // ⚠️ Kontrollen MÅSTE ligga inne i transaktionen med lås — annars kan två
                    // samtidiga körningar båda se "inte flyttad" och skapa var sin träning, varav
                    // den andra står utan deltagare.
                    var already = db.ExecuteScalar<int?>(
                        "SELECT TOP 1 Id FROM dbo.ClubTraining WITH (UPDLOCK, HOLDLOCK) WHERE LegacyEventNodeId = @0", node.Id);
                    if (already is > 0)
                    {
                        row.TrainingId = already;
                        row.Action = ActionAlreadyDone;
                        row.Reason = "Flyttades redan av en annan körning";
                        return;   // tx utan Complete = ingenting skrivet
                    }
                    db.Insert(training);
                    row.Participants = db.Execute(
                        "UPDATE dbo.ClubEventParticipant SET OccasionKind = @0, EventId = @1 WHERE OccasionKind = @2 AND EventId = @3",
                        ClubEvents.OccasionTraining, training.Id, ClubEvents.OccasionEvent, node.Id);
                    if (TableExists(db, "dbo", "FirearmBooking"))
                        row.Bookings = db.Execute(
                            "UPDATE dbo.FirearmBooking SET OccasionKind = @0, OccasionId = @1 WHERE OccasionKind = @2 AND OccasionId = @3",
                            FirearmOccasionKind.Training, training.Id, FirearmOccasionKind.Event, node.Id);
                    tx.Complete();
                }
                row.TrainingId = training.Id;
                row.Action = ActionMigrated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Träningsflytt: händelse {Event} kunde inte flyttas", node.Id);
                row.Action = ActionFailed;
                row.Reason = "Kunde inte flyttas: " + ex.Message;
                return;
            }

            TryUnpublish(node, row, actingMemberId);
        }

        private void TryUnpublish(IContent node, Row row, int actingMemberId)
        {
            try
            {
                var r = _contentService.Unpublish(node);
                if (!r.Success)
                    row.Notes.Add("Den gamla händelsen kunde inte avpubliceras — förhandsgranska och flytta igen, så görs bara det steget");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Träningsflytt: händelse {Event} kunde inte avpubliceras", node.Id);
                row.Notes.Add("Den gamla händelsen kunde inte avpubliceras — förhandsgranska och flytta igen, så görs bara det steget");
            }
        }

        // ══ Ångra ══════════════════════════════════════════════════════════════════════════════

        private class UndoCandidate
        {
            public int Id { get; set; }
            public int ClubId { get; set; }
            public int LegacyEventNodeId { get; set; }
            public string Name { get; set; } = "";
            public DateTime Date { get; set; }
            public string? StartTime { get; set; }
            public int? ScheduleId { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime UpdatedDate { get; set; }
        }

        /// <summary>
        /// Vilka flyttade träningar i klubben som går att ångra, och varför de andra inte gör det.
        /// En träning som fått något den gamla händelsen inte kan bära behålls — att ångra den hade
        /// tappat det tyst.
        /// </summary>
        public UndoResult PreviewUndo(int clubId)
        {
            var result = new UndoResult();
            if (clubId <= 0) { result.Refused = "Välj en klubb."; return result; }
            using var db = _databaseFactory.CreateDatabase();
            var trainings = db.Fetch<UndoCandidate>(@"
SELECT Id, ClubId, LegacyEventNodeId, Name, [Date], StartTime, ScheduleId, CreatedDate, UpdatedDate
FROM dbo.ClubTraining WHERE ClubId = @0 AND LegacyEventNodeId IS NOT NULL ORDER BY [Date], StartTime, Id", clubId);
            if (trainings.Count == 0) return result;

            var ids = trainings.Select(t => t.Id).ToList();
            var parts = db.Fetch<IdPair>(
                    "SELECT EventId AS K, COUNT(*) AS V FROM dbo.ClubEventParticipant WHERE OccasionKind = @0 GROUP BY EventId",
                    ClubEvents.OccasionTraining).ToDictionary(x => x.K, x => x.V);
            var books = TableExists(db, "dbo", "FirearmBooking")
                ? db.Fetch<IdPair>("SELECT OccasionId AS K, COUNT(*) AS V FROM dbo.FirearmBooking WHERE OccasionKind = @0 GROUP BY OccasionId",
                    FirearmOccasionKind.Training).ToDictionary(x => x.K, x => x.V)
                : new Dictionary<int, int>();
            var ledger = SourcesInLedger(db, HpskSite.Models.Ledger.LedgerSourceType.Training);
            var courseLinked = IdsIn(db, "TrainingGroupTraining");
            var courseNotes = IdsIn(db, "TrainingCourseNote");
            var courseSeries = IdsIn(db, "TrainingCourseSeries");

            foreach (var t in trainings)
            {
                var row = new UndoRow
                {
                    TrainingId = t.Id, EventId = t.LegacyEventNodeId, ClubId = t.ClubId, Name = t.Name,
                    Date = t.Date, StartTime = t.StartTime,
                    Participants = parts.GetValueOrDefault(t.Id), Bookings = books.GetValueOrDefault(t.Id)
                };
                var reason = KeepReason(t, courseLinked, courseNotes, courseSeries, ledger);
                if (reason == null)
                {
                    var node = _contentService.GetById(t.LegacyEventNodeId);
                    if (node == null || node.Trashed) reason = "Den gamla händelsen finns inte längre";
                }
                row.Action = reason == null ? ActionRestore : ActionKeep;
                row.Reason = reason ?? "Blir en händelse igen, med sina anmälda och lånevapen";
                result.Rows.Add(row);
            }
            return result;
        }

        private static string? KeepReason(UndoCandidate t, HashSet<int> courseLinked, HashSet<int> courseNotes,
                                          HashSet<int> courseSeries, HashSet<int> ledger)
        {
            if (courseLinked.Contains(t.Id)) return "Kopplad till en kurs";
            if (courseNotes.Contains(t.Id) || courseSeries.Contains(t.Id)) return "Har kursanteckningar eller kursserier";
            if (ledger.Contains(t.Id)) return "Har rader i liggaren som träning";
            if (t.ScheduleId != null) return "Ingår i en serie (har kopierats eller är en kopia)";
            // Ändring av skjutledare, inställd och redigering sätter UpdatedDate. Några sekunders
            // marginal: flytten sätter båda till samma ögonblick.
            if (t.UpdatedDate > t.CreatedDate.AddSeconds(5)) return "Har ändrats efter flytten — en återställning skulle tappa ändringen";
            return null;
        }

        /// <summary>
        /// Ångrar flytten för EXAKT <paramref name="trainingIds"/> i EN klubb: händelsen publiceras
        /// igen, deltagarna och lånevapnen flyttas tillbaka och träningsraden tas bort. Samma regel som
        /// flytten — står något valt id inte som "återställ" vägras hela anropet.
        /// <para>Ordningen är medveten: händelsen publiceras FÖRST. Misslyckas publiceringen rörs
        /// ingenting. Misslyckas sedan databasdelen avpubliceras händelsen igen. Det värsta som kan
        /// stå kvar är alltså en träning och en händelse samtidigt — aldrig ingenting.</para>
        /// </summary>
        public UndoResult UndoSelected(int actingMemberId, int clubId, IReadOnlyCollection<int> trainingIds)
        {
            if (clubId <= 0) return new UndoResult { Refused = "Välj en klubb." };
            var wanted = (trainingIds ?? Array.Empty<int>()).Where(id => id > 0).ToHashSet();
            if (wanted.Count == 0) return new UndoResult { Refused = "Inga träningar valda — ingenting återställdes." };
            if (!Gate.Wait(0))
                return new UndoResult { Refused = "En flytt eller återställning pågår redan. Vänta tills den är klar och förhandsgranska igen." };
            try
            {
                var plan = PreviewUndo(clubId);
                var runnable = plan.Rows.Where(r => r.Action == ActionRestore).Select(r => r.TrainingId).ToHashSet();
                var stale = wanted.Where(id => !runnable.Contains(id)).ToList();
                if (stale.Count > 0)
                    return new UndoResult
                    {
                        Refused = $"{stale.Count} av de valda träningarna kan inte längre återställas (läget har ändrats). Ingenting återställdes — förhandsgranska igen."
                    };

                var result = new UndoResult { Applied = true };
                foreach (var row in plan.Rows.Where(r => wanted.Contains(r.TrainingId)))
                {
                    Restore(row, actingMemberId);
                    result.Rows.Add(row);
                }
                _logger.LogWarning("Träningsflytt ÅNGRAD (klubb {Club}, av medlem {Member}): {Done} återställda, {Failed} fel. Träningar: {Rows}",
                    clubId, actingMemberId, result.Rows.Count(r => r.Action == ActionRestored), result.Rows.Count(r => r.Action == ActionFailed),
                    string.Join(",", result.Rows.Where(r => r.Action == ActionRestored).Select(r => $"{r.TrainingId}→{r.EventId}")));
                return result;
            }
            finally { Gate.Release(); }
        }

        private void Restore(UndoRow row, int actingMemberId)
        {
            var node = _contentService.GetById(row.EventId);
            if (node == null || node.Trashed) { row.Action = ActionFailed; row.Reason = "Den gamla händelsen finns inte längre"; return; }

            try
            {
                var pub = _contentService.Publish(node, new[] { "*" }, actingMemberId > 0 ? -1 : -1);
                if (!pub.Success)
                {
                    row.Action = ActionFailed;
                    row.Reason = "Händelsen kunde inte publiceras igen (" + pub.Result + ") — ingenting ändrades";
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ångra träningsflytt: händelse {Event} kunde inte publiceras", row.EventId);
                row.Action = ActionFailed; row.Reason = "Händelsen kunde inte publiceras igen — ingenting ändrades"; return;
            }

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                using (var tx = db.GetTransaction())
                {
                    // Läs om under lås och pröva samma regel igen — något kan ha hänt sedan planen.
                    var t = db.SingleOrDefault<UndoCandidate>(@"
SELECT Id, ClubId, LegacyEventNodeId, Name, [Date], StartTime, ScheduleId, CreatedDate, UpdatedDate
FROM dbo.ClubTraining WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0 AND LegacyEventNodeId = @1", row.TrainingId, row.EventId);
                    var reason = t == null ? "Träningen finns inte längre"
                        : KeepReason(t, IdsIn(db, "TrainingGroupTraining", t.Id), IdsIn(db, "TrainingCourseNote", t.Id),
                                     IdsIn(db, "TrainingCourseSeries", t.Id),
                                     SourcesInLedger(db, HpskSite.Models.Ledger.LedgerSourceType.Training));
                    if (reason != null) throw new InvalidOperationException(reason);

                    row.Participants = db.Execute(
                        "UPDATE dbo.ClubEventParticipant SET OccasionKind = @0, EventId = @1 WHERE OccasionKind = @2 AND EventId = @3",
                        ClubEvents.OccasionEvent, row.EventId, ClubEvents.OccasionTraining, row.TrainingId);
                    if (TableExists(db, "dbo", "FirearmBooking"))
                        row.Bookings = db.Execute(
                            "UPDATE dbo.FirearmBooking SET OccasionKind = @0, OccasionId = @1 WHERE OccasionKind = @2 AND OccasionId = @3",
                            FirearmOccasionKind.Event, row.EventId, FirearmOccasionKind.Training, row.TrainingId);
                    db.Execute("DELETE FROM dbo.ClubTraining WHERE Id = @0", row.TrainingId);
                    tx.Complete();
                }
                row.Action = ActionRestored;
                row.Reason = "Är en händelse igen";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ångra träningsflytt: träning {Training} kunde inte återställas", row.TrainingId);
                row.Action = ActionFailed;
                row.Reason = "Kunde inte återställas: " + ex.Message + " — träningen står kvar";
                try { _contentService.Unpublish(node); }
                catch (Exception ex2)
                {
                    _logger.LogError(ex2, "Ångra träningsflytt: händelse {Event} kunde inte avpubliceras igen", row.EventId);
                    row.Reason += ". ⚠️ Händelsen är publicerad igen samtidigt som träningen finns — avpublicera händelsen i backoffice";
                }
            }
        }

        // ══ Hjälp ══════════════════════════════════════════════════════════════════════════════

        private static Dictionary<int, int> CountBookings(IUmbracoDatabase db)
        {
            if (!TableExists(db, "dbo", "FirearmBooking")) return new();
            return db.Fetch<IdPair>(
                    "SELECT OccasionId AS K, COUNT(*) AS V FROM dbo.FirearmBooking WHERE OccasionKind = @0 GROUP BY OccasionId",
                    FirearmOccasionKind.Event)
                .ToDictionary(x => x.K, x => x.V);
        }

        /// <summary>Liggarens tabeller som bär (SourceType, SourceId) — i båda schemana.</summary>
        private static readonly string[] LedgerTables =
            { "LedgerPayment", "LedgerCharge", "LedgerJournalEntry", "LedgerJournalEntryDraft", "LedgerProject", "LedgerProjectGroup" };
        private static readonly string[] LedgerSchemas = { "dbo", "sbx" };

        /// <summary>Alla källid:n av <paramref name="sourceType"/> som har NÅGON rad i liggaren.</summary>
        private static HashSet<int> SourcesInLedger(IUmbracoDatabase db, string sourceType)
        {
            var ids = new HashSet<int>();
            foreach (var schema in LedgerSchemas)
                foreach (var table in LedgerTables)
                    if (TableExists(db, schema, table) && ColumnExists(db, schema, table, "SourceType") && ColumnExists(db, schema, table, "SourceId"))
                        foreach (var id in db.Fetch<int>(
                                     $"SELECT DISTINCT SourceId FROM [{schema}].[{table}] WHERE SourceType = @0 AND SourceId IS NOT NULL", sourceType))
                            ids.Add(id);
            return ids;
        }

        /// <summary>TrainingId:n i en kurstabell (eller bara <paramref name="onlyId"/>).</summary>
        private static HashSet<int> IdsIn(IUmbracoDatabase db, string table, int onlyId = 0)
        {
            if (!TableExists(db, "dbo", table)) return new();
            return db.Fetch<int>($"SELECT DISTINCT TrainingId FROM dbo.[{table}] WHERE @0 = 0 OR TrainingId = @0", onlyId).ToHashSet();
        }

        private static bool TableExists(IUmbracoDatabase db, string schema, string table)
            => db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", schema + "." + table) == 1;

        private static bool ColumnExists(IUmbracoDatabase db, string schema, string table, string column)
            => db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = @0 AND TABLE_NAME = @1 AND COLUMN_NAME = @2",
                schema, table, column) > 0;

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
