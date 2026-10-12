using System.Collections.Concurrent;
using HpskSite.Models.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>När medlemmen senast sågs på webben respektive i mobilappen (UTC, som förut).</summary>
    public readonly record struct MemberLastSeen(DateTime? Web, DateTime? Mobile)
    {
        public DateTime? Any => Web.HasValue && Mobile.HasValue
            ? (Web.Value > Mobile.Value ? Web : Mobile)
            : Web ?? Mobile;
    }

    /// <summary>
    /// Medlemmens senaste aktivitet — i tabellen <c>dbo.MemberActivity</c> (skalplanen B1, 2026-10-12).
    ///
    /// <para><b>⚠️⚠️ Skriv ALDRIG aktiviteten som en medlemsegenskap igen.</b> Förut gjorde den här
    /// tjänsten <c>_memberService.Save(member)</c> var 5:e minut per inloggad medlem. Varje sparning tog
    /// Umbracos skrivlås för ALLA medlemmar, skrev om medlemmens egenskaper och byggde om medlemmen i
    /// Examine — vid en rusning hundratals gånger i minuten, i kö med sparningar som faktiskt betyder
    /// något. Nu är det en UPSERT på en rad, utan Umbraco.</para>
    ///
    /// <para><b>Läsning:</b> <see cref="GetAll"/> för listor och statistik (hela tabellen, cachad
    /// 60 s — en rad per medlem som någon gång varit aktiv), <see cref="Get"/> för en medlem. Egenskaperna
    /// <c>lastActiveDate</c>/<c>lastMobileActiveDate</c> finns kvar på medlemstypen men skrivs och läses
    /// inte längre; migreringen förde över deras värden.</para>
    ///
    /// <para>Strypningen (en skrivning per medlem och <c>ThrottleMinutes</c>) ligger kvar i minnet per
    /// process. Med flera instanser blir det som mest en skrivning per instans och period — ofarligt nu
    /// när skrivningen är en rad.</para>
    /// </summary>
    public class MemberActivityService
    {
        private const string CacheKey = "member_activity_all";
        private static readonly ConcurrentDictionary<int, DateTime> _lastWebWrite = new();
        private static readonly ConcurrentDictionary<int, DateTime> _lastMobileWrite = new();
        private static int _missingTableLogged;

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemoryCache _cache;
        private readonly MemberActivityOptions _options;
        private readonly ILogger<MemberActivityService> _logger;

        public MemberActivityService(
            IUmbracoDatabaseFactory databaseFactory,
            IMemoryCache cache,
            IOptions<MemberActivityOptions> options,
            ILogger<MemberActivityService> logger)
        {
            _databaseFactory = databaseFactory;
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Behöver medlemmens webbaktivitet skrivas nu? Billig, ingen databas — middlewaren frågar här
        /// INNAN den slår upp medlemmen, så att en vanlig sidladdning inte kostar något alls.
        /// </summary>
        public bool WebUpdateDue(int memberId, DateTime utc)
            => _options.EnableTracking && Due(_lastWebWrite, memberId, utc);

        public void UpdateActivity(int memberId, DateTime utc)
        {
            if (!WebUpdateDue(memberId, utc)) return;
            Write(memberId, utc, mobile: false);
        }

        public void UpdateMobileActivity(int memberId, DateTime utc)
        {
            if (!_options.EnableTracking || !Due(_lastMobileWrite, memberId, utc)) return;
            Write(memberId, utc, mobile: true);
        }

        public MemberLastSeen Get(int memberId)
            => GetAll().TryGetValue(memberId, out var seen) ? seen : default;

        /// <summary>Hela tabellen, cachad 60 s. Saknas en medlem har hen aldrig setts.</summary>
        public IReadOnlyDictionary<int, MemberLastSeen> GetAll()
        {
            if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<int, MemberLastSeen>? cached) && cached != null)
                return cached;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var rows = db.Fetch<Row>("SELECT MemberId, LastWebUtc, LastMobileUtc FROM dbo.MemberActivity");
                var map = rows.ToDictionary(r => r.MemberId, r => new MemberLastSeen(r.LastWebUtc, r.LastMobileUtc));
                _cache.Set(CacheKey, (IReadOnlyDictionary<int, MemberLastSeen>)map, TimeSpan.FromSeconds(60));
                return map;
            }
            catch (Exception ex)
            {
                LogMissing(ex);
                return new Dictionary<int, MemberLastSeen>();
            }
        }

        private bool Due(ConcurrentDictionary<int, DateTime> last, int memberId, DateTime utc)
            => memberId > 0
               && (!last.TryGetValue(memberId, out var prev) || (utc - prev).TotalMinutes >= _options.ThrottleMinutes);

        private void Write(int memberId, DateTime utc, bool mobile)
        {
            if (memberId <= 0) return;
            var last = mobile ? _lastMobileWrite : _lastWebWrite;
            last[memberId] = utc; // före skrivningen: ett fel ska inte ge ett nytt försök på varje förfrågan
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var column = mobile ? "LastMobileUtc" : "LastWebUtc";
                // ⚠️ Inget @@ROWCOUNT — NPoco läser "@@" som ett escapat "@" och gör det till en parameter.
                db.Execute($@"
IF EXISTS (SELECT 1 FROM dbo.MemberActivity WITH (UPDLOCK, HOLDLOCK) WHERE MemberId = @0)
    UPDATE dbo.MemberActivity SET {column} = @1 WHERE MemberId = @0;
ELSE
    INSERT INTO dbo.MemberActivity (MemberId, {column}) VALUES (@0, @1);", memberId, utc);
            }
            catch (Exception ex)
            {
                LogMissing(ex);
            }
        }

        private void LogMissing(Exception ex)
        {
            // En gång per process — annars fyller varje sidladdning loggen.
            if (Interlocked.Exchange(ref _missingTableLogged, 1) == 0)
                _logger.LogCritical(ex,
                    "Medlemsaktiviteten kunde inte läsas eller skrivas (loggas en gång per process). Saknas dbo.MemberActivity, kör Migrations/create-member-activity-table.sql.");
        }

        public void ClearCache()
        {
            _lastWebWrite.Clear();
            _lastMobileWrite.Clear();
            _cache.Remove(CacheKey);
        }

        private sealed class Row
        {
            public int MemberId { get; set; }
            public DateTime? LastWebUtc { get; set; }
            public DateTime? LastMobileUtc { get; set; }
        }
    }
}
