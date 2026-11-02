using System.Security.Cryptography;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Vilken hemlighet mobilens JWT signeras med (säkerhetsplanen 2026-11-02, A3/H6).
    ///
    /// <para>Bakgrund: koden läste <c>JwtSettings:Secret</c>, prodfilen hade bara <c>JwtSettings:SecretKey</c>
    /// (som ingenting läste), och den spårade <c>appsettings.json</c> hade en platshållare som <c>Secret</c> som
    /// passerade valideringen (≥ 32 tecken). Prod signerade därför troligen med en nyckel som står i källkoden, så
    /// vem som helst som läser repot kunde smida en giltig token för valfri medlem.</para>
    ///
    /// <para>Nu: en riktig <c>Secret</c> används; annars den äldre nyckeln <c>SecretKey</c> (så prodfilen fungerar
    /// utan ändring, med en varning); annars — platshållare, tom eller för kort — en <b>slumpad nyckel för den här
    /// processen</b> (och ett Critical-larm utanför Development). Sajten lever alltså alltid, men en nyckel som
    /// står i repot används aldrig. Ingen JwtBearer-registrering hoppas över: <c>MatchApiController</c> kräver schemat
    /// också för webbanvändare.</para>
    /// </summary>
    public static class JwtSecretResolver
    {
        public const int MinimumLength = 32;

        public enum SecretSource { Configured, LegacySecretKey, Ephemeral }

        public sealed record Resolution(string Secret, SecretSource Source);

        /// <summary>Tom, kort eller en mall/platshållare ("YOUR_…", "CHANGE_ME", "your-…").</summary>
        public static bool IsWeakOrPlaceholder(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret)) return true;
            var s = secret.Trim();
            if (s.Length < MinimumLength) return true;
            return s.StartsWith("YOUR", StringComparison.OrdinalIgnoreCase)
                || s.Contains("CHANGE", StringComparison.OrdinalIgnoreCase)
                || s.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
                || s.Contains("your-secret", StringComparison.OrdinalIgnoreCase)
                || s.Contains("your_secret", StringComparison.OrdinalIgnoreCase);
        }

        public static Resolution Resolve(string? secret, string? legacySecretKey)
        {
            if (!IsWeakOrPlaceholder(secret)) return new Resolution(secret!.Trim(), SecretSource.Configured);
            if (!IsWeakOrPlaceholder(legacySecretKey)) return new Resolution(legacySecretKey!.Trim(), SecretSource.LegacySecretKey);
            return new Resolution(Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)), SecretSource.Ephemeral);
        }
    }
}
