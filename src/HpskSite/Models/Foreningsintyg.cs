using System.Text.Json;
using System.Text.Json.Serialization;

namespace HpskSite.Models
{
    /// <summary>
    /// Ett fält på Polisens blankett <b>Föreningsintyg PM 551.24</b> som hämtas ur medlemsregistret
    /// och alltså inte skrivs in vid utfärdandet.
    ///
    /// <b>Katalogen finns för att markeringen i medlemmens profil och intygets ifyllnad ska läsa
    /// SAMMA lista.</b> Skrivs de på två ställen kommer profilen förr eller senare att markera fält
    /// intyget inte behöver, eller — värre — sluta markera ett fält intyget kräver. Lägg till ett
    /// registerfält HÄR och båda ytorna får det.
    /// </summary>
    public class ForeningsintygField
    {
        /// <summary>Egenskapens alias på <c>hpskMember</c>, eller <see cref="NativeEmail"/>.</summary>
        public string Alias { get; init; } = "";

        /// <summary>Etikett som blanketten använder — inte vår egen formulering.</summary>
        public string FormLabel { get; init; } = "";

        /// <summary>Kort förklaring till medlemmen om varför fältet behövs.</summary>
        public string Why { get; init; } = "";

        /// <summary>
        /// Falskt för fält blanketten klarar sig utan. <b>Det här är ingen spärr</b> — ingenting
        /// hindrar att ett intyg utfärdas med luckor (Stefans beslut 2026-09-01: det ligger i
        /// medlemmens eget intresse att fylla i). Flaggan styr bara hur fältet markeras.
        /// </summary>
        public bool Required { get; init; } = true;

        /// <summary>E-posten är medlemmens inloggning, inte en doctype-egenskap.</summary>
        public const string NativeEmail = "__email";
    }

    /// <summary>
    /// Katalogen över registerfält blanketten behöver, plus reglerna för när ett värde duger.
    /// </summary>
    public static class ForeningsintygFields
    {
        /// <summary>
        /// Personuppgiftsblocket på sidan 1. Ordningen är blankettens.
        ///
        /// ⚠️ <b>Aliasen är kontrollerade mot doctypen</b> (2026-09-01): mobilnumret heter
        /// <c>phoneNumber</c> och fasta telefonen <c>landlinePhone</c> — det finns ingen
        /// <c>mobilePhone</c>, och <c>phone</c> existerar inte alls. En felstavad alias är en TYST
        /// no-op i Umbraco, så den här listan är inte en plats att gissa på.
        /// </summary>
        public static readonly ForeningsintygField[] Personal =
        {
            new() { Alias = "lastName",     FormLabel = "Efternamn",   Why = "Står på intyget som sökandens efternamn." },
            new() { Alias = "firstName",    FormLabel = "Tilltalsnamn", Why = "Står på intyget som sökandens tilltalsnamn." },
            new() { Alias = "personNumber", FormLabel = "Personnummer", Why = "Polisen identifierar sökanden på personnummer. Måste vara fullständigt (12 siffror)." },
            new() { Alias = "address",      FormLabel = "Adress",      Why = "Sökandens postadress." },
            new() { Alias = "postalCode",   FormLabel = "Postnummer",  Why = "Sökandens postadress." },
            new() { Alias = "city",         FormLabel = "Ort",         Why = "Sökandens postadress." },
            new() { Alias = ForeningsintygField.NativeEmail, FormLabel = "E-postadress", Why = "Kontaktuppgift på intyget." },
            new() { Alias = "phoneNumber",  FormLabel = "Telefon (mobil)", Why = "Kontaktuppgift på intyget." },
            new() { Alias = "landlinePhone", FormLabel = "Telefon", Why = "Fast telefon. Behövs inte om mobilnummer finns.", Required = false }
        };

        /// <summary>Aliasen medlemmen själv kan rätta på Min sida → Profil. Används av markeringen
        /// där; <c>landlinePhone</c> och e-post redigeras på andra sätt och ingår därför inte.</summary>
        public static readonly string[] SelfServiceAliases =
        {
            "firstName", "lastName", "personNumber", "address", "postalCode", "city", "phoneNumber"
        };

        /// <summary>
        /// Duger värdet? Personnummer har en egen regel — <b>12 siffror</b>, samma som importens
        /// <c>IsPnrComplete</c>, för annars skulle profilen kalla ett tiosiffrigt nummer komplett
        /// medan importen flaggar det som ofullständigt.
        /// </summary>
        public static bool HasUsableValue(string alias, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (alias == "personNumber") return value.Count(char.IsDigit) == 12;
            return true;
        }
    }

