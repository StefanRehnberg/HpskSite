using System.Text.Json;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>Tävlingarnas ändringslogg och versionsrad</b> (C3-planen S5d). Lagringen anropar
    /// <see cref="Record"/> efter varje lyckad skrivning — skapa, ändra, flytta, papperskorgen — och
    /// <see cref="CompetitionCacheVersion"/> räknas upp i samma anrop.
    ///
    /// <para><b>Ett fel stoppar aldrig skrivningen.</b> Tävlingen ÄR sparad när loggen skrivs; en saknad tabell
    /// eller ett tillfälligt fel loggas som varning (högst en gång var tionde minut), och de lokala
    /// cacharna töms ändå.</para>
    ///
    /// <para>Rad: <c>TargetKind</c> (nodens alias: <c>competition</c> / <c>competitionSeries</c>) + <c>TargetId</c>
    /// — id:t följs alltid av sin sort (LAGEN). <c>MemberId</c> = den inloggade medlemmen, null för system och
    /// bakgrundssvep. <c>Changes</c> = JSON alias → {from, to}, långa texter kapade.</para>
    /// </summary>
    public class CompetitionChangeRecorder
    {
        public const int MaxValueLength = 300;
        private static DateTime _lastWarning = DateTime.MinValue;

        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IHttpContextAccessor _http;
        private readonly AppCaches _caches;
        private readonly ILogger<CompetitionChangeRecorder> _logger;

        public CompetitionChangeRecorder(IScopeProvider scopeProvider, IMemberManager memberManager, IMemberService memberService,
            IHttpContextAccessor http, AppCaches caches, ILogger<CompetitionChangeRecorder> logger)
        {
            _scopeProvider = scopeProvider;
            _memberManager = memberManager;
            _memberService = memberService;
            _http = http;
            _caches = caches;
            _logger = logger;
        }

        /// <summary>En skrivning: loggrad + versionsrad + tömda lokala cachar.</summary>
        public void Record(string targetKind, int targetId, string action, IReadOnlyDictionary<string, (object? From, object? To)> changes)
        {
            try
            {
                var json = JsonSerializer.Serialize(changes.ToDictionary(
                    kv => kv.Key, kv => new Dictionary<string, object?> { ["from"] = Cap(kv.Value.From), ["to"] = Cap(kv.Value.To) }));
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                scope.Database.Execute(
                    "INSERT INTO dbo.CompetitionChange (TargetKind, TargetId, [Action], MemberId, AtUtc, Changes) VALUES (@0, @1, @2, @3, SYSUTCDATETIME(), @4)",
                    targetKind, targetId, action, CurrentMemberId(), json);
                scope.Database.Execute(CompetitionCacheVersion.BumpSql);
            }
            catch (Exception ex)
            {
                if (DateTime.UtcNow - _lastWarning > TimeSpan.FromMinutes(10))
                {
                    _lastWarning = DateTime.UtcNow;
                    _logger.LogWarning(ex, "Ändringsloggen för {Kind} {Id} ({Action}) kunde inte skrivas — finns dbo.CompetitionChange? (Migrations/create-competition-change-tables.sql)",
                        targetKind, targetId, action);
                }
            }
            CompetitionCacheVersion.InvalidateLocal(_caches);
        }

        private int? CurrentMemberId()
        {
            if (_http.HttpContext == null) return null;
            try
            {
                var m = _memberManager.GetCurrentMemberAsync().GetAwaiter().GetResult();
                return m?.Email == null ? null : _memberService.GetByEmail(m.Email)?.Id;
            }
            catch { return null; }
        }

        private static object? Cap(object? v) => v switch
        {
            null => null,
            string s => s.Length > MaxValueLength ? s[..MaxValueLength] + "…" : s,
            DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss"),
            _ => v,
        };
    }

    /// <summary>
    /// <b>Versionsraden</b> (C3-planen S5d): <c>dbo.CompetitionStoreVersion</c> räknas upp vid varje skrivning, och
    /// varje instans läser den (högst var femte sekund) och tömmer meny-, adress- och listcacharna när den
    /// ändrats. Förberett för flera instanser (fas D) — i dag töms cacharna redan i samma instans.
    /// </summary>
    public class CompetitionCacheVersion
    {
        public const string BumpSql = "UPDATE dbo.CompetitionStoreVersion SET [Version] = [Version] + 1, UpdatedUtc = SYSUTCDATETIME() WHERE Id = 1";
        private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

        private readonly IScopeProvider _scopeProvider;
        private readonly AppCaches _caches;
        private long _seen;
        private DateTime _checkedUtc = DateTime.MinValue;
        private readonly object _lock = new();

        public CompetitionCacheVersion(IScopeProvider scopeProvider, AppCaches caches)
        {
            _scopeProvider = scopeProvider;
            _caches = caches;
        }

        /// <summary>Tömmer cacharna om någon (en annan instans) skrivit sedan förra kontrollen.</summary>
        public void EnsureFresh()
        {
            lock (_lock)
            {
                if (DateTime.UtcNow - _checkedUtc < CheckEvery) return;
                _checkedUtc = DateTime.UtcNow;
            }
            long v;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                v = scope.Database.ExecuteScalar<long>("SELECT [Version] FROM dbo.CompetitionStoreVersion WHERE Id = 1");
            }
            catch { return; }
            lock (_lock)
            {
                if (v == _seen) return;
                var first = _seen == 0;
                _seen = v;
                if (first) return;
            }
            InvalidateLocal(_caches);
        }

        /// <summary>Det som visar tävlingar ur en cache: menyn, adressindexet och adminlistorna.</summary>
        public static void InvalidateLocal(AppCaches caches)
        {
            CompetitionNavigationCaches.Invalidate(caches);
            caches.RuntimeCache.ClearByKey("admin_series_list");
            caches.RuntimeCache.ClearByRegex("^admin_series_list_");
            caches.RuntimeCache.ClearByRegex("^admin_competitions_list_");
        }
    }
}
