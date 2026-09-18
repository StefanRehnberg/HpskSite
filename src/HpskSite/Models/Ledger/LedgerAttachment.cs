using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Bilagan till en verifikation — kvittobilden, fakturan, kontoutdraget.
    ///
    /// <para><b>⚠️ FILEN NAMNGES EFTER SITT INNEHÅLL</b> (<see cref="StoredAs"/> = hash + ändelse).
    /// Därmed KAN en bilaga inte tyst bytas ut efter bokföringen: ett nytt innehåll ger ett nytt
    /// namn och en ny rad. Kravet "bilagan ska inte kunna bytas ut" blir en egenskap i stället för
    /// en regel någon måste minnas.</para>
    ///
    /// <para><b>⚠️ Filerna ligger under <c>App_Data</c>, inte i Umbraco media</b> — samma mönster som
    /// <c>PrepDocumentStorage</c>. Publik media är security-by-obscurity, och ett kvitto kan bära
    /// personuppgifter.</para>
    ///
    /// <para><b>Raden raderas aldrig.</b> Sju års bevarande i läsbar form är ett lagkrav, inte en
    /// retentionpolicy vi väljer. ⚠️ Städrutiner och backup måste känna till katalogen: en bilaga
    /// som finns i databasen men inte på disk är en verifikation utan sitt underlag, alltså ingen
    /// verifikation alls.</para>
    /// </summary>
    [TableName("LedgerAttachment")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerAttachment
    {
        public int Id { get; set; }

        public int JournalEntryId { get; set; }

        /// <summary>Originalnamnet, för människan som letar.</summary>
        public string FileName { get; set; } = "";

        /// <summary>Innehållshash + ändelse. Det här är namnet på disk — aldrig originalnamnet.</summary>
        public string StoredAs { get; set; } = "";

        public string ContentType { get; set; } = "";

        public long SizeBytes { get; set; }

        public int UploadedByMemberId { get; set; }

        public DateTime UploadedUtc { get; set; }
    }
}
