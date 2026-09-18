using HpskSite.Models;
using HpskSite.Models.Ledger;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Utkast till verifikationer: spara, läsa, kasta — och bokföra.
    ///
    /// <para>Ett utkast är ett halvfyllt formulär. Det bär inget nummer, det räknas inte i någon
    /// summering, och det kan ändras och kastas fritt. Först <see cref="Commit"/> gör det till
    /// bokföring, och då delas numret ut.</para>
    ///
    /// <para><b>⚠️ Tjänsten VALIDERAR inte ett utkast.</b> Att spara ett tomt, obalanserat eller
    /// halvfärdigt utkast måste gå — annars kan kassören inte avbryta mitt i, vilket är hela skälet
    /// funktionen finns. Kontrollerna ligger i <see cref="LedgerPostingService"/> och slår till vid
    /// bokföringen, en enda gång, på ett ställe.</para>
    /// </summary>
    public class LedgerDraftService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;
        private readonly ILogger<LedgerDraftService> _logger;

        public LedgerDraftService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerPostingService posting,
            ILogger<LedgerDraftService> logger)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
            _logger = logger;
        }

        /// <summary>
        /// Skapar eller uppdaterar ett utkast och returnerar dess id.
        ///
        /// <para><b>Raderna skrivs om helt vid varje sparning.</b> Ett utkast är ett formulär, och
        /// formulärets nuvarande innehåll ÄR sanningen — att försöka diffa rader mot varandra
        /// skulle bara ge ett sätt att tappa en borttagen rad.</para>
        /// </summary>
        public int Save(LedgerJournalEntryDraft draft, IEnumerable<LedgerJournalEntryDraftLine> lines)
        {
            using var db = _databaseFactory.CreateDatabase();
            using var tx = db.GetTransaction();

            if (draft.Id > 0)
            {
                var existing = db.SingleOrDefault<LedgerJournalEntryDraft>(
                    "SELECT * FROM dbo.LedgerJournalEntryDraft WHERE Id = @0", draft.Id);

                if (existing is null)
                    throw new InvalidOperationException($"Utkast {draft.Id} finns inte.");

                // ⚠️ Ett utkast som är på väg att bokföras får inte ändras under fötterna på
                // bokföringen — då kan posten som skrivs vara en annan än den kassören såg.
                if (existing.IsClaimed)
                    throw new InvalidOperationException("Utkastet håller på att bokföras och kan inte ändras.");

                if (existing.PostedEntryId is not null)
                    throw new InvalidOperationException("Utkastet är redan bokfört.");

                draft.CreatedUtc = existing.CreatedUtc;
                draft.CreatedByMemberId = existing.CreatedByMemberId;
                draft.CommitClaimedUtc = existing.CommitClaimedUtc;
                draft.PostedEntryId = existing.PostedEntryId;
                draft.UpdatedUtc = DateTime.UtcNow;

                db.Update(draft);
                db.Execute("DELETE FROM dbo.LedgerJournalEntryDraftLine WHERE DraftId = @0", draft.Id);
            }
            else
            {
                draft.CreatedUtc = DateTime.UtcNow;
                draft.UpdatedUtc = draft.CreatedUtc;
                db.Insert(draft);
            }

            var lineNo = 1;
            foreach (var line in lines)
            {
                line.DraftId = draft.Id;
                line.LineNumber = lineNo++;
                line.Id = 0;
                db.Insert(line);
            }

            tx.Complete();
            return draft.Id;
        }

        public (LedgerJournalEntryDraft? Draft, List<LedgerJournalEntryDraftLine> Lines) Get(int id)
        {
            using var db = _databaseFactory.CreateDatabase();

            var draft = db.SingleOrDefault<LedgerJournalEntryDraft>(
                "SELECT * FROM dbo.LedgerJournalEntryDraft WHERE Id = @0", id);

            if (draft is null) return (null, new List<LedgerJournalEntryDraftLine>());

            var lines = db.Fetch<LedgerJournalEntryDraftLine>(
                "SELECT * FROM dbo.LedgerJournalEntryDraftLine WHERE DraftId = @0 ORDER BY LineNumber", id);

            return (draft, lines);
        }

        /// <summary>Föreningens öppna utkast, senast ändrade först.</summary>
        public List<LedgerJournalEntryDraft> ListOpen(int issuerType, int issuerId)
        {
            using var db = _databaseFactory.CreateDatabase();

            return db.Fetch<LedgerJournalEntryDraft>(
                @"SELECT * FROM dbo.LedgerJournalEntryDraft
                   WHERE IssuerType = @0 AND IssuerId = @1 AND PostedEntryId IS NULL
                   ORDER BY UpdatedUtc DESC",
                issuerType, issuerId);
        }

        /// <summary>
        /// Kastar ett utkast. <b>Går alltid</b> — ett utkast är ingen räkenskapsinformation, och den
        /// som ångrar sig mitt i en inmatning ska inte behöva lämna något efter sig.
        /// <para>⚠️ Ett redan bokfört utkast kastas däremot inte: raden är spåret till posten.</para>
        /// </summary>
        public bool Discard(int id)
        {
            using var db = _databaseFactory.CreateDatabase();

            var posted = db.ExecuteScalar<int?>(
                "SELECT PostedEntryId FROM dbo.LedgerJournalEntryDraft WHERE Id = @0", id);

            if (posted is not null) return false;

            // Raderna följer med via ON DELETE CASCADE.
            return db.Execute("DELETE FROM dbo.LedgerJournalEntryDraft WHERE Id = @0", id) > 0;
        }

        /// <summary>
        /// Bokför utkastet. Lyckas det försvinner utkastet och verifikationen finns.
        ///
        /// <para><b>⚠️⚠️ CLAIM-THEN-POST.</b> Anspråket skrivs FÖRE bokföringen försöks, och ett
        /// anspråkat utkast vägrar bokföras igen. Riktningen på felet är hela skälet: en krasch
        /// mellan anspråk och bokföring lämnar ett fastsittande utkast — irriterande men
        /// hanterbart — medan motsatt ordning riskerar att SAMMA post bokförs två gånger. Det är
        /// pengar, det sker tyst, och det är svårt att upptäcka i efterhand.</para>
        ///
        /// <para><b>⚠️ Anspråket släpps vid ett känt MISSLYCKANDE</b> (obalans, stängt år, saknad
        /// kontoroll) — där vet vi att ingenting skrevs, och att låta utkastet sitta fast efter ett
        /// vanligt valideringsfel vore att straffa kassören för en felskrivning. Vid ett OKÄNT fel
        /// släpps det inte: då vet vi inte om posten skrevs, och ett fastsittande utkast är rätt
        /// utfall tills en människa tittat.</para>
        /// </summary>
        public LedgerPostingResult Commit(int draftId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var (draft, lines) = Get(draftId);
            if (draft is null)
                return LedgerPostingResult.Failed("Utkastet finns inte längre.");

            if (draft.PostedEntryId is int already)
                return LedgerPostingResult.Failed(
                    $"Utkastet är redan bokfört som verifikation {already}.");

            if (draft.IsClaimed)
                return LedgerPostingResult.Failed(
                    "Ett bokföringsförsök för det här utkastet avbröts. Kontrollera i verifikationslistan "
                    + "om posten redan blev bokförd innan du försöker igen.");

            if (draft.AccountingDate is null)
                return LedgerPostingResult.Failed("Utkastet saknar bokföringsdatum.");

            // Anspråket. Villkoret i WHERE är spärren — två samtidiga försök kan inte båda vinna.
            var claimed = db.Execute(
                @"UPDATE dbo.LedgerJournalEntryDraft
                     SET CommitClaimedUtc = @1
                   WHERE Id = @0 AND CommitClaimedUtc IS NULL AND PostedEntryId IS NULL",
                draftId, DateTime.UtcNow);

            if (claimed == 0)
                return LedgerPostingResult.Failed("Utkastet bokförs redan.");

            LedgerPostingResult result;
            try
            {
                result = _posting.Post(new LedgerPostingRequest
                {
                    IssuerType = draft.IssuerType,
                    IssuerId = draft.IssuerId,
                    AccountingDate = draft.AccountingDate.Value,
                    EventDate = draft.EventDate,
                    Description = draft.Description,
                    CounterpartyType = draft.CounterpartyType,
                    CounterpartyId = draft.CounterpartyId,
                    CounterpartyName = draft.CounterpartyName,
                    SourceType = draft.SourceType,
                    SourceId = draft.SourceId,
                    PaymentId = draft.PaymentId,
                    CreatedByMemberId = byMemberId,
                    Lines = lines.Select(l => new LedgerPostingLine
                    {
                        // Rollen vinner över kontonumret — se radens dokumentation.
                        Role = l.Role,
                        AccountNumber = string.IsNullOrWhiteSpace(l.Role) ? l.AccountNumber : null,
                        Debit = l.Debit,
                        Credit = l.Credit,
                        Text = l.Text,
                        VatRate = l.VatRate,
                        // Projektet sitter per rad även i utkastet — ett utkast ska kunna beskriva
                        // en betalning som delar sig mellan två projekt, annars går den inte att
                        // förbereda alls.
                        ProjectId = l.ProjectId
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                // Okänt fel: släpp INTE anspråket. Vi vet inte om posten skrevs.
                _logger.LogError(ex,
                    "Bokföringen av utkast {DraftId} kastade ett oväntat fel. Anspråket behålls, så "
                    + "utkastet inte kan bokföras en andra gång innan någon kontrollerat.", draftId);

                return LedgerPostingResult.Failed(
                    "Något gick fel i bokföringen. Kontrollera i verifikationslistan om posten ändå "
                    + "skrevs innan du försöker igen.");
            }

            if (!result.Success)
            {
                // Känt fel — Post skriver ingenting när den returnerar ett fel, så anspråket släpps
                // och kassören kan rätta och försöka igen.
                db.Execute(
                    "UPDATE dbo.LedgerJournalEntryDraft SET CommitClaimedUtc = NULL WHERE Id = @0",
                    draftId);
                return result;
            }

            // Markera FÖRE raderingen. Misslyckas raderingen står utkastet kvar med sitt
            // PostedEntryId och kan inte bokföras igen — spåret är kvar, dubbelposten omöjlig.
            db.Execute(
                "UPDATE dbo.LedgerJournalEntryDraft SET PostedEntryId = @1 WHERE Id = @0",
                draftId, result.EntryId);

            try
            {
                db.Execute("DELETE FROM dbo.LedgerJournalEntryDraft WHERE Id = @0", draftId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Utkast {DraftId} bokfördes som verifikation {EntryId} men kunde inte raderas. "
                    + "Raden är markerad som bokförd och kan inte bokföras igen.",
                    draftId, result.EntryId);
            }

            return result;
        }
    }
}
