using HpskSite.Models;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Bron mellan medlemsavgifterna och verifikationsliggaren.
    ///
    /// <para><b>⚠️⚠️ MEDLEMSAVGIFTEN ÄR KLUBBENS STÖRSTA INTÄKTSPOST OCH NÅDDE INTE BOKFÖRINGEN.</b>
    /// Avgiftsmodulen skeppades juli 2026 med egen tabell (<c>MembershipFeeCharge</c>) och egen
    /// betalvägg; <c>MarkPaid</c> satte en status och där tog det slut. Mätt 2026-09-22: <b>noll</b>
    /// <c>Ledger</c>-referenser i hela avgiftsmodulen. Kassörens resultaträkning saknade alltså den
    /// post som är störst av alla, utan att något sa ifrån.</para>
    ///
    /// <para><b>⚠️ "Bokförd" är HÄRLETT, aldrig lagrat.</b> En avgift är bokförd om det finns en
    /// verifikation med <c>SourceType = membership-fee</c> och <c>SourceId = chargeId</c>. Det
    /// betyder ingen ny kolumn, ingen migrering — och framför allt ingen flagga som kan glömmas
    /// och lämna en verifikation som tyst aldrig blir av. Samma princip som kön "att bokföra".</para>
    ///
    /// <para><b>⚠️ Bokföring är opt-in.</b> En förening som inte är <c>FullLedger</c> ska kunna ta
    /// emot medlemsavgifter precis som förut; bron gör då ingenting och säger ingenting.</para>
    /// </summary>
    public class LedgerMembershipFeeBridge
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly LedgerPostingService _posting;
        private readonly ILogger<LedgerMembershipFeeBridge> _logger;

        public LedgerMembershipFeeBridge(
            IUmbracoDatabaseFactory databaseFactory,
            LedgerPostingService posting,
            ILogger<LedgerMembershipFeeBridge> logger)
        {
            _databaseFactory = databaseFactory;
            _posting = posting;
            _logger = logger;
        }

        /// <summary>
        /// Läget för klubbens medlemsavgifter: vad som inte kommit in, och vad som kommit in men
        /// inte bokförts.
        /// </summary>
        public MembershipFeeLedgerStatus Summarise(int clubId, int? year = null)
        {
            var status = new MembershipFeeLedgerStatus { Year = year ?? DateTime.Today.Year };

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var charges = db.Fetch<MembershipFeeCharge>(
                    @"SELECT * FROM dbo.MembershipFeeCharge
                       WHERE ClubId = @0 AND Year = @1
                         AND (HouseholdCoveredByChargeId IS NULL)",
                    clubId, status.Year);

                // ⚠️ Familjemedlemmar som täcks av huvudmedlemmens avgift filtreras bort ovan.
                // Räknas de med blir både kravet och intäkten dubblerad — hushållet betalar EN gång.
                var unpaid = charges.Where(c => c.PaymentStatus != "Paid").ToList();
                status.UnpaidCount = unpaid.Count;
                status.UnpaidAmount = unpaid.Sum(c => c.Amount);

                var paid = charges.Where(c => c.PaymentStatus == "Paid").ToList();
                if (paid.Count == 0) return status;

                var postedIds = PostedChargeIds(db, paid.Select(c => c.Id));

                var unposted = paid.Where(c => !postedIds.Contains(c.Id)).ToList();
                status.UnpostedCount = unposted.Count;
                status.UnpostedAmount = unposted.Sum(c => c.Amount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte sammanfatta medlemsavgifterna för klubb {ClubId}.", clubId);
            }

            return status;
        }

        /// <summary>
        /// Bokför en betald medlemsavgift. <b>Idempotent</b> — körs den två gånger händer ingenting
        /// andra gången.
        ///
        /// <para><b>⚠️ Får ALDRIG fälla betalningen.</b> Anropas från <c>MarkPaid</c>, och att en
        /// klubb inte skulle kunna kvittera en mottagen avgift för att liggaren säger ifrån vore
        /// att låta bokföringen stoppa verkligheten. Går postningen inte igenom loggas det och
        /// avgiften hamnar i "att bokföra" i stället.</para>
        /// </summary>
        /// <returns>Verifikationens id, eller null när ingenting bokfördes.</returns>
        public int? PostCharge(int chargeId, int byMemberId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();


                var charge = db.SingleOrDefault<MembershipFeeCharge>(
                    "SELECT * FROM dbo.MembershipFeeCharge WHERE Id = @0", chargeId);

                if (charge is null) return null;
                if (charge.PaymentStatus != "Paid") return null;

                // Ett hushållstäckt krav har inget eget belopp att bokföra.
                if (charge.HouseholdCoveredByChargeId is not null) return null;
                if (charge.Amount <= 0) return null;

                // ⚠️ Spärren mot dubbelbokföring, härledd ur liggaren själv.
                if (PostedChargeIds(db, new[] { charge.Id }).Count > 0) return null;

                // LEDGER-SEAM-OK: medlemsavgifter finns BARA i den levande liggaren. En
                // sandlada har inga MembershipFeeCharge-rader att brygga, och att gora bryggan
                // schemamedveten hade antytt att den kan kora mot en sandlada.
                var settings = db.FirstOrDefault<LedgerIssuerSettings>(
                    "SELECT * FROM dbo.LedgerIssuerSettings WHERE IssuerType = @0 AND IssuerId = @1",
                    DocumentOwnerType.Club, charge.ClubId);

                if (!LedgerIssuerShape.KeepsBooks(settings?.Shape)) return null;

                // Betaldagen är bokföringsdagen — kontantmetoden. Saknas den (gammal rad) faller
                // vi tillbaka på skapandedatumet, aldrig på i dag.
                var date = (charge.PaidDate ?? charge.CreatedDate).Date;

                var result = _posting.Post(new LedgerPostingRequest
                {
                    IssuerType = DocumentOwnerType.Club,
                    IssuerId = charge.ClubId,
                    AccountingDate = date,
                    EventDate = date,
                    Description = $"Medlemsavgift {charge.Year}",
                    CounterpartyId = charge.MemberId,
                    CounterpartyName = charge.MemberName,
                    // ⚠️ SourceType/SourceId ÄR spärren mot dubbelbokföring. Ändras de här måste
                    // PostedChargeIds ändras i samma andetag.
                    SourceType = LedgerSourceType.MembershipFee,
                    SourceId = charge.Id,
                    CreatedByMemberId = byMemberId,
                    Lines = new List<LedgerPostingLine>
                    {
                        new() { Role = LedgerAccountRoles.BankAccount, Debit = charge.Amount, VatRate = 0 },
                        new()
                        {
                            Role = LedgerAccountRoles.RevenueMembershipFee,
                            Credit = charge.Amount,
                            Text = charge.MemberName
                        }
                    }
                });

                if (!result.Success)
                {
                    _logger.LogWarning(
                        "Medlemsavgift {ChargeId} kunde inte bokföras: {Fel}. Avgiften är betald och "
                        + "hamnar i \"att bokföra\".", chargeId, result.Error);
                    return null;
                }

                _logger.LogInformation(
                    "Medlemsavgift {ChargeId} bokförd som verifikation {EntryId}.", chargeId, result.EntryId);

                return result.EntryId;
            }
            catch (Exception ex)
            {
                // ⚠️ Sväljs MED FLIT — se metodens sammanfattning. Betalningen står kvar som betald.
                _logger.LogError(ex,
                    "Bokföringen av medlemsavgift {ChargeId} fallerade. Avgiften är betald och "
                    + "kan bokföras i efterhand.", chargeId);
                return null;
            }
        }

        /// <summary>
        /// Bokför alla betalda men obokförda avgifter för klubben och året.
        /// </summary>
        /// <returns>Hur många som bokfördes.</returns>
        public int PostPending(int clubId, int year, int byMemberId)
        {
            List<int> ids;

            using (var db = _databaseFactory.CreateDatabase())
            {
                var paid = db.Fetch<MembershipFeeCharge>(
                    @"SELECT * FROM dbo.MembershipFeeCharge
                       WHERE ClubId = @0 AND Year = @1 AND PaymentStatus = 'Paid'
                         AND HouseholdCoveredByChargeId IS NULL",
                    clubId, year);

                if (paid.Count == 0) return 0;

                var posted = PostedChargeIds(db, paid.Select(c => c.Id));
                ids = paid.Where(c => !posted.Contains(c.Id)).Select(c => c.Id).ToList();
            }

            var count = 0;
            foreach (var id in ids)
            {
                if (PostCharge(id, byMemberId) is not null) count++;
            }

            return count;
        }

        /// <summary>
        /// Vilka av avgifterna som redan har en verifikation.
        ///
        /// <para>⚠️ <c>IN</c>-listan byggs av id:n direkt i SQL:en och inte som parametrar — en
        /// klubb med över ~2100 betalda avgifter hade annars slagit i parametertaket. Talen är
        /// heltal ur databasen, aldrig indata.</para>
        /// </summary>
        private static HashSet<int> PostedChargeIds(
            Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, IEnumerable<int> chargeIds)
        {
            var ids = chargeIds.ToList();
            if (ids.Count == 0) return new HashSet<int>();

            return db.Fetch<int>(
                    // LEDGER-SEAM-OK: samma skal som ovan - avgiftsbryggan ar levande-bara.
                    $@"SELECT SourceId FROM dbo.LedgerJournalEntry
                        WHERE SourceType = @0 AND SourceId IN ({string.Join(",", ids)})",
                    LedgerSourceType.MembershipFee)
                .ToHashSet();
        }
    }

    /// <summary>Medlemsavgifternas läge, sett från liggaren.</summary>
    public class MembershipFeeLedgerStatus
    {
        public int Year { get; set; }

        /// <summary>Krav som inte kommit in.</summary>
        public int UnpaidCount { get; set; }

        public decimal UnpaidAmount { get; set; }

        /// <summary>Betalda avgifter som ännu inte blivit verifikationer.</summary>
        public int UnpostedCount { get; set; }

        public decimal UnpostedAmount { get; set; }
    }
}
