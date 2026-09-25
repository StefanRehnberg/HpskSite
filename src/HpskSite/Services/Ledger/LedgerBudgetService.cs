using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Budgeten och uppföljningen mot den — motorn bakom Rapport-ytan
    /// (<c>Ekonomi Exempel/Ekonomi_Wireframes.pdf</c>, sida 4).
    ///
    /// <para><b>⚠️ Rapport är INTE en resultaträkning.</b> Den hör till Bokslut, steg 6. Den här
    /// ytan svarar på en annan fråga: <i>håller vi budgeten?</i> — kassörens vanligaste leverans,
    /// oftare än bokslutet.</para>
    ///
    /// <para><b>⚠️ INGEN PROPORTIONERING.</b> Budgeten är hela årets och jämförs rakt av mot
    /// utfallet i perioden. Att räkna om den till "nio tolftedelar" vore fel för en förening:
    /// medlemsavgifterna kommer i januari, tävlingsintäkterna på hösten och anläggningskostnaderna
    /// när taket går sönder. En proportionerad budget hade visat panik i mars och lugn i december,
    /// båda falska. <b>Ytan måste därför skriva ut perioden</b>, annars läses −49 % på kiosken som
    /// en katastrof när säsongen inte ens är slut.</para>
    ///
    /// <para><b>⚠️ Sömmen:</b> varje fråga går via <see cref="LedgerDb"/>, så en sandlåda läser
    /// sitt eget schema. Se <see cref="LedgerSchema"/>.</para>
    /// </summary>
    public class LedgerBudgetService
    {
        /// <summary>
        /// Avvikelsen måste vara minst så här stor för att målas orange.
        ///
        /// <para><b>⚠️ Två villkor, inte ett.</b> Bara procent gör att ett konto på 300 kr skriker
        /// vid 40 kr; bara kronor gör att ett konto på en halv miljon aldrig gör det. En larmfärg
        /// som lyser på allt slutar betyda något — och då missas den gång den betyder något.</para>
        /// </summary>
        public const decimal AttentionPercent = 10m;

        /// <inheritdoc cref="AttentionPercent"/>
        public const decimal AttentionAmount = 2000m;

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerBudgetService> _logger;

        public LedgerBudgetService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerBudgetService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Läsning ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Den budget som gäller: senaste ANTAGNA versionen. Finns ingen antagen returneras null
        /// även om ett utkast ligger och väntar — <b>ett utkast är inte en budget</b>, och en
        /// rapport mot ett opåskrivet förslag är ett tal ingen har beslutat.
        /// </summary>
        public LedgerBudget? GetAdopted(int issuerType, int issuerId, int fiscalYearId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return GetAdopted(new LedgerDb(db, issuerId), issuerType, issuerId, fiscalYearId);
        }

        private static LedgerBudget? GetAdopted(
            LedgerDb ldb, int issuerType, int issuerId, int fiscalYearId)
            => ldb.FirstOrDefault<LedgerBudget>(
                @"SELECT TOP 1 * FROM dbo.LedgerBudget
                   WHERE IssuerType = @0 AND IssuerId = @1 AND FiscalYearId = @2
                     AND AdoptedDate IS NOT NULL
                   ORDER BY Revision DESC",
                issuerType, issuerId, fiscalYearId);

        /// <summary>Utkastet, om det finns ett. Högst ett per år — garanterat av ett filtrerat index.</summary>
        public LedgerBudget? GetDraft(int issuerType, int issuerId, int fiscalYearId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return GetDraft(new LedgerDb(db, issuerId), issuerType, issuerId, fiscalYearId);
        }

        private static LedgerBudget? GetDraft(
            LedgerDb ldb, int issuerType, int issuerId, int fiscalYearId)
            => ldb.FirstOrDefault<LedgerBudget>(
                @"SELECT TOP 1 * FROM dbo.LedgerBudget
                   WHERE IssuerType = @0 AND IssuerId = @1 AND FiscalYearId = @2
                     AND AdoptedDate IS NULL",
                issuerType, issuerId, fiscalYearId);

        public List<LedgerBudgetLine> GetLines(int budgetId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return new LedgerDb(db, budgetId).Fetch<LedgerBudgetLine>(
                "SELECT * FROM dbo.LedgerBudgetLine WHERE BudgetId = @0 ORDER BY AccountNumber",
                budgetId);
        }

        // ── Skrivning ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Hämtar utkastet eller skapar ett. Finns en antagen budget <b>kopieras dess rader in</b>
        /// i utkastet — en revidering börjar i det som gäller, inte i ett tomt formulär. Kassören
        /// som ska höja anläggningsposten ska inte behöva skriva in de nitton andra igen.
        /// </summary>
        public LedgerBudgetResult EnsureDraft(
            int issuerType, int issuerId, int fiscalYearId, int byMemberId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var existing = GetDraft(ldb, issuerType, issuerId, fiscalYearId);
                if (existing is not null) return new LedgerBudgetResult { Budget = existing };

                var adopted = GetAdopted(ldb, issuerType, issuerId, fiscalYearId);

                var nextRevision = ldb.ExecuteScalar<int>(
                    @"SELECT ISNULL(MAX(Revision), 0) + 1 FROM dbo.LedgerBudget
                       WHERE IssuerType = @0 AND IssuerId = @1 AND FiscalYearId = @2",
                    issuerType, issuerId, fiscalYearId);

                using var tx = ldb.GetTransaction();

                var draftId = ldb.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerBudget
                          (IssuerType, IssuerId, FiscalYearId, Revision, CreatedByMemberId, CreatedUtc)
                      VALUES (@0, @1, @2, @3, @4, @5);
                      SELECT CAST(SCOPE_IDENTITY() AS int);",
                    issuerType, issuerId, fiscalYearId, nextRevision, byMemberId, DateTime.UtcNow);

                if (adopted is not null)
                {
                    // ⚠️ Rad för rad, inte INSERT…SELECT: sandlådans tabeller saknar FK och
                    // identiteterna går nedåt, så ett massinsert är inte enklare — och den här
                    // vägen går genom sömmen precis som allt annat.
                    foreach (var line in ldb.Fetch<LedgerBudgetLine>(
                        "SELECT * FROM dbo.LedgerBudgetLine WHERE BudgetId = @0", adopted.Id))
                    {
                        InsertLine(ldb, draftId, line.AccountNumber, line.AccountName, line.Amount);
                    }
                }

                tx.Complete();

                var draft = ldb.SingleOrDefault<LedgerBudget>(
                    "SELECT * FROM dbo.LedgerBudget WHERE Id = @0", draftId);

                return new LedgerBudgetResult { Budget = draft };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte skapa ett budgetutkast för utställare {Typ}/{Id}, år {Ar}.",
                    issuerType, issuerId, fiscalYearId);

                return LedgerBudgetResult.Failed("Budgetutkastet kunde inte skapas. Försök igen.");
            }
        }

        /// <summary>
        /// Skriver om utkastets rader helt. <b>Formulärets nuvarande innehåll ÄR sanningen</b> —
        /// att diffa rader mot varandra ger bara ett sätt att tappa en borttagen rad. Samma regel
        /// som verifikationsutkastet.
        /// </summary>
        public LedgerBudgetResult SaveDraftLines(int budgetId, IEnumerable<LedgerBudgetLine> lines)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, budgetId);

                var budget = ldb.SingleOrDefault<LedgerBudget>(
                    "SELECT * FROM dbo.LedgerBudget WHERE Id = @0", budgetId);

                if (budget is null)
                    return LedgerBudgetResult.Failed("Budgeten finns inte.");

                // ⚠️ Kontrollen finns ÄVEN i databasen (TR_LedgerBudgetLine_AdoptedIsFinal). Den
                // här är för att kunna svara begripligt; triggern är det som garanterar saken.
                if (budget.IsAdopted)
                    return LedgerBudgetResult.Failed(
                        "Budgeten är antagen och kan inte skrivas om. Skapa en ny version i stället — "
                        + "då står båda kvar och går att förklara.");

                using var tx = ldb.GetTransaction();

                // ⚠️ Raderna tas bort explicit. Sandlådans tabeller är klonade med SELECT INTO och
                // bär därför INGEN främmande nyckel, så ON DELETE CASCADE finns bara i dbo.
                ldb.Execute("DELETE FROM dbo.LedgerBudgetLine WHERE BudgetId = @0", budgetId);

                var written = 0;
                foreach (var line in lines)
                {
                    // Ett tomt fält är inte noll — det är ett konto kassören inte budgeterat.
                    // Att skriva in en nolla hade gjort varje orört konto till ett påstående.
                    if (line.Amount == 0) continue;

                    InsertLine(ldb, budgetId, line.AccountNumber,
                               line.AccountName ?? "", Math.Abs(line.Amount));
                    written++;
                }

                tx.Complete();

                return new LedgerBudgetResult { Budget = budget, LineCount = written };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte spara budgetrader för budget {Id}.", budgetId);
                return LedgerBudgetResult.Failed("Budgeten kunde inte sparas. Försök igen.");
            }
        }

        private static void InsertLine(
            LedgerDb ldb, int budgetId, int accountNumber, string accountName, decimal amount)
            => ldb.Execute(
                @"INSERT INTO dbo.LedgerBudgetLine (BudgetId, AccountNumber, AccountName, Amount)
                  VALUES (@0, @1, @2, @3)",
                budgetId, accountNumber, accountName, amount);

        /// <summary>
        /// Antar utkastet. <b>Efter det här går raden inte att ändra</b> — databasen vägrar.
        ///
        /// <para>⚠️ En budget utan rader antas inte. Ett tomt beslut ser ut som ett beslut i
        /// listan och gör varje efterföljande rapport meningslös utan att något säger ifrån.</para>
        /// </summary>
        public LedgerBudgetResult Adopt(
            int budgetId,
            DateTime adoptedDate,
            string adoptedBody,
            string? note,
            int byMemberId,
            int? boardMeetingId = null)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, budgetId);

                var budget = ldb.SingleOrDefault<LedgerBudget>(
                    "SELECT * FROM dbo.LedgerBudget WHERE Id = @0", budgetId);

                if (budget is null)
                    return LedgerBudgetResult.Failed("Budgeten finns inte.");

                if (budget.IsAdopted)
                    return LedgerBudgetResult.Failed("Budgeten är redan antagen.");

                var lineCount = ldb.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM dbo.LedgerBudgetLine WHERE BudgetId = @0", budgetId);

                if (lineCount == 0)
                    return LedgerBudgetResult.Failed(
                        "Budgeten har inga belopp. Fyll i den innan den antas.");

                if (adoptedDate.Date > DateTime.Today)
                    return LedgerBudgetResult.Failed(
                        "Beslutsdatumet ligger i framtiden. Budgeten antas när beslutet är fattat.");

                var body = adoptedBody == LedgerBudgetAdoptedBy.Board
                    ? LedgerBudgetAdoptedBy.Board
                    : LedgerBudgetAdoptedBy.AnnualMeeting;

                ldb.Execute(
                    @"UPDATE dbo.LedgerBudget
                         SET AdoptedDate = @1, AdoptedBody = @2, AdoptedNote = @3,
                             AdoptedByMemberId = @4, BoardMeetingId = @5
                       WHERE Id = @0 AND AdoptedDate IS NULL",
                    budgetId, adoptedDate.Date, body, note, byMemberId, boardMeetingId);

                budget.AdoptedDate = adoptedDate.Date;
                budget.AdoptedBody = body;
                budget.AdoptedNote = note;

                return new LedgerBudgetResult { Budget = budget, LineCount = lineCount };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte anta budget {Id}.", budgetId);
                return LedgerBudgetResult.Failed("Budgeten kunde inte antas. Försök igen.");
            }
        }

        // ── Rapporten ────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Bygger "Utfall mot budget" för en period. <paramref name="from"/> och
        /// <paramref name="to"/> null = räkenskapsårets början fram till i dag.
        /// </summary>
        public LedgerBudgetReport BuildReport(
            int issuerType, int issuerId, LedgerFiscalYear year, DateTime? from = null, DateTime? to = null)
        {
            var report = new LedgerBudgetReport
            {
                FiscalYearId = year.Id,
                Year = year.Year,
                PeriodStart = (from ?? year.StartDate).Date,
                // ⚠️ Aldrig längre än räkenskapsåret. Ett utfall som spiller över till nästa år
                // jämförs mot en budget som inte gäller det.
                PeriodEnd = Min((to ?? DateTime.Today).Date, year.EndDate.Date)
            };

            if (report.PeriodEnd < report.PeriodStart)
                report.PeriodEnd = report.PeriodStart;

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var budget = GetAdopted(ldb, issuerType, issuerId, year.Id);
                var draft = GetDraft(ldb, issuerType, issuerId, year.Id);

                report.HasDraft = draft is not null;
                report.DraftId = draft?.Id;

                if (budget is not null)
                {
                    report.HasBudget = true;
                    report.BudgetId = budget.Id;
                    report.Revision = budget.Revision;
                    report.AdoptedDate = budget.AdoptedDate;
                    report.AdoptedLabel = AdoptedLabel(budget);
                    report.AdoptedBodyLabel = LedgerBudgetAdoptedBy.Label(budget.AdoptedBody);
                    report.AdoptedNote = budget.AdoptedNote;
                }

                var budgetLines = budget is null
                    ? new List<LedgerBudgetLine>()
                    : ldb.Fetch<LedgerBudgetLine>(
                        "SELECT * FROM dbo.LedgerBudgetLine WHERE BudgetId = @0", budget.Id);

                // Utfallet per konto. ⚠️ Samma klassindelning som LedgerAccountClass och
                // LedgerProjectService.Summarise: klass 3 och 8000–8399 är intäkt (Credit − Debit),
                // 4–7 och 8400–8999 kostnad (Debit − Credit) — se LedgerAccountClass om klass 8.
                // Balanskonton (1 och 2) hör inte hemma i en resultatbudget.
                var actuals = ldb.Fetch<AccountActual>(
                    @"SELECT l.AccountNumber,
                             MAX(l.AccountName) AS AccountName,
                             SUM(CASE WHEN l.AccountNumber BETWEEN 3000 AND 3999
                                        OR l.AccountNumber BETWEEN 8000 AND 8399
                                      THEN l.Credit - l.Debit
                                      ELSE l.Debit - l.Credit END) AS Amount
                        FROM dbo.LedgerJournalEntryLine l
                        JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                       WHERE e.IssuerType = @0 AND e.IssuerId = @1
                         AND e.AccountingDate >= @2 AND e.AccountingDate <= @3
                         AND (l.AccountNumber BETWEEN 3000 AND 7999
                           OR l.AccountNumber BETWEEN 8000 AND 8999)
                       GROUP BY l.AccountNumber",
                    issuerType, issuerId, report.PeriodStart, report.PeriodEnd);

                BuildRows(report, budgetLines, actuals);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Kunde inte bygga budgetrapporten för utställare {Typ}/{Id}.", issuerType, issuerId);

                report.Error = "Rapporten kunde inte läsas just nu.";
            }

            return report;
        }

        /// <summary>
        /// Slår ihop budget och utfall till rader.
        ///
        /// <para><b>⚠️ Ett konto tas med när det har en budget ELLER ett utfall.</b> Hela
        /// kontoplanen är ~39 rader varav de flesta är nollor hela året; en tabell som visar dem
        /// är en tabell ingen läser. Och ett konto med utfall men utan budget MÅSTE synas — det är
        /// en post ingen räknade med, alltså precis vad uppföljningen finns för.</para>
        /// </summary>
        internal static void BuildRows(
            LedgerBudgetReport report,
            List<LedgerBudgetLine> budgetLines,
            List<AccountActual> actuals)
        {
            var numbers = budgetLines.Select(b => b.AccountNumber)
                .Concat(actuals.Select(a => a.AccountNumber))
                .Distinct()
                .OrderBy(n => n);

            foreach (var number in numbers)
            {
                var b = budgetLines.FirstOrDefault(x => x.AccountNumber == number);
                var a = actuals.FirstOrDefault(x => x.AccountNumber == number);

                var isIncome = IsIncome(number);

                var row = new LedgerBudgetReportRow
                {
                    AccountNumber = number,
                    // Budgetens snapshot vinner: den beskriver kontot som det hette när beslutet
                    // fattades. Saknas den tas verifikationsradens, som är sin egen snapshot.
                    AccountName = !string.IsNullOrWhiteSpace(b?.AccountName)
                        ? b!.AccountName
                        : (a?.AccountName ?? $"Konto {number}"),
                    Budget = b?.Amount ?? 0m,
                    Actual = a?.Amount ?? 0m
                };

                row.Diff = row.Actual - row.Budget;
                row.DiffPercent = row.Budget == 0m ? null
                    : Math.Round(row.Diff / row.Budget * 100m, 0, MidpointRounding.AwayFromZero);

                row.BarPercent = row.Budget <= 0m ? 0
                    : (int)Math.Min(100m, Math.Max(0m, Math.Round(row.Actual / row.Budget * 100m)));

                row.Tone = ToneFor(isIncome, row);

                if (isIncome) report.Income.Add(row); else report.Costs.Add(row);
            }

            report.BudgetResult = report.Income.Sum(r => r.Budget) - report.Costs.Sum(r => r.Budget);
            report.ActualResult = report.Income.Sum(r => r.Actual) - report.Costs.Sum(r => r.Actual);
            report.ResultDiff = report.ActualResult - report.BudgetResult;
        }

        /// <summary>
        /// Intäkt: klass 3 och 8000–8399. ⚠️ Frågar <see cref="LedgerAccountClass"/> — en egen
        /// kopia av gränsen var precis det som lade räntekostnaden under intäkterna.
        /// </summary>
        internal static bool IsIncome(int accountNumber) => LedgerAccountClass.IsRevenueDirected(accountNumber);

        /// <summary>
        /// Färgen på en rad.
        ///
        /// <para><b>⚠️ EN KOSTNAD UNDER BUDGET BLIR ALDRIG GRÖN.</b> Att ha spenderat mindre än
        /// beslutat är inte en framgång — det betyder ofta att något inte blivit gjort. Grönt
        /// reserveras för intäkter över budget, som är entydigt bra. Skissen gör samma sak: där är
        /// 4010 på −24 % neutral medan 3020 på +9 % är grön.</para>
        ///
        /// <para>⚠️ Orange betyder i den här ytan "titta här", inte "fel". Avvikelsen åt fel håll
        /// måste passera BÅDA trösklarna för att målas.</para>
        /// </summary>
        internal static string ToneFor(bool isIncome, LedgerBudgetReportRow row)
        {
            if (row.Budget <= 0m) return LedgerBudgetTone.Neutral;

            var wrongWay = isIncome ? row.Diff < 0m : row.Diff > 0m;

            if (!wrongWay)
                return isIncome ? LedgerBudgetTone.Good : LedgerBudgetTone.Neutral;

            var pct = Math.Abs(row.DiffPercent ?? 0m);
            var amount = Math.Abs(row.Diff);

            return pct >= AttentionPercent && amount >= AttentionAmount
                ? LedgerBudgetTone.Attention
                : LedgerBudgetTone.Neutral;
        }

        private static string AdoptedLabel(LedgerBudget b)
        {
            if (!string.IsNullOrWhiteSpace(b.AdoptedNote)) return b.AdoptedNote!;

            var who = LedgerBudgetAdoptedBy.Label(b.AdoptedBody);
            return b.AdoptedDate is DateTime d
                ? $"{who} {d:d MMMM yyyy}"
                : who;
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        /// <summary>Utfallet per konto, rakt ur liggaren.</summary>
        internal class AccountActual
        {
            public int AccountNumber { get; set; }
            public string? AccountName { get; set; }
            public decimal Amount { get; set; }
        }
    }

    /// <summary>Resultatet av en budgetändring. <see cref="Error"/> är null när det gick.</summary>
    public class LedgerBudgetResult
    {
        public bool Success => Error is null;

        public string? Error { get; set; }

        public LedgerBudget? Budget { get; set; }

        public int LineCount { get; set; }

        public static LedgerBudgetResult Failed(string error) => new() { Error = error };
    }
}
