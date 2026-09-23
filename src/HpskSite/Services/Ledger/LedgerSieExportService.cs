using System.Text;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// SIE 4-export av ett räkenskapsår (P10.1).
    ///
    /// <para><b>⚠️ Skälen är inlåsning, arkivering och granskning</b> — inte "så revisorn kan
    /// importera till sitt program". Det programmet är det vi ersätter, och den premissen var
    /// fel när den skrevs. Filen ska gå att spara i sju år oberoende av vår drift, och en
    /// förening ska kunna lämna oss utan att lämna sin historik.</para>
    ///
    /// <para><b>⚠️ Bara den förening som bokför HOS OSS har en journal att exportera.</b> För en
    /// klubb vars bokföring ligger i ett eget program är den här filen tom och meningslös —
    /// deras export är en helt annan (fordringar, 1510 mot intäktskontot) och väntar på svar om
    /// vilka konton som ska ingå.</para>
    /// </summary>
    public class LedgerSieExportService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerSieExportService> _logger;

        public LedgerSieExportService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerSieExportService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Bygger filen. Returnerar null när året inte finns.
        /// </summary>
        /// <param name="issuerName">Föreningens namn, som det ska stå i filen.</param>
        /// <param name="orgNumber">Organisationsnummer, eller tomt.</param>
        /// <param name="sandbox">
        /// ⚠️ <b>En sandlådefil MÅSTE märkas.</b> Filen lämnar sidan och tar ingen ram med sig —
        /// samma regel som kvittot följer. En omärkt testfil som hamnar i en förenings arkiv är
        /// oskiljbar från riktig bokföring.
        /// </param>
        public byte[]? Build(
            int issuerType, int issuerId, int fiscalYearId,
            string issuerName, string? orgNumber, bool sandbox)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var year = ldb.Fetch<LedgerFiscalYear>(
                @"SELECT * FROM dbo.LedgerFiscalYear
                   WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                fiscalYearId, issuerType, issuerId).FirstOrDefault();

            if (year == null) return null;

            var sb = new StringBuilder();
            void Post(string name, params string?[] f) => sb.AppendLine(SieFormat.Post(name, f));

            // ── Filhuvud ────────────────────────────────────────────────────────────────────
            Post("FLAGGA", "0");
            Post("PROGRAM", "pistol.nu", "1.0");
            Post("FORMAT", "PC8");
            Post("GEN", SieFormat.Date(DateTime.Today));
            Post("SIETYP", SieFormat.TypeVerifications);

            if (sandbox)
            {
                // ⚠️ Märkningen ligger i BÅDE en läsbar rad och företagsnamnet. En #PROSA kan
                //    filtreras bort av ett importprogram; namnet följer med in i bokföringen.
                Post("PROSA", "SANDLÅDA – testdata från pistol.nu, inte riktig bokföring");
                Post("FNAMN", "SANDLÅDA " + issuerName);
            }
            else
            {
                Post("FNAMN", issuerName);
            }

            if (!string.IsNullOrWhiteSpace(orgNumber)) Post("ORGNR", orgNumber);

            Post("RAR", "0", SieFormat.Date(year.StartDate), SieFormat.Date(year.EndDate));

            // ── Kontoplanen ─────────────────────────────────────────────────────────────────
            // ⚠️ Bara konton som FAKTISKT används. En full kontoplan med 39 rader varav
            //    tolv används gör filen svårare att läsa utan att tillföra något — och ett
            //    oanvänt konto kan vara ett som klubben stängt.
            // ⚠️⚠️ "Används" betyder för ett BALANSKONTO att det har ett saldo, inte att det rörde
            //    sig i år. Ett bankkonto som stod still hela året försvann annars ur filen, och
            //    med det dess #IB och #UB — föreningens pengar fanns inte i bokslutet.
            var accounts = ldb.Fetch<AccountRow>(
                @"SELECT DISTINCT l.AccountNumber, MAX(l.AccountName) AS AccountName
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND e.AccountingDate <= @3
                     AND (e.AccountingDate >= @2 OR l.AccountNumber < 3000)
                   GROUP BY l.AccountNumber
                   ORDER BY l.AccountNumber",
                issuerType, issuerId, year.StartDate, year.EndDate);

            // ⚠️⚠️ TIDIGARE ÅRS RESULTAT MÅSTE IN I EGET KAPITALS INGÅENDE BALANS.
            //    Liggaren gör ingen årsskiftesöverföring (saldona är kumulativa, se
            //    LedgerFinancialStatements.PriorResult). Utan det här summerar #IB inte till noll
            //    från och med föreningens andra år — förra årets överskott står på bankkontot men
            //    ingenstans på kapitalsidan — och ett bokföringsprogram som tar emot filen
            //    avvisar den eller bokar differensen på ett felkonto.
            var priorResult = ldb.Fetch<decimal?>(
                @"SELECT SUM(l.Debit - l.Credit) FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND l.AccountNumber >= 3000 AND e.AccountingDate < @2",
                issuerType, issuerId, year.StartDate).FirstOrDefault() ?? 0m;

            var equityAccount = 0;
            string? equityName = null;
            if (priorResult != 0m)
            {
                // Samma kapitalkonto som de ingående balanserna bokförs mot.
                (equityAccount, equityName) =
                    LedgerOpeningBalanceService.ResolveEquityAccount(ldb, issuerType, issuerId);

                if (!accounts.Any(a => a.AccountNumber == equityAccount))
                {
                    accounts.Add(new AccountRow { AccountNumber = equityAccount, AccountName = equityName });
                    accounts = accounts.OrderBy(a => a.AccountNumber).ToList();
                }
            }

            foreach (var a in accounts)
            {
                Post("KONTO", a.AccountNumber.ToString(), a.AccountName ?? "");
                Post("KTYP", a.AccountNumber.ToString(), SieFormat.AccountType(a.AccountNumber));
            }

            // ── Ingående och utgående balanser ──────────────────────────────────────────────
            // ⚠️⚠️ #IB OCH #UB ÄR VAD SOM GÖR FILEN TILL EN BOKFÖRING och inte en lista.
            //    Utan ingående balanser kan ingen ta emot filen annat än vid ett årsskifte — det
            //    är samma sak som gör IMPORT-riktningen svår, och det är värt att minnas här.
            //    Bara balanskonton: ett resultatkonto har per definition noll i ingående balans.
            foreach (var a in accounts.Where(x => LedgerAccountClass.IsBalance(x.AccountNumber)))
            {
                var ib = SumBefore(ldb, issuerType, issuerId, a.AccountNumber, year.StartDate);
                var ub = SumThrough(ldb, issuerType, issuerId, a.AccountNumber, year.EndDate);

                if (a.AccountNumber == equityAccount)
                {
                    ib += priorResult;
                    ub += priorResult;
                }

                Post("IB", "0", a.AccountNumber.ToString(), SieFormat.Amount(ib));
                Post("UB", "0", a.AccountNumber.ToString(), SieFormat.Amount(ub));
            }

            // Årets resultat per resultatkonto.
            foreach (var a in accounts.Where(x => LedgerAccountClass.IsResult(x.AccountNumber)))
            {
                var res = SumBetween(ldb, issuerType, issuerId, a.AccountNumber,
                                     year.StartDate, year.EndDate);
                Post("RES", "0", a.AccountNumber.ToString(), SieFormat.Amount(res));
            }

            // ── Verifikationerna ────────────────────────────────────────────────────────────
            var entries = ldb.Fetch<EntryRow>(
                @"SELECT e.Id, e.Number, e.AccountingDate, e.Description, e.RegisteredUtc,
                         s.Prefix
                    FROM dbo.LedgerJournalEntry e
                    LEFT JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND e.AccountingDate >= @2 AND e.AccountingDate <= @3
                     AND NOT (e.AccountingDate = @2 AND e.SourceType = @4)
                   ORDER BY e.AccountingDate, e.Number",
                issuerType, issuerId, year.StartDate, year.EndDate, LedgerSourceType.OpeningBalance);

            var lines = ldb.Fetch<LineRow>(
                @"SELECT l.JournalEntryId, l.AccountNumber, l.Debit, l.Credit, l.Text
                    FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND e.AccountingDate >= @2 AND e.AccountingDate <= @3
                     AND NOT (e.AccountingDate = @2 AND e.SourceType = @4)
                   ORDER BY l.JournalEntryId, l.LineNumber",
                issuerType, issuerId, year.StartDate, year.EndDate, LedgerSourceType.OpeningBalance);

            var byEntry = lines.GroupBy(l => l.JournalEntryId)
                               .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var e in entries)
            {
                // ⚠️ Serien är verifikationens SERIE i SIE, och prefixet är det föreningen valt.
                //    Tomt prefix ger serien "A", som är SIE:s vedertagna standardserie.
                var series = string.IsNullOrWhiteSpace(e.Prefix) ? "A" : e.Prefix!;

                sb.AppendLine(SieFormat.Post("VER", series, e.Number.ToString(),
                    SieFormat.Date(e.AccountingDate), e.Description ?? "",
                    SieFormat.Date(e.RegisteredUtc)));

                sb.AppendLine("{");

                foreach (var l in byEntry.TryGetValue(e.Id, out var ls) ? ls : new List<LineRow>())
                {
                    // ⚠️⚠️ SIE HAR ETT TECKNAT BELOPP PER RAD, inte debet och kredit i två fält.
                    //    Debet är positivt. Skrivs kredit positivt balanserar inte verifikationen
                    //    och hela filen avvisas — av mottagaren, inte av oss.
                    var amount = l.Debit - l.Credit;

                    sb.AppendLine("   " + SieFormat.Post("TRANS",
                        l.AccountNumber.ToString(), "{}", SieFormat.Amount(amount),
                        SieFormat.Date(e.AccountingDate), l.Text ?? ""));
                }

                sb.AppendLine("}");
            }

            // ⚠️ CP437 enligt standarden. Registreras i Program.cs — .NET Core bär den inte
            //    som standard, och utan registreringen kastar GetEncoding.
            return Encoding.GetEncoding(SieFormat.CodePage).GetBytes(sb.ToString());
        }

        private static decimal SumBefore(LedgerDb ldb, int t, int i, int account, DateTime from)
            => ldb.Fetch<decimal?>(
                @"SELECT SUM(l.Debit - l.Credit) FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND l.AccountNumber = @2
                     AND (e.AccountingDate < @3
                          OR (e.AccountingDate = @3 AND e.SourceType = @4))",
                t, i, account, from, LedgerSourceType.OpeningBalance).FirstOrDefault() ?? 0m;

        private static decimal SumThrough(LedgerDb ldb, int t, int i, int account, DateTime to)
            => ldb.Fetch<decimal?>(
                @"SELECT SUM(l.Debit - l.Credit) FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND l.AccountNumber = @2 AND e.AccountingDate <= @3",
                t, i, account, to).FirstOrDefault() ?? 0m;

        private static decimal SumBetween(
            LedgerDb ldb, int t, int i, int account, DateTime from, DateTime to)
        {
            var movement = ldb.Fetch<decimal?>(
                @"SELECT SUM(l.Debit - l.Credit) FROM dbo.LedgerJournalEntryLine l
                    JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1
                     AND l.AccountNumber = @2
                     AND e.AccountingDate >= @3 AND e.AccountingDate <= @4",
                t, i, account, from, to).FirstOrDefault() ?? 0m;

            // ⚠️ #RES anges med intäkter NEGATIVA, eftersom de är kreditsaldon — samma tecken som
            //    debet−kredit ger av sig självt. Ingen vändning här; vändningen hör till
            //    presentationen (LedgerAccountClass.InOwnDirection), inte till filen.
            return movement;
        }

        private class AccountRow
        {
            public int AccountNumber { get; set; }
            public string? AccountName { get; set; }
        }

        private class EntryRow
        {
            public int Id { get; set; }
            public int Number { get; set; }
            public DateTime AccountingDate { get; set; }
            public string? Description { get; set; }
            public DateTime RegisteredUtc { get; set; }
            public string? Prefix { get; set; }
        }

        private class LineRow
        {
            public int JournalEntryId { get; set; }
            public int AccountNumber { get; set; }
            public decimal Debit { get; set; }
            public decimal Credit { get; set; }
            public string? Text { get; set; }
        }
    }
}
