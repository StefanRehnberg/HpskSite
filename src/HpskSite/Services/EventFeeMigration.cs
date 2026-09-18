using System.Globalization;
using System.Text.RegularExpressions;

namespace HpskSite.Services
{
    /// <summary>
    /// Tolkar den gamla FRITEXTavgiften (<c>feeAmount</c>, "t.ex. 100 kr, Gratis") till ett tal som
    /// kan debiteras (<c>eventFee</c>).
    ///
    /// <para><b>⚠️⚠️ REN FUNKTION, MEDVETET.</b> Den här tolkningen avgör vad en medlem kommer att
    /// bli debiterad, och den körs EN gång över data vi inte kan läsa i förväg (prod). Ligger den i
    /// en controller går den bara att pröva genom att migrera på riktigt. Här går varje form att
    /// pröva som ett testfall innan en enda rad rörs.</para>
    ///
    /// <para><b>⚠️ TOLKA BARA DET OTVETYDIGA — RAPPORTERA RESTEN.</b> "Gratis" betyder noll.
    /// "Gratis för juniorer" betyder att NÅGON betalar, och vilket belopp står ingenstans. En
    /// gissning där debiterar fel person fel summa, och felet upptäcks av medlemmen vid
    /// betalningen. Allt som inte är entydigt returneras som <see cref="FeeParse.Unparseable"/> och
    /// ska läsas av en människa.</para>
    /// </summary>
    public static class EventFeeMigration
    {
        /// <summary>Vad en fritextavgift blev.</summary>
        public enum FeeParse
        {
            /// <summary>Tomt fält — ingenting att migrera, inget problem.</summary>
            Empty,
            /// <summary>Entydigt belopp (inklusive 0 för "Gratis").</summary>
            Parsed,
            /// <summary>Går inte att tolka entydigt. Rör den INTE — lägg den i rapporten.</summary>
            Unparseable
        }

        public readonly record struct Result(FeeParse Outcome, decimal Amount, string Reason);

        /// <summary>
        /// Orden som ensamma betyder noll kronor.
        /// <para>⚠️ Bara när de står ENSAMMA. "gratis för medlemmar" är ett villkor, inte ett pris —
        /// se klassens varning. Att slå noll på den skulle göra evenemanget gratis för alla.</para>
        /// </summary>
        private static readonly string[] FreeWords =
        {
            "gratis", "fritt", "kostnadsfritt", "kostnadsfri", "ingen avgift", "ingen kostnad", "-"
        };

        /// <summary>
        /// Valutabeteckningar som får strykas. <c>:-</c> och <c>kr</c> är hur folk faktiskt skriver.
        /// </summary>
        private static readonly string[] CurrencyWords = { "kronor", "kr", "sek", ":-", ":‑", "-:" };

        public static Result Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new Result(FeeParse.Empty, 0m, "tomt");

            var s = raw.Trim();

            // ⚠ INGEN EGEN BLANKSTEGSNORMALISERING BEHOVS - och det ar MATT, inte antaget.
            // Hart mellanslag (U+00A0) och smalt mellanslag (U+202F) foljer med vid inklistring
            // fran Word och Excel, och de SER ut som vanliga mellanslag. Men bade string.Trim()
            // och \s i .NET tacker hela Unicode-klassen \p{Z}, sa de hanteras redan av trimningen
            // nedan och av tusentalsregexen.
            //
            // En tidigare version hade en egen Replace-rad har, med en stor varning om hur viktig
            // den var. A/B visade att sviten var GRON med raden bortkopplad - den bar ingenting.
            // En kommentar som utger en rad for att vara barande nar den inte ar det ar sin egen
            // sorts logn. Testet Hart_mellanslag_hanteras_som_vanligt star kvar och vaktar
            // BETEENDET, sa den dag nagon byter \s mot ett literalt mellanslag faller det.

            var lower = s.ToLowerInvariant().Trim();

            // Entydigt gratis — men BARA som ensamt ord.
            if (FreeWords.Contains(lower))
                return new Result(FeeParse.Parsed, 0m, $"\"{s}\" tolkat som 0 kr");

            // Stryk valutabeteckning. Görs FÖRE sifferkontrollen så "300 kr" blir "300".
            var work = lower;
            foreach (var w in CurrencyWords)
                work = work.Replace(w, " ");

