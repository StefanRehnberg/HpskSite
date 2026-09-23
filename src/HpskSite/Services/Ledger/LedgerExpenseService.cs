using HpskSite.Models;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Utgiftssidan: utlägg, leverantörsfakturor, attest och utbetalning.
    ///
    /// <para><b>⚠️⚠️ KONTANTMETODEN STYR HELA FORMEN.</b> Föreningen bokför när pengarna rör sig, så
    /// att registrera en utgift skriver <b>ingen</b> verifikation — den skrivs vid betalningen.
    /// Raden är arbetslistan ("vad ska vi betala, och vem har godkänt det"), och det som ännu inte
    /// är betalt vid årets slut är en <b>leverantörsskuld</b> som bokslutet ska ta hand om.
    /// Byggd tvärtom — bokför vid registrering — hade den lagt in fakturor i ett år de inte hör
    /// hemma i, och ingen hade sett det förrän revisorn frågade.</para>
    ///
    /// <para><b>⚠️⚠️ ATTESTEN BOR PÅ UTGIFTEN, INTE PÅ VERIFIKATIONEN.</b>
    /// <see cref="LedgerApproval"/> är nycklad på en verifikation, och det var riktigt när modellen
    /// skrevs — men under kontantmetoden finns ingen verifikation förrän pengarna redan gått, och
    /// en attest som bara kan sättas EFTER utbetalningen är ingen internkontroll. Godkännandet
    /// registreras därför på utgiften, och <b>speglas till <see cref="LedgerApproval"/> när
    /// verifikationen skrivs</b>, så revisorn hittar attesten från verifikationens sida.
    /// De två kan inte glida isär: en betald utgift går inte att attestera om.</para>
    ///
    /// <para><b>⚠️ Reglerna ligger i <see cref="LedgerExpenseRules"/>, inte här.</b> Attestspärren är
    /// föreningens enda internkontroll över utbetalningar, och den ska gå att pröva utan databas.</para>
    /// </summary>
    public class LedgerExpenseService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;
        private readonly IMemberService _memberService;
        private readonly ILogger<LedgerExpenseService> _logger;

        public LedgerExpenseService(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerPostingService posting,
            IMemberService memberService,
            ILogger<LedgerExpenseService> logger)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
            _memberService = memberService;
            _logger = logger;
        }

        /// <summary>
        /// Utgifterna, nyast först.
        ///
        /// <para>⚠️ Sorterat på <c>ExpenseDate</c> och <c>RegisteredUtc</c>, aldrig på <c>Id</c> —
        /// sandlådans identitet räknar nedåt, så en id-sortering är omvänd just där.</para>
        /// </summary>
        public List<LedgerExpenseView> List(
            int issuerType, int issuerId, DateTime today, bool includeSettled = true)
        {
            var views = new List<LedgerExpenseView>();

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var rows = ldb.Fetch<LedgerExpense>(
                    @"SELECT * FROM dbo.LedgerExpense
                       WHERE IssuerType = @0 AND IssuerId = @1
                       ORDER BY ExpenseDate DESC, RegisteredUtc DESC",
                    issuerType, issuerId);

                if (!includeSettled)
                {
                    rows = rows
                        .Where(r => r.Status != LedgerExpenseStatus.Paid
                                    && r.Status != LedgerExpenseStatus.Rejected)
                        .ToList();
                }

                if (rows.Count == 0) return views;

                // ⚠️ Kontonamn, projektnamn och verifikationsnummer i EN fråga var. Ett uppslag per
                //    utgift är den fälla som gjorde fakturasidan tolv sekunder lång.
                var accounts = ldb.Fetch<AccountName>(
                        @"SELECT Number, Name FROM dbo.LedgerAccount
                           WHERE IssuerType = @0 AND IssuerId = @1",
                        issuerType, issuerId)
                    .GroupBy(a => a.Number)
                    .ToDictionary(g => g.Key, g => g.First().Name);

                var projects = ldb.Fetch<ProjectName>(
                        @"SELECT Id, Name FROM dbo.LedgerProject
                           WHERE IssuerType = @0 AND IssuerId = @1",
                        issuerType, issuerId)
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First().Name);

                var entryNumbers = ResolveEntryNumbers(ldb, issuerType, issuerId,
                    rows.Where(r => r.JournalEntryId.HasValue).Select(r => r.JournalEntryId!.Value));

                var names = ResolveNames(rows
                    .SelectMany(r => new[] { r.RegisteredByMemberId, r.ApprovedByMemberId ?? 0 }));

                foreach (var e in rows)
                {
                    views.Add(new LedgerExpenseView
                    {
                        Id = e.Id,
                        Kind = e.Kind,
                        KindLabel = LedgerExpenseKind.Label(e.Kind),
                        PayeeName = e.PayeeName,
                        PayeeMemberId = e.PayeeMemberId,
                        Description = e.Description,
                        Amount = e.Amount,
                        ExpenseDate = e.ExpenseDate,
                        DueDate = e.DueDate,
                        AccountNumber = e.AccountNumber,
                        AccountName = accounts.TryGetValue(e.AccountNumber, out var an) ? an : "",
                        ProjectId = e.ProjectId,
                        ProjectName = e.ProjectId is int pid && projects.TryGetValue(pid, out var pn) ? pn : null,
                        Status = e.Status,
                        StatusLabel = LedgerExpenseStatus.Label(e.Status),
                        RegisteredByName = names.TryGetValue(e.RegisteredByMemberId, out var rn) ? rn : "",
                        ApprovedByName = e.ApprovedByMemberId is int ab && names.TryGetValue(ab, out var abn) ? abn : null,
                        ApprovedUtc = e.ApprovedUtc,
                        ApprovalNote = e.ApprovalNote,
                        ApprovedBySelf = e.ApprovedBySelf,
                        RejectedReason = e.RejectedReason,
                        PaidDate = e.PaidDate,
                        JournalEntryId = e.JournalEntryId,
                        JournalEntryNumber = e.JournalEntryId is int jid && entryNumbers.TryGetValue(jid, out var num) ? num : null,
                        HasReceipt = e.HasReceipt,
                        ReceiptFileName = e.ReceiptFileName,
                        IsOverdue = LedgerExpenseRules.IsOverdue(e, today),
                        CanEdit = LedgerExpenseRules.IsEditable(e),
                        CanApprove = e.Status == LedgerExpenseStatus.Registered,
                        CanPay = LedgerExpenseRules.PaymentRefusal(e) is null
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utgifterna gick inte att läsa för {Typ}/{Id}.", issuerType, issuerId);
            }

            return views;
        }

        public LedgerExpense? Get(int issuerType, int issuerId, int id)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                return ldb.FirstOrDefault<LedgerExpense>(
                    @"SELECT * FROM dbo.LedgerExpense
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    id, issuerType, issuerId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utgift {Id} gick inte att läsa för {Typ}/{Utgivare}.",
                    id, issuerType, issuerId);
                return null;
            }
        }

        /// <summary>
        /// Lägger till eller ändrar en utgift.
        ///
        /// <para><b>⚠️ En ändring som rör sakuppgifterna RIVER attesten.</b> Godkännandet gällde ett
        /// bestämt belopp på ett bestämt konto; står det kvar efter att beloppet ändrats intygar det
        /// något ingen har sagt. Svaret säger det, så ändringen inte tyst kräver en ny runda.</para>
        /// </summary>
        public (bool Ok, string? Error, int Id, bool ApprovalDropped) Save(
            LedgerExpense e, int byMemberId)
        {
            if (byMemberId <= 0) return (false, "Du måste vara inloggad.", 0, false);

            e.Description = (e.Description ?? "").Trim();
            e.PayeeName = (e.PayeeName ?? "").Trim();

            var invalid = LedgerExpenseRules.Validate(e);
            if (invalid is not null) return (false, invalid, 0, false);

            // ⚠️ Kontoklassen prövas. Ett intäktskonto som kostnadskonto ger en resultatrapport där
            //    utgiften ökar intäkterna, och felet syns först när någon läser rapporten.
            var klass = LedgerAccountClass.Of(e.AccountNumber);
            if (klass is < 4 or > 8)
                return (false, "Utgiften måste bokföras på ett kostnadskonto (klass 4–8).", 0, false);

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, e.IssuerId);

                if (e.Id == 0)
                {
                    var id = ldb.ExecuteScalar<int>(
                        @"INSERT INTO dbo.LedgerExpense
                            (IssuerType, IssuerId, Kind, PayeeMemberId, PayeeName, Description,
                             Amount, ExpenseDate, DueDate, AccountNumber, ProjectId, Status,
                             RegisteredByMemberId, RegisteredUtc)
                          VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13);
                          SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        e.IssuerType, e.IssuerId, e.Kind, e.PayeeMemberId, e.PayeeName,
                        e.Description, e.Amount, e.ExpenseDate.Date, e.DueDate?.Date,
                        e.AccountNumber, e.ProjectId, LedgerExpenseStatus.Registered,
                        byMemberId, DateTime.UtcNow);

                    return (true, null, id, false);
                }

                var current = ldb.FirstOrDefault<LedgerExpense>(
                    @"SELECT * FROM dbo.LedgerExpense
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    e.Id, e.IssuerType, e.IssuerId);

                if (current is null) return (false, "Utgiften finns inte.", 0, false);

                if (!LedgerExpenseRules.IsEditable(current))
                    return (false,
                        "Utgiften är betald och bokförd, så den kan inte ändras. Blev något fel: "
                        + "bokför en rättelse under Bokför.", 0, false);

                var dropApproval = current.IsApproved
                                   && LedgerExpenseRules.ApprovalIsVoidedBy(current, e);

                ldb.Execute(
                    @"UPDATE dbo.LedgerExpense
                         SET Kind = @1, PayeeMemberId = @2, PayeeName = @3, Description = @4,
                             Amount = @5, ExpenseDate = @6, DueDate = @7, AccountNumber = @8,
                             ProjectId = @9
                       WHERE Id = @0 AND IssuerType = @10 AND IssuerId = @11",
                    e.Id, e.Kind, e.PayeeMemberId, e.PayeeName, e.Description, e.Amount,
                    e.ExpenseDate.Date, e.DueDate?.Date, e.AccountNumber, e.ProjectId,
                    e.IssuerType, e.IssuerId);

                if (dropApproval)
                {
                    ldb.Execute(
                        @"UPDATE dbo.LedgerExpense
                             SET Status = @1, ApprovedByMemberId = NULL, ApprovedUtc = NULL,
                                 ApprovalNote = NULL, ApprovedBySelf = 0
                           WHERE Id = @0",
                        e.Id, LedgerExpenseStatus.Registered);
                }

                return (true, null, e.Id, dropApproval);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utgiften kunde inte sparas för {Typ}/{Id}.",
                    e.IssuerType, e.IssuerId);

                return (false, "Utgiften kunde inte sparas. Försök igen.", 0, false);
            }
        }

        /// <summary>Kopplar det lagrade kvittot till utgiften. Filen är redan sparad på disk.</summary>
        public (bool Ok, string? Error) SetReceipt(
            int issuerType, int issuerId, int id, string fileName, string storedAs, long size)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var rows = ldb.Execute(
                    @"UPDATE dbo.LedgerExpense
                         SET ReceiptFileName = @3, ReceiptStoredAs = @4, ReceiptSizeBytes = @5
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2 AND Status <> @6",
                    id, issuerType, issuerId, fileName, storedAs, size, LedgerExpenseStatus.Paid);

                return rows == 1
                    ? (true, null)
                    // ⚠️ Efter bokföringen hör kvittot till VERIFIKATIONEN. Att byta det på utgiften
                    //    då hade lämnat bilagan på verifikationen orörd medan ytan visade en annan
                    //    fil — två svar på "vad är underlaget".
                    : (false, "Kvittot kunde inte kopplas. En betald utgift får sitt underlag på "
                            + "verifikationen i stället.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kvittot kunde inte kopplas till utgift {Id}.", id);
                return (false, "Kvittot kunde inte kopplas. Försök igen.");
            }
        }

        /// <summary>
        /// Attesterar utgiften.
        ///
        /// <para><b>⚠️ Mottagaren nekas, registreraren varnas.</b> Se
        /// <see cref="LedgerExpenseRules.ApprovalRefusal"/> för varför de två fallen behandlas
        /// olika.</para>
        /// </summary>
        public (bool Ok, string? Error, bool BySelf) Approve(
            int issuerType, int issuerId, int id, int approverMemberId, string? note)
        {
            var e = Get(issuerType, issuerId, id);
            if (e is null) return (false, "Utgiften finns inte.", false);

            var refusal = LedgerExpenseRules.ApprovalRefusal(e, approverMemberId);
            if (refusal is not null) return (false, refusal, false);

            var bySelf = e.RegisteredByMemberId == approverMemberId;

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                ldb.Execute(
                    @"UPDATE dbo.LedgerExpense
                         SET Status = @3, ApprovedByMemberId = @4, ApprovedUtc = @5,
                             ApprovalNote = @6, ApprovedBySelf = @7, RejectedReason = NULL
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    id, issuerType, issuerId, LedgerExpenseStatus.Approved, approverMemberId,
                    DateTime.UtcNow, string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    bySelf);

                _logger.LogInformation(
                    "Ekonomi: utgift {Id} attesterad av {Medlem} hos {Typ}/{Utgivare}. Egen registrering: {Egen}.",
                    id, approverMemberId, issuerType, issuerId, bySelf);

                return (true, null, bySelf);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utgift {Id} kunde inte attesteras.", id);
                return (false, "Attesten kunde inte sparas. Försök igen.", false);
            }
        }

        /// <summary>
        /// Avvisar utgiften. <b>Skälet är obligatoriskt</b> — den som lagt ut pengar måste få veta
        /// varför hen inte får dem tillbaka, och ett avslag utan skäl går inte att bemöta.
        /// </summary>
        public (bool Ok, string? Error) Reject(
            int issuerType, int issuerId, int id, int actorMemberId, string reason)
        {
            if (actorMemberId <= 0) return (false, "Du måste vara inloggad.");

            if (string.IsNullOrWhiteSpace(reason))
                return (false, "Skriv varför utgiften avvisas — den som lagt ut pengarna ska kunna "
                             + "läsa skälet.");

            var e = Get(issuerType, issuerId, id);
            if (e is null) return (false, "Utgiften finns inte.");

            if (e.IsPaid)
                return (false, "Utgiften är redan betald och bokförd. En rättelse bokförs under Bokför.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                ldb.Execute(
                    @"UPDATE dbo.LedgerExpense
                         SET Status = @3, RejectedReason = @4, ApprovedByMemberId = NULL,
                             ApprovedUtc = NULL, ApprovalNote = NULL, ApprovedBySelf = 0
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    id, issuerType, issuerId, LedgerExpenseStatus.Rejected, reason.Trim());

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utgift {Id} kunde inte avvisas.", id);
                return (false, "Avslaget kunde inte sparas. Försök igen.");
            }
        }

        /// <summary>
        /// Betalar ut utgiften och bokför den.
        ///
        /// <para><b>⚠️⚠️ DET ÄR HÄR — OCH BARA HÄR — VERIFIKATIONEN SKRIVS.</b> Kostnadskontot i
        /// debet, betalkontot i kredit. Samma riktning som bokför-ytans "Vi betalade"; blandas de
        /// ihop hamnar en utgift som en intäkt.</para>
        ///
        /// <para><b>⚠️ Bokföringsdatumet är BETALDATUMET</b>, inte fakturadatumet. Det är hela
        /// kontantmetoden: en faktura från december som betalas i januari hör till det nya året.
        /// Tar man fakturadatumet blir kontantmetoden en faktureringsmetod utan att någon valt det.</para>
        /// </summary>
        public (bool Ok, string? Error, int EntryId) Pay(
            int issuerType, int issuerId, int id, DateTime paidDate,
            int paymentAccountNumber, int byMemberId)
        {
            if (byMemberId <= 0) return (false, "Du måste vara inloggad.", 0);

            var e = Get(issuerType, issuerId, id);
            if (e is null) return (false, "Utgiften finns inte.", 0);

            var refusal = LedgerExpenseRules.PaymentRefusal(e);
            if (refusal is not null) return (false, refusal, 0);

            if (paymentAccountNumber <= 0)
                return (false, "Välj vilket konto pengarna betalades från.", 0);

            // ⚠️ Samma konto på båda sidor ger en verifikation som balanserar men inte betyder
            //    något. Den ser korrekt ut i varje kontroll utom den mänskliga.
            if (paymentAccountNumber == e.AccountNumber)
                return (false, "Utgiften skulle bokföras mot samma konto på båda sidor. "
                             + "Välj ett annat betalkonto.", 0);

            // ⚠️ Fråga liggaren FÖRST. Vägrar den efter att kassören tryckt "Betald" står hen med
            //    en utbetalning som inte går att bokföra — och pengarna har redan lämnat kontot.
            var blocked = _posting.PostingBlockedReason(issuerType, issuerId, paidDate.Date);
            if (blocked is not null) return (false, blocked, 0);

            var result = _posting.Post(new LedgerPostingRequest
            {
                IssuerType = issuerType,
                IssuerId = issuerId,
                AccountingDate = paidDate.Date,
                EventDate = e.ExpenseDate.Date,
                Description = e.Description,
                CounterpartyName = e.PayeeName,
                SourceType = LedgerSourceType.Expense,
                SourceId = e.Id,
                CreatedByMemberId = byMemberId,
                ProjectId = e.ProjectId,
                Lines = new List<LedgerPostingLine>
                {
                    new() { AccountNumber = e.AccountNumber, Debit = e.Amount, Text = e.Description },
                    new() { AccountNumber = paymentAccountNumber, Credit = e.Amount, VatRate = 0 }
                }
            });

            if (!result.Success)
                return (false, result.Error ?? "Utgiften gick inte att bokföra.", 0);

            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                ldb.Execute(
                    @"UPDATE dbo.LedgerExpense
                         SET Status = @3, PaidDate = @4, PaymentAccountNumber = @5, JournalEntryId = @6
                       WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                    id, issuerType, issuerId, LedgerExpenseStatus.Paid, paidDate.Date,
                    paymentAccountNumber, result.EntryId);

                // ⚠️⚠️ ATTESTEN SPEGLAS TILL VERIFIKATIONEN. Revisorn läser verifikationslistan,
                //    inte utgiftslistan, och frågan "vem godkände den här utbetalningen" ska gå att
                //    besvara därifrån. Härlett ur utgiften i samma operation, så de två inte kan
                //    säga olika saker — och en betald utgift går inte att attestera om.
                if (e.ApprovedByMemberId is int approver && approver > 0)
                {
                    ldb.Execute(
                        @"INSERT INTO dbo.LedgerApproval
                            (JournalEntryId, ApprovedByMemberId, ApprovedUtc, Note)
                          VALUES (@0, @1, @2, @3)",
                        result.EntryId, approver, e.ApprovedUtc ?? DateTime.UtcNow,
                        e.ApprovedBySelf
                            ? "Attesterad av den som registrerade utgiften. " + (e.ApprovalNote ?? "")
                            : e.ApprovalNote);
                }

                // ⚠️ Kvittot blir en bilaga till verifikationen FÖRST NU. Filen har legat på disk
                //    sedan registreringen — lagringen är innehållsadresserad, så bilagraden kan
                //    skapas när verifikationen finns utan att filen rörs.
                if (e.HasReceipt)
                {
                    ldb.Execute(
                        @"INSERT INTO dbo.LedgerAttachment
                            (JournalEntryId, FileName, StoredAs, ContentType, SizeBytes,
                             UploadedByMemberId, UploadedUtc)
                          VALUES (@0, @1, @2, @3, @4, @5, @6)",
                        result.EntryId, e.ReceiptFileName ?? "kvitto", e.ReceiptStoredAs,
                        LedgerAttachmentStorage.ContentTypeFor(e.ReceiptStoredAs!),
                        e.ReceiptSizeBytes, byMemberId, DateTime.UtcNow);
                }

                _logger.LogInformation(
                    "Ekonomi: utgift {Id} betald och bokförd som verifikation {Entry} hos {Typ}/{Utgivare}.",
                    id, result.EntryId, issuerType, issuerId);

                return (true, null, result.EntryId);
            }
            catch (Exception ex)
            {
                // ⚠️⚠️ VERIFIKATIONEN ÄR SKRIVEN OCH GÅR INTE ATT TA TILLBAKA. Att rapportera ett
                //    misslyckande här hade fått kassören att trycka igen och bokföra utgiften två
                //    gånger. Svaret säger i stället vad som hände och vad som måste rättas.
                _logger.LogError(ex,
                    "Utgift {Id} bokfördes som verifikation {Entry} men raden kunde inte uppdateras.",
                    id, result.EntryId);

                return (false,
                    "Utgiften ÄR bokförd, men raden kunde inte uppdateras. Tryck inte igen — "
                    + "kontrollera verifikationen under Verifikationer och hör av dig.",
                    result.EntryId);
            }
        }

        /// <summary>
        /// Obetalda utgifter vid ett datum — bokslutets leverantörsskuld.
        ///
        /// <para><b>⚠️ Spegelbilden av kundfordringssteget, och den fanns inte förrän utgiftssidan
        /// byggdes.</b> Under kontantmetoden är en obetald faktura vid årsskiftet en skuld som ska
        /// bokföras innan året fastställs; utan steget syns den ingenstans och året ser klart ut.</para>
        ///
        /// <para>⚠️ Räknar på <c>ExpenseDate</c> och inte på registreringsdatumet: en faktura från
        /// december som registrerades i januari hör ändå till december.</para>
        /// </summary>
        public (int Count, decimal Amount) UnpaidAt(int issuerType, int issuerId, DateTime asOf)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);

                var rows = ldb.Fetch<LedgerExpense>(
                    @"SELECT * FROM dbo.LedgerExpense
                       WHERE IssuerType = @0 AND IssuerId = @1
                         AND Status NOT IN (@2, @3)
                         AND ExpenseDate <= @4",
                    issuerType, issuerId, LedgerExpenseStatus.Paid, LedgerExpenseStatus.Rejected,
                    asOf.Date);

                return (rows.Count, rows.Sum(r => r.Amount));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Obetalda utgifter gick inte att summera för {Typ}/{Id}.",
                    issuerType, issuerId);

                // ⚠️ (-1, 0) = "vet inte", aldrig "inga". Ett bokslutssteg som säger klart på en
                //    misslyckad läsning är falsk trygghet.
                return (-1, 0m);
            }
        }

        private Dictionary<int, string> ResolveEntryNumbers(
            LedgerDb ldb, int issuerType, int issuerId, IEnumerable<int> entryIds)
        {
            // ⚠️⚠️ `!= 0`, ALDRIG `> 0`. Sandlådans identiteter räknar NEDÅT, så varje
            //    verifikations-id är negativt där — ett `> 0` filtrerar bort dem allihop och
            //    utgiftslistan visar tomma verifikationsnummer på betalda rader. Hittat av sviten
            //    2026-09-23, och det är samma familj som ORDER BY Id DESC-fällan.
            var ids = entryIds.Where(i => i != 0).Distinct().ToList();
            if (ids.Count == 0) return new Dictionary<int, string>();

            try
            {
                var entries = ldb.Fetch<LedgerJournalEntry>(
                    $@"SELECT * FROM dbo.LedgerJournalEntry
                        WHERE IssuerType = @0 AND IssuerId = @1
                          AND Id IN ({string.Join(",", ids)})",
                    issuerType, issuerId);

                var series = ldb.Fetch<LedgerNumberSeries>(
                        "SELECT * FROM dbo.LedgerNumberSeries WHERE IssuerType = @0 AND IssuerId = @1",
                        issuerType, issuerId)
                    .ToDictionary(s => s.Id);

                return entries.ToDictionary(
                    e => e.Id,
                    e => LedgerNumberAllocator.Format(
                        series.TryGetValue(e.SeriesId, out var s) ? s.Prefix : "", e.Number));
            }
            catch
            {
                // Ett nummer som fattas är en tom cell, inte ett fel som ska ta ner listan.
                return new Dictionary<int, string>();
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
                    // En medlem som inte går att slå upp är ett namn som fattas, inte ett fel.
                }
            }

            return names;
        }

        private class AccountName
        {
            public int Number { get; set; }
            public string Name { get; set; } = "";
        }

        private class ProjectName
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        /// <summary>
        /// Kretsavgifter som kretsen har SKICKAT till klubben — räkningar att betala.
        ///
        /// <para><b>⚠️⚠️ KRETSAVGIFTEN FANNS BARA PÅ KRETSENS SIDA.</b> Klubbens kassör fick ett mejl,
        /// betalade, och fick sedan skriva in samma räkning för hand under Utgifter — med risk att
        /// den aldrig kom med, eller kom med två gånger. Här visas den där den ska betalas, och
        /// registreras med ett klick.</para>
        ///
        /// <para>⚠️ "Registrerad" känns igen på betalningsreferensen i beskrivningen (KA2026-12) —
        /// samma sträng som står i mejlet och i bankgiro-QR:en, alltså den kassören ändå skriver.
        /// Ingen ny kolumn: en avgift som registrerats för hand med referensen räknas också.</para>
        /// </summary>
        public List<IncomingRegionFee> IncomingRegionFees(int clubId)
        {
            var result = new List<IncomingRegionFee>();
            if (clubId <= 0) return result;

            using var db = _databaseFactory.CreateDatabase();
            var charges = db.Fetch<MembershipFeeCharge>(
                @"SELECT * FROM dbo.MembershipFeeCharge
                   WHERE IssuerType = @0 AND PayerClubId = @1 AND RequestSentDate IS NOT NULL
                   ORDER BY Year DESC, Id DESC",
                MembershipFeeIssuer.Region, clubId);

            if (charges.Count == 0) return result;

            var expenses = db.Fetch<LedgerExpense>(
                @"SELECT * FROM dbo.LedgerExpense
                   WHERE IssuerType = 0 AND IssuerId = @0 AND Status <> @1",
                clubId, LedgerExpenseStatus.Rejected);

            foreach (var c in charges)
            {
                var reference = c.PaymentReference;
                var registered = expenses.FirstOrDefault(e =>
                    (e.Description ?? "").Contains(reference, StringComparison.OrdinalIgnoreCase));

                result.Add(new IncomingRegionFee
                {
                    ChargeId = c.Id,
                    RegionId = c.RegionId ?? 0,
                    Year = c.Year,
                    Amount = c.Amount,
                    Reference = reference,
                    SentDate = c.RequestSentDate,
                    PaidByUs = string.Equals(c.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase),
                    ExpenseId = registered?.Id,
                    ExpenseStatus = registered?.Status
                });
            }

            return result;
        }
    }

    /// <summary>En kretsavgift som kretsen har skickat till klubben.</summary>
    public class IncomingRegionFee
    {
        public int ChargeId { get; set; }
        public int RegionId { get; set; }
        public string RegionName { get; set; } = "";
        public int Year { get; set; }
        public decimal Amount { get; set; }
        public string Reference { get; set; } = "";
        public DateTime? SentDate { get; set; }

        /// <summary>Kretsen har kvitterat betalningen.</summary>
        public bool PaidByUs { get; set; }

        /// <summary>Utgiften den är registrerad som, om den är det.</summary>
        public int? ExpenseId { get; set; }
        public string? ExpenseStatus { get; set; }
    }
}
