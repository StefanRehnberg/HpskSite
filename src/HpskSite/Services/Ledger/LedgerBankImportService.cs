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
            var header = headerRow >= 0 ? rows[headerRow] : Array.Empty<string>();
            var guess = BankStatementFormat.GuessColumns(header);

            var mapping = new Mapping
            {
                Date = guess.Date,
                Text = guess.Text,
                Amount = guess.Amount,
                AmountOut = guess.AmountOut,
                Balance = guess.Balance,
                Delimiter = delimiter,
                HeaderRow = Math.Max(headerRow, 0)
            };

            // Ett par rader under rubriken räcker för att operatören ska känna igen sin fil.
            var sample = rows.Skip(Math.Max(headerRow, 0) + 1).Take(5).ToList();
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

            // ⚠️ Saldona tas ur FÖRSTA och SISTA raden i filens egen ordning, inte ur datum:
            //    ett utdrag kan vara sorterat nyast först, och då är "sista raden" det
            //    ingående saldot. Ordningen i filen är den banken själv redovisade i.
            var first = parsed.Rows.First();
            var last = parsed.Rows.Last();

            var opening = first.Balance.HasValue ? first.Balance - first.Amount : null;
            var closing = last.Balance;

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

        /// <summary>Tar bort ett utdrag. Raderna följer med via kaskaden.</summary>
        public bool Delete(int issuerId, int importId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            return ldb.Execute("DELETE FROM dbo.LedgerBankImport WHERE Id = @0", importId) > 0;
        }

        /// <summary>Operatörens egen matchning. <c>lineId = null</c> tar bort den.</summary>
        public bool SetMatch(int issuerId, int rowId, int? lineId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            if (lineId is null or <= 0)
                return ldb.Execute(
                    @"UPDATE dbo.LedgerBankRow
                         SET MatchedLineId = NULL, MatchKind = NULL,
                             MatchedByMemberId = NULL, MatchedUtc = NULL
                       WHERE Id = @0", rowId) > 0;

            // ⚠️ Det unika indexet är spärren mot att samma bokföringsrad kvittas två gånger.
            //    Fångas felet här blir beskedet begripligt i stället för ett SQL-undantag.
            try
            {
                return ldb.Execute(
                    @"UPDATE dbo.LedgerBankRow
                         SET MatchedLineId = @1, MatchKind = @2, MatchedByMemberId = @3, MatchedUtc = @4
                       WHERE Id = @0", rowId, lineId, LedgerBankMatchKind.Manual,
                    byMemberId, DateTime.UtcNow) > 0;
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
