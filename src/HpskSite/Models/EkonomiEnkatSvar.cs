using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Ett svar på ekonomifrågorna (<c>/ekonomifragor</c>) — underlaget inför ombyggnaden av
    /// betalningar och bokföring.
    ///
    /// <para><b>⚠️ Kompetensen är obligatorisk, allt annat frivilligt.</b> Svaren ska VÄGAS, inte
    /// räknas: ett svar från en auktoriserad redovisningskonsult väger tyngre än tio från en
    /// välmenande föreningskassör. Utan kompetensen per rad går vägningen inte att göra i
    /// efterhand, och då har vi bytt ett obesvarat beslut mot ett omtvistat.</para>
    ///
    /// <para><b>⚠️ Sidan är öppen</b> — den mejlas ut med direktlänk, så raden bär inget medlems-id.
    /// Namn och e-post är frivilliga; den som bara vill svara ska kunna göra det.</para>
    /// </summary>
    [TableName("EkonomiEnkatSvar")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class EkonomiEnkatSvar
    {
        public int Id { get; set; }

        /// <summary>Nyckel ur <see cref="EkonomiEnkat.Kompetenser"/>.</summary>
        public string Kompetens { get; set; } = "";

        /// <summary>Preciseringen när <c>Kompetens</c> är "annat".</summary>
        public string? KompetensFritext { get; set; }

        public string? Namn { get; set; }
        public string? Epost { get; set; }
        public string? Klubb { get; set; }

        public string? SvarF1 { get; set; }
        public string? SvarF2 { get; set; }
        public string? SvarF3 { get; set; }
        public string? SvarF4 { get; set; }
        public string? SvarF5 { get; set; }
        public string? SvarF6 { get; set; }
        public string? SvarF7 { get; set; }
        public string? SvarF8 { get; set; }
        public string? Ovrigt { get; set; }

        /// <summary>Vill vara med och granska bygget — den som svarar är bästa kandidaten.</summary>
        public bool VillHjalpaTill { get; set; }

        public DateTime SkapadDatum { get; set; }

        /// <summary>Etiketten för <see cref="Kompetens"/>, eller fritexten när den är satt.</summary>
        [Ignore]
        public string KompetensLabel =>
            Kompetens == EkonomiEnkat.KompetensAnnat && !string.IsNullOrWhiteSpace(KompetensFritext)
                ? KompetensFritext!
                : EkonomiEnkat.LabelFor(Kompetens);

        /// <summary>
        /// Hur tungt svaret väger. <b>Rangordning, inte poäng</b> — den finns för att kunna sortera
        /// de tyngsta svaren först, aldrig för att räkna ihop en majoritet.
        /// </summary>
        [Ignore]
        public int Tyngd => EkonomiEnkat.TyngdFor(Kompetens);

        /// <summary>
        /// Svaret på en fråga, slaget upp på frågans id.
        ///
        /// <para>Ligger här i stället för som ett <c>switch</c>-uttryck i vyn: Razors parser är
        /// känslig för C#-uttryckssyntax i kodblock, och ett fel där visar sig först i drift som en
        /// <c>UmbracoCompilationException</c> — inte i <c>dotnet build</c>.</para>
        /// </summary>
        public string? SvarFor(string fragaId) => fragaId switch
        {
            "F1" => SvarF1,
            "F2" => SvarF2,
            "F3" => SvarF3,
            "F4" => SvarF4,
            "F5" => SvarF5,
            "F6" => SvarF6,
            "F7" => SvarF7,
            "F8" => SvarF8,
            _ => null,
        };
    }

    /// <summary>Alternativen och frågorna på <c>/ekonomifragor</c>, på ett ställe.</summary>
    public static class EkonomiEnkat
    {
        public const string KompetensAuktoriserad = "auktoriserad";
        public const string KompetensKonsult      = "konsult";
        public const string KompetensRevisor      = "revisor";
        public const string KompetensEkonom       = "ekonom";
        public const string KompetensKassor       = "kassor";
        public const string KompetensAnnat        = "annat";

        /// <summary>Nyckel → etikett, i den ordning de visas.</summary>
        public static readonly (string Key, string Label)[] Kompetenser =
        {
            (KompetensAuktoriserad, "Auktoriserad revisor eller redovisningskonsult"),
            (KompetensKonsult,      "Arbetar med redovisning eller bokföring i yrket"),
            (KompetensRevisor,      "Förtroendevald revisor i en förening"),
            (KompetensEkonom,       "Ekonom, men inte med redovisning som specialitet"),
            (KompetensKassor,       "Kassör eller motsvarande i en förening"),
            (KompetensAnnat,        "Annat"),
        };

        public static bool IsValid(string? key) =>
            !string.IsNullOrWhiteSpace(key) && Kompetenser.Any(k => k.Key == key);

        public static string LabelFor(string? key) =>
            Kompetenser.FirstOrDefault(k => k.Key == key).Label ?? "Ej angivet";

        /// <summary>
        /// Sorteringsvikt. Yrkesverksam redovisningskompetens först — det är den som kan svara på
        /// kvittoserien, periodiseringen och kontoplanen.
        /// </summary>
        public static int TyngdFor(string? key) => key switch
        {
            KompetensAuktoriserad => 5,
            KompetensKonsult      => 4,
            KompetensRevisor      => 3,
            KompetensEkonom       => 2,
            KompetensKassor       => 2,
            _                     => 1,
        };

        /// <summary>Frågorna, i dokumentets ordning. Id:t är också formulärfältets namn.</summary>
        public static readonly (string Id, string Rubrik, bool Dyr, string Text)[] Fragor =
        {
            ("F1", "Nummerserier — hur ska de delas upp?", true,
             "Vi tänker oss flera serier som var och en är obruten för sig: en för betalningar som " +
             "systemet självt registrerar (deltagaravgifter via Swish), en för sådant föreningen bokför " +
             "manuellt — till exempel ett kvitto från järnhandeln. Numren följer registreringsordning, " +
             "inte händelsedatum, så ingenting infogas mellan befintliga nummer. Är den uppdelningen " +
             "rätt, eller vill du hellre se en enda serie för hela föreningen? Och behöver kvitton till " +
             "deltagarna dessutom en egen obruten serie, eller räcker det att varje kvitto pekar på sin " +
             "verifikation?"),

            ("F2", "Ska raden bära ett intäktsdatum skilt från betaldatumet?", true,
             "En avgift betalas ibland i förskott för en aktivitet som ligger senare — ibland efter " +
             "årsskiftet, till exempel en tävlingsanmälan i december för en tävling i maj. Behöver vi " +
             "registrera aktivitetens datum separat för att kunna periodisera, och finns det en gräns " +
             "under vilken det saknar betydelse för en förening av den här storleken?"),

            ("F3", "Godtas raden som verifikation?", false,
             "Tillsammans med kontoutdraget och kvittokopian — saknas någon uppgift i raden ovan, eller " +
             "är någon av dem överflödig?"),

            ("F4", "Momsraden på kvittot", false,
             "Vi utgår från att deltagaravgifter i en allmännyttig ideell idrottsförening är momsfria och " +
             "att kvittot därför inte redovisar moms. I dag skriver systemet raden \"Föreningen är inte " +
             "momsregistrerad — moms ingår ej\" på alla föreningars kvitton. Bör det i stället vara en " +
             "uppgift varje förening anger själv, och vad gäller för en förening som är momsregistrerad " +
             "för annan verksamhet, som kiosk eller sponsring?"),

            ("F5", "Vad behöver den som faktureras — en klubb eller en krets?", false,
             "Två fall med samma form. En klubb anmäler trettio av sina medlemmar till en tävling och " +
             "betalar för dem i efterhand. Och en krets fakturerar varje år en avgift till alla klubbar " +
             "i kretsen. I båda uppstår åtagandet före betalningen. Räcker en specifikation med belopp, " +
             "förfallodatum och vad som ingår — eller vill du se en faktura i egentlig mening? I så fall: " +
             "vilka uppgifter måste den bära, och behöver fakturanumren vara en obruten serie per " +
             "utställande förening?"),

            ("F6", "Kontoplan, avskrivningar och bokslutsform", true,
             "Vi tänker använda BAS-kontoplanen i den version som passar ideella föreningar, och avsluta " +
             "året med förenklat årsbokslut — vår bild är att årsredovisning krävs först över " +
             "storleksgränser en klubb av den här storleken inte når. Stämmer det? Kontoplanen bestämmer " +
             "varje kontering vi någonsin skriver, så den behöver vara rätt från början. " +
             "En klubb äger dessutom saker som ska synas i balansräkningen — klubbstuga, kulfångsvall, " +
             "klubbvapen, gräsklippare: hur bör vi hantera avskrivningar, och var går gränsen för vad " +
             "som får kostnadsföras direkt? Och: är en förening som vår skyldig att lämna " +
             "inkomstdeklaration?"),

            ("F7", "Arkivering och byte av system", false,
             "Räkenskapsinformation ska bevaras i läsbar form i sju år. Om bokföringen ligger hos oss — " +
             "vad behöver föreningen kunna ta ut, och i vilken form? Och åt andra hållet: en klubb som " +
             "byter till oss mitt i ett år behöver få in sina ingående balanser. Är SIE-import rätt väg?"),

            ("F8", "Pengar som bara passerar genom föreningen", false,
             "Klubben samlar in licensavgifter från sina medlemmar och betalar dem vidare till förbundet. " +
             "Pengarna är aldrig klubbens egna. Vi tänker oss att de bokförs som en skuld tills de " +
             "skickas vidare, och alltså aldrig går över resultaträkningen som en intäkt — så att " +
             "årsmötet inte får ett uppblåst resultat. Är det rätt hanterat, och finns det andra poster " +
             "av samma slag vi borde tänka på?"),
        };
    }
}
