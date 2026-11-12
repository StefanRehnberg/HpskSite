using Microsoft.Data.SqlClient;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Läser och sparar klubbens/kretsens utseende (<see cref="SiteAppearance"/>), kolumnerna <c>SiteOwner.HeaderStyle</c> och
    /// <c>SiteOwner.Palette</c>. Ingen SQL-fråga per sidvisning: ägare med eget utseende ligger som en mängd i minnet (kort TTL, och
    /// cachesignalen <c>site-appearance</c> tömmer den på andra instanser).
    ///
    /// <para><b>Säkert utgångsläge: standard.</b> Saknas kolumnerna (migreringen <c>add-owner-appearance.sql</c> ej körd), kraschar läsningen
    /// eller finns ingen rad är svaret standardutseendet, aldrig ett undantag på en publik sida.</para>
    /// <para>⚠️ Skriv aldrig utan <c>CacheSignals.Notify</c> EFTER att skrivningen är klar.</para>
    /// </summary>
    public class SiteAppearanceService
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);

        private readonly IScopeProvider? _scopeProvider;
        private readonly ILogger<SiteAppearanceService> _logger;
        private readonly object _loadLock = new();
        private volatile Snapshot? _snapshot;
        private volatile bool _warnedMissing;
        private Func<DateTime> _now = () => DateTime.UtcNow;

        public SiteAppearanceService(IScopeProvider scopeProvider, ILogger<SiteAppearanceService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>För testerna: utan databas.</summary>
        protected SiteAppearanceService(ILogger<SiteAppearanceService> logger, Func<DateTime>? now)
        {
            _logger = logger;
            if (now != null) _now = now;
        }

        private static string Key(string ownerType, int ownerId) => SiteOwnerService.Key(ownerType, ownerId);

        /// <summary>Ägarens utseende. Svarar aldrig med ett undantag; okänd ägare = standard.</summary>
        public SiteAppearance.Look Get(string ownerType, int ownerId)
        {
            if (ownerId <= 0) return SiteAppearance.Look.Default;
            return Current().Looks.TryGetValue(Key(ownerType, ownerId), out var look) ? look : SiteAppearance.Look.Default;
        }

        /// <summary>
        /// Sparar utseendet. Okända nycklar vägras (<see cref="ArgumentException"/>), de blir inte tyst standard: den som sparar ska få veta.
        /// Raden måste finnas (den nya sidstrukturen påslagen). Returnerar det som sparades.
        /// </summary>
        public SiteAppearance.Look Set(string ownerType, int ownerId, string header, string palette, int? byMemberId)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region)
                throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Okänd ägartyp");
            if (ownerId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerId), ownerId, "Ogiltigt id");
            if (!SiteAppearance.IsValidHeader(header)) throw new ArgumentException("Okänd grunddesign.", nameof(header));
            if (!SiteAppearance.IsValidPalette(palette)) throw new ArgumentException("Okänd färg.", nameof(palette));

            if (Write(ownerType, ownerId, header, palette) == 0)
                throw new InvalidOperationException($"{ownerType} {ownerId} har ingen rad i SiteOwner: slå på den nya sidstrukturen först.");
            ClearLocal();
            HpskSite.Services.Hosting.CacheSignals.Notify(HpskSite.Services.Hosting.CacheSignals.SiteAppearance);
            _logger.LogWarning("Klubbsidorna: utseende för {OwnerType} {OwnerId} satt till {Header}/{Palette} av medlem {MemberId}",
                ownerType, ownerId, header, palette, byMemberId);
            return SiteAppearance.LookOf(header, palette);
        }

        public void ClearLocal() => _snapshot = null;

        // ── Läsning ────────────────────────────────────────────────────────────

        private Snapshot Current()
        {
            var s = _snapshot;
            if (s != null && s.ExpiresAt > _now()) return s;
            lock (_loadLock)
            {
                s = _snapshot;
                if (s != null && s.ExpiresAt > _now()) return s;
                return _snapshot = Load(s);
            }
        }

        private Snapshot Load(Snapshot? previous)
        {
            try
            {
                var rows = ReadRows();
                if (_warnedMissing) { _warnedMissing = false; _logger.LogInformation("SiteOwner.HeaderStyle/Palette finns nu."); }
                var map = new Dictionary<string, SiteAppearance.Look>(StringComparer.Ordinal);
                foreach (var r in rows) map[Key(r.OwnerType, r.OwnerId)] = SiteAppearance.LookOf(r.HeaderStyle, r.Palette);
                return new Snapshot(map, _now() + Ttl);
            }
            catch (Exception ex) when (IsMissingColumn(ex))
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    _logger.LogError("Kolumnerna SiteOwner.HeaderStyle/Palette saknas — alla visar standardutseendet. Kör Migrations/add-owner-appearance.sql.");
                }
                return new Snapshot(new Dictionary<string, SiteAppearance.Look>(StringComparer.Ordinal), _now() + RetryAfterFailure);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utseendet kunde inte läsas — {What}", previous != null ? "den senast kända mängden används" : "standardutseendet används");
                return new Snapshot(previous?.Looks ?? new Dictionary<string, SiteAppearance.Look>(StringComparer.Ordinal), _now() + RetryAfterFailure);
            }
        }

        public sealed class AppearanceRow
        {
            public string OwnerType { get; set; } = "";
            public int OwnerId { get; set; }
            public string? HeaderStyle { get; set; }
            public string? Palette { get; set; }
        }

        /// <summary>Bara ägare som valt något (resten är standard). Virtuell för testerna.</summary>
        protected virtual List<AppearanceRow> ReadRows()
        {
            using var scope = _scopeProvider!.CreateScope(autoComplete: true);
            return scope.Database.Fetch<AppearanceRow>(
                "SELECT OwnerType, OwnerId, HeaderStyle, Palette FROM dbo.SiteOwner WHERE IsEnabled = 1 AND (HeaderStyle IS NOT NULL OR Palette IS NOT NULL)");
        }

        /// <summary>Standardvalet lagras som NULL: en rad som aldrig rörts och en som valt standard är samma sak.</summary>
        protected virtual int Write(string ownerType, int ownerId, string header, string palette)
        {
            using var scope = _scopeProvider!.CreateScope();
            var n = scope.Database.Execute(
                "UPDATE dbo.SiteOwner SET HeaderStyle = @2, Palette = @3, UpdatedDate = SYSDATETIME() WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId,
                header == SiteAppearance.DefaultHeader ? null : header,
                palette == SiteAppearance.DefaultPalette ? null : palette);
            scope.Complete();
            return n;
        }

        private static bool IsMissingColumn(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql && sql.Number == 207) return true;
            return false;
        }

        private sealed record Snapshot(Dictionary<string, SiteAppearance.Look> Looks, DateTime ExpiresAt);
    }
}
