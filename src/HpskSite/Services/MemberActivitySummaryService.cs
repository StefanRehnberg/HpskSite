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
        public async Task<MemberActivitySummary> GetAsync(
            int memberId, int year, IEnumerable<string>? weaponGroups = null, int? clubId = null)
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

            try { entries.AddRange(await ReadRangeCheckInsAsync(memberId, year, clubId)); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Aktivitetssammanställning: incheckningar kunde inte läsas för {MemberId}", memberId);
                sourceErrors.Add("Incheckningar på banan kunde inte läsas — sammanställningen är ofullständig.");
            }

            var summary = MemberActivitySummary.From(
                memberId, ResolveName(memberId), year, entries, weaponGroups);

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

        /// <summary>
        /// Antal aktivitetsdagar per medlem för ett år — för klubbens medlemslista.
        ///
        /// <b>⚠️ EGEN BULKVÄG, med flit.</b> Att bygga hela sammanställningen per medlem hade blivit
        /// tre källor × hela klubbens roster: ~500 frågor plus innehållsläsningar för 129 medlemmar.
        /// Det är precis den sortens sida som tog tolv sekunder att ladda innan fakturalistan
        /// beskars. Här är det i stället <b>fyra frågor och två batchade innehållsläsningar för hela
        /// listan</b>, oavsett antal medlemmar.
        ///
        /// <b>⚠️ RÄKNEREGLERNA LÅNAS, INTE KOPIERAS</b> — <see cref="MemberActivitySummary.CompetitionCounts"/>
        /// och <see cref="MemberActivitySummary.EventCounts"/>. Två uppsättningar villkor blir förr
        /// eller senare två svar på samma fråga, och då säger badgen en sak och detaljvyn en annan om
        /// samma medlem. Ändras en regel ska BÅDA vägarna följa med av sig själva.
        ///
        /// <b>Filtrerar inte på vapengrupp.</b> Badgen är en översikt; vapengruppen är detaljvyns
        /// fråga. Skulle den filtreras måste även dess text säga det.
        /// </summary>
        public async Task<Dictionary<int, int>> GetActivityDaysForMembersAsync(
            IEnumerable<int> memberIds, int year, int? clubId = null)
        {
            var ids = memberIds.Where(id => id > 0).Distinct().ToList();
            var days = ids.ToDictionary(id => id, _ => new HashSet<DateTime>());
            if (ids.Count == 0) return new Dictionary<int, int>();

            // Taket för en IN-lista är ~2100 parametrar och faller TYST. Klubbens roster kommer aldrig
            // nära, men chunkningen gör gränsen omöjlig att gå in i av misstag.
            foreach (var batch in ids.Chunk(1000))
            {
                var inList = string.Join(",", batch.Select((_, i) => "@" + i));
                var args = batch.Cast<object>().ToArray();

                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;

                // 1) Träningsloggen — varje rad räknas.
                foreach (var row in db.Fetch<MemberDateRow>(
                    $@"SELECT MemberId, CAST(TrainingDate AS date) AS [Date] FROM TrainingScores
                       WHERE MemberId IN ({inList}) AND YEAR(TrainingDate) = @{batch.Length}",
                    args.Append(year).ToArray()))
                {
                    if (days.TryGetValue(row.MemberId, out var set)) set.Add(row.Date);
                }

                // 2) Tävlingar: anmälningar och resultat, plus DNS. Datumet kommer ur tävlingsnoden,
                //    så id:na samlas först och noderna läses batchat efteråt.
                var regs = db.Fetch<MemberCompetitionRow>(
                    $@"SELECT DISTINCT
                              COALESCE(pd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20))) AS MemberId,
                              COALESCE(cd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(cd.varcharValue, cd.textValue), 20))) AS CompetitionId
                       FROM umbracoNode n
                       JOIN umbracoContent c          ON c.nodeId = n.id
                       JOIN cmsContentType ct         ON ct.nodeId = c.contentTypeId AND ct.alias = 'competitionRegistration'
                       JOIN umbracoContentVersion cv  ON cv.nodeId = n.id AND cv.[current] = 1
                       JOIN cmsPropertyType pt        ON pt.contentTypeId = ct.nodeId AND pt.Alias = 'memberId'
                       JOIN umbracoPropertyData pd    ON pd.versionId = cv.id AND pd.propertyTypeId = pt.id
                       JOIN cmsPropertyType ctp       ON ctp.contentTypeId = ct.nodeId AND ctp.Alias = 'competitionId'
                       JOIN umbracoPropertyData cd    ON cd.versionId = cv.id AND cd.propertyTypeId = ctp.id
                       WHERE n.trashed = 0
                         AND COALESCE(pd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20))) IN ({inList})",
                    args);

                var results = db.Fetch<MemberCompetitionRow>(
                    $@"SELECT DISTINCT MemberId, CompetitionId FROM (
                            SELECT MemberId, CompetitionId FROM PrecisionResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM MilsnabbResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM DuellResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM NationellHelmatchResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM MagnumPrecisionResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM StandardpistolResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM SportpistolResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM FaltskytteResultEntry
                            UNION ALL SELECT MemberId, CompetitionId FROM SpringskytteResultEntry) x
                       WHERE MemberId IN ({inList})", args);

                var dnsRows = db.Fetch<MemberCompetitionRow>(
                    $@"SELECT DISTINCT MemberId, CompetitionId FROM CompetitionParticipantStatus
                       WHERE Status = 'DNS' AND MemberId IN ({inList})", args);

                var resultSet = results.Select(r => (r.MemberId, r.CompetitionId)).ToHashSet();
                var dnsSet = dnsRows.Select(r => (r.MemberId, r.CompetitionId)).ToHashSet();

                var compIds = regs.Select(r => r.CompetitionId)
                    .Concat(results.Select(r => r.CompetitionId))
                    .Where(id => id > 0).Distinct().ToList();
                var compDates = LoadCompetitions(compIds);

                foreach (var pair in regs.Select(r => (r.MemberId, r.CompetitionId))
                                         .Concat(resultSet).Distinct())
                {
                    if (!days.TryGetValue(pair.MemberId, out var set)) continue;
                    if (!compDates.TryGetValue(pair.CompetitionId, out var comp)) continue;
                    if (comp.Date.Year != year) continue;

                    bool hasResult = resultSet.Contains(pair);
                    if (MemberActivitySummary.CompetitionCounts(hasResult, dnsSet.Contains(pair)))
                        set.Add(comp.Date.Date);
                }

                // 3) Evenemangsnärvaro. Datumet är EVENEMANGETS, inte radens tidsstämplar.
                var eventRows = db.Fetch<MemberEventRow>(
                    $@"SELECT MemberId, EventId, AttendanceStatus FROM ClubEventParticipant
                       WHERE CancelledAt IS NULL AND MemberId IN ({inList})", args);

                foreach (var row in eventRows)
                {
                    if (!MemberActivitySummary.EventCounts(row.AttendanceStatus)) continue;
                    if (!days.TryGetValue(row.MemberId, out var set)) continue;
                    var ctx = _events.GetEventContext(row.EventId);
                    if (ctx?.EventDate == null || ctx.EventDate.Value.Year != year) continue;
                    set.Add(ctx.EventDate.Value.Date);
                }

                // 4) Incheckningar, om klubben räknar dem. Badgen MÅSTE hedra samma inställning som
                //    detaljvyn, annars säger de olika om samma medlem.
                //
                //    ⚠️ Dedupliceringen sköter sig själv här: aktivitetsdagar är en MÄNGD av datum, så
                //    en incheckning samma dag som en tävling lägger inte till något. Det är samma
                //    egenskap som gör rubriksiffran immun mot dubbelräkning i detaljvyn.
                if (clubId is > 0 && ClubCountsRangeCheckIns(clubId.Value))
                {
                    var rangeIds = db.Fetch<int>("SELECT RangeId FROM ClubRangeLink WHERE ClubId = @0", clubId.Value);
                    if (rangeIds.Count > 0)
                    {
                        var rangeIn = string.Join(",", rangeIds.Select((_, i) => "@" + (i + batch.Length + 1)));
                        var checkInArgs = args.Append((object)year).Concat(rangeIds.Cast<object>()).ToArray();

                        foreach (var row in db.Fetch<MemberDateRow>(
                            $@"SELECT MemberId, [Date] FROM RangeActivitySession
                               WHERE MemberId IN ({inList}) AND YEAR([Date]) = @{batch.Length}
                                 AND RangeId IN ({rangeIn})",
                            checkInArgs))
                        {
                            if (days.TryGetValue(row.MemberId, out var set)) set.Add(row.Date.Date);
                        }
                    }
                }
            }

            return days.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        private class MemberDateRow { public int MemberId { get; set; } public DateTime Date { get; set; } }
        private class MemberCompetitionRow { public int MemberId { get; set; } public int CompetitionId { get; set; } }
        private class MemberEventRow
        {
            public int MemberId { get; set; }
            public int EventId { get; set; }
            public string? AttendanceStatus { get; set; }
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
                // ⚠️ ORDNINGEN ÄR REGELN. En träningsmatch är en träningsmatch även när någon kryssat
                // "tävling" på raden, så TrainingMatchId prövas FÖRE IsCompetition. Läses flaggan
                // först hamnar matcherna i tävlingssiffran — mätt i dev: två av fixturmedlemmens nio
                // rader med IsCompetition=1 för 2026 är träningsmatcher. Rapporterat 2026-09-01.
                bool isPractice = !string.IsNullOrWhiteSpace(r.PracticeType);
                var kind = isPractice ? ActivityKind.Practice
                         : r.TrainingMatchId.HasValue ? ActivityKind.TrainingMatch
                         : r.IsCompetition ? ActivityKind.Competition
                         : ActivityKind.Training;

                string discipline = string.IsNullOrWhiteSpace(r.Discipline) ? "Precision" : r.Discipline;
                string weapon = string.IsNullOrWhiteSpace(r.WeaponClass) ? "" : $"{r.WeaponClass}-vapen";

                string title = kind switch
                {
                    ActivityKind.Practice => $"0-poäng träning ({PracticeLabel(r.PracticeType)})",
                    ActivityKind.TrainingMatch => $"Träningsmatch, {discipline}",
                    ActivityKind.Competition => "Extern tävling (självrapporterad)",
                    _ => $"Träning, {discipline}"
                };

                var detail = new List<string>();
                if (!string.IsNullOrEmpty(weapon)) detail.Add(weapon);
                if (kind != ActivityKind.Practice && r.TotalScore > 0)
                    detail.Add($"{r.TotalScore} p" + (r.XCount > 0 ? $" ({r.XCount} X)" : ""));
                // Klass och placering hör hemma på båda — en träningsmatch har också en placering, och
                // raderna bar dem redan när matcherna felaktigt klassades som tävlingar.
                if (kind == ActivityKind.Competition || kind == ActivityKind.TrainingMatch)
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
                    WeaponGroups = TrainingWeaponGroups(r),
                    CountsAsActivity = true
                });
            }

            return entries;
        }

        /// <summary>
        /// Vapengruppen ur ett värde som kan vara antingen en KLASS ("A3", "C Vet Y", "A Opt 1")
        /// eller redan en GRUPPKOD ("A", "C", "A_Opt").
        ///
        /// ⚠️ <b>Båda formerna förekommer, och det är inte en skönhetsfråga.</b>
        /// <c>ShootingClasses.GetWeaponClassCode</c> resolvar en klass och svarar <b>tomt</b> på en bar
        /// gruppkod — och gruppkoder är precis vad <c>TrainingScores.WeaponClass</c> och
        /// <c>SpringskytteResultEntry.WeaponClass</c> lagrar. Utan det andra steget tappar de källorna
        /// sin vapengrupp helt och faller bort ur varje filter.
        ///
        /// Den råa formen <b>valideras mot <c>WeaponClass</c>-enumet</b>, aldrig gissas: annars hade
        /// vilket skräpvärde som helst blivit en "vapengrupp" i väljaren.
        /// </summary>
        private static string ResolveWeaponGroup(string? classOrGroup)
        {
            var raw = (classOrGroup ?? "").Trim();
            if (raw.Length == 0) return "";

            var fromClass = ShootingClasses.GetWeaponClassCode(raw);
            if (fromClass.Length > 0) return fromClass;

            return Enum.TryParse<WeaponClass>(raw, ignoreCase: true, out var group)
                ? group.ToString()
                : "";
        }

        /// <summary>
        /// Vapengruppen för en träningsrad.
        ///
        /// ⚠️ <c>TrainingScores.WeaponClass</c> bär redan en gruppKOD, inte en klass — mätt i dev är
        /// alla förekommande värden A/B/C/L/M/R. Den ska därför INTE köras genom
        /// <c>ShootingClasses.GetWeaponClassCode</c>, som resolvar en KLASS ("A3" → "A") och svarar
        /// tomt på en bar gruppkod. Klassresolvern används bara på
        /// <c>CompetitionShootingClass</c>, som är en riktig klass.
        ///
        /// <b>Känd begränsning:</b> träningsloggen skiljer inte på öppet sikte och optik — det finns
        /// ingen A_Opt bland värdena. Ett intyg för A_Opt får alltså träningen räknad som A. Att
        /// gissa något annat vore att hitta på uppgifter.
        /// </summary>
        private static List<string> TrainingWeaponGroups(TrainingRow r)
        {
            var groups = new List<string>();

            var fromWeapon = ResolveWeaponGroup(r.WeaponClass);
            if (fromWeapon.Length > 0) groups.Add(fromWeapon);

            var fromClass = ResolveWeaponGroup(r.CompetitionShootingClass);
            if (fromClass.Length > 0) groups.Add(fromClass);

            return groups.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            var registeredGroups = GetRegisteredWeaponGroups(memberId);
            var resultGroups = GetResultWeaponGroups(memberId);
            var dns = GetCompetitionIdsWithDnsOnly(memberId);

            var all = new HashSet<int>(registeredGroups.Keys);
            all.UnionWith(resultGroups.Keys);

            var competitions = LoadCompetitions(all);
            var entries = new List<MemberActivityEntry>();

            foreach (var id in all)
            {
                if (!competitions.TryGetValue(id, out var comp)) continue;
                if (comp.Date.Year != year) continue;

                bool hasResult = resultGroups.ContainsKey(id);
                bool didNotStart = !MemberActivitySummary.CompetitionCounts(hasResult, dns.Contains(id));

                // Vapengrupperna är UNIONEN av det skytten anmälde sig i och det hen faktiskt har
                // resultat i. De kan skilja sig: en klassändring vid disken flyttar resultatet men
                // inte anmälan, och en skytt kan ha anmält två klasser men bara skjutit en.
                var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (registeredGroups.TryGetValue(id, out var rg)) groups.UnionWith(rg);
                if (resultGroups.TryGetValue(id, out var xg)) groups.UnionWith(xg);

                var detail = new List<string>();
                if (!string.IsNullOrWhiteSpace(comp.Venue)) detail.Add(comp.Venue);
                if (groups.Count > 0)
                    detail.Add(string.Join(", ", groups.OrderBy(g => g, StringComparer.Ordinal)));

                entries.Add(new MemberActivityEntry
                {
                    Date = comp.Date,
                    Kind = ActivityKind.Competition,
                    Evidence = hasResult ? ActivityEvidence.OfficialResult : ActivityEvidence.RegisteredOnly,
                    Title = comp.Name,
                    Detail = detail.Count > 0 ? string.Join(" · ", detail) : null,
                    SourceId = id,
                    SourceKind = MemberActivityEntry.SourceKindCompetition,
                    WeaponGroups = groups.OrderBy(g => g, StringComparer.Ordinal).ToList(),
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
        /// Vapengrupperna medlemmen är ANMÄLD i, per tävling. Behövs för de tävlingar som saknar
        /// resultatrader — utan dem hade en anmäld-men-oskjuten tävling ingen vapengrupp och fallit
        /// bort ur varje filter, vilket är precis de poster ett intyg behöver förklara.
        ///
        /// Klasserna ligger som JSON på anmälningsnoden (<c>[{"class":"A1", ...}]</c>) och en anmälan
        /// kan bära flera — dev-data har A1 och L_Vet_A på samma tävling.
        /// </summary>
        private Dictionary<int, HashSet<string>> GetRegisteredWeaponGroups(int memberId)
        {
            var result = new Dictionary<int, HashSet<string>>();

            List<RegistrationClassRow> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                rows = scope.Database.Fetch<RegistrationClassRow>(
                    @"SELECT COALESCE(cd.intValue,
                             TRY_CONVERT(INT, LEFT(COALESCE(cd.varcharValue, cd.textValue), 20))) AS CompetitionId,
                             COALESCE(sd.textValue, sd.varcharValue) AS ClassesJson
                      FROM umbracoNode n
                      JOIN umbracoContent c          ON c.nodeId = n.id
                      JOIN cmsContentType ct         ON ct.nodeId = c.contentTypeId AND ct.alias = 'competitionRegistration'
                      JOIN umbracoContentVersion cv  ON cv.nodeId = n.id AND cv.[current] = 1
                      JOIN umbracoPropertyData pd    ON pd.versionId = cv.id
                      JOIN cmsPropertyType pt        ON pt.id = pd.propertyTypeId AND pt.Alias = 'memberId'
                      JOIN cmsPropertyType ctp       ON ctp.contentTypeId = ct.nodeId AND ctp.Alias = 'competitionId'
                      JOIN umbracoPropertyData cd    ON cd.versionId = cv.id AND cd.propertyTypeId = ctp.id
                      LEFT JOIN cmsPropertyType stp  ON stp.contentTypeId = ct.nodeId AND stp.Alias = 'shootingClasses'
                      LEFT JOIN umbracoPropertyData sd ON sd.versionId = cv.id AND sd.propertyTypeId = stp.id
                      WHERE n.trashed = 0
                        AND (pd.intValue = @0
                             OR (pd.intValue IS NULL
                                 AND TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20)) = @0))",
                    memberId);
            }

            foreach (var row in rows)
            {
                if (row.CompetitionId is not > 0) continue;
                var set = result.TryGetValue(row.CompetitionId.Value, out var existing)
                    ? existing
                    : result[row.CompetitionId.Value] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var cls in ParseRegistrationClasses(row.ClassesJson))
                {
                    var group = ResolveWeaponGroup(cls);
                    if (!string.IsNullOrWhiteSpace(group)) set.Add(group);
                }
            }

            return result;
        }

        /// <summary>
        /// Klassnamnen ur anmälningens JSON. Tolerant med flit: en anmälan kan bära den äldre
        /// skalära formen eller ren skräp, och ett vapengruppsfilter är inte värt ett undantag.
        /// </summary>
        private static IEnumerable<string> ParseRegistrationClasses(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

            var trimmed = json.TrimStart();
            if (!trimmed.StartsWith("["))
                return new[] { json.Trim() };   // äldre skalär form: ett enda klassnamn

            try
            {
                var entries = System.Text.Json.JsonSerializer.Deserialize<List<ShootingClassEntry>>(
                    json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return entries?.Select(e => e.Class).Where(c => !string.IsNullOrWhiteSpace(c))
                       ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private class RegistrationClassRow
        {
            public int? CompetitionId { get; set; }
            public string? ClassesJson { get; set; }
        }

        /// <summary>
        /// Tävlingar där medlemmen har minst en inskriven resultatrad. Unionen över de nio
        /// disciplintabellerna är avsiktligt EN fråga — en tabell i taget hade blivit nio
        /// tur-och-retur och, värre, en ny disciplintabell hade tystnat i stället för att märkas.
        /// </summary>
        private Dictionary<int, HashSet<string>> GetResultWeaponGroups(int memberId)
        {
            // ⚠️ DE NIO TABELLERNA HAR INTE SAMMA KOLUMNER. Åtta bär `ShootingClass`;
            // `SpringskytteResultEntry` har ingen sådan kolumn alls — Springskytte kör vapenklass och
            // ålders-/könsklass i två separata kolumner (`WeaponClass`, `AgeGenderClass`).
            //
            // En union som läste `ShootingClass` överallt gav ett SQL-fel som tog ner HELA
            // tävlingskällan — sammanställningen tappade varje tävling och sa bara "Tävlingsdeltagandet
            // kunde inte läsas". Rapporterat 2026-09-01. Springskytte bidrar därför med `WeaponClass`,
            // som redan är en gruppkod och normaliseras av ResolveWeaponGroup.
            //
            // Lägg aldrig till en disciplintabell här utan att kontrollera dess kolumnnamn.
            const string sql = @"
                SELECT CompetitionId, ShootingClass FROM PrecisionResultEntry        WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM MilsnabbResultEntry          WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM DuellResultEntry             WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM NationellHelmatchResultEntry WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM MagnumPrecisionResultEntry   WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM StandardpistolResultEntry    WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM SportpistolResultEntry       WHERE MemberId = @0
                UNION SELECT CompetitionId, ShootingClass FROM FaltskytteResultEntry        WHERE MemberId = @0
                UNION SELECT CompetitionId, WeaponClass   FROM SpringskytteResultEntry      WHERE MemberId = @0";

            List<ResultClassRow> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                rows = scope.Database.Fetch<ResultClassRow>(sql, memberId);

            var result = new Dictionary<int, HashSet<string>>();
            foreach (var row in rows)
            {
                if (row.CompetitionId <= 0) continue;
                var set = result.TryGetValue(row.CompetitionId, out var existing)
                    ? existing
                    : result[row.CompetitionId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // ⚠️ Klassen lagras i TVÅ former — id ("C_Vet_Y") och visningsnamn ("C Vet Y") — och
                // Springskyttes värde är dessutom redan en gruppkod. ResolveWeaponGroup hanterar alla
                // tre; en egen förstabokstavsläsning hade delat A_opt_1 fel, och optiksikte är inte
                // samma tävling som öppet sikte.
                var group = ResolveWeaponGroup(row.ShootingClass);
                if (!string.IsNullOrWhiteSpace(group)) set.Add(group);
            }
            return result;
        }

        private class ResultClassRow
        {
            public int CompetitionId { get; set; }
            public string? ShootingClass { get; set; }
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
                    CountsAsActivity = MemberActivitySummary.EventCounts(row.AttendanceStatus),
                    NotCountedReason = notCounted
                });
            }

            return entries;
        }

        // ── Källa 4: incheckning på banan ─────────────────────────────

        /// <summary>
        /// Incheckningar på klubbens skjutbana, som aktivitet — <b>bara när klubben slagit på det</b>
        /// (<c>club.activityFromRangeCheckIn</c>). Standard av, så en deploy inte ändrar någon klubbs
        /// siffror i deploy-ögonblicket.
        ///
        /// <b>Vilken klubbs inställning?</b> Den som ska utfärda intyget — <paramref name="clubId"/>,
        /// annars medlemmens primära klubb. Det är rätt nivå: en medlem kan tillhöra två klubbar, och
        /// vad klubb A intygar är A:s beslut, inte B:s.
        ///
        /// <b>⚠️ Bara incheckningar på banor som är LÄNKADE till den klubben räknas.</b> Klubben
        /// intygar verksamhet på sin egen bana; ett pass på någon annans anläggning är inte dess sak
        /// att gå i god för. Det är också därför switchen kräver en <c>ClubRangeLink</c> — utan
        /// länkad bana finns ingenting att räkna.
        ///
        /// <b>⚠️ Bara pass med MemberId.</b> Den manuella banloggen (<c>ShotSourceManual</c>) skriver
        /// sessioner utan medlem — de är anläggningsstatistik (antal skyttar, antal skott), inte
        /// någons aktivitet.
        /// </summary>
        private async Task<List<MemberActivityEntry>> ReadRangeCheckInsAsync(int memberId, int year, int? clubId)
        {
            int club = clubId ?? PrimaryClubIdOf(memberId);
            if (club <= 0) return new List<MemberActivityEntry>();
            if (!ClubCountsRangeCheckIns(club)) return new List<MemberActivityEntry>();

            List<int> rangeIds;
            List<RangeCheckInRow> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                rangeIds = db.Fetch<int>("SELECT RangeId FROM ClubRangeLink WHERE ClubId = @0", club);
                if (rangeIds.Count == 0) return new List<MemberActivityEntry>();

                var inList = string.Join(",", rangeIds.Select((_, i) => "@" + (i + 2)));
                var args = new List<object> { memberId, year };
                args.AddRange(rangeIds.Cast<object>());

                rows = db.Fetch<RangeCheckInRow>(
                    $@"SELECT s.Id, s.RangeId, s.[Date], s.StartTime, s.EndTime, s.ShotCount,
                              s.ShotCountSource, s.LinkedCompetitionId, s.LinkedTrainingScoreId,
                              r.Name AS RangeName
                       FROM RangeActivitySession s
                       JOIN ShootingRange r ON r.Id = s.RangeId
                       WHERE s.MemberId = @0 AND YEAR(s.[Date]) = @1 AND s.RangeId IN ({inList})
                       ORDER BY s.[Date], s.Id",
                    args.ToArray());
            }

            var entries = new List<MemberActivityEntry>();
            foreach (var r in rows)
            {
                var detail = new List<string>();
                if (!string.IsNullOrWhiteSpace(r.RangeName)) detail.Add(r.RangeName!);
                if (r.StartTime != null)
                    detail.Add(r.EndTime != null
                        ? $"{r.StartTime:hh\\:mm}–{r.EndTime:hh\\:mm}"
                        : $"från {r.StartTime:hh\\:mm}");
                if (r.ShotCount > 0) detail.Add($"{r.ShotCount} skott");

                var entry = new MemberActivityEntry
                {
                    Date = r.Date,
                    Kind = ActivityKind.RangeCheckIn,
                    // En QR-skanning på banan har samma tillitsnivå som evenemangets QR-affisch: den
                    // visar att telefonen var där, inte att en funktionär såg personen.
                    Evidence = r.ShotCountSource == RangeConstants.ShotSourceQr
                        ? ActivityEvidence.SelfRegistered
                        : ActivityEvidence.FunctionaryRecorded,
                    Title = "Incheckad på banan",
                    Detail = detail.Count > 0 ? string.Join(" · ", detail) : null,
                    SourceId = r.Id,
                    SourceKind = MemberActivityEntry.SourceKindRangeCheckIn,
                    // Ingen vapengrupp: en incheckning säger inte VAD som sköts. Under ett
                    // vapengruppsfilter faller den därför bort, och det redovisas med antal.
                    CountsAsActivity = true
                };

                // Lager 2: den explicita länken. ⚠️ Ingenting skriver kolumnerna i dag, så det här är
                // förberedelse — men när de börjar fyllas är passet BEVISLIGEN samma tillfälle, och då
                // ska samma-dag-gissningen inte få avgöra.
                if (r.LinkedCompetitionId is > 0)
                {
                    entry.CountsAsActivity = false;
                    entry.NotCountedReason = MemberActivitySummary.RedundantCheckInReason + ": tävling";
                    entry.SameOccasionAs = $"{MemberActivityEntry.SourceKindCompetition}:{r.LinkedCompetitionId}";
                }
                else if (r.LinkedTrainingScoreId is > 0)
                {
                    entry.CountsAsActivity = false;
                    entry.NotCountedReason = MemberActivitySummary.RedundantCheckInReason + ": träning";
                    entry.SameOccasionAs = $"{MemberActivityEntry.SourceKindTraining}:{r.LinkedTrainingScoreId}";
                }

                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// Räknar klubben incheckning som aktivitet? Saknas doctype-egenskapen svarar
        /// <c>GetValue&lt;bool&gt;</c> false, vilket är rätt standard — men det gör också att en
        /// klubb som tror sig ha slagit på det tyst inte har det. Skrivvägen vägrar därför när
        /// egenskapen saknas, i stället för att låta switchen se ut att fungera.
        /// </summary>
        private bool ClubCountsRangeCheckIns(int clubId)
        {
            try
            {
                var club = _contentService.GetById(clubId);
                if (club == null || club.ContentType.Alias != "club") return false;
                return club.GetValue<bool>(MemberActivitySummary.ClubActivityFromRangeCheckInProperty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa incheckningsinställningen för klubb {ClubId}", clubId);
                return false;
            }
        }

        private int PrimaryClubIdOf(int memberId)
        {
            var member = _memberService.GetById(memberId);
            if (member == null) return 0;
            // ⚠️ primaryClubId är en STRÄNG-egenskap; GetValue<int> ger tyst 0.
            int.TryParse(member.GetValue<string>("primaryClubId") ?? "", out int id);
            return id;
        }

        private class RangeCheckInRow
        {
            public int Id { get; set; }
            public int RangeId { get; set; }
            public DateTime Date { get; set; }
            public TimeSpan? StartTime { get; set; }
            public TimeSpan? EndTime { get; set; }
            public int ShotCount { get; set; }
            public string? ShotCountSource { get; set; }
            public int? LinkedCompetitionId { get; set; }
            public int? LinkedTrainingScoreId { get; set; }
            public string? RangeName { get; set; }
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
