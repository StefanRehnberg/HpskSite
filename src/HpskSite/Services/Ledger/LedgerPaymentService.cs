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
            payment.CreatedUtc = DateTime.UtcNow;
            db.Insert(payment);
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

            // Villkoret i WHERE är spärren: en redan bekräftad eller makulerad betalning tar inte
            // emot ett påstående, och ett andra påstående skriver inte över det första.
            return db.Execute(
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
        public ConfirmResult Confirm(
            int paymentId, int byMemberId, DateTime? paymentDate = null, decimal? actualAmount = null)
        {
            using var db = _databaseFactory.CreateDatabase();

            var payment = db.SingleOrDefault<LedgerPayment>(
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

            // ── Bokför FÖRST, utanför kvittots transaktion ───────────────────────────────────
            // ⚠️ Ordningen är medveten: går bokföringen inte igenom (stängt år, saknad kontoroll)
            // ska INGENTING ha hänt — varken ett kvitto hos betalaren eller en bekräftad betalning.
            // Ett kvitto utan bokföring är den enda av kombinationerna som är svår att upptäcka.
            var posting = _posting.Post(new LedgerPostingRequest
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
            });

            if (!posting.Success)
                return ConfirmResult.Failed(posting.Error ?? "Bokföringen gick inte igenom.");

            // ── Bekräftelsen och kvittot ────────────────────────────────────────────────────
            try
            {
                using var tx = db.GetTransaction();

                var (seriesId, number, prefix) = _allocator.Allocate(
                    db, payment.IssuerType, payment.IssuerId, date.Year, LedgerSeriesKind.Receipt);

                var vat = posting.Lines
                    .Where(l => l.VatAmount is > 0)
                    .Sum(l => l.VatAmount!.Value);

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

                db.Insert(receipt);

                db.Execute(
                    @"UPDATE dbo.LedgerPayment
                         SET ConfirmedUtc = @1, ConfirmedByMemberId = @2, ActualAmount = @3,
                             JournalEntryId = @4, ReceiptId = @5
                       WHERE Id = @0",
                    payment.Id, DateTime.UtcNow, byMemberId, actualAmount, posting.EntryId, receipt.Id);

                tx.Complete();

                return new ConfirmResult
                {
                    PaymentId = payment.Id,
                    JournalEntryId = posting.EntryId,
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

            return db.Execute(
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

            return db.Fetch<LedgerPayment>(
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

        private static string DescribeFor(LedgerPayment payment) => payment.SourceType switch
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
                "SELECT * FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1",
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
            public int JournalEntryId { get; set; }
            public int ReceiptId { get; set; }
            public string ReceiptNumber { get; set; } = "";
            public decimal Amount { get; set; }

            public static ConfirmResult Failed(string error) => new() { Error = error };
        }
    }
}
