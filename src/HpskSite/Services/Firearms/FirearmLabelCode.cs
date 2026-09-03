using System.Security.Cryptography;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Vapenetikettens korta kod — den som står i QR-koden på vapnet.
    ///
    /// <para><b>⚠️ FORMEN ÄR VALD FÖR ATT QR-KODEN SKA BLI LITEN, inte för att den är snygg.</b>
    /// QR har ett <i>alfanumeriskt</i> läge som packar två tecken per elva bitar, men bara för
    /// tecknen <c>0-9 A-Z</c> och <c>SPACE $ % * + - . / :</c>. En enda gemen bokstav — eller ett
    /// <c>_</c>, eller ett <c>=</c> ur en base64-sträng — tvingar hela koden till byte-läge med åtta
    /// bitar per tecken, och koden växer med flera versioner. Därför är alfabetet versalt, därför
    /// är adressen versal, och därför får ingenting i den här strängen bli base64.</para>
    ///
    /// <para><b>⚠️ Alfabetet saknar I, L, O och U</b> (Crockford-base32). De tre första förväxlas
    /// med 1, 1 och 0 av den som läser koden med ögat i stället för med kameran, och U tas bort så
    /// att slumpen aldrig stavar något olämpligt på en etikett en klubb ska sätta på sin egendom.
    /// Att alfabetet saknar dem gör dessutom <see cref="Normalize"/> förlustfri: eftersom ingen
    /// giltig kod innehåller I, L, O eller U kan de mappas till 1, 1, 0 och V utan att en kod
    /// någonsin blir en ANNAN giltig kod.</para>
    ///
    /// <para><b>⚠️ Koden är hemligheten.</b> Den är inte vapnets id och får aldrig bli det: en
    /// uppräkningsbar etikettadress hade låtit vilken inloggad medlem som helst checka ut vilket
    /// vapen som helst utan att stå framför det, och hela riktighetsvinsten i skanningen är att
    /// den inte kan ha fel om vilket vapen det är. <see cref="Length"/> tecken ur ett 32-teckens
    /// alfabet är ~50 bitar.</para>
    /// </summary>
    public static class FirearmLabelCode
    {
        /// <summary>
        /// Kodens längd. <b>⚠️ Ändra den inte utan att räkna om QR-versionen.</b> Med adressen
        /// <c>HTTPS://WWW.PISTOL.NU/V/</c> (24 tecken) rymmer QR-version 3 med felkorrigering Q
        /// 47 alfanumeriska tecken — alltså finns det utrymme, men inte oändligt: passeras 47
        /// hoppar koden till version 4 och etiketten måste tryckas större igen.
        /// </summary>
        public const int Length = 10;

        /// <summary>Crockford-base32: 32 tecken, utan I, L, O och U.</summary>
        public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>
        /// En ny slumpad kod.
        ///
        /// <para><b>⚠️ <see cref="RandomNumberGenerator"/>, inte <see cref="Random"/>.</b> Koden ÄR
        /// grinden till ett vapen — en förutsägbar generator gör alfabetets 50 bitar till noll,
        /// och skillnaden syns inte på en etikett som ser precis lika slumpmässig ut.</para>
        ///
        /// <para>Modulo-skevheten som annars följer av <c>byte % 32</c> finns inte här: 256 är
        /// jämnt delbart med 32, så varje tecken är likformigt.</para>
        /// </summary>
        public static string Next()
        {
            var bytes = RandomNumberGenerator.GetBytes(Length);
            var chars = new char[Length];
            for (var i = 0; i < Length; i++)
                chars[i] = Alphabet[bytes[i] % Alphabet.Length];
            return new string(chars);
        }

        /// <summary>
        /// Koden som den ska slås upp, eller <c>null</c> om strängen omöjligt kan vara en kod.
        ///
        /// <para>Versaliserar, tar bort blanksteg och bindestreck (den som läser upp en kod i
        /// telefon grupperar den), och mappar de fyra tecken alfabetet saknar till det de
        /// förväxlats med. En sträng som ändå inte är exakt <see cref="Length"/> tecken ur
        /// alfabetet ger <c>null</c> — då slås ingenting upp, och databasen slipper en fråga per
        /// slumpmässig skanning av något helt annat.</para>
        /// </summary>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var chars = new char[Length];
            var n = 0;

            foreach (var c in raw.Trim().ToUpperInvariant())
            {
                if (c is ' ' or '-' or '\t') continue;

                var mapped = c switch
                {
                    'I' or 'L' => '1',
                    'O' => '0',
                    'U' => 'V',
                    _ => c,
                };

                if (Alphabet.IndexOf(mapped) < 0) return null;
                if (n >= Length) return null;   // För lång — inte en kod.
                chars[n++] = mapped;
            }

            return n == Length ? new string(chars) : null;
        }

        /// <summary>
        /// Etikettens fullständiga adress — <b>den enda platsen den byggs på</b>.
        ///
        /// <para><b>⚠️ VERSALT I SIN HELHET, och det är inte kosmetik.</b> Adressen ska falla inom
        /// QR:ens alfanumeriska läge; en enda gemen bokstav i värdnamnet kastar hela koden till
        /// byte-läge och lägger på flera versioner, alltså moduler, alltså millimetrar på en
        /// etikett som ska få plats på ett vapen. Schema och värdnamn är skiftlägesokänsliga per
        /// RFC 3986, och ASP.NET-routing matchar sökvägen skiftlägesokänsligt, så versalerna
        /// kostar ingenting.</para>
        ///
        /// <para><b>⚠️ Sökvägen är kort med flit.</b> Varje tecken i adressen är tecken i koden —
        /// <c>/lanevapen/skanna?t=</c> hade i sig ätit upp nästan hela version 3.</para>
        /// </summary>
        public static string Url(string scheme, string host, string code) =>
            $"{scheme}://{host}/v/{code}".ToUpperInvariant();
    }
}
