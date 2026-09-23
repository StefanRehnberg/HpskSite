using NPoco;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// En revisors åtkomst till en förenings räkenskaper.
    ///
    /// <para><b>⚠️⚠️ ETT EGET OBJEKT, INTE ETT STYRELSEUPPDRAG.</b> Revisorn granskar styrelsen och
    /// sitter därför inte i den — <c>BoardRoles</c> ger Revisor <c>IsBoardMember = 0</c> med flit.
    /// Den flaggan styr dessutom vilka som seedas som närvarande på styrelsemöten och
    /// <b>räknas i beslutsförheten</b>; att vidga den för att lösa läsrätten hade gjort revisorn
    /// beslutsför i styrelsen. Det är ett allvarligare fel än det man löste.</para>
    ///
    /// <para><b>⚠️ Klubbrevisorn har oftast inget konto hos oss.</b> Det är hela skälet att
    /// åtkomsten börjar med en INBJUDAN och inte med en roll: <c>BoardRoles</c> nycklar på
    /// <c>MemberId</c>, och en utomstående revisor finns inte där att peka på.
    /// <see cref="MemberId"/> är därför null fram till att inbjudan tagits emot.</para>
    ///
    /// <para><b>⚠️ Läsning, aldrig skrivning.</b> Grinden ligger i <c>LedgerAccessService</c> och
    /// ger <c>LedgerAccess.Read</c>. En revisor som kan bokföra granskar sitt eget arbete.</para>
    /// </summary>
    [TableName("LedgerAuditorGrant")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class LedgerAuditorGrant
    {
        public int Id { get; set; }

        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int OwnerType { get; set; }

        /// <summary>
        /// Föreningens riktiga nod-id.
        /// <para><b>⚠️ Aldrig ett utställar-id.</b> En sandlåda har negativt id och är ingen
        /// förening att vara revisor för.</para>
        /// </summary>
        public int OwnerId { get; set; }

        public string Email { get; set; } = "";

        /// <summary>Vad föreningen kallade revisorn i inbjudan.</summary>
        public string Name { get; set; } = "";

        /// <summary>Null tills inbjudan tagits emot. Se klassens sammanfattning.</summary>
        public int? MemberId { get; set; }

        /// <summary>
        /// SHA-256 av inbjudningstoken, hex.
        /// <para><b>⚠️⚠️ ALDRIG TOKEN SJÄLV.</b> Länken är en bärarnyckel — ligger den i klartext
        /// räcker en läsning av databasen för att ge sig ut för revisorn. Samma resonemang som
        /// lösenord, och samma som vapenregistrets etikettkoder inte är gissningsbara.</para>
        /// </summary>
        public string TokenHash { get; set; } = "";

        public int InvitedByMemberId { get; set; }

        public DateTime InvitedUtc { get; set; }

        /// <summary>
        /// När åtkomsten upphör.
        ///
        /// <para><b>⚠️ 13 MÅNADER, inte 12</b> (Stefan 2026-09-23). Uppdraget löper till nästa
        /// årsmöte, och det kan ligga något senare året efter. En länk som dör mitt i
        /// granskningen är värre än en som måste förnyas.</para>
        /// </summary>
        public DateTime ExpiresUtc { get; set; }

        public DateTime? AcceptedUtc { get; set; }

        public DateTime? RevokedUtc { get; set; }

        public int? RevokedByMemberId { get; set; }

        public string? RevokeReason { get; set; }

        /// <summary>Senaste gången revisorn öppnade räkenskaperna. Föreningen ska kunna se det.</summary>
        public DateTime? LastSeenUtc { get; set; }

        [Ignore]
        public bool IsAccepted => AcceptedUtc.HasValue;

        [Ignore]
        public bool IsRevoked => RevokedUtc.HasValue;

        /// <summary>
        /// Gäller åtkomsten just nu?
        ///
        /// <para><b>⚠️ TRE villkor, och alla tre behövs:</b> inte återkallad, inte utgången, och
        /// mottagen. En inbjudan som ingen öppnat är inte en åtkomst — den är ett erbjudande.</para>
        /// </summary>
        [Ignore]
        public bool IsActive =>
            !IsRevoked && IsAccepted && ExpiresUtc > DateTime.UtcNow;

        /// <summary>
        /// Går inbjudningslänken fortfarande att ta emot?
        /// <para>⚠️ Skild från <see cref="IsActive"/>: en oöppnad inbjudan är inte aktiv men
        /// fortfarande giltig, och en redan mottagen länk ska inte kunna tas emot igen av någon
        /// annan.</para>
        /// </summary>
        [Ignore]
        public bool CanBeAccepted =>
            !IsRevoked && !IsAccepted && ExpiresUtc > DateTime.UtcNow;

        /// <summary>Läget i klartext, för föreningens lista.</summary>
        [Ignore]
        public string StatusLabel =>
            IsRevoked ? "Återkallad"
            : ExpiresUtc <= DateTime.UtcNow ? "Utgången"
            : IsAccepted ? "Aktiv"
            : "Inbjuden, inte öppnad";
    }
}
