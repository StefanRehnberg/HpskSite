using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Rätten att arbeta med föreningens ekonomi, given till någon som inte är kassör.
    ///
    /// <para><b>⚠️⚠️ VARFÖR DEN FINNS (Stefan 2026-09-24):</b> skrivrätten följer kassörsuppdraget,
    /// men kassören kan vara borta eller ha ont om tid — mitt i året, när det mest handlar om att
    /// bokföra, eller precis när årsredovisningen ska fram. Föreningen måste kunna lösa det utan
    /// att byta kassör.</para>
    ///
    /// <para><b>⚠️ SAMMA RÄTT SOM KASSÖREN</b>, inte en "bara bokföra"-nivå: behovet kan vara
    /// bokslutet lika gärna som kvittona.</para>
    ///
    /// <para><b>Vem som får ge den:</b> kassören, ordföranden, en administratör och sajtadmin — se
    /// <c>LedgerAccessResult.CanManageWriteGrants</c>. Aldrig revisorn, och aldrig den som själv
    /// bara fått rätten här: inga kedjor.</para>
    ///
    /// <para><b>⚠️ Tidsbegränsad med flit.</b> En behörighet som gäller tills någon minns den är
    /// den som ligger kvar när kassören bytts. Förvalet är räkenskapsårets slut plus tre månader,
    /// så att rätten räcker till bokslutet. Raden raderas aldrig — den avslutas.</para>
    ///
    /// <para><b>⚠️ I <c>dbo</c>, aldrig i sandlådan</b> — samma skäl som revisorns uppdrag: man
    /// får rätten i en FÖRENING, och den gäller för föreningens liggare och dess sandlådor.</para>
    /// </summary>
    [TableName("LedgerWriteGrant")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerWriteGrant
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int OwnerType { get; set; }

        /// <summary>Föreningens riktiga nod-id — aldrig ett utställar-id.</summary>
        public int OwnerId { get; set; }

        public int MemberId { get; set; }

        /// <summary>Namnet när rätten gavs — historiken ska gå att läsa även om medlemmen tas bort.</summary>
        public string MemberName { get; set; } = "";

        public int GrantedByMemberId { get; set; }

        public string GrantedByName { get; set; } = "";

        /// <summary>
        /// I vilken egenskap rätten gavs — "Kassör", "Ordförande", "Administratör",
        /// "Sajtadministratör". Kassören ska kunna se att det var ordföranden, inte bara vem.
        /// </summary>
        public string GrantedByRole { get; set; } = "";

        public DateTime GrantedUtc { get; set; }

        /// <summary>Sista dagen rätten gäller, inklusive.</summary>
        public DateTime EndsDate { get; set; }

        /// <summary>Varför — "Kassören föräldraledig", "Hjälper till med bokslutet". Frivillig.</summary>
        public string? Reason { get; set; }

        public DateTime? RevokedUtc { get; set; }

        public int? RevokedByMemberId { get; set; }

        [Ignore]
        public bool IsActive => RevokedUtc is null && EndsDate.Date >= DateTime.Today;
    }
}
