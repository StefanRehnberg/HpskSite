using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (A3/H6): en JWT-hemlighet som står i repot används aldrig.</summary>
    public class JwtSecretResolverTests
    {
        private const string Strong = "k3J9xQ7vLm2Zr8TnWb4YcP6dHf1GsAe0UiOyNtXqRvMw5Bj";   // 48 tecken, inte en mall

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("tooshort")]
        [InlineData("YOUR_JWT_SECRET_KEY_MIN_32_CHARACTERS_LONG")]       // mallens platshållare
        [InlineData("change-me-change-me-change-me-change-me")]
        [InlineData("your-secret-key-your-secret-key-your-secret")]
        public void Svaga_eller_platshallare_avvisas(string? s) => Assert.True(JwtSecretResolver.IsWeakOrPlaceholder(s));

        [Fact]
        public void En_riktig_nyckel_godtas() => Assert.False(JwtSecretResolver.IsWeakOrPlaceholder(Strong));

        [Fact]
        public void Secret_vinner_over_SecretKey()
        {
            var r = JwtSecretResolver.Resolve(Strong, Strong + "x");
            Assert.Equal(JwtSecretResolver.SecretSource.Configured, r.Source);
            Assert.Equal(Strong, r.Secret);
        }

        [Fact]
        public void Platshallare_som_Secret_faller_tillbaka_pa_SecretKey()
        {
            // Prod: tracked appsettings.json har platshållaren, prodfilen har bara SecretKey.
            var r = JwtSecretResolver.Resolve("YOUR_JWT_SECRET_KEY_MIN_32_CHARACTERS_LONG", Strong);
            Assert.Equal(JwtSecretResolver.SecretSource.LegacySecretKey, r.Source);
            Assert.Equal(Strong, r.Secret);
        }

        [Fact]
        public void Ingen_riktig_nyckel_ger_en_slumpad_per_process_aldrig_platshallaren()
        {
            var a = JwtSecretResolver.Resolve("YOUR_JWT_SECRET_KEY_MIN_32_CHARACTERS_LONG", null);
            var b = JwtSecretResolver.Resolve("YOUR_JWT_SECRET_KEY_MIN_32_CHARACTERS_LONG", null);
            Assert.Equal(JwtSecretResolver.SecretSource.Ephemeral, a.Source);
            Assert.False(JwtSecretResolver.IsWeakOrPlaceholder(a.Secret));   // lång nog att användas
            Assert.NotEqual(a.Secret, b.Secret);                              // slumpad varje gång
            Assert.DoesNotContain("YOUR", a.Secret, StringComparison.OrdinalIgnoreCase);
        }
    }
}
