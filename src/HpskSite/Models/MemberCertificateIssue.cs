using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A single föreningsintyg issued by a club to a member — the record of a licence-support
    /// certificate handed out for a weapon-licence application. Issuing is a club/board act.
    /// </summary>
    [TableName("MemberCertificateIssue")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MemberCertificateIssue
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int ClubId { get; set; }
        public DateTime IssuedDate { get; set; }
        public string Purpose { get; set; } = string.Empty;   // e.g. "Vapenlicens", "Förnyelse"
        public string? Description { get; set; }               // weapon/ändamål detail
        public int? IssuedByMemberId { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// Hela det utfärdade intyget som JSON (<see cref="ForeningsintygDocument"/>).
        ///
        /// <b>Ett föreningsintyg är ett juridiskt dokument styrelsen undertecknat.</b> Varje fält på
        /// blanketten kan ändras efteråt — medlemmen flyttar, klubben byter ordförande, en
        /// resultatrad rättas, ett märke makuleras — så en återutskrift ur DAGENS data visar något
        /// annat än det som skrevs under, utan att något säger ifrån. Snapshotten gör
        /// återutskriften till en återgivning i stället för en ny beräkning.
        ///
        /// <b>NULL = utfärdat innan snapshot fanns.</b> Sådana rader syns i loggen men kan inte
        /// skrivas ut; läsvägen ska säga just det och aldrig tyst bygga ett nytt intyg.
        /// </summary>
        public string? Snapshot { get; set; }

        // Display-only properties (not mapped to DB columns)
        [ResultColumn]
        public string? MemberName { get; set; }

        [ResultColumn]
        public string? IssuedByName { get; set; }
    }
}
