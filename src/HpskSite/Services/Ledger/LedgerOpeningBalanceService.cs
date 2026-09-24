using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Ingående balanser — vad föreningen ägde och var skyldig den dag bokföringen flyttade hit.
    ///
    /// <para><b>⚠️⚠️ UTAN DEN HÄR BÖRJAR VARJE FÖRENING PÅ NOLL.</b> En klubb som funnits i
    /// femtio år har pengar på kontot, en kassa och kanske en klubbstuga. Bokförs bara det som
    /// händer efter anslutningen visar balansräkningen fel från första dagen, bankavstämningen
    /// säger emot kontoutdraget, och SIE-filen har inga ingående balanser. Källtypen
    /// <see cref="LedgerSourceType.OpeningBalance"/> fanns sedan P1 och hade ingen skrivare.</para>
    ///
    /// <para><b>⚠️ KASSÖREN ANGER SALDON, INTE DEBET OCH KREDIT.</b> Samma regel som Bokför:
    /// "på föreningskontot fanns 48 312 kr" är en fråga hen kan svara på. Konteringen byggs här,
    /// och eget kapital RÄKNAS UT som skillnaden — det är den enda raden i en ingående balans
    /// som inte går att läsa av på något papper, och därför ska ingen behöva skriva in den.</para>
    ///
    /// <para><b>⚠️ Bokförs på FÖRSTA räkenskapsårets första dag, och bara där.</b> SIE-exporten
    /// läser en sådan verifikation som årets <c>#IB</c> i stället för som en händelse under året
    /// — annars ser anslutningsdagen ut som att föreningen fick in hela sin förmögenhet.</para>
    /// </summary>
    public class LedgerOpeningBalanceService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;

        public LedgerOpeningBalanceService(IUmbracoDatabaseFactory databaseFactory, LedgerPostingService posting)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
        }

        /// <summary>
        /// Kapitalkontot: föreningens 2060 om det finns, annars det lägsta 20xx-kontot i dess
        /// kontoplan. <b>Samma svar som SIE-exporten använder</b> för tidigare års resultat, så
        /// ingående balanser och överfört resultat hamnar på samma konto.
        /// </summary>
        public static (int Number, string Name) ResolveEquityAccount(LedgerDb ldb, int issuerType, int issuerId)
        {
            var eq = ldb.Fetch<LedgerAccount>(
                @"SELECT * FROM dbo.LedgerAccount
                   WHERE IssuerType = @0 AND IssuerId = @1 AND Number >= 2000 AND Number < 2100
                   ORDER BY CASE WHEN Number = 2060 THEN 0 ELSE 1 END, Number",
                issuerType, issuerId).FirstOrDefault();

            return (eq?.Number ?? 2060, eq?.Name ?? "Eget kapital");
        }

        /// <summary>Läget: första året, balanskontona att fylla i, och det som redan är inlagt.</summary>
        public OpeningBalanceState Get(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var state = new OpeningBalanceState();

            var first = FirstYear(ldb, issuerType, issuerId);
            if (first is null) return state;

            state.FiscalYearId = first.Id;
            state.Year = first.Year;
            state.Date = first.StartDate;
            state.YearIsOpen = first.Status != LedgerFiscalYearStatus.Established;

            var (eqNo, eqName) = ResolveEquityAccount(ldb, issuerType, issuerId);
            state.EquityAccountNumber = eqNo;
            state.EquityAccountName = eqName;

            state.Accounts.AddRange(ldb.Fetch<LedgerAccount>(
                    @"SELECT * FROM dbo.LedgerAccount
                       WHERE IssuerType = @0 AND IssuerId = @1 AND IsActive = 1 AND Number < 3000
                       ORDER BY Number",
                    issuerType, issuerId)
                .Where(a => a.Number != eqNo)
                .Select(a => new OpeningBalanceAccount
                {
                    Number = a.Number,
                    Name = a.Name,
                    IsAsset = a.Number < 2000
                }));

            var live = LiveEntry(ldb, issuerType, issuerId);
            if (live is not null)
            {
                state.ExistingEntryId = live.Id;
                state.ExistingNumber = live.Number;

                var lines = ldb.Fetch<LedgerJournalEntryLine>(
                    "SELECT * FROM dbo.LedgerJournalEntryLine WHERE JournalEntryId = @0 ORDER BY LineNumber",
                    live.Id);

                foreach (var l in lines)
                {
                    if (l.AccountNumber == eqNo) { state.ExistingEquity = l.Credit - l.Debit; continue; }
                    // Tillgångar debet-positiva, skulder kredit-positiva — samma form som formuläret.
                    var amount = l.AccountNumber < 2000 ? l.Debit - l.Credit : l.Credit - l.Debit;
                    state.ExistingLines.Add(new OpeningBalanceLine { AccountNumber = l.AccountNumber, Amount = amount });
                }
            }

            return state;
        }

        /// <summary>
        /// Bokför de ingående balanserna. Finns det redan en levande verifikation rättas den först
        /// (utjämnas med en rättelse) och den nya skrivs efter — ingenting skrivs över.
        /// </summary>
        public OpeningBalanceResult Save(int issuerType, int issuerId, List<OpeningBalanceLine> input, int byMemberId)
        {
            var lines = (input ?? new()).Where(l => l.Amount != 0m).ToList();

            if (lines.GroupBy(l => l.AccountNumber).Any(g => g.Count() > 1))
                return OpeningBalanceResult.Fail("Samma konto står två gånger. Slå ihop beloppen till en rad.");

            OpeningBalanceState state;
            int? replaceId;
            using (var db = _databaseFactory.CreateDatabase())
            {
                var ldb = new LedgerDb(db, issuerId);
                if (FirstYear(ldb, issuerType, issuerId) is null)
                    return OpeningBalanceResult.Fail("Lägg upp räkenskapsåret först.");
            }

            state = Get(issuerType, issuerId);
            replaceId = state.ExistingEntryId;

            if (!state.YearIsOpen)
                return OpeningBalanceResult.Fail(
                    $"Räkenskapsåret {state.Year} är fastställt och tar inte emot bokföring. "
                  + "Ingående balanser läggs på det första året, innan det fastställs.");

            var allowed = state.Accounts.ToDictionary(a => a.Number);
            foreach (var l in lines)
            {
                if (l.AccountNumber == state.EquityAccountNumber)
                    return OpeningBalanceResult.Fail("Eget kapital räknas ut av sig självt — lämna det kontot.");
                if (!allowed.ContainsKey(l.AccountNumber))
                    return OpeningBalanceResult.Fail($"Konto {l.AccountNumber} är inte ett balanskonto i er kontoplan.");
            }

            var postingLines = BuildLines(lines, state.EquityAccountNumber);
            if (postingLines.Count == 0 && replaceId is null)
                return OpeningBalanceResult.Fail("Fyll i minst ett belopp.");

            // ⚠️ Rättelsen FÖRST: går den inte igenom får ingen andra uppsättning skrivas, för då
            //    räknas föreningens förmögenhet två gånger och balansräkningen ser ändå rimlig ut.
            if (replaceId is int oldId)
            {
                var corr = _posting.CreateCorrection(oldId, byMemberId, "Ingående balanser ändrade", state.Date);
                if (!corr.Success)
                    return OpeningBalanceResult.Fail(corr.Error ?? "De tidigare ingående balanserna kunde inte rättas.");
            }

            if (postingLines.Count == 0)
                return new OpeningBalanceResult { Success = true, Removed = true };

            var result = _posting.Post(new LedgerPostingRequest
            {
                IssuerType = issuerType,
                IssuerId = issuerId,
                AccountingDate = state.Date!.Value.Date,
                EventDate = state.Date!.Value.Date,
                Description = "Ingående balanser",
                SourceType = LedgerSourceType.OpeningBalance,
                CreatedByMemberId = byMemberId,
                Lines = postingLines
            });

            if (!result.Success)
                return OpeningBalanceResult.Fail(result.Error ?? "De ingående balanserna kunde inte bokföras.");

            return new OpeningBalanceResult { Success = true, EntryId = result.EntryId };
        }

        /// <summary>
        /// Ingående balanser UR EN SIE-FIL (<c>#IB 0</c>) — exakt som filen säger, eget kapital
        /// inräknat.
        ///
        /// <para><b>⚠️ Skild från <see cref="Save"/>, med flit.</b> Handinmatningen RÄKNAR UT eget
        /// kapital, eftersom kassören inte kan läsa av det på något papper. Filen bär däremot eget
        /// kapital som egna rader (2010, 2060, 2099 …) — att räkna ut det igen hade slagit ihop
        /// föreningens kapitalkonton till ett, och balansräkningen hade inte längre sett ut som den
        /// årsmötet fastställde.</para>
        ///
        /// <para>SIE:s tecken: positivt = debet, negativt = kredit. Filen måste summera till noll —
        /// gör den inte det är den inte en balans, och vi rättar den inte åt någon.</para>
        /// </summary>
        public OpeningBalanceResult SaveFromImport(int issuerType, int issuerId, IReadOnlyDictionary<int, decimal> balances, int byMemberId)
        {
            var lines = balances.Where(kv => kv.Value != 0m).OrderBy(kv => kv.Key).ToList();
            if (lines.Sum(kv => kv.Value) != 0m)
                return OpeningBalanceResult.Fail(
                    $"Filens ingående balanser summerar inte till noll ({lines.Sum(kv => kv.Value):N2} kr).");
            if (lines.Any(kv => kv.Key >= 3000))
                return OpeningBalanceResult.Fail("Ingående balanser får bara stå på balanskonton (1000–2999).");

            var state = Get(issuerType, issuerId);
            if (state.Date is null) return OpeningBalanceResult.Fail("Lägg upp räkenskapsåret först.");
            if (!state.YearIsOpen)
                return OpeningBalanceResult.Fail(
                    $"Räkenskapsåret {state.Year} är fastställt och tar inte emot bokföring.");

            if (state.ExistingEntryId is int oldId)
            {
                var corr = _posting.CreateCorrection(oldId, byMemberId, "Ingående balanser ersatta av SIE-import", state.Date);
                if (!corr.Success)
                    return OpeningBalanceResult.Fail(corr.Error ?? "De tidigare ingående balanserna kunde inte rättas.");
            }
            if (lines.Count == 0) return new OpeningBalanceResult { Success = true, Removed = state.ExistingEntryId != null };

            var result = _posting.Post(new LedgerPostingRequest
            {
                IssuerType = issuerType,
                IssuerId = issuerId,
                AccountingDate = state.Date.Value.Date,
                EventDate = state.Date.Value.Date,
                Description = "Ingående balanser (SIE-import)",
                SourceType = LedgerSourceType.OpeningBalance,
                CreatedByMemberId = byMemberId,
                Lines = lines.Select(kv => kv.Value > 0
                        ? new LedgerPostingLine { AccountNumber = kv.Key, Debit = kv.Value, Text = "Ingående balans", VatRate = 0 }
                        : new LedgerPostingLine { AccountNumber = kv.Key, Credit = -kv.Value, Text = "Ingående balans", VatRate = 0 })
                    .ToList()
            });
            return result.Success
                ? new OpeningBalanceResult { Success = true, EntryId = result.EntryId }
                : OpeningBalanceResult.Fail(result.Error ?? "De ingående balanserna kunde inte bokföras.");
        }

        /// <summary>
        /// Konteringen, som en ren funktion. Tillgångar i debet, skulder i kredit, och eget kapital
        /// som den rad som gör att verifikationen balanserar.
        /// </summary>
        internal static List<LedgerPostingLine> BuildLines(IEnumerable<OpeningBalanceLine> lines, int equityAccount)
        {
            var result = new List<LedgerPostingLine>();
            decimal debit = 0m, credit = 0m;

            foreach (var l in lines.Where(x => x.Amount != 0m).OrderBy(x => x.AccountNumber))
            {
                var asset = l.AccountNumber < 2000;
                // Ett negativt saldo (övertrasserat konto, en fordran som blivit skuld) byter sida.
                var toDebit = asset ? l.Amount > 0 : l.Amount < 0;
                var amount = Math.Abs(l.Amount);

                result.Add(toDebit
                    ? new LedgerPostingLine { AccountNumber = l.AccountNumber, Debit = amount, Text = "Ingående balans" }
                    : new LedgerPostingLine { AccountNumber = l.AccountNumber, Credit = amount, Text = "Ingående balans" });

                if (toDebit) debit += amount; else credit += amount;
            }

            var equity = debit - credit;
            if (equity > 0)
                result.Add(new LedgerPostingLine { AccountNumber = equityAccount, Credit = equity, Text = "Eget kapital vid start" });
            else if (equity < 0)
                result.Add(new LedgerPostingLine { AccountNumber = equityAccount, Debit = -equity, Text = "Eget kapital vid start" });

            return result;
        }

        private static LedgerFiscalYear? FirstYear(LedgerDb ldb, int issuerType, int issuerId)
            => ldb.Fetch<LedgerFiscalYear>(
                @"SELECT TOP 1 * FROM dbo.LedgerFiscalYear
                   WHERE IssuerType = @0 AND IssuerId = @1 ORDER BY StartDate",
                issuerType, issuerId).FirstOrDefault();

        /// <summary>Den gällande verifikationen: inte rättad, och inte själv en rättelse.</summary>
        private static LedgerJournalEntry? LiveEntry(LedgerDb ldb, int issuerType, int issuerId)
            => ldb.Fetch<LedgerJournalEntry>(
                @"SELECT TOP 1 e.* FROM dbo.LedgerJournalEntry e
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1 AND e.SourceType = @2
                     AND e.CorrectsEntryId IS NULL
                     AND NOT EXISTS (SELECT 1 FROM dbo.LedgerJournalEntry c WHERE c.CorrectsEntryId = e.Id)
                   ORDER BY e.Id DESC",
                issuerType, issuerId, LedgerSourceType.OpeningBalance).FirstOrDefault();
    }

    public class OpeningBalanceLine
    {
        public int AccountNumber { get; set; }

        /// <summary>Saldot som det står på papperet: en tillgång positiv, en skuld positiv.</summary>
        public decimal Amount { get; set; }
    }

    public class OpeningBalanceAccount
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public bool IsAsset { get; set; }
    }

    public class OpeningBalanceState
    {
        public int FiscalYearId { get; set; }
        public int Year { get; set; }
        public DateTime? Date { get; set; }
        public bool YearIsOpen { get; set; }
        public int EquityAccountNumber { get; set; }
        public string EquityAccountName { get; set; } = "";
        public List<OpeningBalanceAccount> Accounts { get; } = new();
        public int? ExistingEntryId { get; set; }
        public int? ExistingNumber { get; set; }
        public decimal ExistingEquity { get; set; }
        public List<OpeningBalanceLine> ExistingLines { get; } = new();
    }

    public class OpeningBalanceResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int EntryId { get; set; }
        public bool Removed { get; set; }

        public static OpeningBalanceResult Fail(string e) => new() { Success = false, Error = e };
    }
}
