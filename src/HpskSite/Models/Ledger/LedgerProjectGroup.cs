using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En projektgrupp — föreningens egen indelning av sina projekt, <b>ett sparat urval</b>.
    ///
    /// <para><b>⚠️⚠️ INTE EN BEHÅLLARE, OCH DÄRFÖR INTE <c>ParentProjectId</c>.</b> En förälder är
    /// ett träd: varje projekt tillhör exakt en grupp. Men "vad kostade klubbtävlingarna?" och
    /// "vad kostade fältskyttet?" är två legitima frågor över ÖVERLAPPANDE mängder, och med en
    /// förälder kan man bara ställa den ena — man måste riva den för att bygga den andra. Därför
    /// många-till-många genom <see cref="LedgerProjectGroupMember"/>. Förenkla aldrig tillbaka.</para>
    ///
    /// <para><b>⚠️ RÖR ALDRIG <see cref="LedgerJournalEntryLine"/>.</b> Märkningen på raden är
    /// fryst och går inte att ändra; det är just därför grupperingen måste ligga UTANFÖR den. Att
    /// ändra en grupp är en radändring i en tabell bokföringen inte känner till — och därför får
    /// den ändras för alltid.</para>
    ///
    /// <para><b>Gruppen har ingen egen bokföring, ingen egen budget och ingen egen livscykel.</b>
    /// Den summerar sina medlemmar, punkt. Fick den ett eget saldo skulle den börja konkurrera med
    /// projektet om vad som är sant.</para>
    ///
    /// <para>⚠️ Ligger ändå i liggarens schemasöm (dbo/sbx) — inte för att den är bokföring, utan
    /// för att den pekar på projekt, och en sandlådas projekt finns bara i <c>sbx</c>.</para>
    /// </summary>
    [TableName("LedgerProjectGroup")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerProjectGroup
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        /// <summary>Föreningens eget ord: "Klubbtävlingar 2026", "Fältskytte". Unikt per utställare.</summary>
        public string Name { get; set; } = "";

        public string? Description { get; set; }

        /// <summary>
        /// Satt när gruppen skapats av systemet ur något som redan grupperar — i dag bara en
        /// tävlingsSERIE (<see cref="LedgerProjectSource.Series"/>). Null för en grupp kassören
        /// skapat själv.
        /// </summary>
        public string? SourceType { get; set; }

        public int? SourceId { get; set; }

        public DateTime CreatedUtc { get; set; }

        public int CreatedByMemberId { get; set; }
    }

    /// <summary>
    /// Att ett projekt ingår i en grupp. Raden ÄGER ingenting — den pekar. Att radera den (eller
    /// gruppen) raderar alltså inget annat, vilket gör det riskfritt att skapa en grupp bara för
    /// att titta.
    /// </summary>
    [TableName("LedgerProjectGroupMember")]
    [PrimaryKey("GroupId,ProjectId", AutoIncrement = false)]
    public class LedgerProjectGroupMember
    {
        public int GroupId { get; set; }

        public int ProjectId { get; set; }

        public DateTime AddedUtc { get; set; }

        /// <summary>0 = lagd av systemet (serien som förvald grupp).</summary>
        public int AddedByMemberId { get; set; }
    }
}
