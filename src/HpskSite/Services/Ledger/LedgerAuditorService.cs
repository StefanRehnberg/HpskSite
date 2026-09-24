using System.Security.Cryptography;
using HpskSite.Models;
using HpskSite.Models.Ledger;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Revisorns åtkomst: inbjudan, mottagande, återkallning — och frågan "får den här medlemmen
    /// läsa den här föreningens räkenskaper?".
    ///
    /// <para><b>⚠️⚠️ TABELLEN LEVER I <c>dbo</c>, ALDRIG I SANDLÅDAN.</b> Man är revisor för en
    /// FÖRENING, inte för en kastbar liggare — därför går frågorna via den råa anslutningen och
    /// inte via <see cref="LedgerDb"/>. Skulle den gå genom schemaväljaren hade en sandlåda fått
    /// sina egna revisorer, som ingen kan återkalla från föreningens sida.</para>
    ///
    /// <para>LEDGER-SEAM-OK: revisorsuppdraget nycklas på FÖRENINGEN (OwnerType/OwnerId), aldrig
    /// på en utställare, och finns därför bara i den skarpa tabellen.</para>
    ///
    /// <para><b>⚠️ Inbjudan är en bärarnyckel.</b> Den som har länken kan ta emot den, så den
    /// lagras som hash, har en utgångstid och kan återkallas. Vad den ger är dessutom
    /// <b>läsning av EN förening</b> — inget mer.</para>
    /// </summary>
    public class LedgerAuditorService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<LedgerAuditorService> _logger;

        /// <summary>
        /// ⚠️ 13 månader (Stefan 2026-09-23): uppdraget löper till nästa årsmöte, som kan ligga
        /// något senare året efter. Talet bor HÄR och inte utspritt i anropen — en livslängd som
        /// skrivs på två ställen blir två olika livslängder.
        /// </summary>
        public const int GrantMonths = 13;

        public LedgerAuditorService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<LedgerAuditorService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// Skapar en inbjudan och svarar med den KLARTEXTA token.
        ///
        /// <para><b>⚠️ Token returneras EN gång och lagras aldrig.</b> Tappas den bort är rätt
        /// åtgärd en ny inbjudan, inte en uppslagning — det är vad hashningen betyder.</para>
        /// </summary>
        public (bool Ok, string? Error, string? Token, int GrantId) Invite(
            int ownerType, int ownerId, string email, string name, int byMemberId)
        {
            if (ownerId <= 0) return (false, "Revisorn bjuds in till en förening, inte till en sandlåda.", null, 0);
            if (string.IsNullOrWhiteSpace(email)) return (false, "Ange revisorns e-postadress.", null, 0);
            if (string.IsNullOrWhiteSpace(name)) return (false, "Ange revisorns namn.", null, 0);
            if (byMemberId <= 0) return (false, "Du måste vara inloggad.", null, 0);

            var clean = email.Trim();
            if (!clean.Contains('@') || clean.Length < 5)
                return (false, "E-postadressen ser inte riktig ut.", null, 0);

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                // ⚠️ En levande inbjudan till samma adress ersätts inte tyst. Två giltiga länkar
                //    till samma person är två vägar in som ska återkallas var för sig, och den
                //    som bjuder in vet inte om att den andra finns.
                var live = db.ExecuteScalar<int>(
                    @"SELECT COUNT(1) FROM dbo.LedgerAuditorGrant
                       WHERE OwnerType = @0 AND OwnerId = @1 AND Email = @2
                         AND RevokedUtc IS NULL AND ExpiresUtc > @3",
                    ownerType, ownerId, clean, DateTime.UtcNow);

                if (live > 0)
                    return (false, "Det finns redan en giltig inbjudan till den adressen. Återkalla den först om du vill skicka en ny.", null, 0);

                var token = NewToken();

                db.Execute(
                    @"INSERT INTO dbo.LedgerAuditorGrant
                        (OwnerType, OwnerId, Email, Name, TokenHash,
                         InvitedByMemberId, InvitedUtc, ExpiresUtc)
                      VALUES (@0, @1, @2, @3, @4, @5, @6, @7)",
                    ownerType, ownerId, clean, name.Trim(), Hash(token),
                    byMemberId, DateTime.UtcNow, DateTime.UtcNow.AddMonths(GrantMonths));

                var id = db.ExecuteScalar<int>(
                    @"SELECT TOP 1 Id FROM dbo.LedgerAuditorGrant
                       WHERE TokenHash = @0", Hash(token));

                return (true, null, token, id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte bjuda in revisor till {Typ}/{Id}.", ownerType, ownerId);
                return (false, "Inbjudan kunde inte skapas. Försök igen.", null, 0);
            }
        }

        /// <summary>Inbjudan bakom en token, utan att ta emot den. Null när den inte finns.</summary>
        public LedgerAuditorGrant? FindByToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                return db.Fetch<LedgerAuditorGrant>(
                    "SELECT * FROM dbo.LedgerAuditorGrant WHERE TokenHash = @0", Hash(token))
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte slå upp revisorsinbjudan.");
                return null;
            }
        }

        /// <summary>
        /// Knyter inbjudan till ett konto.
        ///
        /// <para><b>⚠️ Villkoret ligger i WHERE, inte i en if.</b> Två samtidiga öppningar av samma
        /// länk får inte båda lyckas — den andra ska se att den redan är mottagen. En läsning
        /// följd av en skrivning hade släppt igenom båda.</para>
        /// </summary>
        public (bool Ok, string? Error) Accept(string token, int memberId)
        {
            if (memberId <= 0) return (false, "Inget konto att knyta inbjudan till.");

            var grant = FindByToken(token);
            if (grant is null) return (false, "Länken gäller inte.");
            if (grant.IsRevoked) return (false, "Inbjudan är återkallad av föreningen.");
            if (grant.ExpiresUtc <= DateTime.UtcNow) return (false, "Inbjudan har gått ut. Be föreningen skicka en ny.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var rows = db.Execute(
                    @"UPDATE dbo.LedgerAuditorGrant
                         SET MemberId = @1, AcceptedUtc = @2
                       WHERE Id = @0 AND AcceptedUtc IS NULL AND RevokedUtc IS NULL
                         AND ExpiresUtc > @2",
                    grant.Id, memberId, DateTime.UtcNow);

                return rows == 0
                    ? (false, "Inbjudan är redan mottagen. Logga in med det konto den kopplades till.")
                    : (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte ta emot revisorsinbjudan {Id}.", grant.Id);
                return (false, "Inbjudan kunde inte tas emot. Försök igen.");
            }
        }

        /// <summary>
        /// Har medlemmen en levande revisorsåtkomst till föreningen?
        ///
        /// <para><b>⚠️ Villkoren står i SQL:en, inte i C#.</b> Frågan ställs vid varje sidladdning,
        /// och en filtrering i minnet hade hämtat hem varje inbjudan föreningen någonsin
        /// skickat.</para>
        /// </summary>
        public bool HasAccess(int ownerType, int ownerId, int memberId)
        {
            if (memberId <= 0 || ownerId <= 0) return false;

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                if (db.ExecuteScalar<int>(
                        @"SELECT COUNT(1) FROM dbo.LedgerAuditorGrant
                           WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2
                             AND AcceptedUtc IS NOT NULL
                             AND RevokedUtc IS NULL
                             AND ExpiresUtc > @3",
                        ownerType, ownerId, memberId, DateTime.UtcNow) > 0)
                    return true;

                return HasElectedRole(db, ownerType, ownerId, memberId);
            }
            catch (Exception ex)
            {
                // ⚠️ Fel = NEKAD. En trasig fråga får aldrig öppna räkenskaperna.
                _logger.LogError(ex, "Kunde inte pröva revisorsåtkomst för medlem {Id}.", memberId);
                return false;
            }
        }

        /// <summary>
        /// Är medlemmen VALD revisor eller revisorssuppleant i föreningens uppgifter?
        ///
        /// <para><b>⚠️⚠️ SEDAN 2026-09-24 RÄCKER DET.</b> Inbjudningslänken byggde på antagandet att
        /// <i>"klubbrevisorn har oftast inget konto hos oss"</i>. Michael Henriksson (Åmåls PK):
        /// revisorerna läggs in i föreningens uppgifter och är till 99 % medlemmar. Den valda
        /// revisorn får därför läsrätt direkt; länken finns kvar för den externa.</para>
        ///
        /// <para><b>⚠️ Rollen är IsBoardMember = 0</b> och frågas utan den flaggan — revisorn får
        /// aldrig räknas in i beslutsförheten i den styrelse hen granskar. Aktiv = IsActive, samma
        /// regel som styrelsens läsrätt: ett utgånget mandat revoquerar inte.</para>
        /// </summary>
        private static bool HasElectedRole(IUmbracoDatabase db, int ownerType, int ownerId, int memberId)
            => db.ExecuteScalar<int>(
                @"SELECT COUNT(1) FROM BoardRoles
                   WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2
                     AND IsActive = 1 AND RoleKey IN (@3)",
                ownerType, ownerId, memberId, HpskSite.Models.BoardRoleDefinitions.AuditorRoleKeys) > 0;

        /// <summary>
        /// Var medlemmen är revisor: inbjudningar och valda uppdrag. Driver revisionssidans val av
        /// förening. <c>ExpiresUtc</c> null = valt uppdrag (löper till nästa årsmöte, inte på tid).
        /// </summary>
        public List<(int OwnerType, int OwnerId, DateTime? ExpiresUtc)> AssignmentsForMember(int memberId)
        {
            var result = GrantsForMember(memberId)
                .Select(g => (g.OwnerType, g.OwnerId, (DateTime?)g.ExpiresUtc))
                .ToList();

            if (memberId <= 0) return result;

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var elected = db.Fetch<ElectedRow>(
                    @"SELECT DISTINCT OwnerType, OwnerId FROM BoardRoles
                       WHERE MemberId = @0 AND IsActive = 1 AND RoleKey IN (@1)",
                    memberId, HpskSite.Models.BoardRoleDefinitions.AuditorRoleKeys);

                foreach (var e in elected)
                    if (!result.Any(r => r.OwnerType == e.OwnerType && r.OwnerId == e.OwnerId))
                        result.Add((e.OwnerType, e.OwnerId, null));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa valda revisorsuppdrag för medlem {Id}.", memberId);
            }

            return result.OrderBy(r => r.OwnerType).ThenBy(r => r.OwnerId).ToList();
        }

        /// <summary>De valda revisorerna i föreningens uppgifter — för listan under Ekonomi → Revisorer.</summary>
        public List<(int MemberId, string RoleKey)> ElectedForOwner(int ownerType, int ownerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                return db.Fetch<ElectedMember>(
                        @"SELECT MemberId, RoleKey FROM BoardRoles
                           WHERE OwnerType = @0 AND OwnerId = @1 AND IsActive = 1 AND RoleKey IN (@2)
                           ORDER BY SortOrder",
                        ownerType, ownerId, HpskSite.Models.BoardRoleDefinitions.AuditorRoleKeys)
                    .Select(r => (r.MemberId, r.RoleKey))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa valda revisorer för {Typ}/{Id}.", ownerType, ownerId);
                return new();
            }
        }

        private class ElectedRow { public int OwnerType { get; set; } public int OwnerId { get; set; } }
        private class ElectedMember { public int MemberId { get; set; } public string RoleKey { get; set; } = ""; }

        /// <summary>Inbjudna uppdrag en medlem har (bara inbjudningar). Se <see cref="AssignmentsForMember"/>.</summary>
        public List<LedgerAuditorGrant> GrantsForMember(int memberId)
        {
            if (memberId <= 0) return new List<LedgerAuditorGrant>();

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                return db.Fetch<LedgerAuditorGrant>(
                    @"SELECT * FROM dbo.LedgerAuditorGrant
                       WHERE MemberId = @0 AND AcceptedUtc IS NOT NULL
                         AND RevokedUtc IS NULL AND ExpiresUtc > @1
                       ORDER BY OwnerType, OwnerId",
                    memberId, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa revisorsuppdrag för medlem {Id}.", memberId);
                return new List<LedgerAuditorGrant>();
            }
        }

        /// <summary>Föreningens egen lista — inklusive återkallade och utgångna.</summary>
        public List<LedgerAuditorGrant> ListForOwner(int ownerType, int ownerId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                return db.Fetch<LedgerAuditorGrant>(
                    @"SELECT * FROM dbo.LedgerAuditorGrant
                       WHERE OwnerType = @0 AND OwnerId = @1
                       ORDER BY Id DESC",
                    ownerType, ownerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa revisorslistan för {Typ}/{Id}.", ownerType, ownerId);
                return new List<LedgerAuditorGrant>();
            }
        }

        /// <summary>
        /// Återkallar åtkomsten.
        /// <para><b>⚠️ Raden raderas aldrig.</b> Vem som fick läsa föreningens räkenskaper, när,
        /// och vem som stängde det är precis den sortens uppgift som ska gå att läsa i efterhand.</para>
        /// </summary>
        public (bool Ok, string? Error) Revoke(
            int ownerType, int ownerId, int grantId, string? reason, int byMemberId)
        {
            if (byMemberId <= 0) return (false, "Du måste vara inloggad.");

            try
            {
                using var db = _databaseFactory.CreateDatabase();

                var rows = db.Execute(
                    @"UPDATE dbo.LedgerAuditorGrant
                         SET RevokedUtc = @3, RevokedByMemberId = @4, RevokeReason = @5
                       WHERE Id = @0 AND OwnerType = @1 AND OwnerId = @2 AND RevokedUtc IS NULL",
                    grantId, ownerType, ownerId, DateTime.UtcNow, byMemberId,
                    string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

                return rows == 0
                    ? (false, "Åtkomsten var redan återkallad, eller hör till en annan förening.")
                    : (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte återkalla revisorsåtkomst {Id}.", grantId);
                return (false, "Återkallningen kunde inte sparas.");
            }
        }

        /// <summary>
        /// Stämplar att revisorn varit inne.
        /// <para>⚠️ Best-effort och tyst: en misslyckad stämpel får aldrig hindra en läsning.</para>
        /// </summary>
        public void Touch(int ownerType, int ownerId, int memberId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();

                db.Execute(
                    @"UPDATE dbo.LedgerAuditorGrant SET LastSeenUtc = @3
                       WHERE OwnerType = @0 AND OwnerId = @1 AND MemberId = @2
                         AND RevokedUtc IS NULL",
                    ownerType, ownerId, memberId, DateTime.UtcNow);
            }
            catch
            {
                // Tyst med flit.
            }
        }

        /// <summary>
        /// 32 slumpbyte som URL-säker base64.
        /// <para>⚠️ <c>RandomNumberGenerator</c>, aldrig <c>Guid</c> eller <c>Random</c> — det här
        /// är en nyckel till en förenings hela ekonomi.</para>
        /// </summary>
        private static string NewToken() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private static string Hash(string token) =>
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)))
                .ToLowerInvariant();
    }
}
