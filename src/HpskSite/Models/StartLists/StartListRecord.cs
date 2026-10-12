namespace HpskSite.Models.StartLists
{
    /// <summary>Vilken sorts startlista. Skilde förut bara på doctypen.</summary>
    public enum StartListKind
    {
        /// <summary><c>precisionStartList</c> — grundomgången (precisionsfamiljen, egenbokning) OCH
        /// springskyttets namngivna listor (individuellt och stafett); formen avgörs av konfigurationen.</summary>
        Qualifying,

        /// <summary><c>finalsStartList</c> — en per vapengrupp.</summary>
        Finals,
    }

    /// <summary>
    /// En startlista som DATA — det som i dag ligger på en <c>precisionStartList</c>- eller
    /// <c>finalsStartList</c>-nod under tävlingen (skalplanen fas C2, <c>notes/skalplan-2026-10-12.md</c>).
    ///
    /// <para><b>Läs och skriv aldrig noden direkt</b> — gå genom <c>IStartListStore</c>.</para>
    ///
    /// <para><b>⚠️ Id:t är nodens id och bevaras vid flytten.</b> Klienten skickar det till ett trettiotal
    /// endpoints, bokmärkta stationslänkar bär det (<c>&amp;fsl=</c>), och påminnelseloggen och
    /// kalenderfilernas UID innehåller det.</para>
    ///
    /// <para><see cref="ConfigurationData"/> har fyra JSON-former: precisionens
    /// <c>StartListConfiguration</c>, egenbokningens (samma namn, System.Text.Json), springskyttets
    /// <c>Starters[]</c> och stafettens <c>Teams[]</c> med <c>TeamFormat = "SpringskytteStafett"</c>.</para>
    /// </summary>
    public class StartListRecord
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public StartListKind Kind { get; set; }

        /// <summary>Namnet — "Startlista", "Finalstartlista C" eller springskyttets listnamn. Ger adressens segment.</summary>
        public string Name { get; set; } = "";

        public string ConfigurationData { get; set; } = "";

        /// <summary>Den färdiga HTML:en (cachad rendering av konfigurationen).</summary>
        public string StartListContent { get; set; } = "";

        /// <summary>Officiell (publicerad för skyttarna). Noden: <c>isOfficialStartList</c> resp. <c>isOfficialFinalsStartList</c>.</summary>
        public bool IsOfficial { get; set; }

        public DateTime? GeneratedDate { get; set; }
        public string GeneratedBy { get; set; } = "";
        public string TeamFormat { get; set; } = "";
        public string Notes { get; set; } = "";

        /// <summary>Grundomgången: de frysta klassernas kvalresultat (JSON).</summary>
        public string QualifyingResultsSnapshot { get; set; } = "";

        /// <summary>Finalen: arrangörens val per mästerskapsklass (JSON).</summary>
        public string PerClassConfigData { get; set; } = "";
        public int TotalFinalists { get; set; }
        public int MaxShootersPerTeam { get; set; }
        public int QualificationStartListId { get; set; }

        /// <summary>Ligger under den gamla <c>competitionStartListsHub</c> (nodlagringen). Tävlingen är då hubbens förälder.</summary>
        public bool LegacyHub { get; set; }

        /// <summary>Om listan finns publikt (nodlagringen: publicerad nod; tabellen: alltid).</summary>
        public bool IsPublished { get; set; }

        /// <summary>Ordningen bland tävlingens listor (nodens sorteringsordning). Ordnar listorna som förut.</summary>
        public int SortOrder { get; set; }

        /// <summary>
        /// Adressens segment ur namnet ("startlista", "finalstartlista-c"). Den publika adressen är
        /// tävlingens + segmentet — bygg den alltid så, aldrig ur en nods <c>Url()</c>.
        /// </summary>
        public string UrlSegment { get; set; } = "";

        public bool HasConfiguration => !string.IsNullOrWhiteSpace(ConfigurationData);

        /// <summary>Listans publika adress under tävlingen.</summary>
        public string PublicUrl(string competitionUrl) =>
            string.IsNullOrEmpty(competitionUrl) ? "" : competitionUrl.TrimEnd('/') + "/" + UrlSegment + "/";

        public StartListRecord Clone() => (StartListRecord)MemberwiseClone();
    }

    /// <summary>Hur en skrivning i nodlagringen publiceras. Tabellen har ingen publicering.</summary>
    public enum StartListPublish
    {
        /// <summary>Spara och publicera (det vanliga — de publika sidorna läser publicerat läge).</summary>
        Always,

        /// <summary>Publicera bara om noden redan var publicerad (rättelser som inte ska göra ett utkast publikt).</summary>
        IfPublished,

        /// <summary>Bara spara.</summary>
        Never,
    }

    /// <summary>Nodernas alias — en plats, delad av nodlagringen och flytten.</summary>
    public static class StartListNodes
    {
        public const string QualifyingAlias = "precisionStartList";
        public const string FinalsAlias = "finalsStartList";
        public const string LegacyHubAlias = "competitionStartListsHub";
        public const string QualifyingName = "Startlista";

        public static string Alias(StartListKind kind) => kind == StartListKind.Finals ? FinalsAlias : QualifyingAlias;

        public static StartListKind? KindOf(string? alias) => alias switch
        {
            QualifyingAlias => StartListKind.Qualifying,
            FinalsAlias => StartListKind.Finals,
            _ => null,
        };
    }
}
