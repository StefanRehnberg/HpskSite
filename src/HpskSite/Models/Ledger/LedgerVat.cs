using System.Text.RegularExpressions;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// Momsreglerna, samlade där de går att pröva utan databas.
    ///
    /// <para><b>⚠️⚠️ MOMSEN FÖLJER KONTOT, INTE VERIFIKATIONEN.</b> En förening som alls är
    /// momsregistrerad är det nästan alltid bara för en del av verksamheten — kiosken och
    /// sponsringen, sällan startavgifterna (Michael Henriksson 2026-09-17: <i>"en del kanske behöver
    /// kunna redovisa moms för viss del av verksamheten"</i>). Därför sätts satsen en gång på
    /// kontot, och bokföringen räknar fram momsraden. Ett momsfält på varje verifikation hade
    /// tvingat fram ett ställningstagande varje gång — och glömts.</para>
    ///
    /// <para><b>⚠️ Registreringen är en grind, satsen är ett förslag.</b> Är föreningen inte
    /// registrerad bokförs aldrig moms, oavsett vad kontot säger (<c>LedgerPostingService</c>).</para>
    /// </summary>
    public static class LedgerVat
    {
        /// <summary>
        /// De svenska momssatserna: 25 % (normalsatsen), 12 % (bl.a. livsmedel — kiosken) och
        /// 6 %. Vilken sats som gäller är föreningens sak; vi erbjuder bara de som finns. Momsfritt
        /// är ingen sats — det är avsaknaden av en.
        /// </summary>
        public static readonly decimal[] Rates = { 25m, 12m, 6m };

        /// <summary>Är satsen en av de svenska? Null och 0 betyder momsfritt och är alltid giltiga.</summary>
        public static bool IsAllowedRate(decimal? rate) => rate is null or 0m || Rates.Contains(rate.Value);

        /// <summary>
        /// Får kontot bära en momssats? Bara intäkter och kostnader (klass 3–7).
        /// <para>⚠️ Ett balanskonto med moms hade gett en momsrad på en insättning mellan två egna
        /// konton, och finansiella poster (klass 8) är momsfria.</para>
        /// </summary>
        public static bool AccountCanCarryVat(int accountNumber) => accountNumber is >= 3000 and < 8000;

        /// <summary>
        /// Normaliserar ett momsregistreringsnummer: <c>SE</c> + organisationsnumrets tio siffror
        /// + <c>01</c>.
        ///
        /// <para><b>⚠️ Organisationsnumret godtas också</b> ("802412-3456") och byggs om. En kassör
        /// vet föreningens organisationsnummer; momsnumrets form är det få som har i huvudet.</para>
        /// </summary>
        /// <returns>Det normaliserade numret, eller ett fel på svenska.</returns>
        public static (string? Number, string? Error) NormalizeVatNumber(string? input)
        {
            var s = Regex.Replace((input ?? "").ToUpperInvariant(), @"[\s\-]", "");

            if (s.Length == 0)
                return (null, "Ange föreningens momsregistreringsnummer — det ska stå på kvittona.");

            if (Regex.IsMatch(s, @"^\d{10}$")) s = "SE" + s + "01";

            if (!Regex.IsMatch(s, @"^SE\d{10}01$"))
                return (null, "Momsregistreringsnumret ska se ut som SE802412345601 — SE, "
                            + "organisationsnumret och 01. Du kan också skriva bara organisationsnumret.");

            return (s, null);
        }
    }
}