    /// <summary>
    /// Blanketten <b>Föreningsintyg PM 551.24</b> (Ver. 2019-01-18/11) som data.
    ///
    /// <b>Två sorters fält, med olika regler — det är modellens ryggrad:</b>
    /// <list type="bullet">
    /// <item><b>Registerfält</b> läses ur medlemsregistret, klubben, styrelsen och märkesliggaren.
    /// De skrivs ALDRIG in vid utfärdandet och skrivs aldrig tillbaka: personuppgifterna ligger på
    /// det delade inloggningskontot, och en medlem kan tillhöra flera klubbar — en klubbs
    /// intygsutfärdande får inte mutera data en annan klubb förlitar sig på.</item>
    /// <item><b>Intygsfält</b> skrivs av intygaren vid varje utfärdande och lever bara i intygets
    /// snapshot. Vapenuppgifter, §5/§6, behov, ort och datum hör dit.</item>
    /// </list>
    ///
    /// ⚠️ <b>Gränsen följer INTE blankettens sektioner.</b> Krysset "Guldmärke – Svenska
    /// Pistolskytteförbundet" är ett registerfält (märkesliggaren), men <see cref="SkjutprovDatum"/>
    /// kan inte vara det: <c>MemberBadge.AchievedDate</c> stämplas med dagens datum även för ett
    /// märke från 1998, så bara ÅRET är fakta. Datumet skrivs därför in.
    ///
    /// ⚠️ <b>Ingenting här kryssas av oss.</b> §5/§6 och skjutskicklighetsraderna är styrelsens
    /// juridiska intygande, och vårt underlag är till stor del självrapporterat. Aktiviteten visas
    /// som underlag intill rutorna — den sätter dem inte.
    /// </summary>
    public class ForeningsintygDocument
    {
        /// <summary>Blankettens beteckning, skrivs ut i sidfoten så mottagaren ser vad det är.</summary>
        public const string FormReference = "PM 551.24 Ver. 2019-01-18/11";

        // ── Metadata (inte fält på blanketten) ───────────────────────

        public int MemberId { get; set; }
        public int ClubId { get; set; }

        /// <summary>Verksamhetsåret aktivitetsunderlaget visades för när intyget utfärdades.</summary>
        public int ActivityYear { get; set; }

        /// <summary>Sätts när intyget faktiskt utfärdas; null i ett utkast.</summary>
        public DateTime? IssuedAt { get; set; }

        public int? IssuedByMemberId { get; set; }

        // ── Personuppgifter (REGISTERFÄLT) ───────────────────────────

        public string Efternamn { get; set; } = "";
        public string Tilltalsnamn { get; set; } = "";
        public string Personnummer { get; set; } = "";
        public string Adress { get; set; } = "";
        public string Postnummer { get; set; } = "";
        public string Ort { get; set; } = "";
        public string EPostadress { get; set; } = "";
        public string Telefon { get; set; } = "";
        public string TelefonMobil { get; set; } = "";

        // ── Skytteförening och aktivt deltagande ─────────────────────

        /// <summary>REGISTERFÄLT — klubbens <c>orgNumber</c>.</summary>
        public string Organisationsnummer { get; set; } = "";

        /// <summary>REGISTERFÄLT — klubbens namn.</summary>
        public string Skytteforening { get; set; } = "";

        /// <summary>REGISTERFÄLT — <c>ClubMembership.MemberSince</c> för (medlem, denna klubb), med
        /// medlemsegenskapen <c>memberSince</c> som reserv.</summary>
        public string MedlemSedan { get; set; } = "";

        /// <summary>INTYGSFÄLT. Styrelsens kryss, aldrig vårt.</summary>
        public bool AktivtDeltagitSexManader { get; set; }

        /// <summary>INTYGSFÄLT. §5 — för den som inte tidigare har enhandsvapen för målskjutning
        /// (snitt minst två gånger per månad de senaste sex månaderna).</summary>
        public bool AktivMedlemParagraf5 { get; set; }

        /// <summary>INTYGSFÄLT. §6 — för den som sedan tidigare innehar enhandsvapen
        /// (snitt minst en gång per månad de senaste sex månaderna).</summary>
        public bool AktivMedlemParagraf6 { get; set; }

