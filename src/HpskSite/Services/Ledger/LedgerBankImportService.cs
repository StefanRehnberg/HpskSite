using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Bankavstämningen (P11): läser in ett kontoutdrag, parar ihop raderna med bokföringen och
    /// svarar på vad som är kvar.
    ///
    /// <para><b>⚠️ Fullständigheten bevisas inte av bokföringen.</b> Bokföringen ser vad som
    /// bokförts; bara kontoutdraget vet vad som faktiskt rört kontot. Det är den här ytan som gör
    /// att Översiktens andra panel kan säga "allt stämmer".</para>
    ///
    /// <para><b>⚠️ Inget bank-API.</b> PSD2 är licens- och kostnadsdrivet och valdes bort —
    /// föreningen laddar upp filen själv och ingen tredje part får läsrätt till kontot.</para>
    /// </summary>
    public class LedgerBankImportService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerBankImportService> _logger;

        public LedgerBankImportService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerBankImportService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>Hur en fil tolkas. Kommer från operatörens mappning, inte från oss.</summary>
        public class Mapping
        {
            public int Date { get; set; } = -1;
            public int Text { get; set; } = -1;
            public int Amount { get; set; } = -1;

            /// <summary>Satt bara när banken delar beloppet i in- och ut-kolumner.</summary>
            public int AmountOut { get; set; } = -1;

            public int Balance { get; set; } = -1;
            public char Delimiter { get; set; } = ';';
            public int HeaderRow { get; set; }
        }

        public class ParseResult
        {
            public List<LedgerBankRow> Rows { get; } = new();

            /// <summary>Rader som inte gick att tolka, med skälet. <b>Sägs alltid.</b></summary>
            public List<string> Skipped { get; } = new();
        }

        /// <summary>
        /// Förhandsläsning: vad tror vi att filen innehåller?
        /// <para><b>⚠️ Skriver ingenting.</b> Operatören ska se och kunna rätta mappningen innan
        /// något importeras — en tyst felmappning ger ett kontoutdrag som stäms av mot fel
        /// siffror.</para>
        /// </summary>
        public (Mapping Mapping, List<string[]> Sample, string[] Header) Preview(byte[] bytes)
        {
            var text = BankStatementFormat.Decode(bytes);
            var delimiter = BankStatementFormat.SniffDelimiter(text);

            var rows = text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Trim().Length > 0)
                .Select(l => BankStatementFormat.SplitLine(l, delimiter))
                .ToList();

            var headerRow = BankStatementFormat.FindHeaderRow(rows);

            // ⚠️ Kände vi inte igen rubrikerna är rubrikraden ändå oftast raden NÄRMAST FÖRE den
            //    första raden med ett datum — om den har lika många kolumner. Då får kassören
            //    bankens egna namn att välja bland i stället för "Kolumn 1…N", och raden hoppas över
            //    i stället för att läsas som en transaktion.
            if (headerRow < 0)
            {
                var firstData = rows.FindIndex(r => r.Length >= 2 && r.Any(c => BankStatementFormat.TryDate(c, out _)));
                if (firstData > 0 && rows[firstData - 1].Length == rows[firstData].Length
                    && !rows[firstData - 1].Any(c => BankStatementFormat.TryDate(c, out _)))
                    headerRow = firstData - 1;
            }

            var header = headerRow >= 0 ? rows[headerRow] : Array.Empty<string>();
            var guess = BankStatementFormat.GuessColumns(header);

            // ⚠️⚠️ KÄNNS RUBRIKERNA INTE IGEN MÅSTE KOLUMNERNA ÄNDÅ GÅ ATT VÄLJA. Förut blev
            //    rubrikraden tom, och ytan sa "peka ut dem själv" över rutor med bara "— saknas —"
            //    (felrapport 2026-09-24). Går filen att dela upp får operatören "Kolumn 1…N" och
            //    exempelraderna, och kan mappa själv. HeaderRow = -1: ingen rad hoppas över, och en
            //    informationsrad överst redovisas som överhoppad i stället för att tyst försvinna.
            if (headerRow < 0)
            {
                var width = rows.Count == 0 ? 0 : rows
                    .GroupBy(r => r.Length).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
                    .First().Key;

                if (width >= 2)
                    header = Enumerable.Range(1, width).Select(i => $"Kolumn {i}").ToArray();
            }

            var mapping = new Mapping
            {
                Date = guess.Date,
                Text = guess.Text,
                Amount = guess.Amount,
                AmountOut = guess.AmountOut,
                Balance = guess.Balance,
                Delimiter = delimiter,
                HeaderRow = headerRow
            };

            // Ett par rader under rubriken räcker för att operatören ska känna igen sin fil.
            var sample = rows.Skip(headerRow + 1).Take(5).ToList();

            // ⚠️ DATUMET UR DATAT när rubrikerna inte räckte: den första kolumn där VARJE
            //    exempelrad är ett otvetydigt datum. Det är säkert just för datum — TryDate vägrar
            //    03/04/2026 — medan ett belopp aldrig gissas ur datat (radnummer ser ut som belopp).
            if (mapping.Date < 0 && sample.Count > 0)
            {
                var width = sample.Max(r => r.Length);
                for (int c = 0; c < width; c++)
                    if (sample.All(r => c < r.Length && BankStatementFormat.TryDate(r[c], out _)))
                    { mapping.Date = c; break; }
            }

            return (mapping, sample, header);
        }

        /// <summary>
        /// Tolkar filen enligt mappningen. Ren funktion — rör ingen databas, så den går att pröva.
        /// </summary>
        public ParseResult Parse(byte[] bytes, Mapping m)
        {
            var result = new ParseResult();
            var text = BankStatementFormat.Decode(bytes);

            var lines = text.Split('\n')
                .Select(l => l.TrimEnd('\r'))
                .Where(l => l.Trim().Length > 0)
                .ToList();

            int lineNo = 0;

            for (int i = m.HeaderRow + 1; i < lines.Count; i++)
            {
                var f = BankStatementFormat.SplitLine(lines[i], m.Delimiter);

                string At(int idx) => idx >= 0 && idx < f.Length ? f[idx] : "";

                if (!BankStatementFormat.TryDate(At(m.Date), out var date))
                {
                    // ⚠️ SÄGS, aldrig tyst överhoppad. En bortfallen rad är en differens
                    //    operatören annars får leta efter i kontoutdraget för hand.
                    if (f.Any(c => c.Trim().Length > 0))
                        result.Skipped.Add($"Rad {i + 1}: inget läsbart datum ({At(m.Date)}).");
                    continue;
                }

                decimal amount;

                if (m.AmountOut >= 0)
                {
                    // Banken delar beloppet i två kolumner. Uttagskolumnen är alltid ett
                    // utflöde — ⚠️ den bär ofta INGET minustecken, så tecknet sätts här.
                    var hasIn = BankStatementFormat.TryAmount(At(m.Amount), out var amtIn);
                    var hasOut = BankStatementFormat.TryAmount(At(m.AmountOut), out var amtOut);

                    if (!hasIn && !hasOut)
                    {
                        result.Skipped.Add($"Rad {i + 1}: inget läsbart belopp.");
                        continue;
                    }

                    amount = (hasIn ? amtIn : 0m) - (hasOut ? Math.Abs(amtOut) : 0m);
                }
                else if (!BankStatementFormat.TryAmount(At(m.Amount), out amount))
                {
                    result.Skipped.Add($"Rad {i + 1}: beloppet gick inte att tolka ({At(m.Amount)}).");
                    continue;
                }

                BankStatementFormat.TryAmount(At(m.Balance), out var balance);

                var rowText = At(m.Text);

                result.Rows.Add(new LedgerBankRow
                {
                    LineNumber = ++lineNo,
                    BookedDate = date,
                    Text = rowText.Length > 400 ? rowText[..400] : rowText,
                    Amount = amount,
                    Balance = m.Balance >= 0 ? balance : null,
                    Reference = ExtractReference(rowText)
                });
            }

            return result;
        }

        /// <summary>
        /// Plockar ut något som ser ut som vår betalningsreferens ur bankens text.
        ///
        /// <para><b>⚠️ Bäst möjliga gissning, aldrig ett krav.</b> Betalaren kan ha skrivit om
        /// meddelandet, och banken kapar ofta texten. Hittas ingen referens matchas raden på
        /// belopp och datum som förut — referensen är en genväg, inte grunden.</para>
        /// </summary>
        private static string? ExtractReference(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Fakturanumrens form: 1234-5678-1 eller 1234-club-5678-1.
            var m = System.Text.RegularExpressions.Regex.Match(
                text, @"\b\d{3,6}-(?:club-)?\d{1,6}-\d{1,4}\b");

            return m.Success ? m.Value : null;
        }

        /// <summary>
        /// Sparar utdraget och kör den automatiska matchningen.
        /// <para>Returnerar importens id, eller null när ingenting gick att läsa.</para>
        /// </summary>
        public int? Store(
            int issuerType, int issuerId, int accountNumber, string fileName,
            ParseResult parsed, int byMemberId)
        {
            if (parsed.Rows.Count == 0) return null;

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var from = parsed.Rows.Min(r => r.BookedDate);
            var to = parsed.Rows.Max(r => r.BookedDate);

            // ⚠️⚠️ Saldona tas ur den KRONOLOGISKT första och sista raden, inte ur filens.
            //    Förut stod här att filens egen ordning var rätt — men koden tog ändå första
            //    raden som ingående, så en fil med nyast först fick det ingående saldot som
            //    utgående (Michael Henriksson, 2026-09-25). Se BankStatementFormat.Chronological.
            //    Raderna numreras också om kronologiskt, så listorna läses uppifrån och ned i
            //    tidsordning i stället för nerifrån.
            var chrono = BankStatementFormat.Chronological(parsed.Rows);
            for (int n = 0; n < chrono.Count; n++) chrono[n].LineNumber = n + 1;
            var (opening, closing) = BankStatementFormat.Balances(chrono);

            var importId = ldb.ExecuteScalar<int>(
                @"INSERT INTO dbo.LedgerBankImport
                    (IssuerType, IssuerId, AccountNumber, FileName, PeriodFrom, PeriodTo,
                     OpeningBalance, ClosingBalance, RowCount_, ImportedByMemberId, ImportedUtc)
                  OUTPUT INSERTED.Id
                  VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10)",
                issuerType, issuerId, accountNumber, fileName, from, to,
                (object?)opening ?? DBNull.Value, (object?)closing ?? DBNull.Value,
                parsed.Rows.Count, byMemberId, DateTime.UtcNow);

            foreach (var r in parsed.Rows)
            {
                ldb.Execute(
                    @"INSERT INTO dbo.LedgerBankRow
                        (ImportId, IssuerType, IssuerId, LineNumber, BookedDate, Text, Amount,
                         Balance, Reference)
                      VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8)",
                    importId, issuerType, issuerId, r.LineNumber, r.BookedDate, r.Text, r.Amount,
                    (object?)r.Balance ?? DBNull.Value, (object?)r.Reference ?? DBNull.Value);
            }

            AutoMatch(issuerType, issuerId, importId, byMemberId);
            return importId;
        }

        /// <summary>
        /// Parar ihop det som är <b>entydigt</b>.
        ///
        /// <para><b>⚠️⚠️ BARA DET ENTYDIGA.</b> Finns två bokföringsrader som passar lika bra
        /// lämnas båda åt operatören. Att ta den första är ett myntkast som ser granskat ut, och
        /// en felaktig matchning döljer en verklig differens i stället för att visa den.</para>
        ///
        /// <para>⚠️ Referensen provas först när den finns: den är ett starkare bevis än
        /// belopp+datum, eftersom två betalningar på samma summa samma vecka är vardag.</para>
        /// </summary>
        public int AutoMatch(int issuerType, int issuerId, int importId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var import = ldb.Fetch<LedgerBankImport>(
                "SELECT * FROM dbo.LedgerBankImport WHERE Id = @0", importId).FirstOrDefault();
            if (import == null) return 0;

            var rows = ldb.Fetch<LedgerBankRow>(
                "SELECT * FROM dbo.LedgerBankRow WHERE ImportId = @0 AND MatchedLineId IS NULL",
                importId);
            if (rows.Count == 0) return 0;

            var lines = ldb.Fetch<CandidateLine>(
                @"SELECT l.Id, l.Debit, l.Credit, e.AccountingDate, e.Description
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND l.AccountNumber = @2
                     AND NOT EXISTS (SELECT 1 FROM dbo.LedgerBankRow b
                                      WHERE b.MatchedLineId = l.Id
                                        AND b.IssuerType = @0 AND b.IssuerId = @1)",
                issuerType, issuerId, import.AccountNumber);

            var taken = new HashSet<int>();
            int matched = 0;

            foreach (var row in rows)
            {
                var candidates = lines
                    .Where(l => !taken.Contains(l.Id))
                    .Where(l => LedgerBankMatching.CouldMatch(
                        row.Amount, row.BookedDate,
                        LedgerBankMatching.SignedMovement(l.Debit, l.Credit), l.AccountingDate))
                    .ToList();

                // Referensen smalnar av när den finns — men bara om den faktiskt träffar något,
                // annars vore en kapad banktext detsamma som "ingen kandidat".
                if (!string.IsNullOrWhiteSpace(row.Reference))
                {
                    var byRef = candidates
                        .Where(c => (c.Description ?? "").Contains(row.Reference!,
                                        StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (byRef.Count > 0) candidates = byRef;
                }

                var pick = LedgerBankMatching.SingleCandidate(candidates);
                if (pick == null) continue;

                ldb.Execute(
                    @"UPDATE dbo.LedgerBankRow
                         SET MatchedLineId = @1, MatchKind = @2, MatchedByMemberId = @3, MatchedUtc = @4
                       WHERE Id = @0 AND MatchedLineId IS NULL",
                    row.Id, pick.Id, LedgerBankMatchKind.Auto, byMemberId, DateTime.UtcNow);

                taken.Add(pick.Id);
                matched++;
            }

            return matched;
        }

        private class CandidateLine
        {
            public int Id { get; set; }
            public decimal Debit { get; set; }
            public decimal Credit { get; set; }
            public DateTime AccountingDate { get; set; }
            public string? Description { get; set; }
        }

        /// <summary>
        /// Avstämningen för ett utdrag: vad som är hopparat, vad som är kvar, och differensen.
        ///
        /// <para><b>⚠️ Läser bokföringens saldo t.o.m. periodens SLUT, inte bara periodens rader.</b>
        /// Ett konto har ett saldo, inte en periodsumma — jämförs bankens slutsaldo med summan av
        /// periodens bokföringsrader blir differensen hela det ingående saldot, varje gång.</para>
        /// </summary>
        public LedgerReconciliation? Reconciliation(int issuerType, int issuerId, int importId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var import = ldb.Fetch<LedgerBankImport>(
                @"SELECT * FROM dbo.LedgerBankImport
                   WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                importId, issuerType, issuerId).FirstOrDefault();

            if (import == null) return null;

            var view = new LedgerReconciliation
            {
                ImportId = import.Id,
                AccountNumber = import.AccountNumber,
                FileName = import.FileName,
                PeriodFrom = import.PeriodFrom,
                PeriodTo = import.PeriodTo,
                RowCount = import.RowCount,
                BankClosingBalance = import.ClosingBalance
            };

            view.AccountName = ldb.Fetch<string>(
                @"SELECT TOP 1 Name FROM dbo.LedgerAccount
                   WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2",
                issuerType, issuerId, import.AccountNumber).FirstOrDefault() ?? "";

            // ⚠️ Kronologiskt även vid läsning: utdrag inlästa före 2026-09-25 lagrades i filens
            //    ordning med saldot ur fel ände. Raderna bär sina egna saldon, så svaret räknas om
            //    här i stället för att lita på importens lagrade ClosingBalance — ingen migrering
            //    och ingen ny inläsning behövs.
            var rows = BankStatementFormat.Chronological(ldb.Fetch<LedgerBankRow>(
                "SELECT * FROM dbo.LedgerBankRow WHERE ImportId = @0 ORDER BY LineNumber", importId));
            var (_, rowClosing) = BankStatementFormat.Balances(rows);
            if (rowClosing.HasValue) view.BankClosingBalance = rowClosing;

            // ⚠️ Serieprefixet bor på SERIEN, inte på verifikationen, så det måste joinas in —
            //    ett nummer utan prefix går inte att slå upp i föreningens egen pärm.
            var lines = ldb.Fetch<ReconLine>(
                @"SELECT l.Id AS LineId, e.Id AS EntryId, s.Prefix, e.Number,
                         e.AccountingDate, e.Description, l.Debit, l.Credit
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                    LEFT JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1 AND l.AccountNumber = @2
                     AND (@3 IS NULL OR e.AccountingDate <= @3)
                   ORDER BY e.AccountingDate, e.Number",
                issuerType, issuerId, import.AccountNumber,
                (object?)import.PeriodTo ?? DBNull.Value);

            // Saldot är summan av ALLA rörelser t.o.m. periodens slut — se metodens kommentar.
            view.LedgerBalance = lines.Sum(l => LedgerBankMatching.SignedMovement(l.Debit, l.Credit));

            var byLineId = lines.ToDictionary(l => l.LineId);
            var matchedLineIds = new HashSet<int>();

            foreach (var r in rows)
            {
                var bank = ToBankView(r);

                if (r.MatchedLineId is int lid && byLineId.TryGetValue(lid, out var l))
                {
                    matchedLineIds.Add(lid);
                    view.Matched.Add(new LedgerReconciliation.MatchedPair
                    {
                        Bank = bank,
                        Ledger = ToLineView(l),
                        Kind = r.MatchKind ?? ""
                    });
                }
                else
                {
                    // ⚠️ En matchning som pekar på en rad utanför perioden räknas som OMATCHAD
                    //    här. Att visa den som hopparad medan motparten inte syns i listan gör
                    //    differensen omöjlig att förklara.
                    view.BankOnly.Add(bank);
                }
            }

            // ⚠️⚠️ BARA RADER INOM UTDRAGETS PERIOD. Saldot ovanför räknas kumulativt — det är
            //    hela poängen med ett saldo — men en bokning från mars kan omöjligen finnas i ett
            //    utdrag för 19–22 september, och att lista den som "syns inte på kontot" är
            //    precis det brus F6 säger att vi inte ska producera.
            //
            //    ⚠️ INGEN MÄTNING ATT LUTA SIG MOT ÄNNU. Dev-fixturens 34 bokförda rader på 1931
            //    ligger ALLA inom perioden, så filtret ändrar ingenting där — regeln är riktig i
            //    sak men oprövad i drift. (Marsraderna som såg ut att motbevisa den ligger på
            //    1930, ett annat konto.) Skriv inte in en siffra här utan att ha mätt den.
            //
            //    ⚠️ Arbetsfördelningen: RADERNA stäms av inom perioden, SALDOT bevisar allt före
            //    den. En gammal bokning som aldrig nådde banken syns alltså ändå — som en
            //    differens, vilket är den enda plats den ärligt kan synas i det här utdraget.
            var from = view.PeriodFrom?.AddDays(-LedgerBankMatching.DateToleranceDays);
            var to = view.PeriodTo?.AddDays(LedgerBankMatching.DateToleranceDays);

            foreach (var l in lines.Where(l => !matchedLineIds.Contains(l.LineId))
                                   .Where(l => (from is null || l.AccountingDate >= from)
                                               && (to is null || l.AccountingDate <= to)))
                view.LedgerOnly.Add(ToLineView(l));

            view.MatchedCount = view.Matched.Count;
            return view;
        }

        private static LedgerReconciliation.BankRowView ToBankView(LedgerBankRow r) => new()
        {
            Id = r.Id,
            LineNumber = r.LineNumber,
            BookedDate = r.BookedDate,
            Text = r.Text,
            Amount = r.Amount,
            Reference = r.Reference
        };

        private static LedgerReconciliation.LedgerLineView ToLineView(ReconLine l) => new()
        {
            LineId = l.LineId,
            EntryId = l.EntryId,
            EntryNumber = $"{l.Prefix}{l.Number}",
            AccountingDate = l.AccountingDate,
            Description = l.Description ?? "",
            Movement = LedgerBankMatching.SignedMovement(l.Debit, l.Credit)
        };

        private class ReconLine
        {
            public int LineId { get; set; }
            public int EntryId { get; set; }
            public string? Prefix { get; set; }
            public int Number { get; set; }
            public DateTime AccountingDate { get; set; }
            public string? Description { get; set; }
            public decimal Debit { get; set; }
            public decimal Credit { get; set; }
        }

        /// <summary>
        /// Kort läge för Översiktens andra panel: har någon stämt av, och stämde det?
        ///
        /// <para><b>⚠️ EGEN, LÄTT FRÅGA — inte <see cref="Reconciliation"/>.</b> Den läser hela
        /// kontots bokföringsrader för att kunna summera saldot, och Översikt är den panel som
        /// laddas först och oftast. Panelen behöver bara veta om det finns ett utdrag och om
        /// något är kvar.</para>
        ///
        /// <para>⚠️ Returnerar null när inget utdrag finns. Panelen får då säga <i>"inte avstämt
        /// än"</i> — och den får ALDRIG säga "0 kr, allt stämmer" på en avstämning som inte
        /// gjorts. Se panelens egen regel: noll fel ska visas lika stort som fel.</para>
        /// </summary>
        /// <summary>
        /// Avstämningsläget för ETT räkenskapsår: raderna på kontoutdragen som ligger i året.
        ///
        /// <para>⚠️ Revisorn granskar ett bestämt år. Den osprungliga läsningen tog det SENASTE
        /// inlästa utdraget, så en revisor som granskade förra året i mars fick innevarande års
        /// två veckor av kontoutdrag — "alla 14 rader har en motpart" om fel år.</para>
        /// </summary>
        public (DateTime? PeriodTo, int Unmatched, int Rows)? SummaryForYear(
            int issuerType, int issuerId, DateTime from, DateTime to)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var r = ldb.Fetch<YearSummaryRow>(
                    @"SELECT COUNT(*) AS Rows_,
                             SUM(CASE WHEN r.MatchedLineId IS NULL THEN 1 ELSE 0 END) AS Unmatched,
                             MAX(r.BookedDate) AS LastDate
                        FROM dbo.LedgerBankRow r
                       WHERE r.IssuerType = @0 AND r.IssuerId = @1
                         AND r.BookedDate >= @2 AND r.BookedDate <= @3",
                    issuerType, issuerId, from.Date, to.Date).FirstOrDefault();

                if (r is null || r.Rows_ == 0) return null;
                return (r.LastDate, r.Unmatched ?? 0, r.Rows_);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa avstämningsläget för {Typ}/{Id}.", issuerType, issuerId);
                return null;
            }
        }

        private class YearSummaryRow
        {
            public int Rows_ { get; set; }
            public int? Unmatched { get; set; }
            public DateTime? LastDate { get; set; }
        }

        public (DateTime? PeriodTo, int Unmatched, int Rows)? Summary(int issuerType, int issuerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var latest = ldb.Fetch<LedgerBankImport>(
                    @"SELECT TOP 1 * FROM dbo.LedgerBankImport
                       WHERE IssuerType = @0 AND IssuerId = @1
                       ORDER BY PeriodTo DESC, ImportedUtc DESC",
                    issuerType, issuerId).FirstOrDefault();

                if (latest == null) return null;

                var unmatched = ldb.Fetch<int>(
                    @"SELECT COUNT(*) FROM dbo.LedgerBankRow
                       WHERE ImportId = @0 AND MatchedLineId IS NULL", latest.Id).FirstOrDefault();

                return (latest.PeriodTo, unmatched, latest.RowCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa avstämningsläget för {Typ}/{Id}.",
                    issuerType, issuerId);
                return null;
            }
        }

        /// <summary>
        /// Omatchade rader i en bestämd uppsättning kontoutdrag.
        ///
        /// <para><b>⚠️ Skild från <see cref="Summary"/>, som bara ser det SENASTE utdraget.</b>
        /// Bokslutet frågar om de utdrag som täcker årets slut, och det kan vara flera — ett per
        /// konto, eller ett delat i två filer. Att läsa det senaste räcker inte där.</para>
        /// </summary>
        public int UnmatchedInImports(int issuerId, IReadOnlyCollection<int> importIds)
        {
            if (importIds is null || importIds.Count == 0) return 0;

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                // ⚠️ Id:n är int och kommer ur vår egen fråga, så listan kan byggas i strängen.
                //    Antalet utdrag som täcker ett årsskifte är en handfull — parametertaket
                //    (~2100) kan aldrig nås härifrån.
                return ldb.ExecuteScalar<int>(
                    $@"SELECT COUNT(*) FROM dbo.LedgerBankRow
                        WHERE ImportId IN ({string.Join(",", importIds)})
                          AND MatchedLineId IS NULL");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte räkna omatchade rader för {Id}.", issuerId);

                // ⚠️ -1 betyder "vet inte", aldrig "inga". Bokslutssteget måste kunna skilja dem.
                return -1;
            }
        }

        public List<LedgerBankImport> List(int issuerType, int issuerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                return ldb.Fetch<LedgerBankImport>(
                    @"SELECT * FROM dbo.LedgerBankImport
                       WHERE IssuerType = @0 AND IssuerId = @1
                       ORDER BY ImportedUtc DESC",
                    issuerType, issuerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa kontoutdrag för {Typ}/{Id}.",
                    issuerType, issuerId);
                return new List<LedgerBankImport>();
            }
        }

        /// <summary>
        /// En omatchad bankrad med sitt utdrags bankkonto — underlaget för "Bokför…" på raden.
        /// Null om raden inte finns, inte tillhör utställaren eller redan är ihopparad.
        /// </summary>
        public (LedgerBankRow Row, int AccountNumber)? UnmatchedRow(int issuerId, int rowId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var row = ldb.Fetch<LedgerBankRow>(
                "SELECT * FROM dbo.LedgerBankRow WHERE Id = @0 AND IssuerId = @1 AND MatchedLineId IS NULL",
                rowId, issuerId).FirstOrDefault();
            if (row is null) return null;

            var acc = ldb.ExecuteScalar<int>(
                "SELECT AccountNumber FROM dbo.LedgerBankImport WHERE Id = @0 AND IssuerId = @1", row.ImportId, issuerId);
            return acc > 0 ? (row, acc) : null;
        }

        /// <summary>Raden på ett visst konto i en verifikation — bankbenet i en just bokförd post.</summary>
        public int? LineOnAccount(int issuerId, int entryId, int accountNumber)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);
            return ldb.Fetch<int>(
                "SELECT Id FROM dbo.LedgerJournalEntryLine WHERE JournalEntryId = @0 AND AccountNumber = @1",
                entryId, accountNumber).FirstOrDefault() is var id && id != 0 ? id : null;   // ⚠️ negativt i sandlådan
        }

        /// <summary>
        /// Tar bort ett utdrag. Raderna följer med via kaskaden.
        /// <para><b>⚠️ Utdraget måste tillhöra UTSTÄLLAREN.</b> Förut var villkoret bara
        /// <c>Id = @0</c> — en kassör i en förening kunde då ta bort en annan förenings utdrag genom
        /// att gissa ett id (skrivgrinden prövar bara det utställar-id anroparen skickar).</para>
        /// </summary>
        /// <summary>
        /// Hur många av utdragets rader som är ihopparade med bokföringen — automatiskt, för hand
        /// eller via "Bokför…". Null när utdraget inte tillhör utställaren.
        /// </summary>
        public int? MatchedCount(int issuerId, int importId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);
            var exists = ldb.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.LedgerBankImport WHERE Id = @0 AND IssuerId = @1", importId, issuerId);
            if (exists == 0) return null;
            return ldb.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.LedgerBankRow WHERE ImportId = @0 AND IssuerId = @1 AND MatchedLineId IS NOT NULL",
                importId, issuerId);
        }

        public bool Delete(int issuerId, int importId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            // ⚠️ Raderna tas bort UTTRYCKLIGEN, inte via kaskaden: dbo har ON DELETE CASCADE men
            //    sandlådans sbx.LedgerBankRow saknar främmande nyckel helt (mätt 2026-09-25), så
            //    där blev raderna kvar som föräldralösa. Svaret ska inte bero på schemat.
            using var tx = ldb.GetTransaction();
            var gone = ldb.Execute("DELETE FROM dbo.LedgerBankImport WHERE Id = @0 AND IssuerId = @1", importId, issuerId) > 0;
            if (gone)
                ldb.Execute("DELETE FROM dbo.LedgerBankRow WHERE ImportId = @0 AND IssuerId = @1", importId, issuerId);
            tx.Complete();
            return gone;
        }

        /// <summary>
        /// Operatörens egen matchning. <c>lineId = null</c> tar bort den.
        ///
        /// <para><b>⚠️ Både bankraden och bokföringsraden måste tillhöra UTSTÄLLAREN.</b> Förut
        /// räckte ett gissat rad-id för att para ihop — eller lösa upp — en annan förenings rader.</para>
        /// </summary>
        public bool SetMatch(int issuerId, int rowId, int? lineId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            // ⚠️⚠️ BARA 0 (eller null) BETYDER "LÖS UPP". Sandlådans id:n räknas NEDÅT — `<= 0` gjorde
            //    varje manuell ihopparning i en sandlåda till en upplösning (mätt 2026-09-25).
            if (lineId is null or 0)
                return ldb.Execute(
                    @"UPDATE dbo.LedgerBankRow
                         SET MatchedLineId = NULL, MatchKind = NULL,
                             MatchedByMemberId = NULL, MatchedUtc = NULL
                       WHERE Id = @0 AND IssuerId = @1", rowId, issuerId) > 0;

            var lineIsOurs = ldb.ExecuteScalar<int>(
                @"SELECT COUNT(1) FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE l.Id = @0 AND e.IssuerId = @1", lineId, issuerId) > 0;
            if (!lineIsOurs) return false;

            // ⚠️ Det unika indexet är spärren mot att samma bokföringsrad kvittas två gånger.
            //    Fångas felet här blir beskedet begripligt i stället för ett SQL-undantag.
            try
            {
                return ldb.Execute(
                    @"UPDATE dbo.LedgerBankRow
                         SET MatchedLineId = @1, MatchKind = @2, MatchedByMemberId = @3, MatchedUtc = @4
                       WHERE Id = @0 AND IssuerId = @5", rowId, lineId, LedgerBankMatchKind.Manual,
                    byMemberId, DateTime.UtcNow, issuerId) > 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Bokföringsrad {Line} är redan matchad mot en annan bankrad.", lineId);
                return false;
            }
        }
    }
}
