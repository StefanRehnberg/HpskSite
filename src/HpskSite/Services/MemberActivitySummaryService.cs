using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Sammanställer en medlems verksamhet per kalenderår ur de tre källorna: träningsloggen,
    /// tävlingsdeltagandet och närvaron på klubbens/kretsens evenemang.
    ///
    /// <b>Detta är mellanlagret mellan närvarologgning och Föreningsintyg.</b> Utan det är
    /// närvarologgen bara rader i en tabell; med det finns det något ett intyg kan genereras ur.
    /// Därför ETT svar per (medlem, år) som både Min sida och intygsgenereringen läser — två
    /// läsvägar över samma sak blir två svar som får säga emot varandra, och svaret här är ett
    /// myndighetsunderlag.
    ///
    /// <b>Ingen egen lagring.</b> Sammanställningen är helt härledd. Ett lagrat exemplar hade
    /// blivit fel i samma stund någon rättade en träningsrad eller tog ett upprop i efterhand.
    ///
    /// <b>Tre fällor som är inbyggda i koden nedan, inte lämnade åt anroparen:</b>
    /// <list type="number">
    /// <item>Tävlingsanmälningar är Umbraco-noder som sparas OPUBLICERADE. En innehållsfråga mot
    /// den publicerade cachen ger tyst noll — därför SQL mot <c>umbracoPropertyData</c>.</item>
    /// <item><c>TRY_CONVERT(INT, x)</c> sväljer inte trunkeringsfel. Utan <c>LEFT(...,20)</c> kan
    /// EN överstor RTE-text någon annanstans i tabellen spränga hela frågan — i prod, aldrig i dev.
    /// Samma skäl som i <see cref="MemberMergeService"/>.</item>
    /// <item>Evenemangets ÅR är evenemangets, inte radens tidsstämplars. Ett upprop taget i januari
    /// för en decemberhändelse är decemberaktivitet — regeln bor i
    /// <see cref="ClubEventParticipationService.GetForMemberAsync"/> och läses därifrån, inte
    /// kopieras hit.</item>
    /// </list>
    ///
    /// <b>Varför vissa poster räknas och andra inte:</b> en träningsrad och en tävlingsanmälan är
    /// positiva handlingar medlemmen faktiskt utfört, och räknas. Ett upprop som aldrig togs är
    /// frånvaron av en uppgift och räknas inte — men raden SYNS ändå, för annars ser en lucka i
    /// listan ut som frånvaro utan förklaring. DNS ("ej start") räknas inte: skytten var anmäld men
    /// sköt inte.
    /// </summary>
    public class MemberActivitySummaryService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly ClubEventParticipationService _events;
        private readonly ILogger<MemberActivitySummaryService> _logger;

        public MemberActivitySummaryService(
            IScopeProvider scopeProvider,
            IContentService contentService,
            IMemberService memberService,
            ClubEventParticipationService events,
            ILogger<MemberActivitySummaryService> logger)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _memberService = memberService;
            _events = events;
            _logger = logger;
        }

        /// <summary>
        /// Sammanställningen för ett år. Kastar inte — en källa som fallerar loggas och ger en
        /// varning i svaret i stället för att ta ner hela sammanställningen, eftersom en halv
        /// sammanställning med en synlig varning är användbar och ett undantag inte är det.
        /// </summary>
        public async Task<MemberActivitySummary> GetAsync(int memberId, int year)
        {
            var entries = new List<MemberActivityEntry>();
            var sourceErrors = new List<string>();

            try { entries.AddRange(ReadTraining(memberId, year)); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: träningsloggen kunde inte läsas för {MemberId}", memberId);
                sourceErrors.Add("Träningsloggen kunde inte läsas — sammanställningen är ofullständig.");
            }

            try { entries.AddRange(ReadCompetitions(memberId, year)); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: tävlingsdeltagandet kunde inte läsas för {MemberId}", memberId);
                sourceErrors.Add("Tävlingsdeltagandet kunde inte läsas — sammanställningen är ofullständig.");
            }

            try { entries.AddRange(await ReadEventsAsync(memberId, year)); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: evenemangsnärvaron kunde inte läsas för {MemberId}", memberId);
                sourceErrors.Add("Evenemangsnärvaron kunde inte läsas — sammanställningen är ofullständig.");
            }

            var summary = MemberActivitySummary.From(memberId, ResolveName(memberId), year, entries);

            // Källfel först: den som läser måste se att listan är stympad innan hen tolkar siffrorna.
            summary.Warnings.InsertRange(0, sourceErrors);
            return summary;
        }

        /// <summary>
        /// Vilka år medlemmen har någon verksamhet på, nyast först. Används för årsväljaren, så att
        /// den inte erbjuder tomma år eller saknar det år där datat ligger.
        /// </summary>
        public async Task<List<int>> GetYearsWithActivityAsync(int memberId)
        {
            var years = new HashSet<int>();

            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                foreach (var y in scope.Database.Fetch<int>(
                    "SELECT DISTINCT YEAR(TrainingDate) FROM TrainingScores WHERE MemberId = @0", memberId))
                    years.Add(y);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: kunde inte läsa träningsår för {MemberId}", memberId);
            }

            try
            {
                foreach (var comp in LoadCompetitions(GetCompetitionIdsForMember(memberId)).Values)
                    years.Add(comp.Date.Year);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: kunde inte läsa tävlingsår för {MemberId}", memberId);
            }

            try
            {
                foreach (var row in await _events.GetForMemberAsync(memberId))
                {
                    var ctx = _events.GetEventContext(row.EventId);
                    if (ctx?.EventDate != null) years.Add(ctx.EventDate.Value.Year);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: kunde inte läsa evenemangsår för {MemberId}", memberId);
            }

            // Innevarande år ska alltid gå att välja, även för en medlem utan en enda post — annars
            // står årsväljaren tom och sidan ser trasig ut i stället för tom.
            years.Add(DateTime.Today.Year);

            return years.OrderByDescending(y => y).ToList();
        }

        // ── Källa 1: träningsloggen ───────────────────────────────────

        /// <summary>
        /// Träningsloggen. Rader med <c>PracticeType</c> satt är 0-poäng träning — de räknas som
        /// verksamhet men aldrig som poäng, och särskiljs därför som egen sort. Rader med
        /// <c>IsCompetition = 1</c> är SJÄLVRAPPORTERADE externa tävlingsresultat, inte våra egna
        /// tävlingar; de blir Tävling med underlaget "självrapporterad", aldrig "officiellt
        /// resultat".
        /// </summary>
        private List<MemberActivityEntry> ReadTraining(int memberId, int year)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<TrainingRow>(
                @"SELECT Id, TrainingDate, WeaponClass, Discipline, PracticeType, IsCompetition,
                         TotalScore, XCount, CompetitionPlace, CompetitionShootingClass, TrainingMatchId
                  FROM TrainingScores
                  WHERE MemberId = @0 AND YEAR(TrainingDate) = @1
                  ORDER BY TrainingDate DESC", memberId, year);

            var entries = new List<MemberActivityEntry>();
            foreach (var r in rows)
            {
                bool isPractice = !string.IsNullOrWhiteSpace(r.PracticeType);
                var kind = isPractice ? ActivityKind.Practice
                         : r.IsCompetition ? ActivityKind.Competition
                         : ActivityKind.Training;

                string discipline = string.IsNullOrWhiteSpace(r.Discipline) ? "Precision" : r.Discipline;
                string weapon = string.IsNullOrWhiteSpace(r.WeaponClass) ? "" : $"{r.WeaponClass}-vapen";

                string title = kind switch
                {
                    ActivityKind.Practice => $"0-poäng träning ({PracticeLabel(r.PracticeType)})",
                    ActivityKind.Competition => "Extern tävling (självrapporterad)",
                    _ => r.TrainingMatchId.HasValue ? $"Träningsmatch, {discipline}" : $"Träning, {discipline}"
                };

                var detail = new List<string>();
                if (!string.IsNullOrEmpty(weapon)) detail.Add(weapon);
                if (kind != ActivityKind.Practice && r.TotalScore > 0)
                    detail.Add($"{r.TotalScore} p" + (r.XCount > 0 ? $" ({r.XCount} X)" : ""));
                if (kind == ActivityKind.Competition)
                {
                    if (!string.IsNullOrWhiteSpace(r.CompetitionShootingClass)) detail.Add(r.CompetitionShootingClass!);
                    if (r.CompetitionPlace is > 0) detail.Add($"plats {r.CompetitionPlace}");
                }

                entries.Add(new MemberActivityEntry
                {
                    Date = r.TrainingDate,
                    Kind = kind,
                    // ALL träningslogg är självrapporterad. Funktionärsverifiering av träningspass
                    // är INTE byggd (verifierat mot schemat 2026-09-01: TrainingScores har inga
                    // Verified*-kolumner). Den dagen den byggs är det HÄR underlagsstyrkan höjs —
                    // inget annat i sammanställningen behöver ändras.
                    Evidence = ActivityEvidence.SelfReported,
                    Title = title,
                    Detail = detail.Count > 0 ? string.Join(" · ", detail) : null,
                    SourceId = r.Id,
                    SourceKind = MemberActivityEntry.SourceKindTraining,
                    CountsAsActivity = true
                });
            }

            return entries;
        }

        private static string PracticeLabel(string? practiceType) => practiceType switch
        {
            "Vittavla" => "vittavla",
            "Fri" => "fri övning",
            _ => practiceType ?? "övning"
        };

        // ── Källa 2: tävlingsdeltagande ───────────────────────────────

        /// <summary>
        /// Tävlingsdeltagandet. Två källor slås ihop till EN post per tävling:
        /// <list type="bullet">
        /// <item><b>anmälningarna</b> (opublicerade noder → SQL), som täcker alla discipliner men
        /// bara bevisar att skytten anmälde sig;</item>
        /// <item><b>resultatraderna</b> i de nio disciplintabellerna, som bevisar att en funktionär
        /// skrivit in poäng för personen — det starkaste underlaget vi har.</item>
        /// </list>
        /// En skytt som anmält sig i tre vapenklasser till samma tävling har deltagit i EN tävling,
        /// därför slås raderna samman per tävlings-id. Resultat utan anmälan räknas också: en
        /// direktplacerad skytt vid disken har ingen anmälningsnod men har definitivt deltagit.
        /// </summary>
        private List<MemberActivityEntry> ReadCompetitions(int memberId, int year)
        {
            var registered = GetCompetitionIdsForMember(memberId);
            var withResults = GetCompetitionIdsWithResults(memberId);
            var dns = GetCompetitionIdsWithDnsOnly(memberId);

            var all = new HashSet<int>(registered);
            all.UnionWith(withResults);

            var competitions = LoadCompetitions(all);
            var entries = new List<MemberActivityEntry>();

            foreach (var id in all)
            {
                if (!competitions.TryGetValue(id, out var comp)) continue;
                if (comp.Date.Year != year) continue;

                bool hasResult = withResults.Contains(id);
                // DNS spelar bara roll när inget resultat finns. Har skytten resultatrader har hen
                // skjutit, oavsett att en klass markerats som ej start.
                bool didNotStart = !hasResult && dns.Contains(id);

                entries.Add(new MemberActivityEntry
                {
                    Date = comp.Date,
                    Kind = ActivityKind.Competition,
                    Evidence = hasResult ? ActivityEvidence.OfficialResult : ActivityEvidence.RegisteredOnly,
                    Title = comp.Name,
                    Detail = string.IsNullOrWhiteSpace(comp.Venue) ? null : comp.Venue,
                    SourceId = id,
                    SourceKind = MemberActivityEntry.SourceKindCompetition,
                    CountsAsActivity = !didNotStart,
                    NotCountedReason = didNotStart ? "Ej start (DNS) — anmäld men sköt inte" : null
                });
            }

            return entries;
        }

        /// <summary>
        /// Tävlings-id ur medlemmens anmälningsnoder. Anmälningar sparas OPUBLICERADE, så det här
        /// måste gå via SQL — <c>Umbraco.Content</c> mot den publicerade cachen ger tyst noll.
        /// <c>LEFT(...,20)</c> är inte kosmetik, se klasskommentaren.
        /// </summary>
        private List<int> GetCompetitionIdsForMember(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int?>(
                @"SELECT DISTINCT COALESCE(cd.intValue,
                         TRY_CONVERT(INT, LEFT(COALESCE(cd.varcharValue, cd.textValue), 20))) AS CompetitionId
                  FROM umbracoNode n
                  JOIN umbracoContent c          ON c.nodeId = n.id
                  JOIN cmsContentType ct         ON ct.nodeId = c.contentTypeId AND ct.alias = 'competitionRegistration'
                  JOIN umbracoContentVersion cv  ON cv.nodeId = n.id AND cv.[current] = 1
                  JOIN umbracoPropertyData pd    ON pd.versionId = cv.id
                  JOIN cmsPropertyType pt        ON pt.id = pd.propertyTypeId AND pt.Alias = 'memberId'
                  JOIN cmsPropertyType ctp       ON ctp.contentTypeId = ct.nodeId AND ctp.Alias = 'competitionId'
                  JOIN umbracoPropertyData cd    ON cd.versionId = cv.id AND cd.propertyTypeId = ctp.id
                  WHERE n.trashed = 0
                    AND (pd.intValue = @0
                         OR (pd.intValue IS NULL
                             AND TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20)) = @0))",
                memberId)
                .Where(id => id is > 0).Select(id => id!.Value).ToList();
        }

        /// <summary>
        /// Tävlingar där medlemmen har minst en inskriven resultatrad. Unionen över de nio
        /// disciplintabellerna är avsiktligt EN fråga — en tabell i taget hade blivit nio
        /// tur-och-retur och, värre, en ny disciplintabell hade tystnat i stället för att märkas.
        /// </summary>
        private HashSet<int> GetCompetitionIdsWithResults(int memberId)
        {
            const string sql = @"
                SELECT CompetitionId FROM PrecisionResultEntry        WHERE MemberId = @0
                UNION SELECT CompetitionId FROM MilsnabbResultEntry          WHERE MemberId = @0
                UNION SELECT CompetitionId FROM DuellResultEntry             WHERE MemberId = @0
                UNION SELECT CompetitionId FROM NationellHelmatchResultEntry WHERE MemberId = @0
                UNION SELECT CompetitionId FROM MagnumPrecisionResultEntry   WHERE MemberId = @0
                UNION SELECT CompetitionId FROM StandardpistolResultEntry    WHERE MemberId = @0
                UNION SELECT CompetitionId FROM SportpistolResultEntry       WHERE MemberId = @0
                UNION SELECT CompetitionId FROM FaltskytteResultEntry        WHERE MemberId = @0
                UNION SELECT CompetitionId FROM SpringskytteResultEntry      WHERE MemberId = @0";

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(sql, memberId).Where(id => id > 0).ToHashSet();
        }

        /// <summary>Tävlingar där medlemmen har en DNS-markering.</summary>
        private HashSet<int> GetCompetitionIdsWithDnsOnly(int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT DISTINCT CompetitionId FROM CompetitionParticipantStatus WHERE MemberId = @0 AND Status = @1",
                memberId, CompetitionParticipantStatus.Dns).ToHashSet();
        }

        /// <summary>
        /// Namn, datum och plats för en uppsättning tävlings-id. <c>GetByIds</c> i ett svep, inte
        /// <c>GetById</c> i en loop — samma N+1-regel som resten av kodbasen.
        /// </summary>
        private Dictionary<int, CompetitionInfo> LoadCompetitions(IEnumerable<int> competitionIds)
        {
            var ids = competitionIds.Where(id => id > 0).Distinct().ToList();
            var map = new Dictionary<int, CompetitionInfo>();
            if (ids.Count == 0) return map;

            // Taket för en IN-lista är ~2100 parametrar; en medlem kommer aldrig nära, men batchen
            // gör gränsen omöjlig att gå in i av misstag (minnet sql-in-list-parameter-cap).
            foreach (var batch in ids.Chunk(500))
            {
                foreach (var node in _contentService.GetByIds(batch))
                {
                    map[node.Id] = new CompetitionInfo
                    {
                        Name = node.GetValue<string>("competitionName") ?? node.Name ?? $"Tävling #{node.Id}",
                        Date = node.GetValue<DateTime?>("competitionDate") ?? node.CreateDate,
                        Venue = node.GetValue<string>("venue") ?? ""
                    };
                }
            }

            return map;
        }

        // ── Källa 3: evenemangsnärvaro ────────────────────────────────

        /// <summary>
        /// Närvaron på klubbens och kretsens evenemang. Fyra utfall, och skillnaden mellan dem är
        /// hela poängen med källan:
        /// <list type="bullet">
        /// <item><b>Närvarande, prickad av funktionär</b> — starkt underlag.</item>
        /// <item><b>Närvarande, självregistrerad via QR</b> — svagare: en QR-affisch kan
        /// fotograferas och skickas vidare, så ingen funktionär har sett personen.</item>
        /// <item><b>Frånvarande eller giltig frånvaro</b> — räknas inte, men SYNS, för ett
        /// obligatoriskt evenemang med giltig frånvaro är exakt det styrelsen behöver se.</item>
        /// <item><b>Upprop aldrig taget</b> — räknas inte, och betyder INTE frånvaro. Ett upprop
        /// som ingen tog får aldrig läsas som "ingen kom", allra minst in i ett Föreningsintyg.</item>
        /// </list>
        /// Avanmälda rader hoppas över helt: raden lever kvar för avgiftens och historikens skull,
        /// men den är inte verksamhet.
        /// </summary>
        private async Task<List<MemberActivityEntry>> ReadEventsAsync(int memberId, int year)
        {
            // Årsregeln (evenemangets år, inte radens) bor i tjänsten och läses därifrån.
            var rows = await _events.GetForMemberAsync(memberId, year);
            var entries = new List<MemberActivityEntry>();

            foreach (var row in rows)
            {
                if (row.CancelledAt != null) continue;

                var ctx = _events.GetEventContext(row.EventId);
                if (ctx?.EventDate == null) continue;

                bool present = row.AttendanceStatus == ClubEvents.AttendancePresent;
                bool selfRegistered = row.AttendanceStatus != null && row.RecordedByMemberId == row.MemberId;

                string? notCounted = null;
                if (!present)
                {
                    notCounted = row.AttendanceStatus == null
                        ? MemberActivitySummary.NotRecordedReason
                        : ClubEvents.AttendanceDisplay(row.AttendanceStatus);
                }

                var detail = new List<string>();
                if (!string.IsNullOrWhiteSpace(ctx.EventType)) detail.Add(ctx.EventType);
                if (!string.IsNullOrWhiteSpace(ctx.OwnerName)) detail.Add(ctx.OwnerName);
                if (!string.IsNullOrWhiteSpace(row.AttendanceNote)) detail.Add(row.AttendanceNote!);

                entries.Add(new MemberActivityEntry
                {
                    Date = ctx.EventDate.Value,
                    Kind = ActivityKind.Event,
                    // Ett upprop som aldrig togs har INGET underlag. Att märka den raden
                    // "funktionärsregistrerad" hade varit att påstå motsatsen av vad som hänt.
                    Evidence = row.AttendanceStatus == null ? ActivityEvidence.None
                             : selfRegistered ? ActivityEvidence.SelfRegistered
                             : ActivityEvidence.FunctionaryRecorded,
                    Title = string.IsNullOrWhiteSpace(ctx.EventName) ? $"Evenemang #{ctx.EventId}" : ctx.EventName,
                    Detail = detail.Count > 0 ? string.Join(" · ", detail) : null,
                    SourceId = ctx.EventId,
                    SourceKind = MemberActivityEntry.SourceKindEvent,
                    IsMandatoryEvent = ctx.IsMandatory,
                    CountsAsActivity = present,
                    NotCountedReason = notCounted
                });
            }

            return entries;
        }

        // ── Hjälpare ──────────────────────────────────────────────────

        private string ResolveName(int memberId)
        {
            var member = _memberService.GetById(memberId);
            if (member == null) return $"Medlem {memberId}";
            var name = $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}".Trim();
            return string.IsNullOrWhiteSpace(name) ? (member.Name ?? $"Medlem {memberId}") : name;
        }

        private class TrainingRow
        {
            public int Id { get; set; }
            public DateTime TrainingDate { get; set; }
            public string? WeaponClass { get; set; }
            public string? Discipline { get; set; }
            public string? PracticeType { get; set; }
            public bool IsCompetition { get; set; }
            public int TotalScore { get; set; }
            public int XCount { get; set; }
            public int? CompetitionPlace { get; set; }
            public string? CompetitionShootingClass { get; set; }
            public int? TrainingMatchId { get; set; }
        }

        private class CompetitionInfo
        {
            public string Name { get; set; } = "";
            public DateTime Date { get; set; }
            public string Venue { get; set; } = "";
        }
    }
}
