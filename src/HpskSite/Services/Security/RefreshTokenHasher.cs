using System.Security.Cryptography;
using System.Text;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Refresh-token lagras bara som hash (säkerhetsplanen 2026-11-02, H6): den som läser databasen (en dump, en
    /// backup, en SQL-injektion) får inte en token som går att använda. Hashen är SHA-256 av tokenens UTF-8-bytes som
    /// 64 gemena hex-tecken — samma värde som migreringen räknar ur de gamla raderna
    /// (<c>LOWER(CONVERT(CHAR(64), HASHBYTES('SHA2_256', CAST(Token AS VARCHAR(500))), 2))</c>; tokens är ren
    /// base64 = ASCII, så VARCHAR-bytes och UTF-8-bytes är identiska). Tokens är 64 slumpbytes, så en vanlig
    /// (osaltad) hash räcker — den kan inte gissas fram.
    /// </summary>
    public static class RefreshTokenHasher
    {
        public static string Hash(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
