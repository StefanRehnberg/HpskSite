using System.Security.Cryptography;
using System.Text;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>Vad en kod används till. Strängarna lagras i <c>MemberToken.Purpose</c> — ändra dem aldrig.</summary>
    public static class MemberTokenPurpose
    {
        public const string PasswordReset = "password-reset";
        public const string AutoLogin = "auto-login";
        public const string Invitation = "invitation";
    }

    public enum MemberTokenCheck
    {
        /// <summary>Ingen kod utfärdad (eller redan använd).</summary>
        Missing,
        Expired,
        Mismatch,
        Valid
    }

    /// <summary>En rad i <c>MemberToken</c>. Tiderna är UTC (registrerade i <c>StoredTimeBasis</c>).</summary>
    [TableName("MemberToken")]
    [PrimaryKey("MemberId", AutoIncrement = false)]
    public class MemberTokenRow
    {
        public int MemberId { get; set; }
        public string Purpose { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }

    /// <summary>
    /// Engångskoderna i mejlade länkar — lösenordsåterställning, automatisk inloggning efter godkännande
    /// och inbjudan (skalplanen B5, 2026-10-19).
    ///
    /// <para><b>Varför en egen tabell:</b> koderna låg som medlemsegenskaper, så varje utfärdande och varje
    /// förbrukning var en hel medlemssparning (Umbracos skrivlås för medlemmar + omindexering i Examine).
    /// Och de låg i KLARTEXT i medlemmens uppgifter och i sökindexet. Här lagras bara en SHA-256 av koden;
    /// den som läser tabellen kan inte använda en kod.</para>
    ///
    /// <para>En kod per (medlem, syfte): en ny ersätter den förra, precis som egenskapen gjorde.
    /// Jämförelsen sker i konstant tid.</para>
    /// </summary>
    public class MemberTokenService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public MemberTokenService(IUmbracoDatabaseFactory databaseFactory) => _databaseFactory = databaseFactory;

        /// <summary>Hashen av en kod: SHA-256 av UTF-8, hex med versaler (samma som migreringens HASHBYTES).</summary>
        public static string Hash(string token)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

        /// <summary>Utfärdar (eller ersätter) medlemmens kod för syftet.</summary>
        public void Issue(int memberId, string purpose, string token, DateTime expiresUtc)
        {
            using var db = _databaseFactory.CreateDatabase();
            // ⚠️ Inget @@ROWCOUNT: NPoco läser det som en parameter. IF EXISTS med låset i stället.
            db.Execute(@"
SET XACT_ABORT ON; BEGIN TRANSACTION;
IF EXISTS (SELECT 1 FROM MemberToken WITH (UPDLOCK, HOLDLOCK) WHERE MemberId = @0 AND Purpose = @1)
    UPDATE MemberToken SET TokenHash = @2, CreatedUtc = @3, ExpiresUtc = @4 WHERE MemberId = @0 AND Purpose = @1;
ELSE
    INSERT INTO MemberToken (MemberId, Purpose, TokenHash, CreatedUtc, ExpiresUtc) VALUES (@0, @1, @2, @3, @4);
COMMIT TRANSACTION;",
                memberId, purpose, Hash(token), DateTime.UtcNow, ToUtc(expiresUtc));
        }

        /// <summary>Den utfärdade koden, eller null — för spärren mot upprepade begäranden.</summary>
        public MemberTokenRow? Get(int memberId, string purpose)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.FirstOrDefault<MemberTokenRow>(
                "SELECT MemberId, Purpose, TokenHash, CreatedUtc, ExpiresUtc FROM MemberToken WHERE MemberId = @0 AND Purpose = @1",
                memberId, purpose);
        }

        /// <summary>
        /// Prövar en kod. En utgången kod tas bort (som förut), en felaktig lämnas orörd — annars kunde
        /// vem som helst som känner e-postadressen förstöra medlemmens giltiga länk.
        /// </summary>
        public MemberTokenCheck Check(int memberId, string purpose, string? token)
        {
            var row = Get(memberId, purpose);
            if (row == null || string.IsNullOrEmpty(row.TokenHash)) return MemberTokenCheck.Missing;
            if (row.ExpiresUtc < DateTime.UtcNow)
            {
                Clear(memberId, purpose);
                return MemberTokenCheck.Expired;
            }
            if (string.IsNullOrEmpty(token)) return MemberTokenCheck.Mismatch;
            var a = Encoding.ASCII.GetBytes(row.TokenHash.ToUpperInvariant());
            var b = Encoding.ASCII.GetBytes(Hash(token));
            return CryptographicOperations.FixedTimeEquals(a, b) ? MemberTokenCheck.Valid : MemberTokenCheck.Mismatch;
        }

        /// <summary>Förbrukar koden (engångs).</summary>
        public void Clear(int memberId, string purpose)
        {
            using var db = _databaseFactory.CreateDatabase();
            db.Execute("DELETE FROM MemberToken WHERE MemberId = @0 AND Purpose = @1", memberId, purpose);
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
