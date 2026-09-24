using NPoco;

namespace HpskSite.Models.CompetitionFees
{
    /// <summary>
    /// Växeln per tävling: den gamla fakturamodellen eller avgifter i liggaren.
    ///
    /// <para><b>⚠️⚠️ OFÖRÄNDERLIG</b> (<c>TR_CompetitionPaymentModel_Final</c>). En tävling är ett
    /// förlopp och får aldrig byta pengamodell mitt i — då låg dess avgifter i två system som ingen
    /// yta visar samtidigt. Beslutas vid FÖRSTA anmälan, inte när tävlingen skapas: en tävling utan
    /// anmälningar vid deploy blir alltså ny även om den skapades förra veckan.</para>
    /// </summary>
    [TableName("CompetitionPaymentModel")]
    [PrimaryKey("CompetitionId", AutoIncrement = false)]
    public class CompetitionPaymentModelRow
    {
        public int CompetitionId { get; set; }

        /// <summary>Ur <see cref="CompetitionPaymentModels"/>.</summary>
        public string Model { get; set; } = CompetitionPaymentModels.Ledger;

        public DateTime DecidedUtc { get; set; }

        public string Reason { get; set; } = "";
    }

    public static class CompetitionPaymentModels
    {
        /// <summary>Den gamla fakturamodellen — fryst historik som aldrig räknas om.</summary>
        public const string Legacy = "legacy";

        /// <summary>Avgifter som begärda betalningar i liggaren, fakturor bara när arrangören buntar.</summary>
        public const string Ledger = "ledger";
    }

    /// <summary>
    /// Arrangörens val: för vilka anmälningstyper klubben får betala i stället för skytten.
    ///
    /// <para>Stefan 2026-09-24: <i>"arrangören ska kunna tillåta det, inställbart för olika
    /// anmälanstyper, ex. klubben får betala för Lag och Juniorer."</i> För en typ som inte är vald
    /// är direktbetalning obligatorisk: anmälan och betalning är ett steg.</para>
    /// </summary>
    [TableName("CompetitionFeeSettings")]
    [PrimaryKey("CompetitionId", AutoIncrement = false)]
    public class CompetitionFeeSettings
    {
        public int CompetitionId { get; set; }

        /// <summary>Kommaseparerat ur <see cref="CompetitionFeeTypes"/>. Tomt = ingen typ.</summary>
        public string ClubPayableTypes { get; set; } = "";

        public DateTime UpdatedUtc { get; set; }
        public int UpdatedByMemberId { get; set; }

        [Ignore]
        public IReadOnlySet<string> ClubPayable => CompetitionFeeTypes.Parse(ClubPayableTypes);
    }

    /// <summary>
    /// "Klubben betalar" för en anmälan — <b>en hint</b> till arrangören, aldrig ett åtagande.
    /// <para>En skytt kan inte binda sin klubb. Valet gör bara att betalsteget hoppas över för de
    /// klasser arrangören tillåtit; arrangören fakturerar sedan klubben i efterhand — eller inte.</para>
    /// </summary>
    [TableName("CompetitionFeeChoice")]
    [PrimaryKey("RegistrationId", AutoIncrement = false)]
    public class CompetitionFeeChoice
    {
        public int RegistrationId { get; set; }
        public int CompetitionId { get; set; }
        public bool ClubPays { get; set; }
        public int ChosenByMemberId { get; set; }
        public DateTime ChosenUtc { get; set; }
    }

    /// <summary>
    /// Anmälningstyperna — samma indelning som avgifterna (grund, junior, lag, stafett).
    /// <para>⚠️ Nycklarna ligger i databasen. Lägg till, döp aldrig om.</para>
    /// </summary>
    public static class CompetitionFeeTypes
    {
        public const string Individual = "individ";
        public const string Junior = "junior";
        public const string Team = "lag";
        public const string Relay = "stafett";

        public static readonly string[] All = { Individual, Junior, Team, Relay };

        public static string Label(string type) => type switch
        {
            Individual => "Individuell anmälan",
            Junior => "Juniorer",
            Team => "Lag",
            Relay => "Stafett",
            _ => type
        };

        public static IReadOnlySet<string> Parse(string? csv)
            => (csv ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => All.Contains(t))
                .ToHashSet();

        /// <summary>Normaliserar en inskickad lista — okända nycklar faller bort, ordningen blir fast.</summary>
        public static string Format(IEnumerable<string>? types)
        {
            var set = (types ?? Array.Empty<string>()).Select(t => t?.Trim() ?? "").ToHashSet();
            return string.Join(",", All.Where(set.Contains));
        }
    }

    /// <summary>
    /// Vilken del av en anmälans avgift en betalningsrad gäller.
    /// <para>⚠️ Nycklarna ligger i databasen (<c>LedgerPayment.FeePart</c>).</para>
    /// </summary>
    public static class CompetitionFeePart
    {
        /// <summary>Hela avgiften — skytten betalar allt, eller hela anmälan är klubbetald.</summary>
        public const string All = "all";

        /// <summary>Skyttens del när klubben betalar resten.</summary>
        public const string Self = "self";

        /// <summary>Den del klubben ska betala — väntar på arrangörens faktura.</summary>
        public const string Club = "club";
    }
}
