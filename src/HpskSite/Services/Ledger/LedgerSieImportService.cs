using HpskSite.Models;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// SIE-import (P10.2) — en förening som byter till pistol.nu tar med sig kontoplanen, de ingående
    /// balanserna och årets verifikationer hittills.
    ///
    /// <para><b>⚠️⚠️ VARFÖR.</b> Utan import kan en klubb bara ansluta vid ett årsskifte (planens
    /// "underskattade riktning"). Med den kan den byta i mars och ändå göra ett komplett bokslut i
    /// december. Revisorn (F11): <i>"SIE-filer är standard i branschen."</i></para>
    ///
    /// <para><b>Stefans beslut 2026-09-24:</b></para>
    /// <list type="bullet">
    /// <item>Kontoplan + <c>#IB</c> + årets <c>#VER</c>.</item>
    /// <item>Verifikationerna behåller <b>originalnumren i en egen serie</b> (prefix I + serien) —
    ///   spårbart mot det gamla programmet, och föreningens egen serie förblir obruten.</item>
    /// <item><b>Konton som saknas stoppar importen.</b> Förhandsgranskningen namnger dem; kassören
    ///   lägger upp dem (eller låter oss göra det med filens namn, uttryckligen), sedan importerar hen.</item>
    /// <item><b>Förhandsgranska, och prova i en sandlåda först.</b> Importen går att göra om i den
    ///   riktiga bokföringen — sandlådans rader flyttas aldrig över.</item>
    /// </list>
    ///
    /// <para><b>⚠️ Idempotent per verifikation.</b> En verifikation med samma serie, nummer, datum och
    /// belopp som redan importerats hoppas över. Det gör att en kassör kan exportera från det gamla
    /// programmet i april och igen i maj och få med det som tillkommit — och att en import som avbröts
    /// halvvägs går att köra om. Samma nummer med ANNAT innehåll stoppar allt: då säger filen och
    /// bokföringen olika saker, och ingen av dem får vinna i tysthet.</para>
    ///
    /// <para><b>⚠️ Luckor i det gamla programmets serier förklaras, de fylls inte.</b> BFNAR 2013:2
    /// förutsätter att en lucka går att redogöra för; därför skrivs de i <c>LedgerNumberGap</c>, som
    /// byggdes för just det här.</para>
    /// </summary>
    public class LedgerSieImportService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;
        private readonly LedgerOpeningBalanceService _openingBalances;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly ILogger<LedgerSieImportService> _logger;

        public LedgerSieImportService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerPostingService posting,
            LedgerOpeningBalanceService openingBalances,
            IUmbracoContextFactory contextFactory,
            ILogger<LedgerSieImportService> logger)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
            _openingBalances = openingBalances;
            _contextFactory = contextFactory;
            _logger = logger;
        }

        // ── Förhandsgranskningen ─────────────────────────────────────────────────────────────

        public SieImportPreview Preview(int issuerType, int issuerId, byte[] bytes)
        {
            var p = new SieImportPreview();
            SieFile file;
            try { file = SieParser.Parse(bytes); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SIE-filen kunde inte läsas.");
                p.Errors.Add("Filen kunde inte läsas som en SIE-fil.");
                return p;
            }
            p.File = file;
            p.Errors.AddRange(file.Errors);
            p.Notes.AddRange(file.Notes);

            if (file.YearStart is null || file.YearEnd is null)
            {
                p.Errors.Add("Filen saknar räkenskapsår (#RAR 0).");
                return p;
            }
            if (file.SieType is not null and not "4" && file.Vouchers.Count == 0 && file.OpeningBalances.Count == 0)
                p.Warnings.Add($"Filen är SIE typ {file.SieType}. Verifikationer finns bara i typ 4 — exportera \"SIE 4\" om ni vill ha med dem.");

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var years = ldb.Fetch<LedgerFiscalYear>(
                "SELECT * FROM dbo.LedgerFiscalYear WHERE IssuerType = @0 AND IssuerId = @1 ORDER BY StartDate",
                issuerType, issuerId);
            var fy = years.FirstOrDefault(y => y.StartDate.Date == file.YearStart.Value.Date && y.EndDate.Date == file.YearEnd.Value.Date);
            if (fy is null)
            {
                var sameYear = years.FirstOrDefault(y => y.Year == file.YearStart.Value.Year);
                p.Errors.Add(sameYear is null
                    ? $"Räkenskapsåret {file.YearStart:yyyy-MM-dd}–{file.YearEnd:yyyy-MM-dd} finns inte hos föreningen. Lägg upp det under Inställningar först."
                    : $"Filens räkenskapsår ({file.YearStart:yyyy-MM-dd}–{file.YearEnd:yyyy-MM-dd}) har andra datum än föreningens {sameYear.Year} ({sameYear.StartDate:yyyy-MM-dd}–{sameYear.EndDate:yyyy-MM-dd}).");
                return p;
            }
            p.FiscalYearId = fy.Id;
            p.Year = fy.Year;
            if (fy.Status == LedgerFiscalYearStatus.Established)
            {
                p.Errors.Add($"Räkenskapsåret {fy.Year} är fastställt och tar inte emot bokföring.");
                return p;
            }
            p.IsFirstYear = years[0].Id == fy.Id;

            // Organisationsnumret: en varning, inte ett stopp — men ett fel här är filen från fel förening.
            var ourOrg = ReadOrgNumber(issuerType, issuerId);
            if (!string.IsNullOrWhiteSpace(file.OrgNumber) && !string.IsNullOrWhiteSpace(ourOrg)
                && Digits(file.OrgNumber) != Digits(ourOrg))
                p.Warnings.Add($"Filen gäller organisationsnummer {file.OrgNumber}, men föreningen har {ourOrg}. Är det rätt fil?");

            // ── Kontona ────────────────────────────────────────────────────────────────────
            var ours = ldb.Fetch<LedgerAccount>(
                "SELECT * FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1", issuerType, issuerId)
                .ToDictionary(a => a.Number);
            var used = file.OpeningBalances.Where(kv => kv.Value != 0m).Select(kv => kv.Key)
                .Concat(file.Vouchers.SelectMany(v => v.Transactions.Select(t => t.Account)))
                .Distinct().OrderBy(n => n).ToList();

            foreach (var n in used)
            {
                if (!ours.TryGetValue(n, out var acct))
                    p.MissingAccounts.Add(new SieAccountRef(n, file.Accounts.GetValueOrDefault(n) ?? ""));
                else if (!acct.IsActive)
                    p.Errors.Add($"Konto {n} {acct.Name} är stängt hos föreningen men används i filen. Öppna det igen under Inställningar → Kontoplan.");
                else if (file.Accounts.TryGetValue(n, out var fileName) && !string.IsNullOrWhiteSpace(fileName)
                         && !string.Equals(fileName.Trim(), acct.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                    p.RenamedAccounts.Add(new SieRenamedAccount(n, acct.Name, fileName));
            }
            foreach (var n in used.Where(n => n < 1000 || n > 8999))
                p.Errors.Add($"Konto {n} ligger utanför den fyrsiffriga kontoplanen (1000–8999).");

            // ── Ingående balanser ──────────────────────────────────────────────────────────
            var ib = file.OpeningBalances.Where(kv => kv.Value != 0m).ToDictionary(kv => kv.Key, kv => kv.Value);
            p.OpeningBalanceLines = ib.Count;
            if (ib.Count > 0)
            {
                if (!p.IsFirstYear)
                    p.Notes.Add($"Ingående balanser tas bara in på föreningens första år. För {fy.Year} följer de av förra årets bokföring, så filens #IB läses inte in.");
                else if (ib.Values.Sum() != 0m)
                    p.Errors.Add($"Filens ingående balanser summerar inte till noll ({ib.Values.Sum():N2} kr).");
                else if (ib.Keys.Any(k => k >= 3000))
                    p.Errors.Add("Filen har ingående balanser på resultatkonton (3000–8999). Bara balanskonton kan ha en ingående balans.");
                else
                {
                    var existing = _openingBalances.Get(issuerType, issuerId);
                    p.ImportsOpeningBalances = true;
                    p.OpeningBalanceTotal = ib.Where(kv => kv.Value > 0).Sum(kv => kv.Value);
                    if (existing.ExistingEntryId is not null)
                    {
                        if (SameOpeningBalances(ldb, existing.ExistingEntryId.Value, ib))
                        {
                            p.ImportsOpeningBalances = false;
                            p.Notes.Add("Filens ingående balanser är redan inlagda — de tas inte in igen.");
                        }
                        else p.Warnings.Add("Föreningen har redan ingående balanser. De ERSÄTTS av filens (den gamla verifikationen rättas, den raderas aldrig).");
                    }
                }
            }

            // ── Verifikationerna ───────────────────────────────────────────────────────────
            var existingImports = ldb.Fetch<ImportedRow>(
                @"SELECT s.Kind, s.Id AS SeriesId, e.Number, e.AccountingDate,
                         (SELECT SUM(l.Debit) FROM dbo.LedgerJournalEntryLine l WHERE l.JournalEntryId = e.Id) AS Total
                    FROM dbo.LedgerJournalEntry e
                    JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                   WHERE e.IssuerType = @0 AND e.IssuerId = @1 AND s.Year = @2 AND s.Kind LIKE 'import-%'",
                issuerType, issuerId, fy.Year)
                .ToDictionary(r => (r.Kind, r.Number));

            foreach (var v in file.Vouchers)
            {
                var label = $"{v.Series}{v.Number}";
                if (v.Transactions.Count == 0) { p.Errors.Add($"Verifikation {label} har inga rader."); continue; }
                if (v.Imbalance != 0m) { p.Errors.Add($"Verifikation {label} går inte ihop (debet minus kredit är {v.Imbalance:N2} kr)."); continue; }
                if (v.Date < fy.StartDate.Date || v.Date > fy.EndDate.Date)
                { p.Errors.Add($"Verifikation {label} är daterad {v.Date:yyyy-MM-dd}, utanför räkenskapsåret."); continue; }

                if (existingImports.TryGetValue((LedgerNumberAllocator.ImportKind(v.Series), v.Number), out var had))
                {
                    if (had.AccountingDate.Date == v.Date.Date && had.Total == v.Total) p.AlreadyImported++;
                    else p.Conflicts.Add($"{label}: redan importerad med {had.AccountingDate:yyyy-MM-dd} och {had.Total:N2} kr, i filen {v.Date:yyyy-MM-dd} och {v.Total:N2} kr.");
                    continue;
                }
                p.VouchersToImport++;
                p.VoucherTotal += v.Total;
            }
            var dup = file.Vouchers.GroupBy(v => (v.Series, v.Number)).Where(g => g.Count() > 1).Select(g => $"{g.Key.Series}{g.Key.Number}").ToList();
            if (dup.Count > 0) p.Errors.Add($"Samma verifikationsnummer står flera gånger i filen: {string.Join(", ", dup.Take(10))}.");

            // Serierna och luckorna — över filen OCH det som redan importerats.
            var documented = ldb.Fetch<LedgerNumberGap>(
                @"SELECT g.* FROM dbo.LedgerNumberGap g JOIN dbo.LedgerNumberSeries s ON s.Id = g.SeriesId
                   WHERE s.IssuerType = @0 AND s.IssuerId = @1 AND s.Year = @2 AND s.Kind LIKE 'import-%'",
                issuerType, issuerId, fy.Year);
            foreach (var g in file.Vouchers.GroupBy(v => v.Series).OrderBy(g => g.Key))
            {
                var kind = LedgerNumberAllocator.ImportKind(g.Key);
                var numbers = g.Select(v => v.Number)
                    .Concat(existingImports.Keys.Where(k => k.Kind == kind).Select(k => k.Number)).ToList();
                var seriesIds = existingImports.Values.Where(r => r.Kind == kind).Select(r => r.SeriesId).Distinct().ToList();
                var gaps = Gaps(numbers, documented.Where(d => seriesIds.Contains(d.SeriesId)).Select(d => (d.FromNumber, d.ToNumber)));
                p.Series.Add(new SieSeriesSummary(g.Key, LedgerNumberAllocator.ImportPrefix(g.Key),
                    g.Min(v => v.Number), g.Max(v => v.Number), g.Count(), gaps));
            }

            // Överlapp med det föreningen redan bokfört här — samma händelse kan finnas på båda ställena.
            if (file.Vouchers.Count > 0)
            {
                var from = file.Vouchers.Min(v => v.Date);
                var to = file.Vouchers.Max(v => v.Date);
                var own = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerJournalEntry e
                       JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                      WHERE e.IssuerType = @0 AND e.IssuerId = @1 AND e.FiscalYearId = @2
                        AND s.Kind NOT LIKE 'import-%' AND e.SourceType <> @3
                        AND e.AccountingDate BETWEEN @4 AND @5",
                    issuerType, issuerId, fy.Id, LedgerSourceType.OpeningBalance, from.Date, to.Date);
                if (own > 0)
                    p.Warnings.Add($"Föreningen har redan {own} egna verifikationer här mellan {from:yyyy-MM-dd} och {to:yyyy-MM-dd}. "
                                 + "Kontrollera att samma händelser inte också finns i filen — då blir de bokförda två gånger.");
            }

            return p;
        }

        // ── Importen ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gör importen. Kör förhandsgranskningen igen på servern — klientens bild av filen räknas inte.
        /// </summary>
        public SieImportResult Import(int issuerType, int issuerId, byte[] bytes, string? gapExplanation, int byMemberId)
        {
            var p = Preview(issuerType, issuerId, bytes);
            if (!p.CanImport)
                return SieImportResult.Fail(p.Errors.FirstOrDefault()
                    ?? (p.MissingAccounts.Count > 0 ? $"{p.MissingAccounts.Count} konton saknas i kontoplanen. Lägg upp dem först." : null)
                    ?? p.Conflicts.FirstOrDefault() ?? "Filen kan inte importeras.");

            var hasGaps = p.Series.Any(s => s.Gaps.Count > 0);
            if (hasGaps && string.IsNullOrWhiteSpace(gapExplanation))
                return SieImportResult.Fail("Filens serier har luckor. Skriv en kort förklaring — den sparas med luckorna.");

            var result = new SieImportResult { Success = true };
            var file = p.File!;

            if (p.ImportsOpeningBalances)
            {
                var ib = file.OpeningBalances.Where(kv => kv.Value != 0m).ToDictionary(kv => kv.Key, kv => kv.Value);
                var saved = _openingBalances.SaveFromImport(issuerType, issuerId, ib, byMemberId);
                if (!saved.Success) return SieImportResult.Fail("De ingående balanserna kunde inte bokföras: " + saved.Error);
                result.OpeningBalancesImported = true;
            }

            Dictionary<(string, int), bool> done;
            using (var db = _databaseFactory.CreateDatabase())
            {
                var ldb = new LedgerDb(db, issuerId);
                done = ldb.Fetch<ImportedRow>(
                        @"SELECT s.Kind, s.Id AS SeriesId, e.Number FROM dbo.LedgerJournalEntry e
                           JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                          WHERE e.IssuerType = @0 AND e.IssuerId = @1 AND s.Year = @2 AND s.Kind LIKE 'import-%'",
                        issuerType, issuerId, p.Year)
                    .ToDictionary(r => (r.Kind, r.Number), _ => true);
            }

            foreach (var v in file.Vouchers.OrderBy(v => v.Series).ThenBy(v => v.Number))
            {
                if (done.ContainsKey((LedgerNumberAllocator.ImportKind(v.Series), v.Number))) continue;

                var posted = _posting.Post(new LedgerPostingRequest
                {
                    IssuerType = issuerType,
                    IssuerId = issuerId,
                    AccountingDate = v.Date,
                    EventDate = v.Date,
                    Description = string.IsNullOrWhiteSpace(v.Text) ? $"Verifikation {v.Series}{v.Number}" : v.Text,
                    SourceType = LedgerSourceType.SieImport,
                    CreatedByMemberId = byMemberId,
                    ImportSeries = v.Series,
                    ImportNumber = v.Number,
                    // ⚠️ VatRate = 0 på varje rad: filens momsrader står redan som egna rader. Utan det
                    //    hade kontots förvalda moms delat beloppet en gång till.
                    Lines = v.Transactions.Select(t => t.Amount > 0
                            ? new LedgerPostingLine { AccountNumber = t.Account, Debit = t.Amount, Text = t.Text, VatRate = 0 }
                            : new LedgerPostingLine { AccountNumber = t.Account, Credit = -t.Amount, Text = t.Text, VatRate = 0 })
                        .Where(l => l.Debit > 0 || l.Credit > 0)
                        .ToList()
                });

                if (!posted.Success)
                {
                    // ⚠️ Det som redan bokförts står kvar — det är oföränderligt, och en omkörning hoppar
                    //    över det. Säg exakt var det tog stopp.
                    result.Success = false;
                    result.Error = $"Verifikation {v.Series}{v.Number} kunde inte bokföras: {posted.Error} "
                                 + $"{result.VouchersImported} verifikationer hann importeras. Rätta orsaken och importera filen igen — det som redan kommit in hoppas över.";
                    break;
                }
                result.VouchersImported++;
            }

            if (result.Success && hasGaps) result.GapsRecorded = RecordGaps(issuerType, issuerId, p, gapExplanation!.Trim(), byMemberId);

            Audit(issuerType, issuerId, byMemberId, file, result);
            return result;
        }

        private int RecordGaps(int issuerType, int issuerId, SieImportPreview p, string explanation, int byMemberId)
        {
            var count = 0;
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);
            foreach (var s in p.Series.Where(s => s.Gaps.Count > 0))
            {
                var seriesId = ldb.ExecuteScalar<int>(
                    "SELECT Id FROM dbo.LedgerNumberSeries WHERE IssuerType = @0 AND IssuerId = @1 AND Year = @2 AND Kind = @3",
                    issuerType, issuerId, p.Year, LedgerNumberAllocator.ImportKind(s.SourceSeries));
                if (seriesId == 0) continue;
                foreach (var (from, to) in s.Gaps)
                {
                    ldb.Execute(
                        @"INSERT INTO dbo.LedgerNumberGap (SeriesId, FromNumber, ToNumber, Explanation, RecordedByMemberId, RecordedUtc)
                          VALUES (@0, @1, @2, @3, @4, @5)",
                        seriesId, from, to, explanation.Length > 400 ? explanation[..400] : explanation, byMemberId, DateTime.UtcNow);
                    count++;
                }
            }
            return count;
        }

        private void Audit(int issuerType, int issuerId, int byMemberId, SieFile file, SieImportResult result)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);
                var detail = System.Text.Json.JsonSerializer.Serialize(new
                {
                    program = file.Program, org = file.OrgNumber, year = file.YearStart?.Year,
                    vouchers = result.VouchersImported, ib = result.OpeningBalancesImported,
                    gaps = result.GapsRecorded, ok = result.Success, error = result.Error
                });
                ldb.Execute(
                    @"INSERT INTO dbo.LedgerAuditEvent (OccurredUtc, MemberId, Action, ObjectType, ObjectId, Detail)
                      VALUES (@0, @1, @2, @3, @4, @5)",
                    DateTime.UtcNow, byMemberId, LedgerAuditAction.Imported, "SieFile", issuerId, detail);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Importens loggrad kunde inte skrivas för {Typ}/{Id}.", issuerType, issuerId);
            }
        }

        // ── Rena hjälpare ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Luckorna i en nummerföljd, som intervall, minus de som redan är förklarade.
        /// <para>Bara INNANFÖR första och sista numret — en serie som börjar på 5 för att året
        /// började mitt i en äldre serie har ingen lucka före 5 som vi kan veta något om.</para>
        /// </summary>
        internal static List<(int From, int To)> Gaps(IEnumerable<int> numbers, IEnumerable<(int From, int To)> documented)
        {
            var set = numbers.ToHashSet();
            var result = new List<(int, int)>();
            if (set.Count == 0) return result;
            var docs = documented.ToList();
            bool IsDocumented(int n) => docs.Any(d => n >= d.From && n <= d.To);

            int? start = null;
            for (var n = set.Min(); n <= set.Max() + 1; n++)
            {
                var missing = n <= set.Max() && !set.Contains(n) && !IsDocumented(n);
                if (missing && start is null) start = n;
                if (!missing && start is not null) { result.Add((start.Value, n - 1)); start = null; }
            }
            return result;
        }

        private bool SameOpeningBalances(LedgerDb ldb, int entryId, Dictionary<int, decimal> ib)
        {
            var lines = ldb.Fetch<LedgerJournalEntryLine>(
                "SELECT * FROM dbo.LedgerJournalEntryLine WHERE JournalEntryId = @0", entryId);
            var have = lines.GroupBy(l => l.AccountNumber).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
            return have.Where(kv => kv.Value != 0m).OrderBy(kv => kv.Key)
                .SequenceEqual(ib.OrderBy(kv => kv.Key));
        }

        private string? ReadOrgNumber(int issuerType, int issuerId)
        {
            try
            {
                // Utställarens nod: den levande har nodens id; en sandlåda pekar på sin ägare.
                var ownerId = issuerId;
                if (issuerId < 0)
                {
                    using var db = _databaseFactory.CreateDatabase();
                    ownerId = db.ExecuteScalar<int>("SELECT OwnerId FROM dbo.LedgerIssuer WHERE Id = @0", issuerId);
                }
                using var cref = _contextFactory.EnsureUmbracoContext();
                return cref.UmbracoContext.Content?.GetById(ownerId)?.Value<string>("orgNumber");
            }
            catch { return null; }
        }

        private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

        private sealed class ImportedRow
        {
            public string Kind { get; set; } = "";
            public int SeriesId { get; set; }
            public int Number { get; set; }
            public DateTime AccountingDate { get; set; }
            public decimal Total { get; set; }
        }
    }

    public readonly record struct SieAccountRef(int Number, string Name);
    public readonly record struct SieRenamedAccount(int Number, string OurName, string FileName);
    public sealed record SieSeriesSummary(string SourceSeries, string Prefix, int First, int Last, int Count, List<(int From, int To)> Gaps);

    public sealed class SieImportPreview
    {
        public SieFile? File { get; set; }
        public int FiscalYearId { get; set; }
        public int Year { get; set; }
        public bool IsFirstYear { get; set; }
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> Notes { get; } = new();
        public List<SieAccountRef> MissingAccounts { get; } = new();
        public List<SieRenamedAccount> RenamedAccounts { get; } = new();
        public List<string> Conflicts { get; } = new();
        public List<SieSeriesSummary> Series { get; } = new();
        public int OpeningBalanceLines { get; set; }
        public bool ImportsOpeningBalances { get; set; }
        public decimal OpeningBalanceTotal { get; set; }
        public int VouchersToImport { get; set; }
        public decimal VoucherTotal { get; set; }
        public int AlreadyImported { get; set; }

        /// <summary>Inga fel, inga saknade konton, inga motsägelser mot det som redan importerats.</summary>
        public bool CanImport => File != null && Errors.Count == 0 && MissingAccounts.Count == 0 && Conflicts.Count == 0
                                 && (ImportsOpeningBalances || VouchersToImport > 0);
    }

    public sealed class SieImportResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public bool OpeningBalancesImported { get; set; }
        public int VouchersImported { get; set; }
        public int GapsRecorded { get; set; }
        public static SieImportResult Fail(string e) => new() { Success = false, Error = e };
    }
}
