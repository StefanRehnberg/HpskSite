using System.Globalization;
using System.Text;

namespace HpskSite.Models.Ledger
{
    /// <summary>
    /// SIE 4-formatets skrivregler, samlade där de går att pröva utan databas.
    ///
    /// <para><b>⚠️⚠️ SIE ÄR ETT STRIKT FORMAT, och ett fel syns inte hos oss — det syns när
    /// klubbens revisor eller bokföringsprogram vägrar läsa filen, långt härifrån och utan att
    /// någon kan säga varför.</b> Därför ligger reglerna här, med test, i stället för som
    /// strängbyggen inne i en export.</para>
    ///
    /// <para><b>⚠️ Kodsidan är CP437.</b> Det är vad SIE-standarden föreskriver, och det är inte
    /// en detalj: en fil i UTF-8 läses in med sönderslagna å, ä och ö i varje kontonamn och varje
    /// verifikationstext. Det ser ut som vårt fel även när allt annat är rätt.</para>
    ///
    /// <para><b>⚠️ Fältseparator är MELLANSLAG, och fält med mellanslag måste citeras.</b>
    /// "Övriga intäkter" utan citat blir två fält och posten går sönder.</para>
    /// </summary>
    public static class SieFormat
    {
        /// <summary>SIE 4E — transaktioner med ingående och utgående balanser.</summary>
        public const string TypeVerifications = "4";

        /// <summary>
        /// Kodsidan SIE föreskriver.
        /// <para>⚠️ Registreras via <c>CodePagesEncodingProvider</c> i <c>Program.cs</c> — .NET
        /// Core bär inte CP437 som standard, och utan registreringen kastar
        /// <see cref="Encoding.GetEncoding(int)"/>.</para>
        /// </summary>
        public const int CodePage = 437;

        /// <summary>
        /// Ett fält som SIE vill ha det.
        ///
        /// <para>Citeras när det innehåller mellanslag, citattecken eller är tomt; inre
        /// citattecken escapas med bakstreck. <b>⚠️ Ett tomt fält MÅSTE bli <c>""</c></b> —
        /// utelämnas det förskjuts alla fält efter det, och posten betyder något annat.</para>
        /// </summary>
        public static string Field(string? value)
        {
            var v = value ?? "";

            // ⚠️ Radbrytningar och tabbar bryter formatet helt — en verifikationstext kan bära
            //    dem om någon klistrat in text i beskrivningen.
            v = v.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

            if (v.Length == 0) return "\"\"";

            if (v.Contains(' ') || v.Contains('"'))
                return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

            return v;
        }

        /// <summary>
        /// Ett belopp som SIE vill ha det: punkt som decimaltecken, två decimaler, inga
        /// tusentalsavgränsare.
        /// <para>⚠️ <see cref="CultureInfo.InvariantCulture"/> är inte valfritt. Svensk kultur ger
        /// decimalKOMMA, och komma i ett SIE-belopp gör posten oläsbar.</para>
        /// </summary>
        public static string Amount(decimal value)
            => value.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>Datum som <c>ÅÅÅÅMMDD</c>.</summary>
        public static string Date(DateTime value) => value.ToString("yyyyMMdd");

        /// <summary>En hel post: <c>#NAMN fält fält …</c></summary>
        public static string Post(string name, params string?[] fields)
        {
            var sb = new StringBuilder("#").Append(name);
            foreach (var f in fields) sb.Append(' ').Append(Field(f));
            return sb.ToString();
        }

        /// <summary>
        /// Kontots SIE-typ ur numret: <c>T</c>illgång, <c>S</c>kuld, <c>I</c>ntäkt,
        /// <c>K</c>ostnad.
        ///
        /// <para><b>⚠️ Klass 2 är S och klass 8 är I</b> — samma indelning som resten av
        /// liggaren (<see cref="LedgerAccountClass"/>). Vore de olika skulle SIE-filen och
        /// bokslutet beskriva samma konto på två sätt.</para>
        /// </summary>
        public static string AccountType(int accountNumber) => LedgerAccountClass.Of(accountNumber) switch
        {
            1 => "T",
            2 => "S",
            3 => "I",
            8 => "I",
            _ => "K"
        };
    }
}
