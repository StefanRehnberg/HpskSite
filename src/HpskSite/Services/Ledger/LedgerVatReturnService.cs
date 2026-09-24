using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Momsdeklarationens underlag för en period. Räkningen bor i
    /// <see cref="LedgerVatReturnCalculator"/>; här hämtas bara raderna.
    ///
    /// <para><b>⚠️ Läser bara.</b> Momsens avräkning mot redovisningskontot (2650) bokförs inte här
    /// — den är ett eget steg när föreningen vill det.</para>
    /// </summary>
    public class LedgerVatReturnService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public LedgerVatReturnService(IUmbracoDatabaseFactory databaseFactory)
        {
            _databaseFactory = databaseFactory;
        }

        public LedgerVatReturn Build(int issuerType, int issuerId, DateTime from, DateTime to)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var roles = ldb.Fetch<LedgerAccountRole>(
                "SELECT * FROM dbo.LedgerAccountRole WHERE IssuerType = @0 AND IssuerId = @1",
                issuerType, issuerId);

            var outAcc = roles.FirstOrDefault(r => r.RoleKey == LedgerAccountRoles.VatOutgoing)?.AccountNumber ?? 0;
            var inAcc = roles.FirstOrDefault(r => r.RoleKey == LedgerAccountRoles.VatIncoming)?.AccountNumber ?? 0;

            var entries = ldb.Fetch<LedgerJournalEntry>(
                @"SELECT * FROM dbo.LedgerJournalEntry
                   WHERE IssuerType = @0 AND IssuerId = @1
                     AND AccountingDate >= @2 AND AccountingDate <= @3",
                issuerType, issuerId, from.Date, to.Date);

            var lines = LinesFor(ldb, entries.Select(e => e.Id));

            // ⚠️ Originalen till periodens rättelser — de kan ligga i en tidigare period, och det
            //    är deras momsrader rättelsen vänder.
            var originalIds = entries.Where(e => e.CorrectsEntryId.HasValue)
                                     .Select(e => e.CorrectsEntryId!.Value).Distinct().ToList();
            var originals = LinesFor(ldb, originalIds);

            var vatLines = new List<VatLine>();
            var contributing = 0;

            foreach (var e in entries)
            {
                var own = lines.TryGetValue(e.Id, out var l) ? l : new List<LedgerJournalEntryLine>();
                List<LedgerJournalEntryLine>? orig = null;
                if (e.CorrectsEntryId is int oid) orig = originals.TryGetValue(oid, out var o) ? o : new();

                var found = LedgerVatReturnCalculator.LinesOf(own, orig, outAcc, inAcc);
                if (found.Count > 0) contributing++;
                vatLines.AddRange(found);
            }

            // Kontrollen: rörelsen på momskontona i perioden, i samma riktning som rutorna.
            decimal Movement(int acc, bool creditPositive) => acc == 0 ? 0m : lines.Values
                .SelectMany(x => x).Where(x => x.AccountNumber == acc)
                .Sum(x => creditPositive ? x.Credit - x.Debit : x.Debit - x.Credit);

            return LedgerVatReturnCalculator.Compute(from, to, vatLines,
                Movement(outAcc, true), Movement(inAcc, false), contributing, outAcc, inAcc);
        }

        /// <summary>Raderna för ett antal verifikationer, i klumpar — IN-listans tak är ~2100 parametrar.</summary>
        private static Dictionary<int, List<LedgerJournalEntryLine>> LinesFor(LedgerDb ldb, IEnumerable<int> entryIds)
        {
            var result = new Dictionary<int, List<LedgerJournalEntryLine>>();
            foreach (var chunk in entryIds.Distinct().Chunk(1000))
            {
                foreach (var line in ldb.Fetch<LedgerJournalEntryLine>(
                             "SELECT * FROM dbo.LedgerJournalEntryLine WHERE JournalEntryId IN (@0)", chunk))
                {
                    if (!result.TryGetValue(line.JournalEntryId, out var list))
                        result[line.JournalEntryId] = list = new List<LedgerJournalEntryLine>();
                    list.Add(line);
                }
            }
            return result;
        }
    }
}