        /// <summary>INTYGSFÄLT — "Aktivt medlemskap kan visas genom".</summary>
        public bool VisasGenomLoggbok { get; set; }
        public bool VisasGenomSarskildaSkal { get; set; }

        // ── Auktoriserat förbund ─────────────────────────────────────

        /// <summary>
        /// Blanketten listar tolv förbund och säger "Markera endast det förbund vars
        /// tävlingsgren/skytteform vapnet avses användas". För oss är det Svenska
        /// Pistolskytteförbundet, förkryssat men ändringsbart — en klubb kan i teorin intyga för en
        /// gren i ett annat förbund.
        /// </summary>
        public string Forbund { get; set; } = ForbundSpsf;

        public const string ForbundSpsf = "Svenska Pistolskytteförbundet";

        public static readonly string[] AllaForbund =
        {
            "Jägarnas riksförbund/Landsbygdens jägare",
            "Svenska Armborst Unionen",
            "Svenska Dynamiska Sportskytteförbundet",
            "Svenska Jägareförbundet",
            "Sveriges Metallsilhuettförbund",
            "Svenska Mångkampsförbundet",
            ForbundSpsf,
            "Svenska Skidskytteförbundet",
            "Svenska Skyttesportförbundet",
            "Svenska Svartkruts SkytteFederationen",
            "Svenska Westernskytteförbundet",
            "Annat förbund"
        };

        /// <summary>Fritext när <see cref="Forbund"/> är "Annat förbund".</summary>
        public string AnnatForbund { get; set; } = "";

        // ── Föreningsintyget gäller (vapnet) — INTYGSFÄLT ────────────
        //
        // Inget av det här finns i registret. Kedjans punkt 5 (krypterat vapeninnehav) är den enda
        // vägen dit, och när den byggs måste ett vapen bära VILKET FÖRBUNDS VERKSAMHET det används
        // i — blanketten skopar antalet till "det förbund som anges ovan", och samma vapen kan
        // lagligen användas i flera förbunds grenar. Alltså en relation, inte en kolumn.

        /// <summary>Pistol / Revolver / Kulgevär / Hagelgevär / Annat.</summary>
        public string Vapentyp { get; set; } = "";

        public static readonly string[] AllaVapentyper = { "Pistol", "Revolver", "Kulgevär", "Hagelgevär", "Annat" };

        public string AnnanVapentyp { get; set; } = "";
        public string Fabrikat { get; set; } = "";
        public string KaliberPatronbeteckning { get; set; } = "";
        public string Modell { get; set; } = "";
        public string Piplangd { get; set; } = "";

        /// <summary>"Föreningen bedriver skytteverksamhet i denna vapengrupp/skytteform".</summary>
        public bool ForeningenBedriverVerksamhet { get; set; }

        public string VapengruppSkytteform { get; set; } = "";

        // ── Behov av skjutvapen kan visas genom — INTYGSFÄLT ─────────

        public bool BehovInternaTavlingar { get; set; }
        public bool BehovExternaTavlingar { get; set; }
        public bool BehovLoggbok { get; set; }
        public bool BehovAnnat { get; set; }
        public string BehovAnnatText { get; set; } = "";

        // ── Behov av enhandsvapen — INTYGSFÄLT ───────────────────────
        //
        // Ska enligt blanketten inte anges vid det FÖRSTA enhandsvapnet.

        public bool IntygetAvserYtterligareEnhandsvapen { get; set; }
        public bool IntygetAvserFornyelse { get; set; }

        /// <summary>"minst två gånger under de senaste sex månaderna med respektive tidigare
        /// innehavt enhandsvapen". Kräver PER-VAPEN-användning, som vi inte spårar — träningsloggen
        /// bär vapenKLASS (A/B/C/R), inte ett individuellt vapen.</summary>
        public bool Tranat2GangerSexManader { get; set; }

        /// <summary>"minst fyra gånger per år under de senaste två åren med sökt vapen". Samma
        /// begränsning som ovan.</summary>
        public bool Tranat4GangerPerArTvaAr { get; set; }

        public bool EnhandsvapenAnnatBilaga { get; set; }

        /// <summary>"Sökanden har sedan tidigare ___ st skjutvapen för målskjutning i den verksamhet
        /// som bedrivs av det förbund som anges ovan". FÖRBUNDSSKOPAT, inte totalt innehav.</summary>
        public int? AntalVapenSedanTidigare { get; set; }

