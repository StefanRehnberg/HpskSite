using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Underlaget till en verifikation — kvittot, fakturan, kontoutdraget.
    ///
    /// <para><b>⚠️⚠️ DET HÄR ÄR LEDET SOM SAKNADES.</b> Skatteverkets krav är att revisorn
    /// självständigt kan följa <i>bokförd transaktion → verifikation → faktisk betalning</i>.
    /// Bokföringen fanns, bankkopplingen fanns (<c>LedgerBankRow.MatchedLineId</c>) — men mellan
    /// dem satt ingenting, och en revision utan underlag sker i pärmen oavsett hur bra ytan är.
    /// Tabellen har funnits sedan P1 och haft <b>noll skrivare</b> fram till nu.</para>
    ///
    /// <para><b>⚠️ Bilagan läggs till EFTER bokföringen, och det är normalfallet.</b> Kvittot
    /// fotograferas när man kommer hem, fakturan kommer i efterhand. Verifikationen är
    /// oföränderlig — bilagan hänger på den, den ändrar den inte. Därför får ingen spärr här
    /// utgå från att ett låst räkenskapsår saknar bilagor att lägga till: en revisor som ber om
    /// ett underlag i mars ska kunna få det inlagt.</para>
    /// </summary>
    public class LedgerAttachmentService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerAttachmentStorage _storage;
        private readonly ILogger<LedgerAttachmentService> _logger;

        public LedgerAttachmentService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerAttachmentStorage storage,
            ILogger<LedgerAttachmentService> logger)
        {
            _databaseFactory = databaseFactory;
            _storage = storage;
            _logger = logger;
        }

        /// <summary>
        /// Kopplar ett underlag till en verifikation.
        ///
        /// <para><b>⚠️⚠️ VERIFIKATIONENS ÄGARE KONTROLLERAS HÄR, inte bara i controllern.</b> Ett
        /// gissat <c>entryId</c> skulle annars kunna hänga ett kvitto på en annan förenings
        /// bokföring — och den som bläddrar där kan vara en inbjuden revisor.</para>
        /// </summary>
        public async Task<(bool Ok, string? Error)> AddAsync(
            int issuerType, int issuerId, int entryId,
            Stream content, string fileName, long size, int byMemberId)
        {
            var (valid, error) = _storage.Validate(fileName, size);
            if (!valid) return (false, error);

            if (byMemberId <= 0) return (false, "Du måste vara inloggad för att lägga till ett underlag.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var belongs = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerJournalEntry
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    entryId, issuerType, issuerId);

                if (belongs == 0)
                    return (false, "Verifikationen finns inte i den här föreningens bokföring.");

                var (storedAs, realSize) = await _storage.SaveAsync(content, fileName);

                // ⚠️ Samma fil på samma verifikation två gånger är alltid en dubbelklickad knapp.
                //    Namnet ÄR innehållet, så det går att avgöra utan att jämföra byte för byte.
                var already = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerAttachment
                       WHERE JournalEntryId = @0 AND StoredAs = @1 AND VoidedUtc IS NULL",
                    entryId, storedAs);

                if (already > 0)
                    return (false, "Det underlaget är redan kopplat till verifikationen.");

                ldb.Execute(
                    @"INSERT INTO dbo.LedgerAttachment
                        (JournalEntryId, FileName, StoredAs, ContentType, SizeBytes,
                         UploadedByMemberId, UploadedUtc)
                      VALUES (@0, @1, @2, @3, @4, @5, @6)",
                    entryId,
                    SafeFileName(fileName),
                    storedAs,
                    LedgerAttachmentStorage.ContentTypeFor(storedAs),
                    realSize,
                    byMemberId,
                    DateTime.UtcNow);

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte koppla underlag till verifikation {Id} hos {Typ}/{IssuerId}.",
                    entryId, issuerType, issuerId);

                return (false, "Underlaget kunde inte sparas. Försök igen.");
            }
        }

        /// <summary>
        /// Makulerar kopplingen. <b>Raderar aldrig</b> — varken raden eller filen.
        ///
        /// <para><b>⚠️ Skälet är obligatoriskt.</b> En bortkopplad bilaga utan skäl går inte att
        /// bedöma i efterhand, och det är en revisor som kommer att ställa frågan.</para>
        ///
        /// <para><b>⚠️ Filen på disk rörs inte.</b> Namnet är innehållets hash, så samma fil kan
        /// vara underlag till en annan verifikation — ett kontoutdrag är det för tolv. En radering
        /// här hade tagit underlaget från någon annans verifikation.</para>
        /// </summary>
        public (bool Ok, string? Error) Void(
            int issuerType, int issuerId, int attachmentId, string reason, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "Skriv varför underlaget kopplas bort — det syns för revisorn.");

            if (byMemberId <= 0) return (false, "Du måste vara inloggad.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                // ⚠️ Ägarskapet prövas via verifikationen — bilagan själv bär ingen utställare.
                var belongs = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1)
                        FROM dbo.LedgerAttachment a
                        JOIN dbo.LedgerJournalEntry e ON e.Id = a.JournalEntryId
                       WHERE a.Id = @0 AND e.IssuerType = @1 AND e.IssuerId = @2",
                    attachmentId, issuerType, issuerId);

                if (belongs == 0)
                    return (false, "Underlaget hör inte till den här föreningens bokföring.");

                var rows = ldb.Execute(
                    @"UPDATE dbo.LedgerAttachment
                         SET VoidedUtc = @1, VoidedByMemberId = @2, VoidReason = @3
                       WHERE Id = @0 AND VoidedUtc IS NULL",
                    attachmentId, DateTime.UtcNow, byMemberId, reason.Trim());

                // Noll rader = den var redan makulerad. Det är inget fel, men svaret ska säga det
                // i stället för att rapportera en handling som inte hände.
                return rows == 0
                    ? (false, "Underlaget var redan bortkopplat.")
                    : (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte makulera bilaga {Id} hos {Typ}/{IssuerId}.",
                    attachmentId, issuerType, issuerId);

                return (false, "Bortkopplingen kunde inte sparas.");
            }
        }

        /// <summary>
        /// Filen bakom en bilaga — sökväg, filnamn och typ.
        ///
        /// <para><b>⚠️ Slår upp via verifikationen, så utställaren alltid prövas.</b> En makulerad
        /// bilaga går fortfarande att öppna: den är räkenskapsinformation, och revisorn ska kunna
        /// se vad som kopplades bort.</para>
        /// </summary>
        public (string? Path, string FileName, string ContentType) Resolve(
            int issuerType, int issuerId, int attachmentId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var row = ldb.Fetch<LedgerAttachment>(
                    @"SELECT a.* FROM dbo.LedgerAttachment a
                        JOIN dbo.LedgerJournalEntry e ON e.Id = a.JournalEntryId
                       WHERE a.Id = @0 AND e.IssuerType = @1 AND e.IssuerId = @2",
                    attachmentId, issuerType, issuerId).FirstOrDefault();

                if (row is null) return (null, "", "");

                return (_storage.GetFilePath(row.StoredAs), row.FileName, row.ContentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte slå upp bilaga {Id}.", attachmentId);
                return (null, "", "");
            }
        }

        /// <summary>
        /// Hur många verifikationer i ett år som saknar underlag.
        ///
        /// <para><b>⚠️⚠️ BOKSLUTETS STEG "Underlag på plats" KAN ÄNTLIGEN SVARA.</b> Fram till nu
        /// stod det som <i>okänt</i> med texten "kontrollera pärmen för hand" — vilket var ärligt
        /// men oanvändbart.</para>
        ///
        /// <para><b>⚠️ Systemgenererade poster räknas INTE som saknade.</b> En medlemsavgift som
        /// föll ut ur avgiftsmodulen har sitt underlag i anmälan och betalningsraden, inte i ett
        /// papper någon ska fotografera. Att kräva en bilaga där hade gjort steget permanent rött
        /// för varje förening som använder avgiftsdelen — alltså en varning som slutar betyda
        /// något. Kravet gäller det en människa bokfört för hand.</para>
        /// </summary>
        public (int Missing, int Total) MissingForYear(int issuerType, int issuerId, int fiscalYearId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var total = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerJournalEntry
                       WHERE IssuerType = @0 AND IssuerId = @1 AND FiscalYearId = @2
                         AND SourceType = @3",
                    issuerType, issuerId, fiscalYearId, LedgerSourceType.Manual);

                var missing = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerJournalEntry e
                       WHERE e.IssuerType = @0 AND e.IssuerId = @1 AND e.FiscalYearId = @2
                         AND e.SourceType = @3
                         AND NOT EXISTS (SELECT 1 FROM dbo.LedgerAttachment a
                                          WHERE a.JournalEntryId = e.Id AND a.VoidedUtc IS NULL)",
                    issuerType, issuerId, fiscalYearId, LedgerSourceType.Manual);

                return (missing, total);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte räkna saknade underlag för {Typ}/{Id}.",
                    issuerType, issuerId);

                // ⚠️ (-1, -1) betyder "vet inte", aldrig "inga saknas". Bokslutssteget måste kunna
                //    skilja de två — ett tyst noll hade kryssat ett steg som ingen kontrollerat.
                return (-1, -1);
            }
        }

        /// <summary>Filnamnet som visas. Kapas och rensas — det kommer från en uppladdare.</summary>
        private static string SafeFileName(string fileName)
        {
            var bare = Path.GetFileName(fileName ?? "").Trim();
            if (bare.Length == 0) bare = "underlag";
            return bare.Length > 180 ? bare[..180] : bare;
        }
    }
}
