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

        /// <summary>
        /// Sant för fält KLUBBEN sätter, inte medlemmen. Markeringen i profilen måste skilja dem åt:
        /// att be medlemmen fylla i något hen saknar rättighet att ändra är en återvändsgränd, och
        /// att inte visa fältet alls gör att medlemmen inte förstår varför intyget blir ofullständigt.
        /// </summary>
        public bool ClubManaged { get; init; }

        /// <summary>E-posten är medlemmens inloggning, inte en doctype-egenskap.</summary>
        public const string NativeEmail = "__email";

        /// <summary>
        /// "Har varit medlem kontinuerligt sedan datum" — ett faktum om KLUBBMEDLEMSKAPET
        /// (<c>ClubMembership.MemberSince</c>), inte om medlemmens inloggningskonto. Ligger i
        /// profilformuläret som skrivskyddat.
        /// </summary>
        public const string MemberSinceAlias = "memberSince";
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
        /// Fält blanketten kräver som KLUBBEN sätter. De markeras i profilen — medlemmen behöver veta
        /// att de saknas, för utan dem blir intyget ofullständigt — men med en annan text, eftersom
        /// hen inte kan fylla i dem själv.
        /// </summary>
        public static readonly ForeningsintygField[] ClubFields =
        {
            new()
            {
                Alias = ForeningsintygField.MemberSinceAlias,
                FormLabel = "Har varit medlem kontinuerligt sedan datum",
                Why = "Din klubb registrerar när ditt medlemskap började. Blanketten kräver datumet.",
                ClubManaged = true
            }
        };

        /// <summary>Alla fält markeringen bryr sig om — medlemmens egna plus klubbens.</summary>
        public static IEnumerable<ForeningsintygField> MarkerFields =>
            Personal.Where(f => SelfServiceAliases.Contains(f.Alias)).Concat(ClubFields);

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
    /// Underlag till blankettens fält <b>"Datum för godkänt skjutprov"</b>.
    ///
    /// <b>Vad blanketten faktiskt frågar efter.</b> Sidan 3 säger: <i>"Minst en ruta ska vara
    /// markerad och datum för genomfört skjutprov under den senaste tvåårsperioden ska anges."</i>
    /// Guldmärket är PERMANENT och upphör aldrig — vore datumet märkets datum kunde fältet inte
    /// fyllas ärligt av någon som tog guldet för mer än två år sedan, alltså de flesta
    /// guldmärkesskyttar. Kryssrutan intygar MERITEN; datumet intygar PROVET.
    ///
    /// SPSF:s återkommande omprövning av guldmärkets fordringar är <b>guldfodringen</b>, och att
    /// guldfodringar används som skjutprovsunderlag i föreningsintyg är <b>vedertaget</b> (bekräftat
    /// av Stefan 2026-09-01). Datumet blanketten efterfrågar är alltså den dag fordringarna
    /// <b>senast uppfylldes</b> — dagen den tredje av de nödvändiga serierna sköts.
    ///
    /// ⚠️ <b>Detta är UNDERLAG, aldrig ett ifyllt värde.</b> Rutan och datumet är styrelsens
    /// juridiska intygande. Kandidaten visas i utfärdandeformuläret med ett klick för att använda
    /// den; den skrivs aldrig in av sig själv och skrivs aldrig ut på blanketten.
    /// </summary>
    public class SkjutprovCandidate
    {
        /// <summary>Antal år bakåt blanketten godtar.</summary>
        public const int MaxAgeYears = 2;

        /// <summary>Det senaste uppfyllda guldfodringsåret, eller null när inget finns.</summary>
        public int? Year { get; set; }

        /// <summary>Det härledda datumet, "yyyy-MM-dd". Tomt när det inte går att härleda.</summary>
        public string Date { get; set; } = "";

        /// <summary>Dagen del 1 (tre kvalificerande guldserier) blev uppfylld.</summary>
        public string Part1Date { get; set; } = "";

        /// <summary>Dagen del 2 blev uppfylld.</summary>
        public string Part2Date { get; set; } = "";

        /// <summary>Vad som uppfyllde del 2, i klartext.</summary>
        public string Part2Basis { get; set; } = "";

        public bool Derivable { get; set; }

        /// <summary>Varför datumet inte kunde härledas. Tomt när det kunde.</summary>
        public string NotDerivableReason { get; set; } = "";

        /// <summary>Sant när det härledda datumet ligger utanför blankettens tvåårsfönster.</summary>
        public bool OlderThanTwoYears { get; set; }

        /// <summary>
        /// Härleder kandidaten ur redan lästa datum. <b>Ren funktion</b> — inga databasanrop — så
        /// regeln kan A/B-testas utan Umbraco.
        /// </summary>
        /// <param name="year">Det senaste uppfyllda guldfodringsåret, null om inget finns.</param>
        /// <param name="qualifyingPrecisionDates">Datum för årets kvalificerande guldserier, i valfri ordning.</param>
        /// <param name="requiredPrecisionCount">Antal guldserier del 1 kräver (3).</param>
        /// <param name="part2Source">Källan för del 2 (<see cref="Marken"/>-konstanterna).</param>
        /// <param name="tillampningDates">Datum för årets kvalificerande tillämpningsserier.</param>
        /// <param name="requiredTillampningCount">Antal snabbserier del 2 kräver (3).</param>
        /// <param name="standardMedalDate">Fältstandardmedaljens tävlingsdatum, när del 2 vilar på den.</param>
        /// <param name="part2Detail">Klartext om del 2, från kandidatanalysen.</param>
        /// <param name="today">Referensdag för tvåårsfönstret; injicerbar för testbarhet.</param>
        public static SkjutprovCandidate Derive(
            int? year,
            IEnumerable<DateTime> qualifyingPrecisionDates,
            int requiredPrecisionCount,
            string? part2Source,
            IEnumerable<DateTime> tillampningDates,
            int requiredTillampningCount,
            DateTime? standardMedalDate,
            string? part2Detail,
            DateTime today)
        {
            var c = new SkjutprovCandidate { Year = year, Part2Basis = part2Detail ?? "" };

            if (year == null)
            {
                c.NotDerivableReason = "Ingen uppfylld guldfodring finns i märkesliggaren.";
                return c;
            }

            // Del 1: dagen den TREDJE kvalificerande guldserien sköts — då blev fordringen uppfylld.
            // Fler serier efter den ändrar inget; det är fullbordandet som är datumet.
            var p1 = NthDate(qualifyingPrecisionDates, requiredPrecisionCount);

            DateTime? p2;
            if (part2Source == Marken.PartSourceStandardMedal)
            {
                // Del 2 vilar på en standardmedalj i fält — då är tävlingsdagen datumet, inte en series.
                p2 = standardMedalDate;
                if (p2 == null)
                    c.NotDerivableReason = "Del 2 uppfylldes av en standardmedalj i fält, men medaljen saknar tävlingsdatum.";
            }
            else if (part2Source == Marken.PartSourceManualAttest)
            {
                // Historiska år är attesterade för hand och har inga serier att härleda ur.
                p2 = null;
                c.NotDerivableReason =
                    $"Guldfodringen för {year} är intygad på plats i efterhand och har inga serier med datum.";
            }
            else
            {
                p2 = NthDate(tillampningDates, requiredTillampningCount);
                if (p2 == null)
                    c.NotDerivableReason = "Del 2:s snabbserier saknar datum.";
            }

            if (p1 != null) c.Part1Date = p1.Value.ToString("yyyy-MM-dd");
            if (p2 != null) c.Part2Date = p2.Value.ToString("yyyy-MM-dd");

            if (p1 == null && string.IsNullOrEmpty(c.NotDerivableReason))
                c.NotDerivableReason = "Del 1:s guldserier saknar datum, eller är färre än kravet.";

            if (p1 == null || p2 == null) return c;

            // Fordringarna är uppfyllda först när BÅDA delarna är det — alltså det SENARE datumet.
            var fulfilled = p1.Value > p2.Value ? p1.Value : p2.Value;
            c.Date = fulfilled.ToString("yyyy-MM-dd");
            c.Derivable = true;
            c.OlderThanTwoYears = fulfilled.Date < today.Date.AddYears(-MaxAgeYears);
            return c;
        }

        /// <summary>
        /// Det n:te datumet i kronologisk ordning, eller null när de är för få. Null-datum räknas
        /// inte — en serie utan datum kan inte belägga en dag.
        /// </summary>
        private static DateTime? NthDate(IEnumerable<DateTime> dates, int n)
        {
            if (n <= 0) return null;
            var ordered = dates.Where(d => d != default).OrderBy(d => d).ToList();
            return ordered.Count >= n ? ordered[n - 1] : null;
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
        /// <summary>
        /// ⚠️ <b>Polismyndighetens blankettnummer skrivs INTE ut på vårt intyg</b> (Stefans beslut
        /// 2026-09-01). "PM 551.24 Ver. 2019-01-18/11" är deras formulärregisters identifikation, och
        /// ett dokument som bär den utger sig för att VARA den blanketten. Vårt intyg följer dess
        /// innehåll men är inte den, så det bär sin egen beteckning.
        ///
        /// Konstanten finns kvar som KÄLLHÄNVISNING för koden — vilken blankett fältmodellen är byggd
        /// efter — och används medvetet inte av utskriftsvyn.
        /// </summary>
        public const string SourceFormReference = "Polismyndighetens blankett PM 551.24 Ver. 2019-01-18/11";

        /// <summary>Vår egen beteckning i sidfoten, så mottagaren ser vad dokumentet är och varifrån
        /// det kommer utan att det ser ut som myndighetens eget formulär.</summary>
        public const string DocumentTitle = "Föreningsintyg";

        /// <summary>Sidfotens härkomstrad. Nämner INTE blankettnummer — se
        /// <see cref="SourceFormReference"/>.</summary>
        public const string DocumentOrigin =
            "Utfärdat via pistol.nu · Utformat efter Polismyndighetens blankett för föreningsintyg";

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

        /// <summary>
        /// UNDERLAG till <see cref="SkjutprovDatum"/> — den dag guldmärkets fordringar senast
        /// uppfylldes, härledd ur guldfodringen. Visas i utfärdandeformuläret med ett klick för att
        /// använda den. <b>Skrivs aldrig in av sig själv och skrivs aldrig ut på blanketten.</b>
        /// </summary>
        public SkjutprovCandidate? SkjutprovForslag { get; set; }

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
