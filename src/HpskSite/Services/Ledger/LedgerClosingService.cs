using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Bokslutet (P7): resultat- och balansräkningen, de sju stegen, och fastställandet.
    ///
    /// <para><b>⚠️ Görs i januari, ofta av någon som gör det första gången.</b> Det är därför
    /// stegen är HÄRLEDDA och inte kryssrutor — checklistan ska kunna säga emot den som tror att
    /// den är klar.</para>
    /// </summary>
    public class LedgerClosingService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerBankImportService _bank;
        private readonly LedgerAttachmentService _attachments;
        private readonly LedgerAssetService _assets;
        private readonly ILogger<LedgerClosingService> _logger;

        public LedgerClosingService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerBankImportService bank,
            LedgerAttachmentService attachments,
            LedgerAssetService assets,
            ILogger<LedgerClosingService> logger)
        {
            _databaseFactory = databaseFactory;
            _bank = bank;
            _attachments = attachments;
            _assets = assets;
            _logger = logger;
        }

        /// <summary>Resultat- och balansräkningen för ett år.</summary>
        public LedgerFinancialStatements? Statements(int issuerType, int issuerId, int fiscalYearId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var year = ldb.Fetch<LedgerFiscalYear>(
                @"SELECT * FROM dbo.LedgerFiscalYear
                   WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                fiscalYearId, issuerType, issuerId).FirstOrDefault();

            if (year == null) return null;

            var s = new LedgerFinancialStatements
            {
                FiscalYearId = year.Id,
                Year = year.Year,
                From = year.StartDate,
                To = year.EndDate,
                Status = year.Status
            };

            // ⚠️⚠️ TVÅ OLIKA URVAL, och det är inte en detalj.
            //    RESULTATet är årets AFFÄRER — bara rader inom året.
            //    BALANSEN är ett SALDO — allt från början fram till årets slut. Tas bara årets
            //    rader med visar balansräkningen årets förändring i stället för ställningen, och
            //    den skulle då aldrig gå ihop för en förening som funnits mer än ett år.
            var resultRows = ldb.Fetch<AccountSum>(
                @"SELECT l.AccountNumber, MAX(l.AccountName) AS AccountName,
                         SUM(l.Debit) AS Debit, SUM(l.Credit) AS Credit
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND e.AccountingDate >= @2 AND e.AccountingDate <= @3
                     AND l.AccountNumber >= 3000
                   GROUP BY l.AccountNumber
                   ORDER BY l.AccountNumber",
                issuerType, issuerId, year.StartDate, year.EndDate);

            foreach (var r in resultRows)
            {
                var amount = LedgerAccountClass.InOwnDirection(r.AccountNumber, r.Debit, r.Credit);

                // ⚠️ Ett konto som landat på noll utelämnas. En rad som säger "0 kr" i en
                //    resultaträkning är inte information, den är brus — och årsmötet ska läsa
                //    handlingen, inte leta i den.
                if (amount == 0m) continue;

                var row = new LedgerFinancialStatements.StatementRow
                {
                    AccountNumber = r.AccountNumber,
                    AccountName = r.AccountName ?? "",
                    Amount = amount
                };

                if (LedgerAccountClass.IsRevenueDirected(r.AccountNumber)) s.Revenue.Add(row);
                else s.Costs.Add(row);
            }

            var balanceRows = ldb.Fetch<AccountSum>(
                @"SELECT l.AccountNumber, MAX(l.AccountName) AS AccountName,
                         SUM(l.Debit) AS Debit, SUM(l.Credit) AS Credit
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND e.AccountingDate <= @2
                     AND l.AccountNumber < 3000
                   GROUP BY l.AccountNumber
                   ORDER BY l.AccountNumber",
                issuerType, issuerId, year.EndDate);

            foreach (var r in balanceRows)
            {
                var amount = LedgerAccountClass.BalanceAmount(r.AccountNumber, r.Debit, r.Credit);
                if (amount == 0m) continue;

                var row = new LedgerFinancialStatements.StatementRow
                {
                    AccountNumber = r.AccountNumber,
                    AccountName = r.AccountName ?? "",
                    Amount = amount
                };

                if (LedgerAccountClass.Of(r.AccountNumber) == LedgerAccountClass.Assets)
                    s.Assets.Add(row);
                else
                    s.EquityAndLiabilities.Add(row);
            }

            return s;
        }

        /// <summary>
        /// De sju stegen, härledda ur verkligt tillstånd.
        ///
        /// <para>⚠️ Ett steg vi inte kan MÄTA redovisas som <c>Unknown</c> och säger det rakt ut.
        /// Ett omätt steg som visas som klart är värre än inget steg alls — det är precis den
        /// sortens falska trygghet ett bokslut inte tål.</para>
        /// </summary>
        public LedgerClosingChecklist? Checklist(int issuerType, int issuerId, int fiscalYearId)
        {
            var s = Statements(issuerType, issuerId, fiscalYearId);
            if (s == null) return null;

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var list = new LedgerClosingChecklist
            {
                FiscalYearId = s.FiscalYearId,
                Year = s.Year,
                Status = s.Status
            };

            // 1 — Allt bokfört. Mäts som kön "att bokföra": bekräftade betalningar utan verifikation.
            var unposted = ldb.Fetch<int>(
                @"SELECT COUNT(*) FROM dbo.LedgerPayment
                   WHERE IssuerType = @0 AND IssuerId = @1
                     AND ConfirmedUtc IS NOT NULL AND VoidedUtc IS NULL AND JournalEntryId IS NULL",
                issuerType, issuerId).FirstOrDefault();

            list.Steps.Add(new()
            {
                Key = "posted",
                Title = "Alla poster bokförda",
                State = unposted == 0 ? LedgerClosingChecklist.StepState.Done
                                      : LedgerClosingChecklist.StepState.Todo,
                Detail = unposted == 0 ? "" : $"{unposted} bekräftade betalningar saknar verifikation.",
                GoTo = "avgifter"
            });

            // 2 — Kontoutdraget avstämt.
            // ⚠️ Kravet är att ett utdrag TÄCKER årets slut, inte att det finns något alls. Ett
            //    utdrag från mars bevisar ingenting om decembers ställning.
            var imports = _bank.List(issuerType, issuerId)
                .Where(i => i.PeriodTo.HasValue && i.PeriodTo.Value >= s.To.AddDays(-31)
                            && i.PeriodFrom.HasValue && i.PeriodFrom.Value <= s.To)
                .ToList();

            // ⚠️⚠️ ETT INLÄST UTDRAG ÄR INTE ETT AVSTÄMT UTDRAG. Fram till 2026-09-23 kryssades
            //    steget så snart ett täckande utdrag FANNS, utan att titta på om raderna var
            //    matchade — så en klubb som läst in decemberutdraget och inte rört det fick
            //    "avstämt". Avstämningsytan har haft den riktiga regeln hela tiden ("allt
            //    stämmer" kräver noll omatchade); det var checklistan som inte läste den.
            //    Det här är den uppgift en revisor lutar sig mot.
            var unmatched = imports.Count == 0
                ? 0
                : _bank.UnmatchedInImports(issuerId, imports.Select(i => i.Id).ToList());

            list.Steps.Add(new()
            {
                Key = "reconciled",
                Title = "Kontoutdraget avstämt",
                State = imports.Count == 0 ? LedgerClosingChecklist.StepState.Todo
                      // ⚠️ -1 = gick inte att räkna. "Vet inte" är ett eget svar, aldrig "klart".
                      : unmatched < 0 ? LedgerClosingChecklist.StepState.Unknown
                      : unmatched == 0 ? LedgerClosingChecklist.StepState.Done
                      : LedgerClosingChecklist.StepState.Todo,
                Detail = imports.Count == 0
                    ? "Inget inläst kontoutdrag som täcker årets slut."
                    : unmatched < 0 ? "Antalet omatchade rader gick inte att läsa."
                    : unmatched == 0 ? ""
                    : $"{unmatched} rader i kontoutdraget saknar motpart i bokföringen.",
                GoTo = "avstamning"
            });

            // 3 — Underlag på plats.
            // ⚠️ Steget stod som UNKNOWN fram till 2026-09-23, med texten "kontrollera pärmen för
            //    hand". Det var ärligt så länge bilagorna inte gick att ladda upp; nu gör de det.
            //
            // ⚠️⚠️ BARA HANDBOKFÖRDA POSTER RÄKNAS. En medlemsavgift som föll ut ur avgiftsmodulen
            //    har sitt underlag i anmälan och betalningsraden, inte i ett papper någon ska
            //    fotografera. Att kräva en bilaga där hade gjort steget permanent rött för varje
            //    förening som använder avgiftsdelen — alltså en varning som slutar betyda något.
            var (missingDocs, totalDocs) = _attachments.MissingForYear(issuerType, issuerId, fiscalYearId);

            list.Steps.Add(new()
            {
                Key = "attachments",
                Title = "Underlag på plats",
                // ⚠️ -1 betyder "gick inte att räkna", aldrig "inga saknas". Ett tyst noll hade
                //    kryssat av en kontroll ingen gjort.
                State = missingDocs < 0 ? LedgerClosingChecklist.StepState.Unknown
                      : missingDocs == 0 ? LedgerClosingChecklist.StepState.Done
                      : LedgerClosingChecklist.StepState.Todo,
                Detail = missingDocs < 0
                    ? "Underlagsläget gick inte att läsa."
                    : missingDocs == 0
                        ? (totalDocs == 0
                            ? "Inga handbokförda poster i året — avgifternas underlag är anmälan."
                            : "")
                        : $"{missingDocs} av {totalDocs} handbokförda poster saknar underlag.",
                GoTo = "verifikationer"
            });

            // 4 — Obetalda avgifter som kundfordran.
            var outstanding = ldb.Fetch<decimal?>(
                @"SELECT SUM(Amount) FROM dbo.LedgerPayment
                   WHERE IssuerType = @0 AND IssuerId = @1
                     AND ConfirmedUtc IS NULL AND VoidedUtc IS NULL
                     AND CreatedUtc <= @2",
                issuerType, issuerId, s.To.AddDays(1)).FirstOrDefault() ?? 0m;

            list.Steps.Add(new()
            {
                Key = "receivables",
                Title = "Obetalda avgifter som kundfordran",
                // ⚠️ Finns inget obetalt är steget klart — inte "ej tillämpligt". En förening där
                //    allt kom in har gjort steget genom att inte behöva det.
                State = outstanding == 0m ? LedgerClosingChecklist.StepState.Done
                                          : LedgerClosingChecklist.StepState.Unknown,
                Detail = outstanding == 0m ? ""
                    : $"{outstanding:N0} kr är begärt men inte mottaget vid årets slut. "
                      + "Bokför det som kundfordran innan året fastställs.",
                GoTo = "bokfor"
            });

            // 5 — Avskrivningar.
            // ⚠️ Steget stod som UNKNOWN fram till 2026-09-23 med texten "anläggningsregister
            //    finns inte ännu". Nu finns det, och steget kan svara.
            //
            // ⚠️⚠️ ETT TOMT REGISTER ÄR "KLART", INTE "OKÄNT". De allra flesta föreningar har
            //    ingenting att skriva av — en evig frågetecken där hade lärt kassören att stegen
            //    inte går att lita på. Men texten säger att registret ÄR tomt, så skillnaden mot
            //    "vi har inte tittat" står på skärmen.
            var assets = _assets.List(issuerType, issuerId, s.From, s.To);
            var needing = assets.Where(a => a.NeedsPosting).ToList();
            var toPost = needing.Sum(a => a.Remaining);

            list.Steps.Add(new()
            {
                Key = "depreciation",
                Title = "Avskrivningar",
                State = needing.Count == 0 ? LedgerClosingChecklist.StepState.Done
                                           : LedgerClosingChecklist.StepState.Todo,
                Detail = assets.Count == 0
                    ? "Anläggningsregistret är tomt — föreningen har inget att skriva av."
                    : needing.Count == 0
                        ? ""
                        : $"{needing.Count} tillgångar har {toPost:N0} kr kvar att skriva av för året.",
                GoTo = "tillgangar"
            });

            // 6 — Resultat- och balansräkning. Klar när den BALANSERAR.
            list.Steps.Add(new()
            {
                Key = "statements",
                Title = "Resultat- och balansräkning",
                State = s.Balances ? LedgerClosingChecklist.StepState.Done
                                   : LedgerClosingChecklist.StepState.Todo,
                Detail = s.Balances ? ""
                    : $"Balansräkningen går inte ihop — {s.BalanceDifference:N2} kr i differens."
            });

            // 7 — Årsmötets fastställande.
            list.Steps.Add(new()
            {
                Key = "established",
                Title = "Årsmötet har fastställt",
                State = s.Status == LedgerFiscalYearStatus.Established
                    ? LedgerClosingChecklist.StepState.Done
                    : LedgerClosingChecklist.StepState.Todo,
                Detail = s.Status == LedgerFiscalYearStatus.Established ? ""
                    : "Året tar emot bokföring tills det fastställs."
            });

            return list;
        }

        /// <summary>
        /// Flyttar året mellan lägena.
        ///
        /// <para><b>⚠️⚠️ FASTSTÄLLANDE ÄR ENKELRIKTAT.</b> Ett fastställt år tar inte emot
        /// skrivningar — spärren ligger i databastriggern, inte här — och en rättelse blir en ny
        /// verifikation i ett öppet år. Därför krävs att balansräkningen GÅR IHOP: att frysa ett
        /// år som inte balanserar låser in felet.</para>
        /// </summary>
        public (bool Ok, string? Message) SetStatus(
            int issuerType, int issuerId, int fiscalYearId, string status, int byMemberId)
        {
            if (status is not (LedgerFiscalYearStatus.Open or LedgerFiscalYearStatus.Closing
                               or LedgerFiscalYearStatus.Established))
                return (false, "Okänt läge.");

            var s = Statements(issuerType, issuerId, fiscalYearId);
            if (s == null) return (false, "Räkenskapsåret hittades inte.");

            if (s.Status == LedgerFiscalYearStatus.Established)
                return (false, "Året är fastställt och kan inte ändras. "
                             + "En rättelse blir en ny verifikation i ett öppet år.");

            if (status == LedgerFiscalYearStatus.Established)
            {
                if (!s.Balances)
                    return (false, $"Balansräkningen går inte ihop — {s.BalanceDifference:N2} kr i "
                                 + "differens. Att fastställa ett år som inte balanserar låser in felet.");

                // ⚠️⚠️ EN AVSTÄNGD KNAPP ÄR INGEN SPÄRR. Ytan stängde av knappen när steg
                //    återstod, men servern släppte igenom — och en verifieringssvit som bad om
                //    det fastställde dev:s levande räkenskapsår, som därmed slutade ta emot
                //    bokföring. Handlingen är ENKELRIKTAD, så den måste vägras där den utförs.
                var list = Checklist(issuerType, issuerId, fiscalYearId);
                var remaining = list?.Steps
                    .Where(x => x.State == LedgerClosingChecklist.StepState.Todo
                                && x.Key != "established")
                    .Select(x => x.Title).ToList() ?? new List<string>();

                if (remaining.Count > 0)
                    return (false, "Det finns steg kvar innan året kan fastställas: "
                                 + string.Join(", ", remaining) + ".");
            }

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var established = status == LedgerFiscalYearStatus.Established;

            ldb.Execute(
                @"UPDATE dbo.LedgerFiscalYear
                     SET Status = @1,
                         EstablishedDate = CASE WHEN @2 = 1 THEN @3 ELSE NULL END,
                         EstablishedByMemberId = CASE WHEN @2 = 1 THEN @4 ELSE NULL END
                   WHERE Id = @0 AND IssuerType = @5 AND IssuerId = @6",
                fiscalYearId, status, established ? 1 : 0,
                established ? (object)DateTime.UtcNow : DBNull.Value,
                established ? (object)byMemberId : DBNull.Value,
                issuerType, issuerId);

            return (true, null);
        }

        private class AccountSum
        {
            public int AccountNumber { get; set; }
            public string? AccountName { get; set; }
            public decimal Debit { get; set; }
            public decimal Credit { get; set; }
        }
    }
}
