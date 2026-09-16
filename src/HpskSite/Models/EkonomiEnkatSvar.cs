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
        public string? SvarF9 { get; set; }
        public string? SvarF10 { get; set; }
        public string? SvarF11 { get; set; }
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
            "F9" => SvarF9,
            "F10" => SvarF10,
            "F11" => SvarF11,
            _ => null,
        };

        /// <summary>
        /// Skriver svaret på en fråga, slaget upp på frågans id.
        ///
        /// <para><b>⚠️ Finns för att ta bort en PARALLELL LISTA.</b> Utan den måste varje skrivväg
        /// räkna upp <c>SvarF1 = …, SvarF2 = …</c> för hand, och en sådan uppräkning glider isär
        /// från <see cref="EkonomiEnkat.Fragor"/> så fort en fråga läggs till. Exakt den formen
        /// kostade två misslyckade proddeployer när <c>SvarF8</c> glömdes i migreringens
        /// guardlista.</para>
        /// </summary>
        public void SetSvar(string fragaId, string? varde)
        {
            switch (fragaId)
            {
                case "F1": SvarF1 = varde; break;
                case "F2": SvarF2 = varde; break;
                case "F3": SvarF3 = varde; break;
                case "F4": SvarF4 = varde; break;
                case "F5": SvarF5 = varde; break;
                case "F6": SvarF6 = varde; break;
                case "F7": SvarF7 = varde; break;
                case "F8": SvarF8 = varde; break;
                case "F9": SvarF9 = varde; break;
                case "F10": SvarF10 = varde; break;
                case "F11": SvarF11 = varde; break;
            }
        }
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

        public const string GruppForening = "I föreningslivet";
        public const string GruppYrke     = "I yrket";
        public const string GruppOvrigt   = "";

        /// <summary>
        /// Nyckel → etikett, i den ordning de visas.
        ///
        /// <para><b>⚠️ TVÅ GRUPPER, INTE EN RANGSTEGE.</b> Alternativen låg först i fallande
        /// prestige (auktoriserad överst, kassör näst sist), och en sådan lista läses som en
        /// rangordning även utan ett ord om saken — position ett betyder "bäst". Det ger samma
        /// incitament att ta i som den borttagna vikttexten gjorde. Grupperade blir
        /// föreningsrollerna en egen, jämbördig kategori i stället för botten av en stege.</para>
        ///
        /// <para>Föreningsgruppen står först för att den är det vanligaste svaret bland medlemmar
        /// — vanligast först är dessutom vanlig formulärhygien.</para>
        /// </summary>
        public static readonly (string Key, string Label, string Grupp)[] Kompetenser =
        {
            (KompetensKassor,       "Kassör eller motsvarande i en förening",        GruppForening),
            (KompetensRevisor,      "Förtroendevald revisor i en förening",          GruppForening),
            (KompetensKonsult,      "Arbetar med redovisning eller bokföring i yrket", GruppYrke),
            (KompetensAuktoriserad, "Auktoriserad revisor eller redovisningskonsult", GruppYrke),
            (KompetensEkonom,       "Ekonom, men inte med redovisning som specialitet", GruppYrke),
            (KompetensAnnat,        "Annat",                                          GruppOvrigt),
        };

        /// <summary>Grupperna i visningsordning, tom sträng sist och utan rubrik.</summary>
        public static IEnumerable<(string Grupp, (string Key, string Label, string Grupp)[] Val)> KompetensGrupper =>
            new[] { GruppForening, GruppYrke, GruppOvrigt }
                .Select(g => (Grupp: g, Val: Kompetenser.Where(k => k.Grupp == g).ToArray()))
                .Where(x => x.Val.Length > 0);

        public static bool IsValid(string? key) =>
            !string.IsNullOrWhiteSpace(key) && Kompetenser.Any(k => k.Key == key);

        public static string LabelFor(string? key) =>
            Kompetenser.FirstOrDefault(k => k.Key == key).Label ?? "Ej angivet";

        /// <summary>
        /// Sorteringsvikt. Yrkesverksam redovisningskompetens först — det är den som kan svara på
        /// nummerserierna, periodiseringen och kontoplanen.
        ///
        /// <para><b>⚠️ RANGORDNINGEN FÅR ALDRIG STÅ PÅ FORMULÄRET.</b> En tidigare version förklarade
        /// för den svarande att yrkesfolk väger tyngre än "många välmenande", vilket gav två skäl att
        /// överdriva sin bakgrund: att bli tagen på allvar, och att slippa känna att ens tid var
        /// bortkastad. Fältet finns för att få en SANN bild — en text som förstör precis det den
        /// samlar in är sämre än inget fält alls. Vikten är dessutom inte densamma på alla frågor:
        /// på praktikfrågorna (F6, F7) är en föreningskassörs svar ofta det mest användbara.</para>
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

        /// <summary>
        /// Frågorna, i dokumentets ordning. Id:t är formulärfältets namn och lagringsnyckeln.
        ///
        /// <para><b>⚠️⚠️ ID:T ÄR PERMANENT OCH ÅTERANVÄNDS ALDRIG. Numret på skärmen är POSITIONEN,
        /// inte id:t.</b> När F8 togs bort 2026-09-16 (frågan blev besvarad) hade en omnumrering
        /// betytt att svar lagrade i <c>SvarF9</c> plötsligt visades under en annan fråga — alltså
        /// tyst omtolkad data, det värsta slaget av fel i den här kodbasen. Därför: ta bort en fråga
        /// ur listan och lämna luckan i id-serien. Kolumnen får ligga kvar tom i databasen; att
        /// släppa en kolumn med svar i vore att radera någons text.</para>
        ///
        /// <para><b>⚠️ VARJE FRÅGA MÅSTE KLARA TESTET "ändrar svaret vad vi bygger?"</b> Är svaret
        /// redan känt är det ett KRAV och hör i backloggen, inte här. Att fråga om något vi ändå
        /// måste göra ger ett "ja, det behövs" som inte flyttar en kodrad — och tränger ut de frågor
        /// som gör det. Så föll en första omgång revisorsfrågor bort (oföränderlighet, ändringslogg,
        /// bankavstämning, revisorns läsrätt): allt sant, inget av det en fråga.</para>
        ///
        /// <para><b>Dyr att ändra</b> = svaret bestämmer formen på varje rad, eller kontostrukturen,
        /// redan från första kronan. Ändras det senare måste levande bokföringsdata räknas om, och
        /// uppgifter som aldrig sparades går inte att rekonstruera.</para>
        /// </summary>
        public static readonly (string Id, string Rubrik, bool Dyr, string Text)[] Fragor =
        {
            ("F1", "Nummerserier — vad ska föreningen kunna ställa in?", true,
             "Vi tänker oss flera serier som var och en är obruten för sig: en för betalningar som " +
             "systemet självt registrerar, en för sådant föreningen bokför manuellt — till exempel ett " +
             "kvitto från järnhandeln. Numren följer registreringsordning, inte händelsedatum, så " +
             "ingenting infogas mellan befintliga nummer. Vi har också fått tipset att varje förening " +
             "bör kunna välja en egen prefixbokstav för sina serier. " +
             "Vad av det här bör vara en INSTÄLLNING per förening, och vad är i så fall ett rimligt " +
             "förval för den som inte vill välja? Och behöver kvitton till deltagarna en egen obruten " +
             "serie, eller räcker det att varje kvitto pekar på sin verifikation?"),

            // ⚠️ F2 (fakturerings- kontra kontantmetoden) är BORTTAGEN 2026-09-16: besvarad och
            // avgjord — kontantmetoden, och obetalda fakturor bokförs som kundfordran vid bokslut.
            // Id:t F2 återanvänds ALDRIG och kolumnen SvarF2 ligger kvar i databasen med sina svar.


            ("F3", "En rättelse som upptäcks efter fastställt bokslut — var bokförs den?", true,
             "Vi låser året när årsmötet fastställt resultat- och balansräkningen; ingenting ska kunna " +
             "ändras i efterhand. Frågan är vad låset ska släppa igenom. Upptäcks ett fel efter " +
             "fastställandet — bokförs rättelsen i det gamla året, som då måste kunna öppnas, eller i " +
             "det innevarande? Svaret avgör om ett låst år ska tillåta skrivningar över huvud taget, " +
             "vilket är mycket svårt att ändra i efterhand."),

            ("F4", "Kontoplan, avskrivningar och ändamålsbestämda medel", true,
             "Vi tänker använda BAS-kontoplanen i den version som passar ideella föreningar. " +
             "Kontoplanen bestämmer varje kontering vi någonsin skriver, så den behöver vara rätt från " +
             "början — vilken vill du se, och hur mycket bör en förening kunna lägga till egna konton? " +
             "En klubb äger dessutom saker som ska synas i balansräkningen: klubbstuga, kulfångsvall, " +
             "klubbvapen, gräsklippare. Vi har förstått att avskrivningstiden är ett beslut klubben " +
             "fattar, och att mindre inköp kan kostnadsföras direkt — vad ska då vara inställbart per " +
             "tillgång, och vad är rimliga förval? " +
             "Och: många klubbar har öronmärkta medel — en banfond, en ungdomsfond. Ska de redovisas " +
             "som ändamålsbestämt eget kapital eller som en avsättning?"),

            ("F5", "Bokslutsform och deklaration", false,
             "Vi tänker avsluta året med förenklat årsbokslut — vår bild är att årsredovisning krävs " +
             "först över storleksgränser som en klubb av den här storleken inte når. Stämmer det? " +
             "Och är en förening som vår skyldig att lämna inkomstdeklaration? I så fall: räcker det " +
             "att vi tar fram siffrorna som underlag, eller förväntar du dig att systemet lämnar in dem?"),

            ("F6", "Spårbarhet — vad behöver du för att kunna granska utan att be om komplettering?", false,
             "Vi bygger så att varje bokföringspost bär sin verifikation och sitt underlag, och så att " +
             "inget kan ändras i efterhand. Det vi inte kan läsa oss till är vad som gör granskningen " +
             "smidig i praktiken: vad behöver du kunna följa från en bokföringspost bakåt till " +
             "kvittot och framåt till raden på kontoutdraget? Finns det uppgifter du regelmässigt " +
             "tvingas efterfråga när du granskar en förening?"),

            ("F7", "Kontantkassa — vad krävs av en ideell förening?", false,
             "Klubbens kiosk på tävlingsdagen tar kontanter, och ibland betalas en anmälningsavgift " +
             "kontant vid disken. Vad gäller för en ideell förening i fråga om kassaregister, och vad " +
             "behöver dokumenteras vid dagsavslut och insättning för att du ska godta hanteringen? " +
             "Svaret avgör om vi bygger stöd för kontanter alls eller om vi ska avråda från dem."),

            // ⚠️ F8 (genomströmningsposter — licensavgifter som skuld eller intäkt) är BORTTAGEN
            // 2026-09-16: frågan är besvarad och avgjord (intäkt + kostnad, inte skuld). Id:t F8
            // återanvänds ALDRIG och kolumnen SvarF8 ligger kvar i databasen med sina svar — se
            // varningen ovanför listan.

            ("F9", "Vad behöver den som faktureras — en klubb eller en krets?", false,
             "Två fall med samma form. En klubb anmäler trettio av sina medlemmar till en tävling och " +
             "betalar för dem i efterhand. Och en krets fakturerar varje år en avgift till alla klubbar " +
             "i kretsen. I båda uppstår åtagandet före betalningen. Räcker en specifikation med belopp, " +
             "förfallodatum och vad som ingår — eller vill du se en faktura i egentlig mening? I så fall: " +
             "vilka uppgifter måste den bära, och behöver fakturanumren vara en obruten serie per " +
             "utställande förening?"),

            ("F10", "Vad måste stå på kvittot om föreningen ÄR momsregistrerad?", false,
             "Vi utgår från att deltagaravgifter i en allmännyttig ideell idrottsförening är momsfria, " +
             "och att uppgiften ska anges per förening i stället för som i dag, där systemet skriver " +
             "\"Föreningen är inte momsregistrerad\" på allas kvitton. Det vi behöver veta är vad " +
             "kvittot måste bära för en förening som ÄR momsregistrerad för annan verksamhet — " +
             "momssats, momsbelopp, registreringsnummer — och om deltagaravgiften då ska särredovisas " +
             "från det momspliktiga."),

            ("F11", "Arkivering och byte av system", false,
             "Räkenskapsinformation ska bevaras i läsbar form i sju år. Om bokföringen ligger hos oss — " +
             "vad behöver föreningen kunna ta ut, och i vilken form, för att kravet ska vara uppfyllt " +
             "oberoende av att vår tjänst finns kvar? Och åt andra hållet: en klubb som byter till oss " +
             "mitt i ett år behöver få in sina ingående balanser och gärna tidigare verifikationer. " +
             "Är SIE-import rätt väg, eller finns det en praxis du hellre ser?"),
        };
    }
}
