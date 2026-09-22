using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Den manuella bokför-ytan: ett kvitto, en kontant insättning, en korrigering.
    ///
    /// <para><b>⚠️⚠️ KASSÖREN ANGER BELOPP OCH RIKTNING — INTE DEBET OCH KREDIT.</b> Att kräva en
    /// kontering av en förtroendevald är att kräva att hen kan dubbel bokföring, vilket är precis
    /// det den här modulen finns för att slippa. Konteringen byggs här och <b>visas</b> innan den
    /// sparas ("Så bokförs det" i skissen) — vi gömmer inte, vi sorterar.</para>
    ///
    /// <para><b>⚠️ Det här är enda stället som får sätta ett uttryckligt kontonummer på en rad.</b>
    /// <see cref="LedgerPostingLine.AccountNumber"/> finns för just den här ytan; en
    /// systemgenererad postning pekar på en ROLL, annars står kontoplanen i koden igen.</para>
    /// </summary>
    public class LedgerManualPostingService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;

        /// <summary>Hur många verifikationer "Senast bokfört" visar. En puls, inte ett arkiv.</summary>
        private const int RecentLimit = 6;

        public LedgerManualPostingService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerPostingService posting)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
        }

        /// <summary>
        /// Föreningens konton, betalkonton och de senaste verifikationerna.
        /// </summary>
        public ManualPostingContext BuildContext(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var ctx = new ManualPostingContext();

            // ⚠️ Ur FÖRENINGENS kontoplan. Avstängda konton utelämnas — de får inte väljas på nytt,
            // men de raderas aldrig, för en historisk rad ska fortfarande gå att förklara.
            ctx.Accounts.AddRange(db.Fetch<LedgerAccount>(
                    @"SELECT * FROM dbo.LedgerAccount
                       WHERE IssuerType = @0 AND IssuerId = @1 AND IsActive = 1
                       ORDER BY Number",
                    issuerType, issuerId)
                .Select(a => new AccountOption
                {
                    Number = a.Number,
                    Name = a.Name,
                    // Kontoklass 1 och 2 är balanskonton — pengarnas väg, inte vad de var.
                    IsBalance = a.Number < 3000
                }));

            // Betalkontot föreslås ur rollmappningen, aldrig ur ett hårdkodat nummer.
            var roles = db.Fetch<LedgerAccountRole>(
                "SELECT * FROM dbo.LedgerAccountRole WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            foreach (var key in new[] { LedgerAccountRoles.BankAccount, LedgerAccountRoles.CashBox, LedgerAccountRoles.Swish })
            {
                var mapped = roles.FirstOrDefault(r => r.RoleKey == key);
                if (mapped is null) continue;

                var acc = ctx.Accounts.FirstOrDefault(a => a.Number == mapped.AccountNumber);

                ctx.PaymentAccounts.Add(new AccountOption
                {
                    Number = mapped.AccountNumber,
                    Name = acc?.Name ?? key,
                    IsBalance = true
                });
            }

            var series = db.FirstOrDefault<LedgerNumberSeries>(
                @"SELECT TOP 1 * FROM dbo.LedgerNumberSeries
                   WHERE IssuerType = @0 AND IssuerId = @1 AND Kind = @2
                   ORDER BY Year DESC",
                issuerType, issuerId, LedgerSeriesKind.JournalEntry);

            ctx.SeriesLabel = series is null ? "" : $"Serie {series.Prefix} · {series.Year}";

            var recent = db.Fetch<LedgerJournalEntry>(
                @"SELECT TOP " + RecentLimit + @" * FROM dbo.LedgerJournalEntry
                   WHERE IssuerType = @0 AND IssuerId = @1
                   ORDER BY Id DESC",
                issuerType, issuerId);

            if (recent.Count > 0)
            {
                var seriesById = db.Fetch<LedgerNumberSeries>(
                        "SELECT * FROM dbo.LedgerNumberSeries WHERE IssuerType = @0 AND IssuerId = @1",
                        issuerType, issuerId)
                    .ToDictionary(s => s.Id);

                // Beloppet är verifikationens debetsumma — en balanserad verifikation har samma
                // summa på båda sidor, så vilken som helst duger; debet är den konventionella.
                var sums = db.Fetch<EntrySum>(
                        $@"SELECT JournalEntryId, SUM(Debit) AS Total
                             FROM dbo.LedgerJournalEntryLine
                            WHERE JournalEntryId IN ({string.Join(",", recent.Select(e => e.Id))})
                            GROUP BY JournalEntryId")
                    .ToDictionary(s => s.JournalEntryId, s => s.Total);

                foreach (var e in recent)
                {
                    seriesById.TryGetValue(e.SeriesId, out var s);

                    ctx.Recent.Add(new RecentEntry
                    {
                        Id = e.Id,
                        Number = LedgerNumberAllocator.Format(s?.Prefix ?? "", e.Number),
                        Description = e.Description,
                        AccountingDate = e.AccountingDate,
                        Amount = sums.TryGetValue(e.Id, out var t) ? t : 0m
                    });
                }
            }

            return ctx;
        }

        /// <summary>
        /// Bygger konteringen och bokför den.
        ///
        /// <para><b>⚠️ Riktningen är hela regeln.</b> "Vi betalade" = kostnadskontot i DEBET och
        /// betalkontot i kredit; "Vi fick in" = tvärtom. Blandas de ihop hamnar en utgift som en
        /// intäkt, och felet syns först i resultatrapporten.</para>
        /// </summary>
        public ManualPostingResult Post(ManualEntryRequest r, int byMemberId)
        {
            var validation = Validate(r);
            if (validation is not null) return ManualPostingResult.Failed(validation);

            var lines = BuildLines(r);

            var result = _posting.Post(new LedgerPostingRequest
            {
                IssuerType = r.IssuerType,
                IssuerId = r.IssuerId,
                AccountingDate = r.Date!.Value.Date,
                EventDate = r.Date!.Value.Date,
                Description = r.Description.Trim(),
                SourceType = LedgerSourceType.Manual,
                CreatedByMemberId = byMemberId,
                ProjectId = r.ProjectId,
                Lines = lines
            });

            if (!result.Success) return ManualPostingResult.Failed(result.Error ?? "Bokföringen gick inte igenom.");

            // ⚠️ Prefixet bor på SERIEN, inte på resultatet — läs det tillbaka, annars visar ytan
            // ett naket nummer där kassören förväntar sig "V-215".
            string? number = null;
            using (var db = _databaseFactory.CreateDatabase())
            {
                var prefix = db.ExecuteScalar<string>(
                    @"SELECT TOP 1 Prefix FROM dbo.LedgerNumberSeries
                       WHERE IssuerType = @0 AND IssuerId = @1 AND Kind = @2
                       ORDER BY Year DESC",
                    r.IssuerType, r.IssuerId, LedgerSeriesKind.JournalEntry);

                number = LedgerNumberAllocator.Format(prefix ?? "", result.Number);
            }

            return new ManualPostingResult
            {
                Success = true,
                EntryId = result.EntryId,
                Number = number
            };
        }

        /// <summary>
        /// Kontrollerna, som en ren funktion. <b>Ligger utanför databasen med flit</b> så reglerna
        /// går att pröva utan hela stacken.
        /// </summary>
        /// <returns>Felmeddelande på svenska, eller null när begäran duger.</returns>
        internal static string? Validate(ManualEntryRequest r)
        {
            if (r.Amount <= 0)
                return "Beloppet måste vara större än noll.";

            if (r.Date is null)
                return "Ange vilket datum posten gäller.";

            if (string.IsNullOrWhiteSpace(r.Description))
                return "Skriv vad posten avser — det är den texten som står i bokföringen.";

            if (r.AccountNumber <= 0)
                return "Välj vilket konto posten hör till.";

            if (r.PaymentAccountNumber <= 0)
                return "Välj vilket konto pengarna gick till eller från.";

            // ⚠️ Samma konto på båda sidor ger en verifikation som balanserar men inte betyder
            // något — 3 150 in och 3 150 ut på föreningskontot. Den ser korrekt ut i varje
            // kontroll utom den mänskliga.
            if (r.AccountNumber == r.PaymentAccountNumber)
                return "Posten skulle bokföras mot samma konto på båda sidor. Välj olika konton.";

            return null;
        }

        /// <summary>
        /// Konteringens två rader. <b>Ren funktion</b> — samma indata ger samma rader, och de går
        /// att pröva utan databas. Det är också exakt de rader ytan visar under "Så bokförs det",
        /// så det kassören godkände är det som bokförs.
        /// </summary>
        internal static List<LedgerPostingLine> BuildLines(ManualEntryRequest r)
        {
            var amount = r.Amount;

            // Vi betalade: kostnaden i debet, pengarna ut ur betalkontot (kredit).
            // Vi fick in:  pengarna in på betalkontot (debet), intäkten i kredit.
            return r.WeReceived
                ? new List<LedgerPostingLine>
                {
                    new() { AccountNumber = r.PaymentAccountNumber, Debit = amount, VatRate = 0 },
                    new() { AccountNumber = r.AccountNumber, Credit = amount, Text = r.Description.Trim() }
                }
                : new List<LedgerPostingLine>
                {
                    new() { AccountNumber = r.AccountNumber, Debit = amount, Text = r.Description.Trim() },
                    new() { AccountNumber = r.PaymentAccountNumber, Credit = amount, VatRate = 0 }
                };
        }

        private class EntrySum
        {
            public int JournalEntryId { get; set; }
            public decimal Total { get; set; }
        }
    }

    /// <summary>Det bokför-ytan skickar.</summary>
    public class ManualEntryRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }

        public decimal Amount { get; set; }

        /// <summary><c>true</c> = "Vi fick in", <c>false</c> = "Vi betalade".</summary>
        public bool WeReceived { get; set; }

        public DateTime? Date { get; set; }

        public string Description { get; set; } = "";

        /// <summary>Kostnads- eller intäktskontot — vad posten VAR.</summary>
        public int AccountNumber { get; set; }

        /// <summary>Betalkontot — pengarnas VÄG.</summary>
        public int PaymentAccountNumber { get; set; }

        public int? ProjectId { get; set; }
    }

    public class ManualPostingResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public int EntryId { get; set; }
        public string? Number { get; set; }

        public static ManualPostingResult Failed(string error) => new() { Success = false, Error = error };
    }

    public class ManualPostingContext
    {
        public List<AccountOption> Accounts { get; } = new();

        /// <summary>Betalkonton ur rollmappningen — bank, kontantkassa, Swish.</summary>
        public List<AccountOption> PaymentAccounts { get; } = new();

        /// <summary>"Serie V · 2026", som i skissen.</summary>
        public string SeriesLabel { get; set; } = "";

        public List<RecentEntry> Recent { get; } = new();
    }

    public class AccountOption
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";

        /// <summary>Kontoklass 1–2: pengarnas väg, inte vad de var.</summary>
        public bool IsBalance { get; set; }
    }

    public class RecentEntry
    {
        public int Id { get; set; }
        public string Number { get; set; } = "";
        public string Description { get; set; } = "";
        public DateTime AccountingDate { get; set; }
        public decimal Amount { get; set; }
    }
}
