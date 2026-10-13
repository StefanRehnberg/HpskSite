using System.Collections.Concurrent;

namespace HpskSite.Services.Ops
{
    /// <summary>
    /// C3-planen F6: de två signaler som inte syns som ett felsvar — något som saknas i lagringen, och en
    /// behörighetsgrind som föll på ett fel och därför svarade nej. Båda loggas som Warning (prod loggar
    /// Warning och uppåt) och räknas, så att <c>/health/competitions</c> och driftskollen kan visa dem.
    ///
    /// <para><b>Strypt:</b> samma signal (sort + nyckel) loggas högst en gång per <see cref="Window"/>, och
    /// högst <see cref="MaxLinesPerWindow"/> rader totalt per fönster. Utan det hade en trasig databas eller en
    /// bot med gamla länkar fyllt loggen — och då försvinner den rad som är orsaken. Räknaren räknar allt.</para>
    ///
    /// <para><b>Nekad behörighet loggas bara när grinden FÖLL</b> (ett undantag som svarar nej). Ett vanligt
    /// nej är inget fel, och att logga varje nej vore brus.</para>
    /// </summary>
    public static class OpsSignals
    {
        public static readonly TimeSpan Window = TimeSpan.FromHours(1);
        public const int MaxLinesPerWindow = 100;

        public const string KindStoreMiss = "store-miss";
        public const string KindAuthError = "auth-error";

        private static readonly ConcurrentDictionary<string, DateTime> _lastLogged = new();
        private static readonly ConcurrentDictionary<string, long> _counts = new();
        private static DateTime _windowStart = DateTime.UtcNow;
        private static int _linesInWindow;
        private static readonly object _windowLock = new();

        /// <summary>Sedan appen startade: antal per sort. Nollställs vid omstart.</summary>
        public static DateTime Since { get; } = DateTime.Now;

        public static long Count(string kind) => _counts.TryGetValue(kind, out var c) ? c : 0;

        /// <summary>
        /// Något som pekas ut med ett id finns inte i lagringen. <paramref name="sort"/> är sorten
        /// ("anmälan", "händelse", "tävling"), <paramref name="store"/> vilken lagring som gällde.
        ///
        /// <para>Varje miss räknas. <b>Loggas bara i tabelläget</b> — det är där en tappad rad syns som en miss —
        /// och strypt. ⚠️ En miss är inte bevis på förlust: en gammal länk, en raderad rad (tabellen raderar på
        /// riktigt och den gamla noden står kvar) och en sond ("är det här en händelse?") ger också en miss.
        /// Ett stort antal olika id:n på kort tid efter ett byte är signalen. Själva flytten kontrolleras av
        /// jämförelsen i underhållsfönstret, inte här.</para>
        /// </summary>
        public static void StoreMiss(ILogger logger, string sort, int id, string store)
        {
            _counts.AddOrUpdate(KindStoreMiss, 1, (_, c) => c + 1);
            if (!string.Equals(store, "table", StringComparison.OrdinalIgnoreCase) || id <= 0) return;
            if (!ShouldLog(KindStoreMiss + "|" + sort + "|" + id)) return;
            logger.LogWarning("Saknas i lagringen: {Sort} {Id} finns inte i tabellen. En gammal länk, en raderad rad "
                + "eller en tappad rad — många olika id:n på kort tid efter ett byte betyder det sista.", sort, id);
        }

        /// <summary>
        /// En behörighetsgrind kastade och svarar därför nej. Loggar grinden, vad den gällde och felet.
        /// Returnerar alltid <c>false</c>, så att anropet kan stå i grindens <c>catch</c>.
        /// </summary>
        public static bool AuthGateFailed(ILogger logger, Exception ex, string gate, object? subject = null)
        {
            _counts.AddOrUpdate(KindAuthError, 1, (_, c) => c + 1);
            if (ShouldLog(KindAuthError + "|" + gate + "|" + ex.GetType().Name))
                logger.LogWarning(ex, "Nekad behörighet på grund av ett fel: grinden {Gate} ({Subject}) kunde inte avgöras och svarade nej.",
                    gate, subject ?? "");
            return false;
        }

        internal static bool ShouldLog(string key)
        {
            var now = DateTime.UtcNow;
            lock (_windowLock)
            {
                if (now - _windowStart >= Window)
                {
                    _windowStart = now;
                    _linesInWindow = 0;
                    foreach (var kv in _lastLogged)
                        if (now - kv.Value >= Window) _lastLogged.TryRemove(kv.Key, out _);
                }
                if (_lastLogged.TryGetValue(key, out var last) && now - last < Window) return false;
                if (_linesInWindow >= MaxLinesPerWindow) return false;
                _lastLogged[key] = now;
                _linesInWindow++;
                return true;
            }
        }

        /// <summary>Bara för testerna.</summary>
        internal static void ResetForTests()
        {
            lock (_windowLock) { _lastLogged.Clear(); _counts.Clear(); _linesInWindow = 0; _windowStart = DateTime.UtcNow; }
        }
    }
}
