using System.Security.Cryptography;
using System.Text;
using HpskSite.Models.Firearms;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Kastas när något krypterat inte går att läsa. Bär ALDRIG klartext eller nyckelmaterial i
    /// meddelandet — men namnger de troliga orsakerna, eftersom de kräver helt olika åtgärder:
    /// fel rotnyckel (återställ nyckeln), manipulerad eller flyttad rad (utred), okänd version
    /// (koden är äldre än datat).
    /// </summary>
    public class FirearmCryptoException : Exception
    {
        public FirearmCryptoException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// REN kryptografi: inga anrop till databasen, ingen DI, inget tillstånd. Allt som kan gå fel
    /// här går att pröva i ett enhetstest, och det är hela skälet att lagret är eget.
    ///
    /// <para><b>Nyttolastens form:</b> <c>[version:1][nonce:12][ciphertext:n][tag:16]</c>, AES-256-GCM.
    /// Versionsbyten först gör det möjligt att byta form senare utan att gissa på längden.</para>
    ///
    /// <para><b>⚠️ AAD:n är inte dekoration — den är bindningen till RADEN.</b> Utan den kan någon med
    /// databasåtkomst kopiera medlem A:s <c>EncryptedDetails</c> till medlem B:s vapenrad, och B:s
    /// session skulle avkryptera och visa A:s uppgifter. Chiffret är därför bundet till (vapen-id,
    /// ägartyp, ägar-id) och till nyckelns version. Flyttas blobben MISSLYCKAS avkrypteringen; den
    /// returnerar aldrig fel ägares data.</para>
    ///
    /// <para><b>⚠️ Strängarna nedan får aldrig ändras</b> — de är en del av chiffret. En ändrad
    /// separator eller ett ändrat prefix gör varje redan lagrad rad oläsbar, utan kompileringsfel.
    /// Behöver formen ändras: höj <see cref="PayloadVersion"/> och behåll den gamla vägen.</para>
    /// </summary>
    public static class FirearmCrypto
    {
        public const byte PayloadVersion = 1;

        public const int KeySizeBytes = 32;   // AES-256
        public const int NonceSizeBytes = 12; // GCM-standard
        public const int TagSizeBytes = 16;

        /// <summary>Minsta möjliga nyttolast: en tom klartext bär ändå version, nonce och tag.</summary>
        public const int MinPayloadBytes = 1 + NonceSizeBytes + TagSizeBytes;

        /// <summary>En ny slumpad datanyckel (DEK). Anropas en gång per ägare, aldrig per vapen.</summary>
        public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeySizeBytes);

        /// <summary>
        /// Härleder ägarens nyckelinpackningsnyckel (KEK) ur rothemligheten. Per ägare OCH per
        /// nyckelversion, så en rotnyckelrotation ger en helt ny KEK utan att DEK:en ändras — och
        /// därmed utan att en enda vapenrad behöver krypteras om.
        /// </summary>
        public static byte[] DeriveKeyEncryptionKey(ReadOnlySpan<byte> rootSecret, FirearmScope scope, int keyVersion)
        {
            if (rootSecret.Length < KeySizeBytes)
                throw new FirearmCryptoException(
                    $"Rothemligheten är för kort ({rootSecret.Length} byte); minst {KeySizeBytes} byte krävs.");

            var info = Encoding.UTF8.GetBytes($"FirearmVault.v1|{scope.KindName}|{scope.Id}|k{keyVersion}");

            // Span-överlagringen skriver in i utdatabufferten i stället för att returnera en array —
            // den array-tagande överlagringen tar inte en ReadOnlySpan för ikm.
            // Tomt salt är avsiktligt: RFC 5869 låter salt utelämnas, och rothemligheten är redan
            // 32 slumpbytes med full entropi. Det som skiljer nycklarna åt ligger i info.
            var kek = new byte[KeySizeBytes];
            HKDF.DeriveKey(HashAlgorithmName.SHA256, rootSecret, kek, ReadOnlySpan<byte>.Empty, info);
            return kek;
        }

        /// <summary>AAD för en inpackad DEK. Binder inpackningen till ägaren och till nyckelversionen.</summary>
        public static string VaultAad(FirearmScope scope, int keyVersion)
            => $"vault|{scope.KindName}|{scope.Id}|k{keyVersion}";

        /// <summary>AAD för ett vapens uppgifter. Binder chiffret till den rad det ligger på.</summary>
        public static string DetailsAad(FirearmScope scope, int firearmId)
            => $"firearm|{firearmId}|{scope.KindName}|{scope.Id}";

        /// <summary>Krypterar och autentiserar. <paramref name="aad"/> måste vara identisk vid öppning.</summary>
        public static byte[] Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext, string aad)
        {
            RequireKey(key);

            var payload = new byte[1 + NonceSizeBytes + plaintext.Length + TagSizeBytes];
            payload[0] = PayloadVersion;

            var nonce = payload.AsSpan(1, NonceSizeBytes);
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = payload.AsSpan(1 + NonceSizeBytes, plaintext.Length);
            var tag = payload.AsSpan(1 + NonceSizeBytes + plaintext.Length, TagSizeBytes);

            using var gcm = new AesGcm(key, TagSizeBytes);
            gcm.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(aad));

            return payload;
        }

        /// <summary>
        /// Öppnar och verifierar. Kastar <see cref="FirearmCryptoException"/> vid fel nyckel, ändrad
        /// AAD (flyttad rad), manipulerat chiffer eller okänd version — <b>returnerar aldrig
        /// skräpdata och aldrig null</b>. Att svara tomt hade varit det värsta utfallet: ett saknat
        /// fält ser ut som att medlemmen inte fyllt i något.
        /// </summary>
        public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload, string aad)
        {
            RequireKey(key);

            if (payload.Length < MinPayloadBytes)
                throw new FirearmCryptoException(
                    $"Nyttolasten är för kort ({payload.Length} byte); minst {MinPayloadBytes} byte krävs. " +
                    "Raden är trunkerad eller inte krypterad av oss.");

            if (payload[0] != PayloadVersion)
                throw new FirearmCryptoException(
                    $"Okänd nyttolastversion {payload[0]} (koden hanterar {PayloadVersion}). " +
                    "Datat är skrivet av en nyare version än den som körs.");

            var nonce = payload.Slice(1, NonceSizeBytes);
            var cipherLength = payload.Length - MinPayloadBytes;
            var ciphertext = payload.Slice(1 + NonceSizeBytes, cipherLength);
            var tag = payload.Slice(1 + NonceSizeBytes + cipherLength, TagSizeBytes);

            var plaintext = new byte[cipherLength];
            try
            {
                using var gcm = new AesGcm(key, TagSizeBytes);
                gcm.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(aad));
            }
            catch (CryptographicException ex)
            {
                // GCM skiljer inte på orsakerna, så meddelandet räknar upp dem alla. Åtgärderna är
                // olika, och en operatör som bara får "kunde inte avkrypteras" letar på fel ställe.
                throw new FirearmCryptoException(
                    "Kunde inte avkryptera. Möjliga orsaker: fel eller saknad rotnyckel " +
                    "(Firearm:MasterKeys), raden har flyttats till en annan ägare eller ett annat " +
                    "vapen-id, eller chiffret har ändrats.", ex);
            }

            return plaintext;
        }

        public static byte[] SealString(ReadOnlySpan<byte> key, string plaintext, string aad)
            => Seal(key, Encoding.UTF8.GetBytes(plaintext ?? string.Empty), aad);

        public static string OpenString(ReadOnlySpan<byte> key, ReadOnlySpan<byte> payload, string aad)
            => Encoding.UTF8.GetString(Open(key, payload, aad));

        private static void RequireKey(ReadOnlySpan<byte> key)
        {
            if (key.Length != KeySizeBytes)
                throw new FirearmCryptoException(
                    $"Nyckeln måste vara exakt {KeySizeBytes} byte (var {key.Length}).");
        }
    }
}
