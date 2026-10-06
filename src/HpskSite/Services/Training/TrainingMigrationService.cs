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
    /// <see cref="ClubTraining"/>. Regeln för VAD som flyttas bor i
    /// <see cref="TrainingMigrationRules"/>; här bor hur.
    ///
    /// <para><b>Torrkörning är standard.</b> <see cref="RunAsync"/> med <c>apply = false</c> skriver
    /// ingenting och svarar med exakt vad en riktig körning skulle göra, rad för rad.</para>
    ///
    /// <para><b>⚠️ Idempotent, och det är vad som gör en avbruten körning ofarlig.</b> En händelse
    /// som redan har en träning (<c>LegacyEventNodeId</c>) skapas aldrig igen. Står den noden
    /// fortfarande publicerad avpubliceras den — det är det enda steget som kan ha fallerat efter
    /// att transaktionen gick igenom.</para>
    ///
    /// <para><b>Vad som flyttas, i EN transaktion per händelse:</b> träningsraden, deltagarraderna
    /// (<c>OccasionKind = Training</c>, <c>EventId</c> = träningens id) och lånevapenbokningarna.
    /// Går något av det fel blir ingenting av det kvar — en träning utan sina deltagare, eller
    /// deltagare som pekar på en träning som inte finns, är värre än en händelse som står kvar.</para>
    ///
    /// <para><b>⚠️ Händelser med betalningar i liggaren flyttas INTE.</b> Verifikationer är
    /// oföränderliga och projekten nycklade på nodens id; att flytta deltagarna men inte pengarna
    /// hade gett en avprickningslista som inte hittar sina betalningar. Torrkörningen i prod
    /// 2026-10-05 hittade 0 sådana — regeln finns för att det ska förbli sant, inte för att det
    /// är vanligt.</para>
    ///
    /// <para><b>Noderna avpubliceras, raderas aldrig</b> (Stefan 2026-10-03): de gamla URL:erna
    /// spelar ingen roll, men innehållet är kvar om något behöver läsas i efterhand.</para>
    /// </summary>
    public class TrainingMigrationService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly ILogger<TrainingMigrationService> _logger;

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
        }

        public class Result
        {
            public bool Applied { get; set; }
            public List<Row> Rows { get; set; } = new();
            public int ToMigrate => Rows.Count(r => r.Action is ActionMigrate or ActionMigrated);
            public int Skipped => Rows.Count(r => r.Action == ActionSkip);
            public int Failed => Rows.Count(r => r.Action == ActionFailed);
            public int ParticipantsMoved => Rows.Where(r => r.Action is ActionMigrate or ActionMigrated).Sum(r => r.Participants);
            public int BookingsMoved => Rows.Where(r => r.Action is ActionMigrate or ActionMigrated).Sum(r => r.Bookings);
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

        /// <summary>
        /// Går igenom klubbarnas händelser. <paramref name="apply"/> = false ⇒ torrkörning, ingenting
        /// skrivs. <paramref name="clubId"/> &gt; 0 begränsar till en klubb (för att kunna prova på EN
        /// klubb först).
        /// </summary>
        public Result Run(bool apply, int actingMemberId, int clubId = 0)
        {
            var result = new Result { Applied = apply };

            List<EventNodeRow> nodes;
            Dictionary<int, int> migrated;   // legacy node id → training id
            Dictionary<int, int> participants;
            Dictionary<int, int> bookings;
            HashSet<int> withPayments;
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
                withPayments = EventsWithPayments(db);
            }

            foreach (var n in nodes.OrderBy(n => n.OwnerId).ThenBy(n => n.EventId))
            {
                var node = _contentService.GetById(n.EventId);
                if (node == null) continue;
                var row = Describe(node, n.OwnerId);

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
                    // Händelser som inte är träning listas inte — de är majoriteten och inget händer med dem.
                    continue;
                }

                row.Participants = participants.GetValueOrDefault(n.EventId);
                row.Bookings = bookings.GetValueOrDefault(n.EventId);

                if (!node.Published)
                {
                    row.Action = ActionSkip;
                    row.Reason = "Opublicerad — syns redan inte, lämnas som den är";
                }
                else if (withPayments.Contains(n.EventId))
                {
                    row.Action = ActionSkip;
                    row.Reason = "Har betalningar i liggaren — flyttas inte (verifikationerna pekar på händelsen)";
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
                    if (apply) Migrate(node, row, actingMemberId);
                }
                result.Rows.Add(row);
            }

            if (apply)
                _logger.LogInformation(
                    "Träningsmigrering (klubb {Club}): {Migrated} flyttade, {Skipped} lämnade, {Failed} fel, {P} deltagarrader, {B} lånevapenbokningar",
                    clubId, result.Rows.Count(r => r.Action == ActionMigrated), result.Skipped, result.Failed,
                    result.ParticipantsMoved, result.BookingsMoved);
            return result;
        }

        private Row Describe(IContent node, int clubId)
        {
            var club = _contentService.GetById(clubId);
            var start = ClubEvents.RealDate(node.GetValue<DateTime?>("eventDate"));
            return new Row
            {
                EventId = node.Id,
                ClubId = clubId,
                ClubName = club?.Name ?? $"Klubb {clubId}",
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
            var (date, startTime, endTime, endDropped) = TrainingMigrationRules.SplitTimes(start, end);
            if (endDropped) row.Notes.Add("Sluttiden låg på en annan dag och togs inte med");

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
                // över; en kurs krav sätts på kursens koppling — se noten nedan.
                IsMandatory = false,
                LegacyEventNodeId = node.Id,
                CreatedByMemberId = actingMemberId,
                CreatedDate = now,
                UpdatedDate = now
            };
            var url = node.GetValue<string>("registrationUrl");
            if (!string.IsNullOrWhiteSpace(url)) row.Notes.Add($"Extern anmälningslänk togs inte med: {url}");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                using (var tx = db.GetTransaction())
                {
                    db.Insert(training);
                    row.Participants = db.Execute(
                        "UPDATE dbo.ClubEventParticipant SET OccasionKind = @0, EventId = @1 WHERE OccasionKind = @2 AND EventId = @3",
                        ClubEvents.OccasionTraining, training.Id, ClubEvents.OccasionEvent, node.Id);
                    if (TableExists(db, "FirearmBooking"))
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
                _logger.LogError(ex, "Träningsmigrering: händelse {Event} kunde inte flyttas", node.Id);
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
                    row.Notes.Add("Händelsen kunde inte avpubliceras — kör migreringen igen");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Träningsmigrering: händelse {Event} kunde inte avpubliceras", node.Id);
                row.Notes.Add("Händelsen kunde inte avpubliceras — kör migreringen igen");
            }
        }

        private static Dictionary<int, int> CountBookings(IUmbracoDatabase db)
        {
            if (!TableExists(db, "FirearmBooking")) return new();
            return db.Fetch<IdPair>(
                    "SELECT OccasionId AS K, COUNT(*) AS V FROM dbo.FirearmBooking WHERE OccasionKind = @0 GROUP BY OccasionId",
                    FirearmOccasionKind.Event)
                .ToDictionary(x => x.K, x => x.V);
        }

        private static HashSet<int> EventsWithPayments(IUmbracoDatabase db)
        {
            if (!TableExists(db, "LedgerPayment")) return new();
            return db.Fetch<int>(
                    "SELECT DISTINCT SourceId FROM dbo.LedgerPayment WHERE SourceType = @0",
                    HpskSite.Models.Ledger.LedgerSourceType.Event)
                .ToHashSet();
        }

        private static bool TableExists(IUmbracoDatabase db, string table)
            => db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + table) == 1;

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