            // Tusentalsavgränsare: mellanslag mellan siffror ("1 200").
            work = Regex.Replace(work, @"(?<=\d)\s+(?=\d)", "").Trim();

            // ⚠️⚠️ PUNKTEN AVVISAS, OCH DEN MÅSTE AVVISAS HÄR — före kommatecknet görs om till
            // punkt. På svenska betyder "1.200" tusental och "1.50" decimal: samma tecken, två
            // betydelser som skiljer sig med faktor 1000. Skillnaden mellan 1 kr och 1 200 kr på
            // en faktura går inte att gissa sig till.
            //
            // Första versionen lät punkten passera in i sifferkontrollen nedan, som accepterade
            // den — "1.200" hade migrerats som 1,20 kr. Enhetstestet fångade det innan en enda rad
            // rördes, vilket är hela skälet tolkningen ligger som ren funktion.
            // ⚠️⚠️ ORDNINGEN MELLAN DE HÄR TVÅ KONTROLLERNA ÄR INTE GODTYCKLIG — SKÄLET LÄSES AV EN
            // MÄNNISKA SOM SKA BESTÄMMA VAD SOM SKA GÖRAS.
            // Prod bar "180 spann per vuxen, 90 for barn och smattingar gratis." Punktkontrollen låg
            // först och fyrade på meningens SLUTPUNKT, så rapporten sa "innehaller punkt, som ar
            // tvetydig (tusental eller decimal)" om en mening med tre olika priser i. Sant om tecknet,
            // vilseledande om problemet — och den som läser det letar efter fel sak.
            //
            // Frågan "är det här över huvud taget ett tal?" måste alltså komma FÖRST. Punktens
            // tvetydighet är bara intressant när strängen i övrigt ÄR ett tal.
            // ⚠️ RÅDET MÅSTE PEKA PÅ DET SOM FAKTISKT GÅR ATT GÖRA.
            // Första formuleringen sa "sätt avgiften som ett tal och skriv villkoren i
            // beskrivningen" — sant när avgiften var ETT tal, men föråldrat i samma stund
            // prisraderna fanns. "180 spänn per vuxen, 90 för barn och småttingar gratis" går nu
            // att uttrycka exakt som tre rader, och ett råd som säger åt operatören att platta till
            // den till 180 + prosa återinför precis den motsägelse raderna byggdes för att ta bort.
            if (!Regex.IsMatch(work, @"^[\d.,]+$"))
                return new Result(FeeParse.Unparseable, 0m,
                    $"\"{s}\" ar inte ett enda belopp utan en text - t.ex. flera priser eller en "
                    + "betalningsinstruktion. Lagg upp en PRISRAD per pris pa evenemanget "
                    + "(t.ex. Vuxen 180, Barn 90, Under 7 ar 0).");

            if (work.Contains('.'))
                return new Result(FeeParse.Unparseable, 0m,
                    $"\"{s}\" innehaller punkt, som ar tvetydig (tusental eller decimal) - kontrollera for hand");

            work = work.Replace(',', '.');

            // Sista nätet: rätt tecken men fel form, t.ex. "1,2,3".
            if (!Regex.IsMatch(work, @"^\d+(\.\d+)?$"))
                return new Result(FeeParse.Unparseable, 0m, $"\"{s}\" ar inte ett entydigt belopp");

            if (!decimal.TryParse(work, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                return new Result(FeeParse.Unparseable, 0m, $"\"{s}\" gick inte att lasa som tal");

            if (amount < 0)
                return new Result(FeeParse.Unparseable, 0m, $"\"{s}\" ar negativt");

            // ⚠️ Taket är en RIMLIGHETSkontroll, inte en regel. Dev bar 14300 i fältet — sannolikt
            // en felskrivning eller något annat än en deltagaravgift. Att tyst migrera in den som
            // ett debiterbart belopp vore att göra en gammal felskrivning till en faktura.
            if (amount > 5000)
                return new Result(FeeParse.Unparseable, amount,
                    $"\"{s}\" ar ovanligt hogt for en evenemangsavgift - kontrollera for hand");

            return new Result(FeeParse.Parsed, amount, $"\"{s}\" tolkat som {amount} kr");
        }
    }
}
