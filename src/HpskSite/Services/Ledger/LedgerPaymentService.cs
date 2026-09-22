using HpskSite.Models;
using HpskSite.Models.Ledger;
using NPoco;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Direktbetalningens flöde: begär → betalarens påstående → arrangörens bekräftelse.
    /// Bekräftelsen är det enda som skapar pengar, ett kvitto och en verifikation.
    ///
    /// <para><b>⚠️⚠️ INGEN FAKTURA NÅGONSTANS HÄR.</b> Direktbetalning betyder att betalningen är
    /// villkor för platsen, så ingen fordran uppstår — och då har en faktura inget föremål. Det är
    /// hela skälet att fem av den gamla modellens tio fel upphör i den här vägen.</para>
    ///
    /// <para><b>⚠️ Bekräftelsen gör TRE saker i en transaktion:</b> markerar betalningen mottagen,
    /// utfärdar kvittot och bokför verifikationen. Delas de upp får vi mellanlägen där pengar finns
    /// utan kvitto eller kvitto utan bokföring — och båda är svårare att upptäcka än att rätta.</para>
    /// </summary>
    public class LedgerPaymentService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly LedgerPostingService _posting;
        private readonly LedgerNumberAllocator _allocator;
        private readonly ILogger<LedgerPaymentService> _logger;

        public LedgerPaymentService(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextFactory contextFactory,
            LedgerPostingService posting,
            LedgerNumberAllocator allocator,
            ILogger<LedgerPaymentService> logger)
        {
            _databaseFactory = databaseFactory;
            _contextFactory = contextFactory;
            _posting = posting;
            _allocator = allocator;
            _logger = logger;
        }

        /// <summary>
        /// Skapar betalningsraden. Det som ska betalas finns nu, men inga pengar har rört sig.
        /// <para><b>⚠️ Ett belopp om noll skapar INGEN rad.</b> Noll betyder gratis, inte "ofylld" —
        /// och en betalningsrad på noll kronor skulle dyka upp i varje avprickningslista som något
        /// att bevaka.</para>
        /// </summary>
        public int? Request(LedgerPayment payment)
        {
            if (payment.Amount <= 0) return null;

            if (!LedgerPaymentMethod.IsValid(payment.Method))
                throw new ArgumentException($"Okänt betalsätt: {payment.Method}", nameof(payment));

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, payment.IssuerId);
            payment.CreatedUtc = DateTime.UtcNow;

            // SQL, inte db.Insert: NPocos [TableName] bar inget schema och hade alltid
            // hamnat i dbo. Se LedgerDb.Insert, som kastar av samma skal.
            payment.Id = ldb.ExecuteScalar<int>(
                @"INSERT INTO dbo.LedgerPayment
                    (IssuerType, IssuerId, SourceType, SourceId, PayerMemberId, PayerName, Amount,
                     Method, ClaimedUtc, ClaimedByMemberId, ConfirmedUtc, ConfirmedByMemberId,
                     ActualAmount, JournalEntryId, ReceiptId, VoidedUtc, VoidedByMemberId,
                     VoidReason, CreatedUtc)
                  OUTPUT INSERTED.Id
                  VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16,@17,@18)",
                payment.IssuerType, payment.IssuerId, payment.SourceType,
                (object?)payment.SourceId ?? DBNull.Value, (object?)payment.PayerMemberId ?? DBNull.Value,
                payment.PayerName, payment.Amount, payment.Method,
                (object?)payment.ClaimedUtc ?? DBNull.Value, (object?)payment.ClaimedByMemberId ?? DBNull.Value,
                (object?)payment.ConfirmedUtc ?? DBNull.Value, (object?)payment.ConfirmedByMemberId ?? DBNull.Value,
                (object?)payment.ActualAmount ?? DBNull.Value, (object?)payment.JournalEntryId ?? DBNull.Value,
                (object?)payment.ReceiptId ?? DBNull.Value, (object?)payment.VoidedUtc ?? DBNull.Value,
                (object?)payment.VoidedByMemberId ?? DBNull.Value, (object?)payment.VoidReason ?? DBNull.Value,
                payment.CreatedUtc);

            return payment.Id;
        }

        /// <summary>
        /// Betalaren säger att hen betalat.
        ///
        /// <para><b>⚠️⚠️ DET HÄR ÄR INTE PENGAR.</b> Vi har ingen Swish-API och ingen callback, så
        /// påståendet är allt vi vet. Metoden får aldrig sätta <c>ConfirmedUtc</c>, aldrig utfärda
        /// ett kvitto och aldrig bokföra — och det är också varför den är en EGEN metod och inte en
        /// flagga på bekräftelsen.</para>
        /// </summary>
        public bool RegisterClaim(int paymentId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, paymentId);

            // Villkoret i WHERE är spärren: en redan bekräftad eller makulerad betalning tar inte
            // emot ett påstående, och ett andra påstående skriver inte över det första.
            return ldb.Execute(
                @"UPDATE dbo.LedgerPayment
                     SET ClaimedUtc = @1, ClaimedByMemberId = @2
                   WHERE Id = @0 AND ClaimedUtc IS NULL AND ConfirmedUtc IS NULL AND VoidedUtc IS NULL",
                paymentId, DateTime.UtcNow, byMemberId) > 0;
        }

        /// <summary>
        /// Arrangören bekräftar att pengarna kommit. <b>Nu</b> blir det pengar, kvitto och bokföring.
        /// </summary>
        /// <param name="actualAmount">
        /// Vad som faktiskt togs emot, när det skiljer sig från det begärda. Null = ingen avvikelse.
        /// </param>
        /// <summary>
        /// Mottagna betalningar som saknar verifikation — <b>kön "att bokföra"</b>.
        ///
        /// <para>⚠⚠ HÄRLETT, ALDRIG LAGRAT. Tillståndet är "bekräftad men utan
        /// <see cref="LedgerPayment.JournalEntryId"/>", vilket gör kön omojlig att glömma
        /// uppdatera. En lagrad flagga hade behövt skrivas på två ställen, och en missad
        /// skrivning är en verifikation som tyst aldrig blir av.</para>
        ///
        /// <para>⚠️ För en förening som bokför någon annanstans är listan ALLTID full, och
        /// helt ointressant — den ytan ska därför bara visas för
        /// <see cref="LedgerIssuerShape.FullLedger"/>. Se DecidePosting.</para>
        /// </summary>
        public List<LedgerPayment> UnpostedConfirmed(int issuerType, int issuerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var ldb = new LedgerDb(db, issuerId);
                return ldb.Fetch<LedgerPayment>(
                    @"SELECT * FROM dbo.LedgerPayment
                       WHERE IssuerType = @0 AND IssuerId = @1
                         AND ConfirmedUtc IS NOT NULL
                         AND VoidedUtc IS NULL
                         AND JournalEntryId IS NULL
                       ORDER BY ConfirmedUtc",
                    issuerType, issuerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Kunde inte läsa obokförda betalningar för utställare {Type}/{Id}.",
                    issuerType, issuerId);
                return new List<LedgerPayment>();
            }
        }

        /// <summary>
        /// En betalningsrad, eller null.
        ///
        /// <para><b>⚠️ Finns för behörighetskontrollen.</b> En yta som får ett <c>paymentId</c> från
        /// en klient vet ingenting om vem som äger raden — utställaren måste läsas ur databasen
        /// innan något görs med den, annars räcker ett giltigt id för att nå en annan förenings
        /// liggare.</para>
        /// </summary>
        public LedgerPayment? GetById(int paymentId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, paymentId);
            return ldb.SingleOrDefault<LedgerPayment>(
                "SELECT * FROM dbo.LedgerPayment WHERE Id = @0", paymentId);
        }

        /// <summary>
        /// Bokför en betalning som redan är bekräftad men saknar verifikation — <b>åtgärden som
        /// tömmer kön "att bokföra"</b>.
        ///
        /// <para>Kön uppstår av en enda anledning: pengarna togs emot medan bokföringen var
        /// avstängd eller blockerad. Vanligast är att föreningen inte var uppsatt ännu, vilket
        /// gällde <b>varje</b> förening fram till 2026-09-21. Utan den här metoden är kön en lista
        /// man kan titta på men inte göra något åt, och då står pengarna utanför bokföringen för
        /// alltid.</para>
        ///
        /// <para><b>⚠️ Bokföringsdatum är <see cref="LedgerPayment.ConfirmedUtc"/>, inte i dag.</b>
        /// Kontantmetoden är metoden, så posten hör till den dag pengarna kom in. Att bokföra dem
        /// i dag hade flyttat en intäkt mellan räkenskapsår varje gång kön tömts sent.</para>
        ///
        /// <para><b>⚠️ Ett <c>paymentDate</c> som avvek vid bekräftelsen går förlorat.</b>
        /// <see cref="Confirm"/> tar emot ett datum men <b>sparar det inte</b> — raden bär bara
        /// <c>ConfirmedUtc</c>. För en betalning som bekräftats med ett avvikande datum bokför den
        /// här metoden alltså på bekräftelsedagen, inte på betaldagen. Rätt åtgärd är en kolumn
        /// för betaldatumet; tills den finns är det här det ärligaste vi kan göra, och det är
        /// skillnaden mellan två datum som oftast är samma dag.</para>
        /// </summary>
        public PostPendingResult PostPending(int paymentId, int byMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, paymentId);

            var payment = ldb.SingleOrDefault<LedgerPayment>(
                "SELECT * FROM dbo.LedgerPayment WHERE Id = @0", paymentId);

            if (payment is null) return PostPendingResult.Failed("Betalningen finns inte.");
            if (payment.VoidedUtc is not null) return PostPendingResult.Failed("Betalningen är återtagen.");
            if (payment.ConfirmedUtc is null)
                return PostPendingResult.Failed("Betalningen är inte bekräftad som mottagen ännu.");
            if (payment.JournalEntryId is not null)
                return PostPendingResult.Failed("Betalningen är redan bokförd.");

            var date = payment.ConfirmedUtc.Value.Date;
            var settings = LoadSettings(db, payment.IssuerType, payment.IssuerId);

            var decision = _posting.DecidePosting(payment.IssuerType, payment.IssuerId, date);

            if (!decision.ShouldPost)
            {
                return PostPendingResult.Failed(
                    decision.SkipReason
                    ?? "Föreningen bokför inte i pistol.nu, så det finns ingen verifikation att skriva.");
            }

            // ⚠️⚠️ KVITTOT ÄR REDAN UTFÄRDAT, OCH DET PÅSTÅR NÅGOT OM MOMSEN. Blev föreningen
            // momsregistrerad EFTER att den här betalningen bekräftades, skulle en efterbokföring
            // nu räkna fram moms som betalarens kvitto inte visar — två handlingar om samma pengar
            // som säger olika saker, och den ena ligger redan hos någon annan. Vägra hellre.
            if (settings?.IsVatRegistered == true)
            {
                var receiptSaysVatFree = ldb.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerReceipt
                       WHERE PaymentId = @0 AND IssuerIsVatRegistered = 0",
                    payment.Id);

                if (receiptSaysVatFree > 0)
                {
                    return PostPendingResult.Failed(
                        "Kvittot för den här betalningen säger att föreningen inte är momsregistrerad, "
                        + "men det är den nu. Att bokföra i efterhand skulle räkna fram en moms som "
                        + "kvittot inte visar. Rätta med en rättelseverifikation i stället.");
                }
            }

            var posting = _posting.Post(BuildPostingRequest(payment, payment.SettledAmount, date, byMemberId));

            if (!posting.Success)
                return PostPendingResult.Failed(posting.Error ?? "Bokföringen gick inte igenom.");

            // ⚠️ VILLKORET I WHERE ÄR SPÄRREN mot dubbelbokföring. Två samtidiga klick — eller två
            // öppna flikar — skulle annars skriva två verifikationer för samma pengar och bara den
            // sista syns på raden. Den första hade blivit osynlig och omöjlig att hitta.
            var rows = ldb.Execute(
                @"UPDATE dbo.LedgerPayment SET JournalEntryId = @1
                   WHERE Id = @0 AND JournalEntryId IS NULL AND VoidedUtc IS NULL",
                payment.Id, posting.EntryId);

            if (rows == 0)
            {
                // Verifikationen är skriven och går inte att ta bort. Säg det rakt ut och namnge
                // den — precis som Confirm gör i sitt motsvarande läge.
                _logger.LogError(
                    "Betalning {PaymentId} bokfördes som verifikation {EntryId}, men raden hade "
                    + "hunnit bokföras eller återtas av någon annan. Verifikationen står kvar.",
                    payment.Id, posting.EntryId);

                return PostPendingResult.Failed(
                    $"Verifikation {posting.EntryId} skrevs, men betalningen hann ändras av någon "
                    + "annan under tiden. Kontakta support så reds raden ut — bokför inte igen.");
            }

            _logger.LogInformation(
                "Betalning {PaymentId} efterbokfördes som verifikation {EntryId} på {Datum}.",
                payment.Id, posting.EntryId, date);

            return new PostPendingResult
            {
                Success = true,
                PaymentId = payment.Id,
                JournalEntryId = posting.EntryId,
                AccountingDate = date
            };
        }

        /// <summary>
        /// Hur en betalning ser ut som verifikation. <b>EN beskrivning, två anropare</b>
        /// (<see cref="Confirm"/> och <see cref="PostPending"/>).
        ///
        /// <para>⚠️ Låg först bara inne i <c>Confirm</c>. Hade efterbokföringen fått en egen kopia
        /// vore samma pengar bokförda på två olika sätt beroende på NÄR någon råkade trycka —
        /// och skillnaden hade synts först i en resultatrapport långt senare.</para>
        /// </summary>
        private static LedgerPostingRequest BuildPostingRequest(
            LedgerPayment payment, decimal amount, DateTime date, int byMemberId)
            => new()
            {
                IssuerType = payment.IssuerType,
                IssuerId = payment.IssuerId,
                AccountingDate = date,
                EventDate = date,
                Description = DescribeFor(payment),
                CounterpartyType = null,
                CounterpartyId = payment.PayerMemberId,
                CounterpartyName = payment.PayerName,
                SourceType = payment.SourceType,
                SourceId = payment.SourceId,
                PaymentId = payment.Id,
                CreatedByMemberId = byMemberId,
                Lines = new List<LedgerPostingLine>
                {
                    // Pengarna in på det konto betalsättet landar på …
                    new()
                    {
                        Role = LedgerPaymentMethod.RoleFor(payment.Method),
                        Debit = amount,
                        // ⚠️ VatRate = 0 på betalkontot. Momsen hör till INTÄKTEN, inte till
                        // pengarnas väg in — annars delas beloppet två gånger.
                        VatRate = 0
                    },
                    // … och intäkten i kredit. Momsen faller ut ur kontots DefaultVatRate.
                    new()
                    {
                        Role = RevenueRoleFor(payment.SourceType),
                        Credit = amount,
                        Text = payment.PayerName
                    }
                }
            };

        public ConfirmResult Confirm(
            int paymentId, int byMemberId, DateTime? paymentDate = null, decimal? actualAmount = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, paymentId);

            var payment = ldb.SingleOrDefault<LedgerPayment>(
                "SELECT * FROM dbo.LedgerPayment WHERE Id = @0", paymentId);

            if (payment is null) return ConfirmResult.Failed("Betalningen finns inte.");
            if (payment.VoidedUtc is not null) return ConfirmResult.Failed("Betalningen är makulerad.");
            if (payment.ConfirmedUtc is not null)
                return ConfirmResult.Failed("Betalningen är redan bekräftad som mottagen.");

            if (actualAmount is <= 0)
                return ConfirmResult.Failed("Ett mottaget belopp måste vara större än noll. Makulera i stället.");

            var settings = LoadSettings(db, payment.IssuerType, payment.IssuerId);
            var issuerDetails = LoadIssuerDetails(payment.IssuerType, payment.IssuerId);
            var amount = actualAmount ?? payment.Amount;
            var date = (paymentDate ?? DateTime.Today).Date;

            // ── ⚠⚠ BOKFÖR FÖRST — NÄR FÖRENINGEN BOKFÖR HOS OSS ────────────────────────────
            //
            // Ordningen är medveten: går bokföringen inte igenom ska ingenting ha hänt. Men
            // bokföring är OPT-IN (se DecidePosting). De flesta klubbar vill bara kunna ta betalt
            // och bokför någon annanstans — för dem finns ingen verifikation att skriva, och ett
            // kvitto är ändå fullt giltigt: det är ett kvitto på en MOTTAGEN BETALNING, inte en
            // bokföringshandling.
            //
            // ⚠️ ETT UNDANTAG, och det är inte försiktighet: är föreningen MOMSREGISTRERAD måste
            // kvittot ange momsen, och momsbeloppet faller ut ur bokföringens kontorader. Utan
            // posting finns ingen moms att skriva, och ett momsfritt kvitto från en momsregistrerad
            // förening är en oriktig uppgift. Där vägrar vi hellre än utfärdar.
            var decision = _posting.DecidePosting(payment.IssuerType, payment.IssuerId, date);

            if (!decision.ShouldPost && settings?.IsVatRegistered == true)
                return ConfirmResult.Failed(
                    (decision.SkipReason ?? "Föreningen bokför inte i pistol.nu.")
                    + " Föreningen är momsregistrerad, och då kan kvittot inte utfärdas utan "
                    + "bokföring — momsen härleds ur verifikationens konton.");

            LedgerPostingResult? posting = null;
            // ⚠️ Samma BuildPostingRequest som efterbokföringen använder. Se dess kommentar:
            // två kopior hade gett samma pengar två olika konteringar beroende på tidpunkt.
            if (decision.ShouldPost)
                posting = _posting.Post(BuildPostingRequest(payment, amount, date, byMemberId));

            if (posting is { Success: false })
                return ConfirmResult.Failed(posting.Error ?? "Bokföringen gick inte igenom.");

            // ── Bekräftelsen och kvittot ────────────────────────────────────────────────────
            try
            {
                using var tx = ldb.GetTransaction();

                var (seriesId, number, prefix) = _allocator.Allocate(
                    db, payment.IssuerType, payment.IssuerId, date.Year, LedgerSeriesKind.Receipt);

                // Utan bokföring finns ingen momsuppdelning — och då är föreningen inte heller
                // momsregistrerad, eftersom det fallet vägrades ovan.
                var vat = posting?.Lines
                    .Where(l => l.VatAmount is > 0)
                    .Sum(l => l.VatAmount!.Value) ?? 0m;

                var receipt = new LedgerReceipt
                {
                    IssuerType = payment.IssuerType,
                    IssuerId = payment.IssuerId,
                    SeriesId = seriesId,
                    Number = number,
                    PaymentId = payment.Id,
                    IssuedUtc = DateTime.UtcNow,
                    IssuedByMemberId = byMemberId,
                    // ⚠️ SNAPSHOT — fel 10. Ändras klubbens bankgiro i morgon ska det här kvittot
                    // fortfarande visa det som gällde i dag.
                    IssuerName = issuerDetails.Name,
                    IssuerOrgNumber = issuerDetails.OrgNumber,
                    IssuerAddress = issuerDetails.Address,
                    IssuerEmail = issuerDetails.Email,
                    PaymentDetails = issuerDetails.PaymentDetails,
                    // ⚠️ Momspåståendet ur DATA, aldrig ur en textrad i koden — fel 8.
                    IssuerIsVatRegistered = settings?.IsVatRegistered ?? false,
                    IssuerVatNumber = settings?.VatNumber,
                    VatAmount = vat > 0 ? vat : null,
                    Description = DescribeFor(payment),
                    Amount = amount
                };

                // ⚠️⚠️ SCOPE_IDENTITY, inte OUTPUT: kvittotabellen bär en
                // oföränderlighetstrigger, och SQL Server vägrar OUTPUT utan INTO då — medan
                // `INTO @new` i sin tur läses av NPoco som en parameter. Se LedgerPostingService.
                receipt.Id = ldb.ExecuteScalar<int>(
                    @"INSERT INTO dbo.LedgerReceipt
                        (IssuerType, IssuerId, SeriesId, Number, PaymentId, IssuedUtc,
                         IssuedByMemberId, IssuerName, IssuerOrgNumber, IssuerAddress, IssuerEmail,
                         PaymentDetails, IssuerIsVatRegistered, IssuerVatNumber, VatAmount,
                         Description, Amount)
                      VALUES (@0,@1,@2,@3,@4,@5,@6,@7,@8,@9,@10,@11,@12,@13,@14,@15,@16);
                      SELECT CAST(SCOPE_IDENTITY() AS int);",
                    receipt.IssuerType, receipt.IssuerId, receipt.SeriesId, receipt.Number,
                    receipt.PaymentId, receipt.IssuedUtc, receipt.IssuedByMemberId, receipt.IssuerName,
                    (object?)receipt.IssuerOrgNumber ?? DBNull.Value, (object?)receipt.IssuerAddress ?? DBNull.Value,
                    (object?)receipt.IssuerEmail ?? DBNull.Value, (object?)receipt.PaymentDetails ?? DBNull.Value,
                    receipt.IssuerIsVatRegistered, (object?)receipt.IssuerVatNumber ?? DBNull.Value,
                    (object?)receipt.VatAmount ?? DBNull.Value, receipt.Description, receipt.Amount);

                ldb.Execute(
                    @"UPDATE dbo.LedgerPayment
                         SET ConfirmedUtc = @1, ConfirmedByMemberId = @2, ActualAmount = @3,
                             JournalEntryId = @4, ReceiptId = @5
                       WHERE Id = @0",
                    // ⚠️ JournalEntryId = null är inte ett fel — det ÄR kön "att bokföra".
                    // Bekräftad utan verifikation är ett härlett tillstånd, ingen lagrad flagga att
                    // glömma uppdatera.
                    payment.Id, DateTime.UtcNow, byMemberId, actualAmount,
                    (object?)posting?.EntryId ?? DBNull.Value, receipt.Id);

                tx.Complete();

                return new ConfirmResult
                {
                    PaymentId = payment.Id,
                    JournalEntryId = posting?.EntryId,
                    PostingSkippedReason = decision.SkipReason,
                    ReceiptId = receipt.Id,
                    ReceiptNumber = LedgerNumberAllocator.Format(prefix, number),
                    Amount = amount
                };
            }
            catch (Exception ex)
            {
                // ⚠️ Verifikationen är redan skriven och kan inte tas bort. Att låtsas att
                // bekräftelsen misslyckades vore fel: pengarna ÄR bokförda. Säg vad som gäller och
                // namnge verifikationen, så någon kan reda ut det.
                if (posting is null)
                {
                    _logger.LogError(ex,
                        "Betalning {PaymentId} kunde inte bekräftas. Ingen verifikation skrevs, så "
                        + "inget behöver rattas — försök igen.", payment.Id);

                    return ConfirmResult.Failed(
                        "Betalningen kunde inte bekräftas. Ingenting sparades — försök igen.");
                }

                _logger.LogError(ex,
                    "Betalning {PaymentId} bokfördes som verifikation {EntryId} men kvittot eller "
                    + "bekräftelsen kunde inte skrivas. Betalningen står kvar som obekräftad.",
                    payment.Id, posting.EntryId);

                return ConfirmResult.Failed(
                    $"Pengarna är bokförda (verifikation {posting.EntryId}) men kvittot kunde inte "
                    + "utfärdas. Bekräfta inte igen — kontakta support så raden rättas.");
            }
        }

        /// <summary>
        /// Ångrar en betalning. <b>EN ingång för tre olika verkligheter</b>, och vilken det är
        /// avgörs av RADEN — aldrig av anroparen.
        ///
        /// <list type="table">
        /// <item><term>Inte bekräftad</term><description>Ingenting har hänt med pengarna. Raden
        ///   makuleras. Ingen bokföring, inget kvitto.</description></item>
        /// <item><term>Bekräftad, inte bokförd</term><description>Föreningen bokför inte hos oss,
        ///   eller bokföringen var blockerad. Det finns ingen verifikation att rätta — raden
        ///   återtas.</description></item>
        /// <item><term>Bekräftad och bokförd</term><description>En RÄTTELSEVERIFIKATION skrivs
        ///   först, sedan återtas raden.</description></item>
        /// </list>
        ///
        /// <para><b>⚠️⚠️ BOKFÖR FÖRST, MARKERA SEDAN</b> — samma ordning som <see cref="Confirm"/>,
        /// och av samma skäl: går rättelsen inte igenom ska ingenting ha hänt. Omvänd ordning hade
        /// lämnat en återtagen betalning vars pengar står kvar i bokföringen.</para>
        ///
        /// <para><b>⚠️ Fastställt räkenskapsår löser sig självt.</b>
        /// <see cref="LedgerPostingService.CreateCorrection"/> bokför rättelsen I DAG, inte på
        /// originalets datum — perioden då felet upptäcktes är den som är sann, och originalets
        /// period kan vara stängd. Är även dagens år fastställt vägrar liggaren med sitt eget
        /// besked, och det är rätt: ett fastställt år tar inte emot skrivningar.</para>
        ///
        /// <para><b>⚠️ Kvittot rivs inte.</b> Det ligger hos betalaren och är en handling om vad som
        /// hände då. Att raden är återtagen följer av <see cref="LedgerPayment.IsMoney"/>, som läser
        /// <c>VoidedUtc</c> — så varje ställe som räknar pengar ser det utan att någon behöver komma
        /// ihåg en flagga till. Ett kreditkvitto hör till ekonomibygget (P2).</para>
        ///
        /// <para>Skälet är OBLIGATORISKT. En återtagen betalning utan skäl är en rad ingen kan
        /// granska i efterhand, och det är hela poängen med en liggare.</para>
        /// </summary>
        public ReverseResult Reverse(int paymentId, int byMemberId, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return ReverseResult.Failed(
                    "Ange varför betalningen ångras — det är det som gör raden granskningsbar.");
            reason = reason.Trim();

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, paymentId);
            var payment = ldb.SingleOrDefault<LedgerPayment>(
                "SELECT * FROM dbo.LedgerPayment WHERE Id = @0", paymentId);

            if (payment is null) return ReverseResult.Failed("Betalningen finns inte.");
            if (payment.VoidedUtc is not null)
                return ReverseResult.Failed("Betalningen är redan ångrad.");

            // ── 1. Inte bekräftad: ingenting har hänt med pengarna ─────────────────────────────
            if (payment.ConfirmedUtc is null)
            {
                if (!Void(paymentId, byMemberId, reason))
                    return ReverseResult.Failed("Betalningen kunde inte makuleras. Försök igen.");

                return new ReverseResult
                {
                    Success = true,
                    Outcome = ReverseOutcome.Voided,
                    Message = "Betalningsbegäran är makulerad. Inga pengar hade tagits emot, "
                            + "så det finns ingenting att bokföra."
                };
            }

            // ── 2. Bekräftad OCH bokförd: rättelsen skrivs FÖRST ───────────────────────────────
            int? correctionId = null;
            if (payment.JournalEntryId is int entryId)
            {
                var correction = _posting.CreateCorrection(
                    entryId, byMemberId, $"Ångrad betalning: {reason}");

                if (!correction.Success)
                    return ReverseResult.Failed(
                        (correction.Error ?? "Rättelsen kunde inte bokföras.")
                        + " Betalningen står kvar som mottagen — ingenting har ändrats.");

                correctionId = correction.EntryId;
            }

            // ── 3. Återta raden ────────────────────────────────────────────────────────────────
            // ⚠️ EGEN UPDATE, inte Void(). Void vägrar med flit en bekräftad rad, så att ingen av
            // misstag tar bort pengar ur liggaren utan att rätta bokföringen. Den här skrivningen
            // är tillåten just därför att rättelsen redan är skriven ovanför.
            var rows = ldb.Execute(
                @"UPDATE dbo.LedgerPayment
                     SET VoidedUtc = @1, VoidedByMemberId = @2, VoidReason = @3
                   WHERE Id = @0 AND VoidedUtc IS NULL",
                paymentId, DateTime.UtcNow, byMemberId, reason);

            if (rows == 0)
            {
                // ⚠️ Rättelsen är skriven och kan inte tas bort. Säg vad som gäller och namnge
                // verifikationen — samma hållning som Confirm när kvittot fallerar efter bokföring.
                _logger.LogError(
                    "Betalning {PaymentId} rättades som verifikation {EntryId} men raden kunde inte "
                    + "markeras ångrad. Bokföringen och betalningen är nu oöverens.",
                    paymentId, correctionId);

                return ReverseResult.Failed(
                    correctionId is null
                        ? "Betalningen kunde inte ångras. Försök igen."
                        : $"Rättelsen är bokförd (verifikation {correctionId}) men betalningsraden "
                          + "kunde inte markeras ångrad. Ångra inte igen — kontakta support.");
            }

            return new ReverseResult
            {
                Success = true,
                Outcome = correctionId is null
                    ? ReverseOutcome.ReversedUnposted
                    : ReverseOutcome.ReversedCorrected,
                CorrectionEntryId = correctionId,
                Message = correctionId is null
                    ? "Betalningen är ångrad. Föreningen bokför inte i pistol.nu, så det fanns "
                    + "ingen verifikation att rätta."
                    : $"Betalningen är ångrad och rättad i bokföringen (verifikation {correctionId})."
                    + " Originalet står kvar — en liggare raderar inte, den rättar."
            };
        }

        /// <summary>
        /// Makulerar en betalningsrad.
        ///
        /// <para><b>⚠️ En bekräftad betalning makuleras INTE här.</b> Pengarna är bokförda och
        /// kvittot ligger hos betalaren; rätt väg är en rättelse i liggaren
        /// (<c>LedgerPostingService.CreateCorrection</c>) plus en återbetalningsrad. Att bara
        /// markera raden makulerad skulle lämna bokföringen och betalningen oense.</para>
        /// </summary>
        public bool Void(int paymentId, int byMemberId, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;

            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, paymentId);

            return ldb.Execute(
                @"UPDATE dbo.LedgerPayment
                     SET VoidedUtc = @1, VoidedByMemberId = @2, VoidReason = @3
                   WHERE Id = @0 AND ConfirmedUtc IS NULL AND VoidedUtc IS NULL",
                paymentId, DateTime.UtcNow, byMemberId, reason) > 0;
        }

        /// <summary>
        /// Avprickningslistan: alla betalningsrader för en källa (ett evenemang, en tävling).
        /// <para>Det HÄR ersätter Pending-fakturorna, som är arrangörens arbetslista i dag.</para>
        /// </summary>
        public List<LedgerPayment> ForSource(string sourceType, int sourceId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, LedgerSchema.LiveOnly);

            return ldb.Fetch<LedgerPayment>(
                @"SELECT * FROM dbo.LedgerPayment
                   WHERE SourceType = @0 AND SourceId = @1
                   ORDER BY PayerName, Id",
                sourceType, sourceId);
        }

        /// <summary>
        /// Vad som fortfarande saknas för en källa. <b>Härlett, aldrig lagrat.</b>
        /// <para>Fullständighetsfrågan Fredrik formulerade: <i>"det är för kassören okänt … hur många
        /// anmälningsavgifter som förväntas komma"</i>. Den går bara att svara på genom att räkna
        /// raderna, och därför får svaret aldrig vara en lagrad siffra som kan glida.</para>
        /// </summary>
        public (int Expected, int Settled, decimal Outstanding) Completeness(string sourceType, int sourceId)
        {
            var rows = ForSource(sourceType, sourceId).Where(p => p.VoidedUtc is null).ToList();

            return (
                rows.Count,
                rows.Count(p => p.IsMoney),
                rows.Where(p => !p.IsMoney).Sum(p => p.Amount));
        }

        // ── Uppslag ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Intäktsrollen för en källa. <b>Alltid en ROLL, aldrig ett kontonummer</b> — det är vad som
        /// gör att en förening kan bygga om sin kontoplan utan att en kodrad ändras.
        /// </summary>
        private static string RevenueRoleFor(string sourceType) => sourceType switch
        {
            LedgerSourceType.CompetitionRegistration => LedgerAccountRoles.RevenueParticipationFee,
            LedgerSourceType.TeamFee => LedgerAccountRoles.RevenueParticipationFee,
            LedgerSourceType.MembershipFee => LedgerAccountRoles.RevenueMembershipFee,
            LedgerSourceType.RegionFee => LedgerAccountRoles.RevenueRegionFee,
            // Ett evenemang kan vara vad som helst — träning, kurs, städdag. Övriga intäkter är det
            // ärliga svaret; föreningen kan peka om rollen om den vill skilja dem ut.
            _ => LedgerAccountRoles.RevenueOther
        };

        /// <summary>
        /// Vad betalningen gallde, i klartext. <b>Publik for att ytorna ska visa SAMMA text som
        /// verifikationen bar</b> — en egen oversattning i vyn hade kunnat saga "Startavgift" om
        /// en rad som star bokford som "Anmalningsavgift".
        /// </summary>
        public static string DescribeFor(LedgerPayment payment) => payment.SourceType switch
        {
            LedgerSourceType.CompetitionRegistration => "Anmälningsavgift",
            LedgerSourceType.TeamFee => "Lagavgift",
            LedgerSourceType.Event => "Evenemangsavgift",
            LedgerSourceType.MembershipFee => "Medlemsavgift",
            LedgerSourceType.RegionFee => "Kretsavgift",
            _ => "Betalning"
        };

        private static LedgerIssuerSettings? LoadSettings(IDatabase db, int issuerType, int issuerId)
            => db.FirstOrDefault<LedgerIssuerSettings>(
                LedgerSchema.Sql(issuerId,
                    "SELECT * FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1"),
                issuerType, issuerId);

        private readonly record struct IssuerDetails(
            string Name, string? OrgNumber, string? Address, string? Email, string? PaymentDetails);

        /// <summary>
        /// Läser utställarens uppgifter ur noden för att snapshotta dem på kvittot.
        ///
        /// <para><b>⚠️ Går INTE via <c>ClubService</c>.</b> Dess <c>ClubInfo</c> saknar
        /// <c>orgNumber</c>, adress och e-post — precis de fält ett kvitto måste bära. Husregeln
        /// "använd alltid ClubService" gäller namnuppslag, inte adressblocket; samma fälla som
        /// föreningsintyget gick i.</para>
        /// </summary>
        private IssuerDetails LoadIssuerDetails(int issuerType, int issuerId)
        {
            try
            {
                using var cref = _contextFactory.EnsureUmbracoContext();
                var node = cref.UmbracoContext.Content?.GetById(issuerId);
                if (node is null) return new IssuerDetails("", null, null, null, null);

                var name = issuerType == DocumentOwnerType.Club
                    ? node.Value<string>("clubName") ?? node.Name ?? ""
                    : node.Name ?? "";

                var street = node.Value<string>("address");
                var zip = node.Value<string>("postalCode");
                var city = node.Value<string>("city");
                var address = string.Join(", ",
                    new[] { street, string.Join(" ", new[] { zip, city }.Where(s => !string.IsNullOrWhiteSpace(s))) }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                // receiptEmail finns just för kvittot och faller tillbaka på kontaktadressen.
                var email = node.Value<string>("receiptEmail");
                if (string.IsNullOrWhiteSpace(email)) email = node.Value<string>("contactEmail");

                var swish = node.Value<string>("swishNumber");
                var bg = node.Value<string>("bankgiro");
                var details = !string.IsNullOrWhiteSpace(swish) ? $"Swish {swish}"
                            : !string.IsNullOrWhiteSpace(bg) ? $"Bankgiro {bg}"
                            : null;

                return new IssuerDetails(
                    name,
                    node.Value<string>("orgNumber"),
                    string.IsNullOrWhiteSpace(address) ? null : address,
                    email,
                    details);
            }
            catch (Exception ex)
            {
                // ⚠️ Ett kvitto med tomma utställarrader är sämre än inget kvitto, men att fälla
                // bekräftelsen på ett uppslagsfel är värre: pengarna har kommit. Logga och skriv
                // kvittot med vad vi har — det syns på handlingen att raderna fattas.
                _logger.LogError(ex,
                    "Kunde inte läsa utställarens uppgifter för {Typ}/{Id}. Kvittot skrivs utan dem.",
                    issuerType, issuerId);
                return new IssuerDetails("", null, null, null, null);
            }
        }

        /// <summary>Vad bekräftelsen gav. <see cref="Error"/> är null när det gick.</summary>
        public class ConfirmResult
        {
            public bool Success => Error is null;
            public string? Error { get; set; }
            public int PaymentId { get; set; }

            /// <summary><c>null</c> = betalningen är mottagen men inte bokförd hos oss.</summary>
            public int? JournalEntryId { get; set; }

            /// <summary>
            /// Varför ingen verifikation skrevs, när det är något arrangören behöver veta.
            /// <para>⚠️ <c>null</c> betyder <b>inget att säga</b> — föreningen bokför någon
            /// annanstans. Det är inte ett fel och ska inte visas som ett.</para>
            /// </summary>
            public string? PostingSkippedReason { get; set; }
            public int ReceiptId { get; set; }
            public string ReceiptNumber { get; set; } = "";
            public decimal Amount { get; set; }

            public static ConfirmResult Failed(string error) => new() { Error = error };
        }
    
    /// <summary>Vad som faktiskt hände när en betalning ångrades.</summary>
    public static class ReverseOutcome
    {
        /// <summary>Obekräftad begäran makulerad — inga pengar, ingen bokföring.</summary>
        public const string Voided = "voided";

        /// <summary>Mottagen betalning återtagen. Föreningen bokför inte hos oss.</summary>
        public const string ReversedUnposted = "reversed-unposted";

        /// <summary>Mottagen betalning återtagen OCH rättad med en motverifikation.</summary>
        public const string ReversedCorrected = "reversed-corrected";
    }

    /// <summary>
    /// Utfallet av <see cref="LedgerPaymentService.Reverse"/>.
    ///
    /// <para>⚠️ <see cref="Outcome"/> finns för att skärmen ska kunna säga VAD som hände. "Ångrad"
    /// betyder tre olika saker beroende på om pengar tagits emot och om föreningen bokför, och ett
    /// gemensamt besked hade varit sant i högst ett av fallen.</para>
    /// </summary>
    /// <summary>Vad efterbokföringen gjorde. Se <see cref="LedgerPaymentService.PostPending"/>.</summary>
    public sealed class PostPendingResult
    {
        public bool Success { get; set; }

        public string? Error { get; set; }

        public int PaymentId { get; set; }

        public int? JournalEntryId { get; set; }

        /// <summary>
        /// Datumet posten hamnade på — betalningens bekräftelsedag, aldrig i dag. Skickas tillbaka
        /// för att den som bokför ska se vilket år intäkten landade i, inte behöva gissa.
        /// </summary>
        public DateTime AccountingDate { get; set; }

        public static PostPendingResult Failed(string error) => new() { Success = false, Error = error };
    }

    public sealed class ReverseResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string Outcome { get; set; } = "";
        public int? CorrectionEntryId { get; set; }
        public string Message { get; set; } = "";

        public static ReverseResult Failed(string error) => new() { Success = false, Error = error };
    }
}
}