        // ── Skjutskicklighet ─────────────────────────────────────────

        /// <summary>INTYGSFÄLT — "Sökanden har uppfyllt nedanstående fordringar för skjutskicklighet".</summary>
        public bool UppfyllerSkjutskicklighet { get; set; }

        /// <summary>INTYGSFÄLT. Blanketten kräver ett DATUM; märkesliggaren kan bara belägga ett år
        /// (se klasskommentaren). Förifylls därför inte.</summary>
        public string SkjutprovDatum { get; set; } = "";

        /// <summary>REGISTERFÄLT-förslag — sant när medlemmen har Pistolskyttemärket i guld i
        /// liggaren. Fortfarande styrelsens kryss; vi föreslår.</summary>
        public bool GuldmarkeSpsf { get; set; }

        /// <summary>Guldmärkets nationella registreringsnummer, när det finns. Inte ett fält på
        /// blanketten men det som gör krysset kontrollerbart, så det skrivs ut som en notering.</summary>
        public string GuldmarkeNummer { get; set; } = "";

        /// <summary>Året liggaren belägger guldmärket. Underlag till intygaren, inte blankettens datum.</summary>
        public int? GuldmarkeAr { get; set; }

        public bool SilvermarkeSkyttesport { get; set; }
        public bool GuldmarkeAutomatvapenSkyttesport { get; set; }
        public bool SilvermarkeDynamiska { get; set; }
        public bool SkjutskicklighetAnnat { get; set; }
        public string SkjutskicklighetAnnatText { get; set; } = "";

        // ── Underskrift ──────────────────────────────────────────────

        /// <summary>INTYGSFÄLT — dagens datum vid utfärdandet.</summary>
        public string UnderskriftDatum { get; set; } = "";

        /// <summary>INTYGSFÄLT — orten intyget skrivs under på. Förifylls med klubbens ort som
        /// FÖRSLAG; styrelsen kan skriva under någon annanstans.</summary>
        public string UnderskriftOrt { get; set; } = "";

        /// <summary>REGISTERFÄLT-förslag — ordförandens namn ur styrelseregistret.</summary>
        public string Namnfortydligande { get; set; } = "";

        /// <summary>REGISTERFÄLT-förslag — <c>BoardRole.DisplayTitle</c>, som hanterar egen titel.</summary>
        public string BefattningFunktion { get; set; } = "";

        /// <summary>REGISTERFÄLT-förslag. ⚠️ <c>BoardRoleService</c> resolvar bara NAMNET — kontakten
        /// måste hämtas per medlem, se tjänsten.</summary>
        public string UnderskriftTelefon { get; set; } = "";
        public string UnderskriftTelefonMobil { get; set; } = "";
        public string UnderskriftEPost { get; set; } = "";

        // ── Härledda hjälpvärden för utskriften ──────────────────────

        [JsonIgnore]
        public string HelaNamnet => $"{Tilltalsnamn} {Efternamn}".Trim();

        [JsonIgnore]
        public string PostadressRad => $"{Postnummer} {Ort}".Trim();

        /// <summary>
        /// Registerfält som saknar värde, med blankettens etikett. Ingen spärr — den som utfärdar
        /// ska bara se luckorna INNAN hen skriver under, i stället för att upptäcka dem när Polisen
        /// avvisar intyget.
        /// </summary>
        public List<string> SaknadeRegisterfalt { get; set; } = new();

        // ── Snapshot ─────────────────────────────────────────────────

        private static readonly JsonSerializerOptions SnapshotJson = new()
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>
        /// Serialisering för <c>MemberCertificateIssue.Snapshot</c>. Ett utfärdat intyg är ett
        /// juridiskt dokument och måste kunna skrivas ut igen <b>exakt som det undertecknades</b> —
        /// aldrig återberäknat ur dagens data, som hunnit ändras.
        /// </summary>
        public string ToSnapshot() => JsonSerializer.Serialize(this, SnapshotJson);

        /// <summary>Läser tillbaka en snapshot. Null när den saknas eller inte går att tolka —
        /// anroparen ska då säga att intyget inte kan återges, inte tysta bygga ett nytt ur dagens data.</summary>
        public static ForeningsintygDocument? FromSnapshot(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<ForeningsintygDocument>(json, SnapshotJson); }
            catch { return null; }
        }
    }
}
