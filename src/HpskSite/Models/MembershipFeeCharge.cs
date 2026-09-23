using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// One membership-fee charge per member per year. Mirrors the competition-invoice
    /// two-state model: the payer lodges a claim (PaymentSentDate/By via "Jag har
    /// betalat"), the club admin confirms received (PaymentStatus = "Paid").
    /// See Documentation/MEMBER_DATABASE.md §4.
    /// </summary>
    [TableName("MembershipFeeCharge")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MembershipFeeCharge
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public int Year { get; set; }

        /// <summary>Soft reference to MembershipFeeCategory.Id (null when charged ad hoc).</summary>
        public int? CategoryId { get; set; }

        public decimal Amount { get; set; }

        /// <summary>Pending / Paid / Cancelled.</summary>
        public string PaymentStatus { get; set; } = "Pending";

        // Payer's claim ("Jag har betalat") — never sets Paid.
        public DateTime? PaymentSentDate { get; set; }
        public string? PaymentSentBy { get; set; }

        // Organizer's authoritative "received" state.
        public DateTime? PaidDate { get; set; }
        public int? PaidConfirmedByMemberId { get; set; }

        /// <summary>
        /// Familjeavgift: a non-primary household member's charge points at the primary
        /// member's charge (which carries the actual family amount). Null = billed individually.
        /// </summary>
        public int? HouseholdCoveredByChargeId { get; set; }

        public DateTime CreatedDate { get; set; }

        // ── Kretsavgiften: samma motor, en annan partstyp (2026-09-23) ──────────────────
        //
        // ⚠️⚠️ TVÅ FORMER, OCH DATABASEN HÅLLER ISÄR DEM (CK_MembershipFeeCharge_Shape):
        //    klubb → medlem:  IssuerType 0, ClubId = klubben, MemberId = medlemmen
        //    krets → klubb:   IssuerType 1, RegionId = kretsen, PayerClubId = klubben,
        //                     ClubId = 0 och MemberId = 0
        //    Därför syns en kretsavgift aldrig i en klubbs medlemsavgiftslista, som läser ClubId.
        //
        // ⚠️ Inga speglade IssuerId/PayerId-kolumner. Medlemssammanslagningen flyttar MemberId,
        //    och en kopia av samma uppgift i en annan kolumn hade glidit isär. Använd
        //    IssuerOwnerType/IssuerOwnerId och IsRegionFee nedan i stället för att läsa formen själv.

        /// <summary>0 = klubbens medlemsavgift, 1 = kretsens avgift till en klubb.</summary>
        public int IssuerType { get; set; }

        /// <summary>Kretsens nod-id. Satt bara för en kretsavgift.</summary>
        public int? RegionId { get; set; }

        /// <summary>Klubben som ska betala. Satt bara för en kretsavgift.</summary>
        public int? PayerClubId { get; set; }

        /// <summary>
        /// Antalet medlemmar kravet räknades på. Registrets förslag kan ha rättats av kretsen, och
        /// det tal som faktiskt låg till grund ska gå att läsa i efterhand.
        /// </summary>
        public int? MemberCount { get; set; }

        /// <summary>
        /// När betalningsuppmaningen FÖRST gick ut (mejl, eller betallänken kopierad). Null = inte skickad.
        ///
        /// <para><b>⚠️ En kretsavgift finns INNAN den skickas</b> (Stefan 2026-09-23): kretsen ska kunna
        /// lägga till en lagavgift och titta på mejlet före utskicket, och raden ska se likadan ut före
        /// och efter. Följderna: en oskickad avgift räknas INTE som obetald fordran (ingen har fått en
        /// räkning), och den följer taxan när taxan ändras — en skickad gör det inte.</para>
        /// </summary>
        public DateTime? RequestSentDate { get; set; }

        [Ignore]
        public bool IsRequestSent => RequestSentDate.HasValue;

        /// <summary>
        /// Referensen vid betalning via bankgiro — "KA2026-123" (kretsavgift) / "MA2026-123"
        /// (medlemsavgift). <b>EN plats</b>: betalsidan, bankgiro-QR:en och mejlet visar samma, annars
        /// går en inbetalning inte att para ihop med avgiften. Max 25 tecken (bankgiro-QR:ens iref).
        /// </summary>
        [Ignore]
        public string PaymentReference => $"{(IsRegionFee ? "KA" : "MA")}{Year}-{Id}";

        [Ignore]
        public bool IsRegionFee => IssuerType == MembershipFeeIssuer.Region;

        /// <summary>Utställarens ägartyp i liggarens mening (<see cref="DocumentOwnerType"/>).</summary>
        [Ignore]
        public int IssuerOwnerType => IsRegionFee ? DocumentOwnerType.Region : DocumentOwnerType.Club;

        /// <summary>Utställarens nod-id: kretsen för en kretsavgift, annars klubben.</summary>
        [Ignore]
        public int IssuerOwnerId => IsRegionFee ? RegionId ?? 0 : ClubId;

        /// <summary>Kravets rader (grundavgift, per medlem, tillägg). Tom för en medlemsavgift.</summary>
        [Ignore]
        public List<MembershipFeeChargeLine> Lines { get; set; } = new();

        /// <summary>Den betalande klubbens namn, för en kretsavgift.</summary>
        [ResultColumn]
        public string? PayerClubName { get; set; }

        // Display-only (resolved in the service, not mapped to DB columns).
        [ResultColumn]
        public string? MemberName { get; set; }

        [ResultColumn]
        public string? MemberEmail { get; set; }
    }
}
