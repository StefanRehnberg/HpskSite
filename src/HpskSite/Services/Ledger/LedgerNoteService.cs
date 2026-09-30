using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Noterna till resultat- och balansräkningen. Se <see cref="LedgerNote"/>.
    ///
    /// <para><b>⚠️ Varje skrivning prövar tre saker på servern</b>, oavsett vad ytan visar: att
    /// räkenskapsåret tillhör utställaren, att det inte är fastställt, och att kontona finns i
    /// föreningens kontoplan. Ytan gömmer knapparna för ett fastställt år, men en gömd knapp är
    /// ingen spärr.</para>
    /// </summary>
    public class LedgerNoteService
    {
        public const int MaxTitleLength = 200;
        public const int MaxBodyLength = 4000;

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerNoteService> _logger;

        public LedgerNoteService(IUmbracoDatabaseFactory databaseFactory, ILogger<LedgerNoteService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>Noterna för året, numrerade mot räkningarna. Skriver ingenting.</summary>
        public LedgerNotesView Get(int issuerType, int issuerId, LedgerFinancialStatements statements)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var view = new LedgerNotesView
            {
                FiscalYearId = statements.FiscalYearId,
                Year = statements.Year,
                Locked = statements.Status == LedgerFiscalYearStatus.Established
            };

            var notes = ReadNotes(ldb, issuerType, issuerId, statements.FiscalYearId);
            var links = ReadLinks(ldb, notes.Select(n => n.Id).ToList());

            var names = ldb.Fetch<LedgerAccount>(
                    "SELECT * FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1",
                    issuerType, issuerId)
                .GroupBy(a => a.Number)
                .ToDictionary(g => g.Key, g => g.First().Name);

            var numbered = LedgerNoteNumbering.Number(
                notes.Select(n => new LedgerNoteNumbering.Input(
                    n.Id, links.TryGetValue(n.Id, out var a) ? a : new List<int>())),
                LedgerNoteNumbering.StatementOrder(statements));

            var byId = notes.ToDictionary(n => n.Id);
            foreach (var x in numbered)
            {
                var n = byId[x.Id];
                var note = new LedgerNotesView.Note
                {
                    Id = n.Id,
                    Number = x.Number,
                    Title = n.Title,
                    Body = n.Body,
                    IsGeneral = x.IsGeneral,
                    IsUnreferenced = x.IsUnreferenced,
                    MissingAccounts = x.MissingAccounts
                };

                foreach (var acc in (links.TryGetValue(n.Id, out var l) ? l : new List<int>()).OrderBy(v => v))
                {
                    note.Accounts.Add(new LedgerNotesView.NoteAccount
                    {
                        Number = acc,
                        Name = names.TryGetValue(acc, out var nm) ? nm : ""
                    });

                    if (!view.AccountRefs.TryGetValue(acc, out var refs))
                        view.AccountRefs[acc] = refs = new List<int>();
                    refs.Add(x.Number);
                }

                view.Notes.Add(note);
            }

            foreach (var refs in view.AccountRefs.Values) refs.Sort();

            // Förra årets noter erbjuds bara till ett år utan egna — att kopiera in i ett år som
            // redan har noter ger dubbletter som kassören sedan får plocka bort för hand.
            if (view.Notes.Count == 0 && !view.Locked)
            {
                var previous = PreviousYear(ldb, issuerType, issuerId, statements.FiscalYearId);
                if (previous is not null)
                {
                    var count = ldb.ExecuteScalar<int>(
                        "SELECT COUNT(1) FROM dbo.LedgerNote WHERE IssuerType = @0 AND IssuerId = @1 AND FiscalYearId = @2",
                        issuerType, issuerId, previous.Id);
                    if (count > 0)
                    {
                        view.CopyFromYear = previous.Year;
                        view.CopyFromCount = count;
                    }
                }
            }

            return view;
        }

        /// <summary>
        /// Skapar eller ändrar en not. <paramref name="noteId"/> 0 = ny.
        /// <para>Kontolistan ERSÄTTER den gamla. Tom lista = en allmän not.</para>
        /// </summary>
        public (bool Ok, string? Message, int Id) Save(
            int issuerType, int issuerId, int fiscalYearId, int noteId,
            string? title, string? body, IEnumerable<int>? accountNumbers, int byMemberId)
        {
            title = (title ?? "").Trim();
            body = (body ?? "").Trim();
            var accounts = (accountNumbers ?? Array.Empty<int>()).Distinct().ToList();

            if (title.Length == 0) return (false, "Skriv en rubrik för noten.", 0);
            if (title.Length > MaxTitleLength)
                return (false, $"Rubriken får vara högst {MaxTitleLength} tecken.", 0);
            if (body.Length == 0) return (false, "Skriv vad noten ska säga.", 0);
            if (body.Length > MaxBodyLength)
                return (false, $"Noten får vara högst {MaxBodyLength} tecken.", 0);

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var refusal = YearRefusal(ldb, issuerType, issuerId, fiscalYearId);
            if (refusal is not null) return (false, refusal, 0);

            if (accounts.Count > 0)
            {
                var known = ldb.Fetch<int>(
                    "SELECT Number FROM dbo.LedgerAccount WHERE IssuerType = @0 AND IssuerId = @1 AND Number IN (@2)",
                    issuerType, issuerId, accounts).ToHashSet();
                var unknown = accounts.Where(a => !known.Contains(a)).ToList();
                if (unknown.Count > 0)
                    return (false, "Kontot finns inte i föreningens kontoplan: " + string.Join(", ", unknown) + ".", 0);
            }

            using var tx = ldb.GetTransaction();

            if (noteId == 0)
            {
                noteId = ldb.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerNote
                          (IssuerType, IssuerId, FiscalYearId, Title, Body, CreatedUtc, CreatedByMemberId)
                      OUTPUT INSERTED.Id
                      VALUES (@0, @1, @2, @3, @4, @5, @6)",
                    issuerType, issuerId, fiscalYearId, title, body, DateTime.UtcNow, byMemberId);
            }
            else
            {
                // ⚠️ Id:t kommer från klienten. Raden måste tillhöra samma utställare OCH samma år,
                //    annars kunde en not flyttas till en annan förening med ett handpostat anrop.
                var rows = ldb.Execute(
                    @"UPDATE dbo.LedgerNote
                         SET Title = @0, Body = @1, ModifiedUtc = @2, ModifiedByMemberId = @3
                       WHERE Id = @4 AND IssuerType = @5 AND IssuerId = @6 AND FiscalYearId = @7",
                    title, body, DateTime.UtcNow, byMemberId, noteId, issuerType, issuerId, fiscalYearId);
                if (rows == 0) return (false, "Noten finns inte längre.", 0);

                ldb.Execute("DELETE FROM dbo.LedgerNoteAccount WHERE NoteId = @0", noteId);
            }

            foreach (var a in accounts)
                ldb.Execute("INSERT INTO dbo.LedgerNoteAccount (NoteId, AccountNumber) VALUES (@0, @1)", noteId, a);

            tx.Complete();
            return (true, null, noteId);
        }

        /// <summary>Tar bort en not. Övriga noter numreras om av sig själva.</summary>
        public (bool Ok, string? Message) Delete(int issuerType, int issuerId, int noteId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var note = ldb.FirstOrDefault<LedgerNote>(
                "SELECT * FROM dbo.LedgerNote WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                noteId, issuerType, issuerId);
            if (note is null) return (false, "Noten finns inte längre.");

            var refusal = YearRefusal(ldb, issuerType, issuerId, note.FiscalYearId);
            if (refusal is not null) return (false, refusal);

            // Kontoraderna följer med genom ON DELETE CASCADE.
            ldb.Execute("DELETE FROM dbo.LedgerNote WHERE Id = @0", noteId);
            return (true, null);
        }

        /// <summary>
        /// Kopierar föregående räkenskapsårs noter, med sina konton, till ett år som saknar noter.
        /// <para>Texten ska läsas igen — siffror i den gäller förra året. Svaret säger det.</para>
        /// </summary>
        public (bool Ok, string? Message, int Copied) CopyFromPreviousYear(
            int issuerType, int issuerId, int fiscalYearId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, issuerId);

            var refusal = YearRefusal(ldb, issuerType, issuerId, fiscalYearId);
            if (refusal is not null) return (false, refusal, 0);

            if (ReadNotes(ldb, issuerType, issuerId, fiscalYearId).Count > 0)
                return (false, "Året har redan noter. Förra årets kopieras bara till ett år utan noter.", 0);

            var previous = PreviousYear(ldb, issuerType, issuerId, fiscalYearId);
            if (previous is null) return (false, "Det finns inget tidigare räkenskapsår.", 0);

            var source = ReadNotes(ldb, issuerType, issuerId, previous.Id);
            if (source.Count == 0) return (false, $"Räkenskapsåret {previous.Year} har inga noter.", 0);

            var links = ReadLinks(ldb, source.Select(n => n.Id).ToList());

            using var tx = ldb.GetTransaction();
            foreach (var n in source)
            {
                var newId = ldb.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerNote
                          (IssuerType, IssuerId, FiscalYearId, Title, Body, CreatedUtc, CreatedByMemberId)
                      OUTPUT INSERTED.Id
                      VALUES (@0, @1, @2, @3, @4, @5, @6)",
                    issuerType, issuerId, fiscalYearId, n.Title, n.Body, DateTime.UtcNow, byMemberId);

                if (links.TryGetValue(n.Id, out var accounts))
                    foreach (var a in accounts)
                        ldb.Execute("INSERT INTO dbo.LedgerNoteAccount (NoteId, AccountNumber) VALUES (@0, @1)", newId, a);
            }
            tx.Complete();

            _logger.LogInformation(
                "Verifikationsliggaren: {Antal} noter kopierade från {FranAr} till räkenskapsår {TillAr} för utställare {Typ}/{Id}.",
                source.Count, previous.Year, fiscalYearId, issuerType, issuerId);

            return (true, null, source.Count);
        }

        private static string? YearRefusal(LedgerDb ldb, int issuerType, int issuerId, int fiscalYearId)
        {
            var fy = ldb.FirstOrDefault<LedgerFiscalYear>(
                "SELECT * FROM dbo.LedgerFiscalYear WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                fiscalYearId, issuerType, issuerId);

            if (fy is null) return "Räkenskapsåret finns inte.";
            if (fy.Status == LedgerFiscalYearStatus.Established)
                return $"Räkenskapsåret {fy.Year} är fastställt av årsmötet, och dess noter kan inte ändras.";
            return null;
        }

        private static List<LedgerNote> ReadNotes(LedgerDb ldb, int issuerType, int issuerId, int fiscalYearId)
            => ldb.Fetch<LedgerNote>(
                @"SELECT * FROM dbo.LedgerNote
                   WHERE IssuerType = @0 AND IssuerId = @1 AND FiscalYearId = @2
                   ORDER BY CreatedUtc, Id",
                issuerType, issuerId, fiscalYearId);

        private static Dictionary<int, List<int>> ReadLinks(LedgerDb ldb, List<int> noteIds)
        {
            if (noteIds.Count == 0) return new();
            return ldb.Fetch<LedgerNoteAccount>(
                    "SELECT * FROM dbo.LedgerNoteAccount WHERE NoteId IN (@0)", noteIds)
                .GroupBy(l => l.NoteId)
                .ToDictionary(g => g.Key, g => g.Select(l => l.AccountNumber).ToList());
        }

        private static LedgerFiscalYear? PreviousYear(LedgerDb ldb, int issuerType, int issuerId, int fiscalYearId)
        {
            var years = ldb.Fetch<LedgerFiscalYear>(
                "SELECT * FROM dbo.LedgerFiscalYear WHERE IssuerType = @0 AND IssuerId = @1 ORDER BY StartDate",
                issuerType, issuerId);
            var current = years.FirstOrDefault(y => y.Id == fiscalYearId);
            if (current is null) return null;
            return years.LastOrDefault(y => y.StartDate < current.StartDate);
        }
    }
}
