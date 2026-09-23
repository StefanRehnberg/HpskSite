using HpskSite.Models.Ledger;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Verifikationslistan, verifikationen och huvudboken. <b>Läser bara.</b>
    ///
    /// <para><b>⚠️⚠️ DEN HÄR YTAN ÄR REVISIONENS RYGGRAD.</b> Skatteverket kräver att revisorn
    /// självständigt kan följa <i>bokförd transaktion → verifikation → faktisk betalning</i> åt
    /// båda hållen. Alla tre leden finns i databasen sedan tidigare; det som saknades var en väg
    /// att gå dem. Därför bär varje rad både sitt antal bilagor och sitt antal bankmatchade
    /// rader — utan dem är kedjan osynlig, och en osynlig kedja går inte att granska.</para>
    ///
    /// <para><b>⚠️ Ingenting här får kunna skriva.</b> Tjänsten tar aldrig emot ett medlems-id att
    /// skriva med och har ingen metod som muterar. Verifikationen är oföränderlig och bevakas av
    /// en databastrigger; en skrivväg här hade varit ett sätt runt den.</para>
    /// </summary>
    public class LedgerJournalService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly ILogger<LedgerJournalService> _logger;

        /// <summary>Sidstorlekens tak. En revisor bläddrar; en klient som ber om allt får inte ta ner sidan.</summary>
        public const int MaxTake = 200;

        public LedgerJournalService(
            IUmbracoDatabaseFactory databaseFactory,
            IMemberService memberService,
            ILogger<LedgerJournalService> logger)
        {
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _logger = logger;
        }

        /// <summary>
        /// Verifikationslistan.
        ///
        /// <para><b>⚠️ Ordnad på NUMMER, fallande.</b> Aldrig på <c>Id</c>: sandlådans identitet
        /// räknar nedåt (<c>IDENTITY(-1,-1)</c>), så <c>Id DESC</c> ger omvänd ordning just där.
        /// Numret är luckfritt och stigande i båda schemana.</para>
        /// </summary>
        public LedgerJournalPage List(
            int issuerType, int issuerId,
            int? fiscalYearId = null,
            DateTime? from = null, DateTime? to = null,
            int? accountNumber = null,
            string? search = null,
            int skip = 0, int take = 50)
        {
            var page = new LedgerJournalPage
            {
                Skip = Math.Max(0, skip),
                Take = Math.Clamp(take, 1, MaxTake)
            };

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var where = " WHERE e.IssuerType = @0 AND e.IssuerId = @1";
                var args = new List<object> { issuerType, issuerId };

                if (fiscalYearId is > 0 or < 0)
                {
                    where += $" AND e.FiscalYearId = @{args.Count}";
                    args.Add(fiscalYearId.Value);
                }

                if (from.HasValue)
                {
                    where += $" AND e.AccountingDate >= @{args.Count}";
                    args.Add(from.Value.Date);
                }

                if (to.HasValue)
                {
                    where += $" AND e.AccountingDate <= @{args.Count}";
                    args.Add(to.Value.Date);
                }

                // ⚠️ Kontofiltret är ett EXISTS mot raderna, inte en join. En join hade gett
                //    verifikationen en gång per träffande rad, och en verifikation med två rader
                //    på samma konto hade då stått två gånger i listan.
                if (accountNumber is > 0)
                {
                    where += $@" AND EXISTS (SELECT 1 FROM dbo.LedgerJournalEntryLine fl
                                              WHERE fl.JournalEntryId = e.Id
                                                AND fl.AccountNumber = @{args.Count})";
                    args.Add(accountNumber.Value);
                }

                // ⚠️ Fritexten söker i beskrivning, motpart OCH radernas text. Kassören minns
                //    "Clas Ohlson", och det ordet står ofta bara på konteringsraden.
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var like = "%" + search.Trim() + "%";
                    where += $@" AND (e.Description LIKE @{args.Count}
                                   OR e.CounterpartyName LIKE @{args.Count}
                                   OR EXISTS (SELECT 1 FROM dbo.LedgerJournalEntryLine sl
                                               WHERE sl.JournalEntryId = e.Id
                                                 AND (sl.Text LIKE @{args.Count}
                                                   OR sl.AccountName LIKE @{args.Count})))";
                    args.Add(like);
                }

                page.TotalCount = ldb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM dbo.LedgerJournalEntry e" + where, args.ToArray());

                var rows = ldb.Fetch<JournalListRow>(
                    $@"SELECT e.Id, e.Number, s.Prefix, e.AccountingDate, e.EventDate,
                              e.Description, e.CounterpartyName, e.SourceType, e.CorrectsEntryId,
                              (SELECT SUM(l.Debit) FROM dbo.LedgerJournalEntryLine l
                                WHERE l.JournalEntryId = e.Id) AS Amount,
                              (SELECT COUNT(1) FROM dbo.LedgerAttachment a
                                WHERE a.JournalEntryId = e.Id AND a.VoidedUtc IS NULL) AS AttachmentCount,
                              (SELECT COUNT(1) FROM dbo.LedgerBankRow b
                                JOIN dbo.LedgerJournalEntryLine bl ON bl.Id = b.MatchedLineId
                               WHERE bl.JournalEntryId = e.Id) AS BankMatchedLines
                         FROM dbo.LedgerJournalEntry e
                         JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                         {where}
                        ORDER BY e.Number DESC
                        OFFSET {page.Skip} ROWS FETCH NEXT {page.Take} ROWS ONLY",
                    args.ToArray());

                foreach (var r in rows) page.Rows.Add(ToRow(r));

                BuildSeriesSpans(ldb, page, issuerType, issuerId, fiscalYearId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa verifikationslistan för {Typ}/{Id}.", issuerType, issuerId);
            }

            return page;
        }

        /// <summary>
        /// Numrens omfång per serie, för luckkontrollen.
        ///
        /// <para><b>⚠️ Räknas på HELA året, aldrig på den filtrerade sidan.</b> Ett filter tar
        /// naturligtvis bort nummer, och att rapportera det som en lucka hade gett falskt larm på
        /// varje sökning — alltså en varning som slutar betyda något.</para>
        /// </summary>
        private static void BuildSeriesSpans(
            LedgerDb ldb, LedgerJournalPage page, int issuerType, int issuerId, int? fiscalYearId)
        {
            var yearWhere = fiscalYearId is > 0 or < 0 ? " AND e.FiscalYearId = @2" : "";
            var args = fiscalYearId is > 0 or < 0
                ? new object[] { issuerType, issuerId, fiscalYearId!.Value }
                : new object[] { issuerType, issuerId };

            var spans = ldb.Fetch<SeriesSpanRow>(
                $@"SELECT s.Prefix, MIN(e.Number) AS First_, MAX(e.Number) AS Last_, COUNT(1) AS Count_
                     FROM dbo.LedgerJournalEntry e
                     JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                    WHERE e.IssuerType = @0 AND e.IssuerId = @1{yearWhere}
                    GROUP BY s.Prefix",
                args);

            foreach (var s in spans)
                page.Series.Add(new LedgerSeriesSpan
                {
                    Prefix = s.Prefix,
                    First = s.First_,
                    Last = s.Last_,
                    Count = s.Count_
                });
        }

        /// <summary>En verifikation med rader, bilagor och bankmatchningar.</summary>
        public LedgerJournalDetail? Detail(int issuerType, int issuerId, int entryId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                // ⚠️ Utställaren står i WHERE. Utan den räcker ett gissat entryId för att läsa en
                //    ANNAN förenings verifikation — och den här ytan delas med en revisor som
                //    bara är inbjuden till EN förening.
                var head = ldb.Fetch<JournalListRow>(
                    @"SELECT e.Id, e.Number, s.Prefix, e.AccountingDate, e.EventDate,
                             e.Description, e.CounterpartyName, e.SourceType, e.CorrectsEntryId,
                             (SELECT SUM(l.Debit) FROM dbo.LedgerJournalEntryLine l
                               WHERE l.JournalEntryId = e.Id) AS Amount,
                             (SELECT COUNT(1) FROM dbo.LedgerAttachment a
                               WHERE a.JournalEntryId = e.Id AND a.VoidedUtc IS NULL) AS AttachmentCount,
                             (SELECT COUNT(1) FROM dbo.LedgerBankRow b
                               JOIN dbo.LedgerJournalEntryLine bl ON bl.Id = b.MatchedLineId
                              WHERE bl.JournalEntryId = e.Id) AS BankMatchedLines
                        FROM dbo.LedgerJournalEntry e
                        JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                       WHERE e.Id = @0 AND e.IssuerType = @1 AND e.IssuerId = @2",
                    entryId, issuerType, issuerId).FirstOrDefault();

                if (head is null) return null;

                var detail = new LedgerJournalDetail { Head = ToRow(head) };

                detail.Lines.AddRange(ldb.Fetch<LedgerJournalEntryLine>(
                    "SELECT * FROM dbo.LedgerJournalEntryLine WHERE JournalEntryId = @0 ORDER BY LineNumber",
                    entryId));

                var attachments = ldb.Fetch<LedgerAttachment>(
                    "SELECT * FROM dbo.LedgerAttachment WHERE JournalEntryId = @0 ORDER BY Id",
                    entryId);

                var bank = ldb.Fetch<BankMatchRow>(
                    @"SELECT b.BookedDate, b.Text, b.Amount, b.Reference, b.MatchKind,
                             b.MatchedByMemberId, b.MatchedUtc
                        FROM dbo.LedgerBankRow b
                        JOIN dbo.LedgerJournalEntryLine bl ON bl.Id = b.MatchedLineId
                       WHERE bl.JournalEntryId = @0
                       ORDER BY b.BookedDate",
                    entryId);

                // ⚠️ Namnen slås upp i EN omgång. En uppslagning per rad är den fälla som gjorde
                //    fakturasidan tolv sekunder lång.
                var names = ResolveNames(
                    attachments.Select(a => a.UploadedByMemberId)
                        .Concat(attachments.Where(a => a.VoidedByMemberId.HasValue).Select(a => a.VoidedByMemberId!.Value))
                        .Concat(bank.Where(b => b.MatchedByMemberId.HasValue).Select(b => b.MatchedByMemberId!.Value)));

                foreach (var a in attachments)
                    detail.Attachments.Add(new LedgerAttachmentView
                    {
                        Id = a.Id,
                        FileName = a.FileName,
                        ContentType = a.ContentType,
                        SizeBytes = a.SizeBytes,
                        UploadedUtc = a.UploadedUtc,
                        UploadedByName = names.GetValueOrDefault(a.UploadedByMemberId, ""),
                        IsVoided = a.IsVoided,
                        VoidReason = a.VoidReason
                    });

                foreach (var b in bank)
                    detail.BankRows.Add(new LedgerBankMatchView
                    {
                        BookedDate = b.BookedDate,
                        Text = b.Text,
                        Amount = b.Amount,
                        Reference = b.Reference,
                        MatchKind = b.MatchKind,
                        MatchedByName = b.MatchedByMemberId.HasValue
                            ? names.GetValueOrDefault(b.MatchedByMemberId.Value, "") : "",
                        MatchedUtc = b.MatchedUtc
                    });

                return detail;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa verifikation {Id} för {Typ}/{IssuerId}.",
                    entryId, issuerType, issuerId);
                return null;
            }
        }

        /// <summary>
        /// Huvudboken för ett konto.
        ///
        /// <para><b>⚠️ Det ingående saldot räknas ur ALLT före perioden</b>, inte ur årets rader.
        /// Ett ingående saldo som bara ser innevarande år är noll för varje förening som funnits
        /// längre — samma urvalsfälla som balansräkningen redan bär en varning om.</para>
        /// </summary>
        public LedgerAccountLedger? AccountLedger(
            int issuerType, int issuerId, int accountNumber, DateTime from, DateTime to)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var name = ldb.ExecuteScalar<string>(
                    @"SELECT TOP 1 Name FROM dbo.LedgerAccount
                       WHERE IssuerType = @0 AND IssuerId = @1 AND Number = @2",
                    issuerType, issuerId, accountNumber);

                var result = new LedgerAccountLedger
                {
                    AccountNumber = accountNumber,
                    AccountName = name ?? ""
                };

                var opening = ldb.Fetch<DebitCredit>(
                    @"SELECT ISNULL(SUM(l.Debit), 0) AS Debit, ISNULL(SUM(l.Credit), 0) AS Credit
                        FROM dbo.LedgerJournalEntryLine l
                        JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                       WHERE e.IssuerType = @0 AND e.IssuerId = @1
                         AND l.AccountNumber = @2 AND e.AccountingDate < @3",
                    issuerType, issuerId, accountNumber, from.Date).FirstOrDefault()
                    ?? new DebitCredit();

                result.OpeningBalance =
                    LedgerAccountClass.InOwnDirection(accountNumber, opening.Debit, opening.Credit);

                var rows = ldb.Fetch<LedgerRowRaw>(
                    @"SELECT e.Id AS EntryId, e.Number, s.Prefix, e.AccountingDate,
                             e.Description, e.CounterpartyName, l.Text, l.Debit, l.Credit
                        FROM dbo.LedgerJournalEntryLine l
                        JOIN dbo.LedgerJournalEntry e ON e.Id = l.JournalEntryId
                        JOIN dbo.LedgerNumberSeries s ON s.Id = e.SeriesId
                       WHERE e.IssuerType = @0 AND e.IssuerId = @1
                         AND l.AccountNumber = @2
                         AND e.AccountingDate >= @3 AND e.AccountingDate <= @4
                       ORDER BY e.AccountingDate, e.Number, l.LineNumber",
                    issuerType, issuerId, accountNumber, from.Date, to.Date);

                var running = result.OpeningBalance;

                foreach (var r in rows)
                {
                    running += LedgerAccountClass.InOwnDirection(accountNumber, r.Debit, r.Credit);

                    result.Rows.Add(new LedgerAccountLedgerRow
                    {
                        EntryId = r.EntryId,
                        Number = LedgerNumberAllocator.Format(r.Prefix, r.Number),
                        AccountingDate = r.AccountingDate,
                        // Radens egen text när den finns — annars verifikationens.
                        Text = string.IsNullOrWhiteSpace(r.Text) ? r.Description : r.Text!,
                        Counterparty = r.CounterpartyName ?? "",
                        Debit = r.Debit,
                        Credit = r.Credit,
                        Balance = running
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa huvudboken för konto {Konto} hos {Typ}/{Id}.",
                    accountNumber, issuerType, issuerId);
                return null;
            }
        }

        private Dictionary<int, string> ResolveNames(IEnumerable<int> memberIds)
        {
            var names = new Dictionary<int, string>();

            foreach (var id in memberIds.Where(i => i > 0).Distinct())
            {
                try
                {
                    var m = _memberService.GetById(id);
                    if (m is not null) names[id] = m.Name ?? "";
                }
                catch
                {
                    // En medlem som inte går att slå upp är ett namn som fattas, inte ett fel som
                    // ska ta ner verifikationen.
                }
            }

            return names;
        }

        private static LedgerJournalRow ToRow(JournalListRow r) => new()
        {
            EntryId = r.Id,
            Number = LedgerNumberAllocator.Format(r.Prefix, r.Number),
            NumberValue = r.Number,
            AccountingDate = r.AccountingDate.Date,
            EventDate = r.EventDate.Date,
            Description = r.Description,
            Counterparty = r.CounterpartyName ?? "",
            Amount = r.Amount ?? 0m,
            AttachmentCount = r.AttachmentCount,
            BankMatchedLines = r.BankMatchedLines,
            CorrectsEntryId = r.CorrectsEntryId,
            SourceType = r.SourceType ?? ""
        };

        private class JournalListRow
        {
            public int Id { get; set; }
            public int Number { get; set; }
            public string Prefix { get; set; } = "";
            public DateTime AccountingDate { get; set; }
            public DateTime EventDate { get; set; }
            public string Description { get; set; } = "";
            public string? CounterpartyName { get; set; }
            public string? SourceType { get; set; }
            public int? CorrectsEntryId { get; set; }
            public decimal? Amount { get; set; }
            public int AttachmentCount { get; set; }
            public int BankMatchedLines { get; set; }
        }

        private class SeriesSpanRow
        {
            public string Prefix { get; set; } = "";
            public int First_ { get; set; }
            public int Last_ { get; set; }
            public int Count_ { get; set; }
        }

        private class BankMatchRow
        {
            public DateTime BookedDate { get; set; }
            public string Text { get; set; } = "";
            public decimal Amount { get; set; }
            public string? Reference { get; set; }
            public string? MatchKind { get; set; }
            public int? MatchedByMemberId { get; set; }
            public DateTime? MatchedUtc { get; set; }
        }

        private class LedgerRowRaw
        {
            public int EntryId { get; set; }
            public int Number { get; set; }
            public string Prefix { get; set; } = "";
            public DateTime AccountingDate { get; set; }
            public string Description { get; set; } = "";
            public string? CounterpartyName { get; set; }
            public string? Text { get; set; }
            public decimal Debit { get; set; }
            public decimal Credit { get; set; }
        }

        private class DebitCredit
        {
            public decimal Debit { get; set; }
            public decimal Credit { get; set; }
        }
    }
}
