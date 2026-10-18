using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// <b>Skalplanen E: medlemmarnas lösenord går att flytta från Umbraco.</b> En medlems hash ur <c>cmsMember</c>
    /// (formatet <c>PBKDF2.ASPNETCORE.V3</c>) verifieras här på två sätt utan Umbraco: med ASP.NET Core Identitys
    /// vanliga <see cref="PasswordHasher{TUser}"/>, och med en egen avkodning av formatet som bara använder PBKDF2
    /// ur .NET:s kryptobibliotek. Det andra visar att formatet är dokumenterat och går att läsa i vilken plattform
    /// som helst — ingen medlem behöver byta lösenord vid ett byte av CMS.
    ///
    /// <para>Körs mot dev-databasen när <c>HPSK_TEST_SQL</c> är satt. Testkontona har lösenordet 123456 (bara dev).
    /// Vilka format prod har: <c>Migrations/check-prod-password-formats-2026-10-18.sql</c>.</para>
    /// </summary>
    public class PasswordHashPortabilityTests
    {
        private const string KnownPassword = "123456";
        private static readonly string[] TestAccounts =
            { "builder.claude@pistol.nu", "admin.claude@pistol.nu", "collaborator.claude@pistol.nu", "outsider.claude@pistol.nu" };

        private static List<(string Email, string Config, string Hash)> ReadTestAccounts()
        {
            var list = new List<(string, string, string)>();
            using var conn = new SqlConnection(Environment.GetEnvironmentVariable("HPSK_TEST_SQL"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Email, ISNULL(passwordConfig, ''), Password FROM cmsMember WHERE Email IN (@a, @b, @c, @d)";
            for (var i = 0; i < TestAccounts.Length; i++) cmd.Parameters.AddWithValue("@" + (char)('a' + i), TestAccounts[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
            return list;
        }

        [SqlFact]
        public void Identitys_PasswordHasher_verifierar_medlemmarnas_hashar_utan_Umbraco()
        {
            var accounts = ReadTestAccounts();
            accounts.Should().NotBeEmpty("utan testkonton prövar testet ingenting");
            var hasher = new PasswordHasher<object>();
            foreach (var (email, config, hash) in accounts)
            {
                config.Should().Contain("PBKDF2.ASPNETCORE.V3", email);
                hasher.VerifyHashedPassword(new object(), hash, KnownPassword)
                    .Should().NotBe(PasswordVerificationResult.Failed, email);
                hasher.VerifyHashedPassword(new object(), hash, KnownPassword + "x")
                    .Should().Be(PasswordVerificationResult.Failed, email + " (fel lösenord ska nekas)");
            }
        }

        [SqlFact]
        public void Formatet_gar_att_lasa_med_bara_PBKDF2()
        {
            var accounts = ReadTestAccounts();
            accounts.Should().NotBeEmpty();
            foreach (var (email, _, hash) in accounts)
            {
                V3.Verify(hash, KnownPassword).Should().BeTrue(email);
                V3.Verify(hash, "fel").Should().BeFalse(email);
            }
        }

        /// <summary>
        /// Varje medlems hash har ett V3-huvud som avkodningen kan läsa (utan att känna lösenordet).
        /// <para>⚠️ <c>___UIDEMPTYPWORD___…</c> är Umbracos platshållare för en medlem UTAN lösenord (importerad
        /// eller seedad, har aldrig loggat in — 244 i dev 2026-10-18). Ingen hash att flytta; medlemmen sätter
        /// ett lösenord via "glömt lösenord", som i dag.</para>
        /// </summary>
        [SqlFact]
        public void Varje_medlems_hash_har_ett_lasbart_V3_huvud()
        {
            var bad = new List<string>();
            var count = 0;
            using var conn = new SqlConnection(Environment.GetEnvironmentVariable("HPSK_TEST_SQL"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT nodeId, Password FROM cmsMember WHERE Password IS NOT NULL AND Password <> '' AND Password NOT LIKE '[_][_][_]UIDEMPTYPWORD%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                count++;
                if (!V3.TryReadHeader(r.GetString(1), out _, out _, out _)) bad.Add(r.GetInt32(0).ToString());
            }
            count.Should().BeGreaterThan(0);
            bad.Should().BeEmpty("de här medlemmarnas hashar är inte V3: " + string.Join(", ", bad.Take(20)));
        }

        [Fact]
        public void Egen_avkodning_och_Identity_ar_overens_om_en_ny_hash()
        {
            var hash = new PasswordHasher<object>().HashPassword(new object(), "Sommar-2026!");
            V3.Verify(hash, "Sommar-2026!").Should().BeTrue();
            V3.Verify(hash, "sommar-2026!").Should().BeFalse();
        }

        /// <summary>
        /// ASP.NET Core Identitys V3-format: 0x01 | PRF (uint32, big endian) | iterationer (uint32) |
        /// saltlängd (uint32) | salt | härledd nyckel. PRF 0 = SHA1, 1 = SHA256, 2 = SHA512.
        /// </summary>
        private static class V3
        {
            public static bool TryReadHeader(string base64, out int prf, out int iterations, out int saltLength)
            {
                prf = iterations = saltLength = 0;
                byte[] b;
                try { b = Convert.FromBase64String(base64); } catch (FormatException) { return false; }
                if (b.Length < 13 || b[0] != 0x01) return false;
                prf = b[1] << 24 | b[2] << 16 | b[3] << 8 | b[4];
                iterations = b[5] << 24 | b[6] << 16 | b[7] << 8 | b[8];
                saltLength = b[9] << 24 | b[10] << 16 | b[11] << 8 | b[12];
                return prf is >= 0 and <= 2 && iterations > 0 && saltLength >= 8 && 13 + saltLength < b.Length;
            }

            public static bool Verify(string base64, string password)
            {
                var bytes = Convert.FromBase64String(base64);
                if (bytes.Length < 13 || bytes[0] != 0x01) return false;
                static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
                var prf = U32(bytes, 1);
                var iterations = (int)U32(bytes, 5);
                var saltLength = (int)U32(bytes, 9);
                var salt = bytes.AsSpan(13, saltLength).ToArray();
                var expected = bytes.AsSpan(13 + saltLength).ToArray();
                var algorithm = prf switch { 0 => HashAlgorithmName.SHA1, 1 => HashAlgorithmName.SHA256, 2 => HashAlgorithmName.SHA512, _ => default };
                if (algorithm == default) return false;
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
        }
    }
}
