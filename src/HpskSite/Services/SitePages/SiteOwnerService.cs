using Microsoft.Data.SqlClient;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.SitePages
{
    /// <summary>En rad ur <c>dbo.SiteOwner</c> för sajtadmins kort.</summary>
    public sealed class SiteOwnerRow
    {
        public string OwnerType { get; set; } = "";
        public int OwnerId { get; set; }
        public bool IsEnabled { get; set; }
        public int? TemplateVersion { get; set; }
        public DateTime? EnabledDate { get; set; }
        public int? EnabledByMemberId { get; set; }
    }

    /// <summary>
    /// Brytaren per klubb och krets för den nya sidstrukturen (klubbsidorna plan v4, 2.7).
    ///
    /// <para><b>Säkert utgångsläge: AV.</b> Saknas tabellen, går läsningen sönder eller finns ingen rad är svaret "av",
    /// alltså dagens vy. Det är motsatsen till lagringsväxlarna i <c>SiteSettingService</c>, där ett gissat värde var
    /// farligt: här är ett fel som ger dagens sida alltid det rätta.</para>
    ///
    /// <para><b>Ingen SQL-fråga per sidvisning.</b> De påslagna ägarna ligger som en mängd i minnet, läst en gång och
    /// ogiltigförklarad av <see cref="Set"/> och av cachesignalen <c>site-owners</c> (andra instanser). En kort TTL
    /// täcker det fall där signalen inte är på (<c>ScaleOut:ServerRole</c> ej satt och två processer, t.ex. en omstart med
    /// överlapp).</para>
    ///
    /// <para>⚠️ Skriv aldrig brytaren utan <c>CacheSignals.Notify</c> EFTER <c>scope.Complete()</c>: annars läser en annan
    /// instans gammalt tillstånd.</para>
    /// </summary>
    public class SiteOwnerService
    {
        public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan RetryAfterMissingTable = TimeSpan.FromSeconds(60);

        private readonly IScopeProvider? _scopeProvider;
        private readonly ILogger<SiteOwnerService> _logger;
        private readonly object _loadLock = new();
        private volatile Snapshot? _snapshot;
        private volatile bool _warnedMissing;
        private Func<DateTime> _now = () => DateTime.UtcNow;

        public SiteOwnerService(IScopeProvider scopeProvider, ILogger<SiteOwnerService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>För testerna: utan databas, och med en klocka att styra.</summary>
        protected SiteOwnerService(ILogger<SiteOwnerService> logger, Func<DateTime>? now)
        {
            _logger = logger;
            if (now != null) _now = now;
        }

        public static string Key(string ownerType, int ownerId) => ownerType + ":" + ownerId;

        /// <summary>Är den nya sidstrukturen påslagen för ägaren? Svarar aldrig med ett undantag.</summary>
        public bool IsEnabled(string ownerType, int ownerId) =>
            ownerId > 0 && Current().Enabled.Contains(Key(ownerType, ownerId));

        /// <summary>
        /// Brytare 2: får ägarens admin redigera egna sidor? Effektivt läge = <c>IsEnabled AND EditingEnabled</c> — på ETT ställe
        /// (v3 A1). Slås brytare 1 av ser nästa begäran AV, och slås den på igen finns innehållet kvar.
        /// </summary>
        public bool IsEditingEnabled(string ownerType, int ownerId)
        {
            if (ownerId <= 0) return false;
            var key = Key(ownerType, ownerId);
            var s = Current();
            return s.Enabled.Contains(key) && s.Editing.Contains(key);
        }

        /// <summary>Slå på eller av brytare 2. <see cref="Set"/> rör aldrig den. Raden måste finnas (brytare 1 först).</summary>
        public void SetEditing(string ownerType, int ownerId, bool enabled, int? byMemberId)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region)
                throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Okänd ägartyp");
            if (ownerId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerId), ownerId, "Ogiltigt id");
            if (WriteEditing(ownerType, ownerId, enabled) == 0)
                throw new InvalidOperationException($"{ownerType} {ownerId} har ingen rad i SiteOwner: slå på den nya sidstrukturen först.");
            ClearLocal();
            HpskSite.Services.Hosting.CacheSignals.Notify(HpskSite.Services.Hosting.CacheSignals.SiteOwners);
            _logger.LogWarning("Klubbsidorna: egen redigering för {OwnerType} {OwnerId} {State} av medlem {MemberId}",
                ownerType, ownerId, enabled ? "PÅ" : "AV", byMemberId);
        }

        /// <summary>Slå på eller av. Skrivs med MERGE; cachen töms och de andra instanserna meddelas efter att skrivningen är klar.</summary>
        public void Set(string ownerType, int ownerId, bool enabled, int? byMemberId)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region)
                throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Okänd ägartyp");
            if (ownerId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerId), ownerId, "Ogiltigt id");
            var version = SiteTemplates.For(ownerType).Version;

            WriteRow(ownerType, ownerId, enabled, version, byMemberId);
            ClearLocal();
            HpskSite.Services.Hosting.CacheSignals.Notify(HpskSite.Services.Hosting.CacheSignals.SiteOwners);
            _logger.LogWarning("Klubbsidorna: {OwnerType} {OwnerId} {State} av medlem {MemberId} (mallversion {Version})",
                ownerType, ownerId, enabled ? "PÅ" : "AV", byMemberId, version);
        }

        /// <summary>
        /// Tar bort ägarens rad när klubben eller kretsen raderas för gott (<c>SiteOwnerCleanupHandler</c>). En borttagen
        /// rad är samma sak som AV, och ett återanvänt nod-id kan aldrig ärva en gammal brytare. Cachen töms och de andra
        /// instanserna meddelas efter att raderingen är klar. Returnerar antalet borttagna rader.
        /// </summary>
        public int Remove(string ownerType, int ownerId)
        {
            if (ownerType != SiteTemplates.Club && ownerType != SiteTemplates.Region)
                throw new ArgumentOutOfRangeException(nameof(ownerType), ownerType, "Okänd ägartyp");
            if (ownerId <= 0) throw new ArgumentOutOfRangeException(nameof(ownerId), ownerId, "Ogiltigt id");
            var n = DeleteRow(ownerType, ownerId);
            ClearLocal();
            HpskSite.Services.Hosting.CacheSignals.Notify(HpskSite.Services.Hosting.CacheSignals.SiteOwners);
            if (n > 0) _logger.LogWarning("Klubbsidorna: {OwnerType} {OwnerId} raderades, brytarraden borttagen", ownerType, ownerId);
            return n;
        }

        /// <summary>Alla rader, direkt ur databasen (sajtadmins kort). Ett fel kastas: kortet ska säga att det inte gick.</summary>
        public List<SiteOwnerRow> ListAll() => ReadAllRows();

        /// <summary>Tömmer den här instansens cache (cachesignalen).</summary>
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
                var enabled = LoadEnabled();
                // Brytare 2 läses isolerat: ett fel där får aldrig göra brytare 1 AV (redan påslagna klubbar tappar inte sin nya struktur).
                // Redigering faller i stället tillbaka på senast kända mängd, annars AV.
                HashSet<string> editing;
                try { editing = LoadEditing(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SiteOwner.EditingEnabled kunde inte läsas — {What}", previous != null ? "den senast kända mängden används" : "egen redigering är AV tills vidare");
                    editing = previous?.Editing ?? new HashSet<string>(StringComparer.Ordinal);
                }
                if (_warnedMissing) { _warnedMissing = false; _logger.LogInformation("Tabellen SiteOwner finns nu."); }
                return new Snapshot(enabled, editing, _now() + Ttl);
            }
            catch (Exception ex) when (IsMissingTable(ex))
            {
                if (!_warnedMissing)
                {
                    _warnedMissing = true;
                    _logger.LogError("Tabellen SiteOwner saknas — klubbsidorna är AV för alla (dagens vy). Kör Migrations/create-site-owner-table.sql.");
                }
                return new Snapshot(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), _now() + RetryAfterMissingTable);
            }
            catch (Exception ex)
            {
                // Senast kända mängd om det finns en (den var rätt för högst fem minuter sedan), annars AV.
                _logger.LogError(ex, "SiteOwner kunde inte läsas — {What}", previous != null ? "den senast kända mängden används" : "klubbsidorna är AV tills vidare");
                return new Snapshot(previous?.Enabled ?? new HashSet<string>(StringComparer.Ordinal),
                    previous?.Editing ?? new HashSet<string>(StringComparer.Ordinal), _now() + RetryAfterFailure);
            }
        }

        /// <summary>
        /// Ägare med brytare 2 (egen redigering) — läses i samma snapshot som brytare 1. Saknas kolumnen (migreringen
        /// <c>create-site-pages-tables.sql</c> inte körd) är redigering AV för alla och det loggas Critical: brytare 1 ska
        /// aldrig bli tyst AV av att brytare 2 inte kan läsas. Virtuell för testerna.
        /// </summary>
        protected virtual HashSet<string> LoadEditing()
        {
            try
            {
                using var scope = _scopeProvider!.CreateScope(autoComplete: true);
                var rows = scope.Database.Fetch<SiteOwnerRow>(
                    "SELECT OwnerType, OwnerId FROM dbo.SiteOwner WHERE IsEnabled = 1 AND EditingEnabled = 1");
                return rows.Select(r => Key(r.OwnerType, r.OwnerId)).ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (IsMissingColumn(ex))
            {
                _logger.LogCritical("KLUBBSIDOR: kolumnen SiteOwner.EditingEnabled saknas — egen redigering är AV för alla. Kör Migrations/create-site-pages-tables.sql.");
                return new HashSet<string>(StringComparer.Ordinal);
            }
        }

        /// <summary>Själva läsningen. Virtuell för testerna.</summary>
        protected virtual HashSet<string> LoadEnabled()
        {
            using var scope = _scopeProvider!.CreateScope(autoComplete: true);
            var rows = scope.Database.Fetch<SiteOwnerRow>(
                "SELECT OwnerType, OwnerId, IsEnabled, TemplateVersion, EnabledDate, EnabledByMemberId FROM dbo.SiteOwner WHERE IsEnabled = 1");
            return rows.Select(r => Key(r.OwnerType, r.OwnerId)).ToHashSet(StringComparer.Ordinal);
        }

        protected virtual List<SiteOwnerRow> ReadAllRows()
        {
            using var scope = _scopeProvider!.CreateScope(autoComplete: true);
            return scope.Database.Fetch<SiteOwnerRow>(
                "SELECT OwnerType, OwnerId, IsEnabled, TemplateVersion, EnabledDate, EnabledByMemberId FROM dbo.SiteOwner ORDER BY OwnerType, OwnerId");
        }

        protected virtual void WriteRow(string ownerType, int ownerId, bool enabled, int templateVersion, int? byMemberId)
        {
            using (var scope = _scopeProvider!.CreateScope())
            {
                scope.Database.Execute(@"
MERGE dbo.SiteOwner WITH (HOLDLOCK) AS t
USING (SELECT @0 AS OwnerType, @1 AS OwnerId) AS s ON t.OwnerType = s.OwnerType AND t.OwnerId = s.OwnerId
WHEN MATCHED THEN UPDATE SET IsEnabled = @2, TemplateVersion = @3, UpdatedDate = SYSDATETIME(),
                             EnabledDate = CASE WHEN @2 = 1 THEN SYSDATETIME() ELSE t.EnabledDate END,
                             EnabledByMemberId = CASE WHEN @2 = 1 THEN @4 ELSE t.EnabledByMemberId END
WHEN NOT MATCHED THEN INSERT (OwnerType, OwnerId, IsEnabled, TemplateVersion, EnabledDate, EnabledByMemberId)
     VALUES (@0, @1, @2, @3, CASE WHEN @2 = 1 THEN SYSDATETIME() ELSE NULL END, CASE WHEN @2 = 1 THEN @4 ELSE NULL END);",
                    ownerType, ownerId, enabled, templateVersion, byMemberId);
                scope.Complete();
            }
        }

        protected virtual int WriteEditing(string ownerType, int ownerId, bool enabled)
        {
            using var scope = _scopeProvider!.CreateScope();
            var n = scope.Database.Execute(
                "UPDATE dbo.SiteOwner SET EditingEnabled = @2, UpdatedDate = SYSDATETIME() WHERE OwnerType = @0 AND OwnerId = @1",
                ownerType, ownerId, enabled);
            scope.Complete();
            return n;
        }

        protected virtual int DeleteRow(string ownerType, int ownerId)
        {
            using var scope = _scopeProvider!.CreateScope();
            var n = scope.Database.Execute("DELETE FROM dbo.SiteOwner WHERE OwnerType = @0 AND OwnerId = @1", ownerType, ownerId);
            scope.Complete();
            return n;
        }

        /// <summary>SQL-fel 208 = "Invalid object name": tabellen finns inte.</summary>
        protected static bool IsMissingTable(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql && sql.Number == 208) return true;
            return false;
        }

        /// <summary>SQL-fel 207 = "Invalid column name": kolumnen finns inte.</summary>
        protected static bool IsMissingColumn(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
                if (e is SqlException sql && sql.Number == 207) return true;
            return false;
        }

        private sealed record Snapshot(HashSet<string> Enabled, HashSet<string> Editing, DateTime ExpiresAt);
    }
}
