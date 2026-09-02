using HpskSite.Models.Firearms;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Firearms
{
    /// <summary>
    /// Äger <c>FirearmKeyVault</c>: hämtar, skapar, packar om och förstör ägarnas datanycklar.
    /// Ingen annan kod ska röra tabellen.
    ///
    /// <para><b>⚠️ Läsning skapar ALDRIG en nyckel.</b> Skulle en läsning skapa ett tomt valv för en
    /// ägare vars rad har raderats, skulle varje befintlig vapenrad bli oläsbar med den nya nyckeln
    /// — och felet hade lästs som "uppgifterna är borta" i stället för "nyckeln är borta". Skapandet
    /// sker bara på skrivvägen, där en ny nyckel är rätt svar.</para>
    /// </summary>
    public class FirearmVaultService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly FirearmMasterKeyRing _keyRing;
        private readonly ILogger<FirearmVaultService> _logger;

        public FirearmVaultService(
            IScopeProvider scopeProvider,
            FirearmMasterKeyRing keyRing,
            ILogger<FirearmVaultService> logger)
        {
            _scopeProvider = scopeProvider;
            _keyRing = keyRing;
            _logger = logger;
        }

        /// <summary>
        /// Ägarens datanyckel i klartext, eller <c>null</c> när inget valv finns (aldrig skrivet, eller
        /// kryptografiskt raderat). Anroparen måste skilja på de två — därför null och inte ett kast.
        /// </summary>
        public byte[]? TryGetDataKey(FirearmScope scope)
        {
            RequireValid(scope);
            var row = FetchRow(scope);
            return row is null ? null : UnwrapDek(row);
        }

        /// <summary>
        /// Ägarens datanyckel, skapad om den inte finns. Bara för SKRIVVÄGEN.
        ///
        /// <para><b>⚠️ Samtidighetsfaran som måste hanteras här:</b> två samtidiga första skrivningar
        /// för samma medlem skulle båda se ett tomt valv och båda skapa en DEK. Det unika indexet
        /// låter bara den ena landa — och utan den här återläsningen hade förloraren sedan krypterat
        /// med en nyckel som ingen rad bär, alltså skrivit data ingen kan läsa. Vid en krock läses
        /// den vinnande raden i stället; det är en RIKTIG kodväg, inte en teoretisk.</para>
        /// </summary>
        public byte[] GetOrCreateDataKey(FirearmScope scope)
        {
            RequireValid(scope);

            var existing = FetchRow(scope);
            if (existing is not null) return UnwrapDek(existing);

            if (!_keyRing.CanWrapNewKeys)
                throw new FirearmCryptoException(
                    $"Kan inte skapa ett valv för {scope}: rotnyckel version " +
                    $"{_keyRing.CurrentKeyVersion} saknas. Se Documentation/FIREARM_KEY_MANAGEMENT.md.");

            var dek = FirearmCrypto.NewDataKey();
            var version = _keyRing.CurrentKeyVersion;
            var row = new FirearmVaultKey
            {
                ScopeKind = scope.KindName,
                ScopeId = scope.Id,
                KeyVersion = version,
                WrappedDek = WrapDek(dek, scope, version),
                CreatedAt = DateTime.Now,
            };

            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                uow.Database.Insert(row);
            }
            catch (Exception ex)
            {
                // Kan vara den unika indexkrocken (SQL 2627/2601) eller något helt annat. Fråga
                // databasen i stället för att tolka undantaget — svaret är entydigt och
                // leverantörsoberoende: finns raden nu, vann någon annan kapplöpningen.
                var winner = FetchRow(scope);
                if (winner is null)
                {
                    CryptographicWipe(dek);
                    throw;
                }

                _logger.LogInformation(
                    "Valvet för {Scope} skapades samtidigt av en annan begäran; använder den lagrade nyckeln. ({Message})",
                    scope, ex.Message);

                CryptographicWipe(dek);
                return UnwrapDek(winner);
            }

            _logger.LogInformation("Skapade valv för {Scope} med nyckelversion {Version}.", scope, version);
            return dek;
        }

        /// <summary>
        /// Kryptografisk radering: tar bort valvsraden. Ägarens vapenuppgifter är därefter oläsbara
        /// för alla, oss inräknade. Returnerar false när det inte fanns något valv.
        ///
        /// <para><b>Vapenraderna raderas INTE här.</b> Det är avsiktligt — de bär klartextkolumner
        /// (alias, vapenklass, förfallodatum) som kan behöva finnas kvar, och vem som får radera dem
        /// är en behörighetsfråga som hör i anropande lager. Den här metoden gör en sak: gör
        /// hemligheterna oläsbara.</para>
        /// </summary>
        public bool Shred(FirearmScope scope)
        {
            RequireValid(scope);

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var affected = uow.Database.Execute(
                "DELETE FROM FirearmKeyVault WHERE ScopeKind = @0 AND ScopeId = @1",
                scope.KindName, scope.Id);

            if (affected > 0)
                _logger.LogWarning("Valvet för {Scope} raderades. Ägarens vapenuppgifter är nu oläsbara.", scope);

            return affected > 0;
        }

        /// <summary>
        /// Packar om varje valv som inte redan ligger på den aktuella nyckelversionen.
        /// <b>DEK:en är oförändrad, så ingen vapenrad krypteras om</b> — det är hela vinsten med
        /// kuvertkonstruktionen. Idempotent; körs om utan skada.
        ///
        /// <para>⚠️ Den GAMLA rotnyckeln måste ligga kvar i konfigurationen under körningen. Ett valv
        /// vars gamla version är borttagen kan inte packas om, och det rapporteras som ett fel i
        /// stället för att hoppas över tyst.</para>
        /// </summary>
        public FirearmRewrapResult RewrapAll()
        {
            var target = _keyRing.CurrentKeyVersion;
            if (!_keyRing.CanWrapNewKeys)
                throw new FirearmCryptoException(
                    $"Kan inte packa om: rotnyckel version {target} saknas i '{FirearmMasterKeyRing.MasterKeysPath}'.");

            var result = new FirearmRewrapResult { TargetVersion = target };

            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            var db = uow.Database;

            var stale = db.Fetch<FirearmVaultKey>(
                "SELECT * FROM FirearmKeyVault WHERE KeyVersion <> @0 ORDER BY Id", target);
            result.Examined = stale.Count;

            foreach (var row in stale)
            {
                try
                {
                    var scope = row.Scope;
                    var dek = UnwrapDek(row);
                    var rewrapped = WrapDek(dek, scope, target);
                    CryptographicWipe(dek);

                    db.Execute(
                        "UPDATE FirearmKeyVault SET WrappedDek = @0, KeyVersion = @1, RotatedAt = @2 WHERE Id = @3",
                        rewrapped, target, DateTime.Now, row.Id);

                    result.Rewrapped++;
                }
                catch (Exception ex)
                {
                    // Ett valv som inte går att packa om är ett larm, inte en rad att hoppa över:
                    // dess data blir oläsbar den dag den gamla nyckeln tas bort.
                    result.Failures.Add($"Valv {row.Id} ({row.ScopeKind}:{row.ScopeId}, " +
                                        $"version {row.KeyVersion}): {ex.Message}");
                    _logger.LogError(ex, "Kunde inte packa om valv {VaultId}.", row.Id);
                }
            }

            _logger.LogInformation(
                "Ompackning klar: {Rewrapped} av {Examined} valv till version {Version}, {Failed} fel.",
                result.Rewrapped, result.Examined, target, result.Failures.Count);

            return result;
        }

        /// <summary>
        /// Antal valv, och hur många som ligger på en version vi inte har nyckeln till. Läses av
        /// startkontrollen. <b>Svarar <c>null</c> när tabellen inte finns</b> — en omigrerad miljö är
        /// inte ett larm, den har ingen krypterad data.
        /// </summary>
        public FirearmVaultInventory? TryGetInventory()
        {
            try
            {
                using var uow = _scopeProvider.CreateScope(autoComplete: true);
                var db = uow.Database;

                if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.FirearmKeyVault','U') IS NULL THEN 0 ELSE 1 END") == 0)
                    return null;

                var versions = db.Fetch<VersionCount>(
                    "SELECT KeyVersion, COUNT(*) AS [Count] FROM FirearmKeyVault GROUP BY KeyVersion");

                var known = _keyRing.AvailableVersions.ToHashSet();
                return new FirearmVaultInventory
                {
                    TotalVaults = versions.Sum(v => v.Count),
                    UnreadableVaults = versions.Where(v => !known.Contains(v.KeyVersion)).Sum(v => v.Count),
                    MissingVersions = versions.Where(v => !known.Contains(v.KeyVersion))
                                              .Select(v => v.KeyVersion).OrderBy(v => v).ToList(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte inventera FirearmKeyVault.");
                return null;
            }
        }

        // ── Internt ──────────────────────────────────────────────────────────────────────────────

        private FirearmVaultKey? FetchRow(FirearmScope scope)
        {
            using var uow = _scopeProvider.CreateScope(autoComplete: true);
            return uow.Database.FirstOrDefault<FirearmVaultKey>(
                "SELECT * FROM FirearmKeyVault WHERE ScopeKind = @0 AND ScopeId = @1",
                scope.KindName, scope.Id);
        }

        private byte[] WrapDek(byte[] dek, FirearmScope scope, int keyVersion)
        {
            var kek = FirearmCrypto.DeriveKeyEncryptionKey(_keyRing.RootSecretFor(keyVersion), scope, keyVersion);
            try
            {
                return FirearmCrypto.Seal(kek, dek, FirearmCrypto.VaultAad(scope, keyVersion));
            }
            finally
            {
                CryptographicWipe(kek);
            }
        }

        private byte[] UnwrapDek(FirearmVaultKey row)
        {
            var scope = row.Scope;
            var kek = FirearmCrypto.DeriveKeyEncryptionKey(
                _keyRing.RootSecretFor(row.KeyVersion), scope, row.KeyVersion);
            try
            {
                return FirearmCrypto.Open(kek, row.WrappedDek, FirearmCrypto.VaultAad(scope, row.KeyVersion));
            }
            finally
            {
                CryptographicWipe(kek);
            }
        }

        private static void RequireValid(FirearmScope scope)
        {
            if (!scope.IsValid)
                throw new ArgumentException(
                    $"Ogiltig ägare {scope}: id måste vara större än 0.", nameof(scope));
        }

        /// <summary>
        /// Nollställer nyckelmaterial så snart det inte behövs. Ingen garanti mot en GC som redan
        /// hunnit kopiera bufferten — men det förkortar fönstret, och en minnesdump ur en kraschad
        /// process är ett realistiskt läckage.
        /// </summary>
        private static void CryptographicWipe(byte[]? key)
        {
            if (key is not null) Array.Clear(key, 0, key.Length);
        }

        private class VersionCount
        {
            public int KeyVersion { get; set; }
            public int Count { get; set; }
        }
    }

    public class FirearmRewrapResult
    {
        public int TargetVersion { get; set; }
        public int Examined { get; set; }
        public int Rewrapped { get; set; }
        public List<string> Failures { get; } = new();
        public bool Ok => Failures.Count == 0;
    }

    public class FirearmVaultInventory
    {
        public int TotalVaults { get; set; }
        public int UnreadableVaults { get; set; }
        public List<int> MissingVersions { get; set; } = new();
    }
}
