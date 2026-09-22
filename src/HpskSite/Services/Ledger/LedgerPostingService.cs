using HpskSite.Models;
using HpskSite.Models.Ledger;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Skriver verifikationer. <b>Den enda vägen in i liggaren.</b>
    ///
    /// <para><b>⚠️⚠️ ORDNINGEN I <see cref="Post"/> ÄR BÄRANDE, inte en optimering.</b> Allt som kan
    /// räknas ut görs FÖRE transaktionen öppnas; inuti den ligger bara allokeringen och skrivningen.
    /// Numret delas ut med <c>UPDLOCK</c>, vilket serialiserar bokföringen per utställare tills
    /// transaktionen committar — så ett mejlutskick, en filskrivning eller en Umbraco-publicering
    /// innanför den låser hela föreningens bokföring medan den pågår. Gör biverkningarna EFTER.</para>
    ///
    /// <para><b>⚠️ Rättelsen skrivs av <see cref="CreateCorrection"/>, aldrig genom att ändra något.</b>
    /// <c>UPDATE</c> är förbjudet på en bokförd verifikation, så rättelsens koppling till originalet
    /// måste sättas i samma <c>INSERT</c> som raden skrivs.</para>
    /// </summary>
    public class LedgerPostingService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerNumberAllocator _allocator;
        private readonly ILogger<LedgerPostingService> _logger;

        public LedgerPostingService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerNumberAllocator allocator,
            ILogger<LedgerPostingService> logger)
        {
            _databaseFactory = databaseFactory;
            _allocator = allocator;
            _logger = logger;
        }

        /// <summary>
        /// Bokför en affärshändelse. Returnerar ett resultat med felet i klartext i stället för att
        /// kasta — varje anropare står på en yta där en människa ska få veta vad som gick fel.
        /// </summary>
        public LedgerPostingResult Post(LedgerPostingRequest request)
        {
            using var db = _databaseFactory.CreateDatabase();

            // ── Före transaktionen: allt som går att räkna ut ────────────────────────────────
            if (request.Lines.Count == 0)
                return LedgerPostingResult.Failed("Verifikationen saknar konteringsrader.");

            var fiscalYear = ResolveFiscalYear(db, request);
            if (fiscalYear is null)
            {
                // Skapas INTE automatiskt: ett år har start- och slutdatum som bara föreningen
                // känner (brutet räkenskapsår), och ett gissat år är fel i tysthet.
                return LedgerPostingResult.Failed(
                    $"Det finns inget räkenskapsår som omfattar {request.AccountingDate:yyyy-MM-dd}. "
                    + "Lägg upp året i ekonomiinställningarna först.");
            }

            if (fiscalYear.Status == LedgerFiscalYearStatus.Established)
            {
                // Triggern vägrar ändå, men ett svar som namnger orsaken är skillnaden mellan
                // "jag förstår" och "systemet är trasigt".
                return LedgerPostingResult.Failed(
                    $"Räkenskapsåret {fiscalYear.Year} är fastställt av årsmötet och tar inte emot fler poster.");
            }

            var accounts = LoadAccounts(db, request.IssuerType, request.IssuerId);
            var roles = LoadRoleMap(db, request.IssuerType, request.IssuerId);
            var projects = LoadProjects(db, request.IssuerType, request.IssuerId);

            var built = BuildLines(request, accounts, roles, projects, out var buildError);
            if (buildError is not null) return LedgerPostingResult.Failed(buildError);

            var imbalance = LedgerAmounts.Imbalance(built);
            if (imbalance != 0)
            {
                if (!LedgerAmounts.IsRoundable(imbalance))
                {
                    return LedgerPostingResult.Failed(
                        $"Verifikationen går inte ihop: debet minus kredit är {imbalance:0.00} kr.");
                }

                var roundingLine = BuildRoundingLine(imbalance, accounts, roles, out var roundError);
                if (roundError is not null) return LedgerPostingResult.Failed(roundError);

                // Öresdifferensen hör till verifikationen som helhet, inte till någon enskild rad,
                // så den får begärans projekt — aldrig en enskild rads. Delar verifikationen sig
                // mellan två projekt finns det inget sant svar på vilket öret tillhör, och då är
                // "inget projekt" ärligare än att lägga det på det första.
                StampProject(roundingLine!, request.ProjectId, projects);
                built.Add(roundingLine!);
            }

            // ── Transaktionen: allokera och skriv. Ingenting långsamt härinne. ───────────────
            try
            {
                using var tx = db.GetTransaction();

                var (seriesId, number, prefix) = _allocator.Allocate(
                    db, request.IssuerType, request.IssuerId, fiscalYear.Year,
                    LedgerSeriesKind.JournalEntry);

                var entry = new LedgerJournalEntry
                {
                    IssuerType = request.IssuerType,
                    IssuerId = request.IssuerId,
                    SeriesId = seriesId,
                    Number = number,
                    FiscalYearId = fiscalYear.Id,
                    AccountingDate = request.AccountingDate.Date,
                    EventDate = (request.EventDate ?? request.AccountingDate).Date,
                    RegisteredUtc = DateTime.UtcNow,
                    Description = request.Description ?? "",
                    CounterpartyType = request.CounterpartyType,
                    CounterpartyId = request.CounterpartyId,
                    CounterpartyName = request.CounterpartyName,
                    SourceType = request.SourceType,
                    SourceId = request.SourceId,
                    PaymentId = request.PaymentId,
                    // ⚠️ I INSERT:en. Kan inte sättas efteråt — se klassens varning.
                    CorrectsEntryId = request.CorrectsEntryId,
                    CreatedByMemberId = request.CreatedByMemberId
                };

                // ⚠️⚠️ SKRIVS SOM SQL, INTE db.Insert. NPocos [TableName] bär inget schema, så
                // en Insert hamnar ALLTID i dbo — en sandlådas verifikation hade då skrivits i
                // den skarpa tabellen. (Numera avvisad av CK_dbo_*_Live, men det är en spärr,
                // inte en design.) Se LedgerDb.Insert, som kastar av samma skäl.
                // ⚠️⚠️ VARKEN `OUTPUT INSERTED.Id` ELLER EN TABELLVARIABEL GÅR HÄR.
                // SQL Server vägrar ett naket OUTPUT på en tabell med triggrar (Msg 334), och
                // liggarens oföränderlighetstriggrar sitter på exakt den här tabellen — felet
                // slog till på VARJE bokföring och såg ut som "Bokföringen kunde inte skrivas".
                // Och `OUTPUT … INTO @new` går inte heller: NPoco läser varje `@namn` som en
                // parameter och kastar "Parameter '@new' specified but none of the passed
                // arguments have a property with this name". SCOPE_IDENTITY klarar båda —
                // triggrarna är INSTEAD OF UPDATE/DELETE och rör inte INSERT:en.
                entry.Id = db.ExecuteScalar<int>(
                    LedgerSchema.Sql(request.IssuerId,
                    @"INSERT INTO dbo.LedgerJournalEntry
                        (IssuerType, IssuerId, SeriesId, Number, FiscalYearId, AccountingDate,
                         EventDate, RegisteredUtc, Description, CounterpartyType, CounterpartyId,
                         CounterpartyName, SourceType, SourceId, PaymentId, CorrectsEntryId,
                         CreatedByMemberId)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15, @16);
                      SELECT CAST(SCOPE_IDENTITY() AS int);"),
                    entry.IssuerType, entry.IssuerId, entry.SeriesId, entry.Number, entry.FiscalYearId,
                    entry.AccountingDate, entry.EventDate, entry.RegisteredUtc, entry.Description,
                    (object?)entry.CounterpartyType ?? DBNull.Value, (object?)entry.CounterpartyId ?? DBNull.Value,
                    (object?)entry.CounterpartyName ?? DBNull.Value, entry.SourceType,
                    (object?)entry.SourceId ?? DBNull.Value, (object?)entry.PaymentId ?? DBNull.Value,
                    (object?)entry.CorrectsEntryId ?? DBNull.Value, entry.CreatedByMemberId);

                var lineNo = 1;
                foreach (var line in built)
                {
                    line.JournalEntryId = entry.Id;
                    line.LineNumber = lineNo++;

                    db.Execute(
                        LedgerSchema.Sql(request.IssuerId,
                        @"INSERT INTO dbo.LedgerJournalEntryLine
                            (JournalEntryId, LineNumber, AccountNumber, AccountName, Debit, Credit,
                             Text, VatRate, VatAmount, ProjectId, ProjectName)
                          VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10)"),
                        line.JournalEntryId, line.LineNumber, line.AccountNumber, line.AccountName,
                        line.Debit, line.Credit, (object?)line.Text ?? DBNull.Value,
                        (object?)line.VatRate ?? DBNull.Value, (object?)line.VatAmount ?? DBNull.Value,
                        (object?)line.ProjectId ?? DBNull.Value, (object?)line.ProjectName ?? DBNull.Value);
                }

                db.Execute(
                    LedgerSchema.Sql(request.IssuerId,
                    @"INSERT INTO dbo.LedgerAuditEvent
                        (OccurredUtc, MemberId, Action, ObjectType, ObjectId, Detail)
                      VALUES (@0, @1, @2, @3, @4, @5)"),
                    DateTime.UtcNow,
                    request.CreatedByMemberId,
                    request.CorrectsEntryId is null ? LedgerAuditAction.Posted : LedgerAuditAction.Corrected,
                    "JournalEntry",
                    entry.Id,
                    $"{{\"number\":{number},\"source\":\"{request.SourceType}\"" +
                        (request.CorrectsEntryId is int c ? $",\"corrects\":{c}}}" : "}"));

                tx.Complete();

                return new LedgerPostingResult
                {
                    EntryId = entry.Id,
                    Number = number,
                    FormattedNumber = LedgerNumberAllocator.Format(prefix, number),
                    FiscalYearId = fiscalYear.Id,
                    Lines = built
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Bokföringen misslyckades för utställare {Typ}/{Id}, källa {Kalla}/{KallId}.",
                    request.IssuerType, request.IssuerId, request.SourceType, request.SourceId);

                return LedgerPostingResult.Failed(
                    "Bokföringen kunde inte skrivas. Ingenting har sparats — försök igen.");
            }
        }

        /// <summary>
        /// Bokför en RÄTTELSE av en befintlig verifikation: samma rader med debet och kredit
        /// omkastade, och <c>CorrectsEntryId</c> satt.
        ///
        /// <para><b>Originalet rörs aldrig.</b> Det är hela granskningsbarhetens fundament, och det
        /// är också varför en rättelse måste vara en egen metod i stället för ett flaggat
        /// <c>Post</c>-anrop: kopplingen till originalet kan bara sättas när raden skrivs.</para>
        ///
        /// <para>⚠️ Momsen kastas om med raden. En rättelse av en momspliktig försäljning måste
        /// vända även momsen, annars står utgående moms kvar på en intäkt som tagits tillbaka.</para>
        /// </summary>
        public LedgerPostingResult CreateCorrection(
            int entryId, int byMemberId, string reason, DateTime? accountingDate = null)
        {
            using var db = _databaseFactory.CreateDatabase();

            // Verifikationens EGET id avslojar schemat: negativa id finns bara i sandladan.
            // Det ar hela vinsten med att tecknet betyder samma sak overallt.
            var original = db.SingleOrDefault<LedgerJournalEntry>(
                LedgerSchema.Sql(entryId, "SELECT * FROM dbo.LedgerJournalEntry WHERE Id = @0"), entryId);

            if (original is null)
                return LedgerPostingResult.Failed("Verifikationen som skulle rättas hittades inte.");

            var lines = db.Fetch<LedgerJournalEntryLine>(
                LedgerSchema.Sql(entryId,
                    "SELECT * FROM dbo.LedgerJournalEntryLine WHERE JournalEntryId = @0 ORDER BY LineNumber"),
                entryId);

            if (lines.Count == 0)
                return LedgerPostingResult.Failed("Verifikationen saknar konteringsrader att rätta.");

            var request = new LedgerPostingRequest
            {
                IssuerType = original.IssuerType,
                IssuerId = original.IssuerId,
                // Rättelsen bokförs I DAG som förval, inte på originalets datum: perioden då felet
                // upptäcktes är den som är sann, och originalets period kan vara stängd.
                AccountingDate = (accountingDate ?? DateTime.Today).Date,
                EventDate = original.EventDate,
                Description = string.IsNullOrWhiteSpace(reason)
                    ? $"Rättelse av verifikation {original.Number}"
                    : $"Rättelse av verifikation {original.Number}: {reason}",
                CounterpartyType = original.CounterpartyType,
                CounterpartyId = original.CounterpartyId,
                CounterpartyName = original.CounterpartyName,
                SourceType = original.SourceType,
                SourceId = original.SourceId,
                PaymentId = original.PaymentId,
                CorrectsEntryId = original.Id,
                CreatedByMemberId = byMemberId
            };

            request.Lines.AddRange(BuildCorrectionLines(lines));

            return Post(request);
        }

        /// <summary>
        /// Vänder originalets rader till en rättelse. <b>Ren funktion</b>, av samma skäl som
        /// <see cref="BuildLines"/>: det är den här mappningen som är lätt att tappa något i, och
        /// det den tappar syns inte förrän någon jämför två rapporter.
        ///
        /// <para>Raderna byggs med UTTRYCKLIGA kontonummer och inte med roller: rättelsen ska
        /// träffa exakt de konton originalet träffade, även om föreningen pekat om en roll sedan
        /// dess.</para>
        /// </summary>
        internal static List<LedgerPostingLine> BuildCorrectionLines(
            IEnumerable<LedgerJournalEntryLine> original)
            => original.Select(line => new LedgerPostingLine
            {
                AccountNumber = line.AccountNumber,
                // Debet och kredit byter plats — det är hela rättelsen.
                Debit = line.Credit,
                Credit = line.Debit,
                Text = line.Text,
                // Momsen är redan uträknad i originalet och ligger på en egen rad; en ny uträkning
                // här skulle lägga moms på momsen.
                VatRate = 0,
                // ⚠️ PROJEKTET MÅSTE FÖLJA MED, per rad och ur ORIGINALET. En rättelse som tappar
                // projektet lämnar kostnaden kvar i projektets resultat medan den är borta ur
                // bokföringens — och då visar projektrapporten ett underskott som ingen kan hitta i
                // böckerna. Samma familj av fel som momsen ovan.
                ProjectId = line.ProjectId
            }).ToList();

        // ── Uppbyggnaden ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bygger konteringsraderna ur begäran. <b>Rör ingen databas</b> — allt den behöver kommer
        /// in som färdiga uppslagstabeller, och därför går den att pröva som en ren funktion.
        /// <c>internal</c> just för det; publik hade inbjudit anropare att gå förbi
        /// <see cref="Post"/>, som är den enda vägen in i liggaren.
        /// </summary>
        internal static List<LedgerJournalEntryLine> BuildLines(
            LedgerPostingRequest request,
            IReadOnlyDictionary<int, LedgerAccount> accounts,
            IReadOnlyDictionary<string, int> roles,
            IReadOnlyDictionary<int, LedgerProject> projects,
            out string? error)
        {
            error = null;
            var result = new List<LedgerJournalEntryLine>();

            foreach (var line in request.Lines)
            {
                if (line.Debit < 0 || line.Credit < 0)
                {
                    error = "Ett belopp kan inte vara negativt — vänd på debet och kredit i stället.";
                    return result;
                }

                if (line.Debit > 0 == line.Credit > 0)
                {
                    error = "Varje rad måste ha antingen ett debetbelopp eller ett kreditbelopp.";
                    return result;
                }

                var accountNumber = line.AccountNumber;
                if (accountNumber is null)
                {
                    if (string.IsNullOrWhiteSpace(line.Role))
                    {
                        error = "En konteringsrad måste ange antingen en kontoroll eller ett konto.";
                        return result;
                    }

                    if (!roles.TryGetValue(line.Role!, out var mapped))
                    {
                        // Namnger rollen: "det gick inte att bokföra" utan att säga vilket konto som
                        // saknas är en återvändsgränd för den som ska åtgärda det.
                        error = $"Kontorollen \"{line.Role}\" saknar konto hos föreningen. "
                              + "Komplettera kontoinställningarna.";
                        return result;
                    }

                    accountNumber = mapped;
                }

                if (!accounts.TryGetValue(accountNumber.Value, out var account))
                {
                    error = $"Kontot {accountNumber} finns inte i föreningens kontoplan.";
                    return result;
                }

                // Radens eget projekt vinner över begärans. Det är så en betalning som täcker två
                // projekt bokförs; den vanliga vägen är att bara begäran bär ett projekt.
                var projectId = line.ProjectId ?? request.ProjectId;
                if (projectId is int wanted && !projects.ContainsKey(wanted))
                {
                    // Namnger id:t: ett projekt som tillhör en ANNAN förening ser likadant ut här
                    // som ett som inte finns, och båda är fel att bokföra på.
                    error = $"Projektet {wanted} finns inte hos föreningen.";
                    return result;
                }

                var gross = line.Debit > 0 ? line.Debit : line.Credit;
                var rate = line.VatRate ?? account.DefaultVatRate ?? 0m;
                var (net, vat) = LedgerAmounts.SplitGross(gross, rate);

                var posted = new LedgerJournalEntryLine
                {
                    // ⚠️ Nummer OCH namn som snapshot — kontoplanen får byggas om utan att
                    // historiken skrivs om.
                    AccountNumber = account.Number,
                    AccountName = account.Name,
                    Debit = line.Debit > 0 ? net : 0m,
                    Credit = line.Credit > 0 ? net : 0m,
                    Text = line.Text,
                    VatRate = vat > 0 ? rate : null,
                    VatAmount = vat > 0 ? vat : null
                };

                StampProject(posted, projectId, projects);
                result.Add(posted);

                if (vat <= 0) continue;

                // Momsen balanserar på EGEN rad. Fälten ovan är metadata på källraden.
                var outgoing = line.VatIsOutgoing ?? LedgerAmounts.IsOutgoingVatAccount(account.Number);
                var vatRole = outgoing ? LedgerAccountRoles.VatOutgoing : LedgerAccountRoles.VatIncoming;

                if (!roles.TryGetValue(vatRole, out var vatAccountNumber)
                    || !accounts.TryGetValue(vatAccountNumber, out var vatAccount))
                {
                    error = $"Kontot för {(outgoing ? "utgående" : "ingående")} moms saknas hos "
                          + "föreningen, så en momspliktig post kan inte bokföras.";
                    return result;
                }

                var vatLine = new LedgerJournalEntryLine
                {
                    AccountNumber = vatAccount.Number,
                    AccountName = vatAccount.Name,
                    // Momsen följer källradens sida: en intäkt i kredit ger moms i kredit.
                    Debit = line.Debit > 0 ? vat : 0m,
                    Credit = line.Credit > 0 ? vat : 0m,
                    Text = $"Moms {rate:0.##} %"
                };

                // ⚠️ Momsraden ärver källradens projekt. Gjorde den inte det skulle projektets
                // resultat inte gå ihop med bokföringens — kioskens intäkt hade legat på projektet
                // och dess moms utanför.
                StampProject(vatLine, projectId, projects);
                result.Add(vatLine);
            }

            return result;
        }

        internal static LedgerJournalEntryLine? BuildRoundingLine(
            decimal imbalance,
            IReadOnlyDictionary<int, LedgerAccount> accounts,
            IReadOnlyDictionary<string, int> roles,
            out string? error)
        {
            error = null;

            if (!roles.TryGetValue(LedgerAccountRoles.Rounding, out var number)
                || !accounts.TryGetValue(number, out var account))
            {
                error = "Avrundningskontot saknas hos föreningen, så öresdifferensen kan inte bokföras.";
                return null;
            }

            // Överskott i debet balanseras med en kredit, och tvärtom.
            return new LedgerJournalEntryLine
            {
                AccountNumber = account.Number,
                AccountName = account.Name,
                Debit = imbalance < 0 ? Math.Abs(imbalance) : 0m,
                Credit = imbalance > 0 ? imbalance : 0m,
                Text = "Öresavrundning"
            };
        }

        /// <summary>
        /// Kan den här utställaren bokföra på det här datumet? <c>null</c> = ja, annars skälet i
        /// klartext.
        ///
        /// <para><b>⚠️ FINNS FÖR ATT PENGAR INTE SKA TAS EMOT SOM INTE GÅR ATT BOKFÖRA.</b>
        /// <see cref="Post"/> vägrar korrekt när räkenskapsåret saknas — men då har betalaren redan
        /// swishat, och arrangören står med pengar hen inte kan kvittera. Den här kontrollen låter
        /// anroparen fråga FÖRE hen erbjuder betalning.</para>
        ///
        /// <para><b>⚠️ SAMMA regler som <see cref="Post"/>, aldrig en avskrift.</b> Två
        /// uppfattningar om när bokföring är möjlig är fria att glida isär, och då skulle
        /// betalningen erbjudas precis när den inte går att ta emot. Lägger du en spärr i
        /// <c>Post</c> ska den speglas här.</para>
        ///
        /// <para>⚠️ Svarar <c>null</c> vid läsfel: en trasig kontroll får inte stänga av
        /// betalningen för en förening vars liggare är hel. <see cref="Post"/> är den som verkligen
        /// vägrar.</para>
        /// </summary>
        /// <summary>
        /// Ska den här betalningen bokföras hos oss — och om inte, finns det något att säga?
        ///
        /// <para><b>⚠⚠ BOKFÖRING ÄR OPT-IN, OCH OPT-IN:EN ÄR <see cref="LedgerIssuerShape"/>.</b>
        /// De flesta klubbar vill skapa ett evenemang och kunna ta betalt; de bokför i sitt eget
        /// program eller hos en byrå. Att kräva ett upplagt räkenskapsår för att ens få visa en
        /// Swish-kod gör produkten obrukbar för dem — och löser ingenting, eftersom pengarna
        /// rör sig ändå: klubben swishar vid sidan om och då har vi ingen registrering alls.
        /// Samma regel som lånevapnen redan följer: <b>systemet ska registrera, inte grinda.</b></para>
        ///
        /// <para>⚠️ Ingen inställningsrad = föreningen har inte valt något = vi bokför inte.
        /// Det är den säkra riktningen: vi påstår hellre att vi INTE bokfört än tvärtom.</para>
        ///
        /// <para>⚠️ <see cref="LedgerPostingDecision.SkipReason"/> är <c>null</c> när det är
        /// helt normalt att inte bokföra, och en text när en förening som RÄKNAR med bokföring
        /// inte kan få den. Skillnaden är vad arrangören ska få veta — tystnad i det andra fallet
        /// vore en utebliven verifikation som ingen upptäcker.</para>
        /// </summary>
        public LedgerPostingDecision DecidePosting(int issuerType, int issuerId, DateTime accountingDate)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var shape = db.ExecuteScalar<string>(
                    LedgerSchema.Sql(issuerId, "SELECT TOP 1 Shape FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1"),
                    issuerType, issuerId);

                // ⚠️ Regeln själv bor i LedgerPostingDecision.For — den är ren och därmed
                // provbar. Här hämtas bara de två uppgifter den behöver.
                // PostingBlockedReason frågas BARA när formen säger att vi bokför: annars är
                // svaret ointressant, och en tom liggare hade gett en onödig fråga per betalning.
                if (!LedgerIssuerShape.KeepsBooks(shape))
                    return LedgerPostingDecision.For(shape, null);

                return LedgerPostingDecision.For(
                    shape, PostingBlockedReason(issuerType, issuerId, accountingDate));
            }
            catch (Exception ex)
            {
                // ⚠️ Går det inte att avgöra bokför vi INTE, men vi säger det. Att posta i blindo
                // kan fälla bekräftelsen och då står arrangören med pengar hen inte kan kvittera;
                // en obokförd rad går däremot att bokföra i efterhand.
                _logger.LogWarning(ex,
                    "Kunde inte avgöra bokföringsläget för utställare {Type}/{Id} — betalningen "
                    + "bekräftas utan verifikation.", issuerType, issuerId);

                return LedgerPostingDecision.Skip(
                    "Bokföringsläget gick inte att läsa, så betalningen är inte bokförd.");
            }
        }

        public string? PostingBlockedReason(int issuerType, int issuerId, DateTime accountingDate)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var year = db.FirstOrDefault<LedgerFiscalYear>(
                    LedgerSchema.Sql(issuerId, @"SELECT * FROM dbo.LedgerFiscalYear
                       WHERE IssuerType = @0 AND IssuerId = @1
                         AND StartDate <= @2 AND EndDate >= @2
                       ORDER BY Year"),
                    issuerType, issuerId, accountingDate.Date);

                if (year is null)
                    return $"Det finns inget räkenskapsår som omfattar {accountingDate:yyyy-MM-dd}. "
                         + "Lägg upp året i ekonomiinställningarna först.";

                if (year.Status == LedgerFiscalYearStatus.Established)
                    return $"Räkenskapsåret {year.Year} är fastställt av årsmötet och tar inte emot fler poster.";

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Kunde inte kontrollera bokföringsberedskap för utställare {Type}/{Id}.",
                    issuerType, issuerId);
                return null;
            }
        }

        private static LedgerFiscalYear? ResolveFiscalYear(IDatabase db, LedgerPostingRequest request)
            => db.FirstOrDefault<LedgerFiscalYear>(
                LedgerSchema.Sql(request.IssuerId, @"SELECT * FROM dbo.LedgerFiscalYear
                   WHERE IssuerType = @0 AND IssuerId = @1
                     AND StartDate <= @2 AND EndDate >= @2
                   ORDER BY Year"),
                request.IssuerType, request.IssuerId, request.AccountingDate.Date);

        private static Dictionary<int, LedgerAccount> LoadAccounts(IDatabase db, int issuerType, int issuerId)
            => db.Fetch<LedgerAccount>(
                    LedgerSchema.Sql(issuerId, "SELECT * FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1"),
                    issuerType, issuerId)
                 .ToDictionary(a => a.Number);

        /// <summary>
        /// Sätter projektets id och namnsnapshot på en rad. Null lämnar raden omärkt, vilket är
        /// det normala för en förening som inte använder projekt.
        /// </summary>
        private static void StampProject(
            LedgerJournalEntryLine line,
            int? projectId,
            IReadOnlyDictionary<int, LedgerProject> projects)
        {
            if (projectId is not int id || !projects.TryGetValue(id, out var project)) return;

            line.ProjectId = project.Id;
            line.ProjectName = project.Name;
        }

        /// <summary>
        /// Föreningens projekt, <b>inklusive stängda</b>.
        ///
        /// <para>⚠️ Ett stängt projekt tas bort ur väljaren, men inte ur bokföringen. En kostnad
        /// för en tävling kommer ofta in flera veckor efter att den avslutats — Hallands krets
        /// bokförde SSM-fakturor en månad efteråt — och att vägra den posten hade tvingat den till
        /// "inget projekt", vilket är sämre än att låta den träffa rätt projekt sent.</para>
        /// </summary>
        private static Dictionary<int, LedgerProject> LoadProjects(IDatabase db, int issuerType, int issuerId)
            => db.Fetch<LedgerProject>(
                    LedgerSchema.Sql(issuerId, "SELECT * FROM dbo.LedgerProject WHERE IssuerType = @0 AND IssuerId = @1"),
                    issuerType, issuerId)
                 .ToDictionary(p => p.Id);

        private static Dictionary<string, int> LoadRoleMap(IDatabase db, int issuerType, int issuerId)
            => db.Fetch<LedgerAccountRole>(
                    LedgerSchema.Sql(issuerId, "SELECT * FROM dbo.LedgerAccountRole WHERE IssuerType = @0 AND IssuerId = @1"),
                    issuerType, issuerId)
                 .ToDictionary(r => r.RoleKey, r => r.AccountNumber);
    }
}
