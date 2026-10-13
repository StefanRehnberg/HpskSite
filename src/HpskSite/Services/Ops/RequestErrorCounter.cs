using System.Collections.Concurrent;

namespace HpskSite.Services.Ops
{
    /// <summary>
    /// C3-planen F6: räknar 4xx- och 5xx-svar i minnet, per dag, statuskod, område och bot/människa.
    /// <see cref="RequestErrorFlushService"/> skriver dem till <c>dbo.RequestErrorLog</c> en gång i minuten.
    ///
    /// <para><b>Varför i minnet och inte en rad per svar:</b> en bot som provar tusen adresser på en minut ska
    /// inte bli tusen skrivningar. Besöksloggen hoppar medvetet över alla svar ≥ 400, så före den här fanns
    /// ingen statistik alls över 404:or och 500:or — och det är de som säger om bytet till tabellerna gick
    /// sönder någonstans.</para>
    ///
    /// <para><b>Taket:</b> högst <see cref="MaxKeys"/> olika nycklar mellan två skrivningar. Resten räknas
    /// under området <see cref="RequestErrorArea.Overflow"/>, så antalet per dag stämmer även när någon
    /// skannar sajten.</para>
    /// </summary>
    public sealed class RequestErrorCounter
    {
        public const int MaxKeys = 2000;

        public readonly record struct Key(DateTime Day, int Status, string Area, bool IsBot);

        public sealed class Entry
        {
            public int Count;
            public string LastPath = "";
            public DateTime LastAt;
        }

        private ConcurrentDictionary<Key, Entry> _counts = new();

        /// <summary>Räknar ett svar. Billig; anropas för varje felsvar.</summary>
        public void Record(int status, string? path, bool isBot, DateTime nowLocal)
        {
            if (status < 400) return;
            var area = RequestErrorArea.For(path);
            var counts = _counts;
            var key = new Key(nowLocal.Date, status, area, isBot);
            if (!counts.ContainsKey(key) && counts.Count >= MaxKeys)
                key = new Key(nowLocal.Date, status, RequestErrorArea.Overflow, isBot);
            var e = counts.GetOrAdd(key, _ => new Entry());
            lock (e)
            {
                e.Count++;
                e.LastAt = nowLocal;
                e.LastPath = RequestErrorArea.TrimPath(path);
            }
        }

        /// <summary>Tar allt som räknats sedan förra gången och börjar om på noll.</summary>
        public List<(Key Key, int Count, string LastPath, DateTime LastAt)> Drain()
        {
            var old = Interlocked.Exchange(ref _counts, new ConcurrentDictionary<Key, Entry>());
            var list = new List<(Key, int, string, DateTime)>(old.Count);
            foreach (var kv in old)
                lock (kv.Value) list.Add((kv.Key, kv.Value.Count, kv.Value.LastPath, kv.Value.LastAt));
            return list;
        }

        /// <summary>Lägger tillbaka det som inte gick att skriva, så att ett tillfälligt databasfel inte tappar räkningen.</summary>
        public void Restore(IEnumerable<(Key Key, int Count, string LastPath, DateTime LastAt)> rows)
        {
            foreach (var r in rows)
            {
                var e = _counts.GetOrAdd(r.Key, _ => new Entry());
                lock (e)
                {
                    e.Count += r.Count;
                    if (r.LastAt > e.LastAt) { e.LastAt = r.LastAt; e.LastPath = r.LastPath; }
                }
            }
        }
    }

    /// <summary>
    /// Vilket "område" ett felsvar hör till. Ren funktion, så att nycklarna går att testa och inte kan växa
    /// utan gräns: första sökvägsdelen, och för Umbracos surface- och API-anrop även kontrollern.
    /// </summary>
    public static class RequestErrorArea
    {
        public const string Overflow = "(övrigt)";
        public const int MaxAreaLength = 60;
        public const int MaxPathLength = 400;

        public static string For(string? path)
        {
            var p = (path ?? "").Trim();
            if (p.Length == 0 || p == "/") return "/";
            var q = p.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) p = p.Substring(0, q);
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "/";
            var first = Clean(parts[0]);
            if (first.Contains('.')) return "(fil " + Extension(first) + ")";
            if (first == "umbraco" && parts.Length >= 3 && (Clean(parts[1]) == "surface" || Clean(parts[1]) == "api"))
                return Cap("/umbraco/" + Clean(parts[1]) + "/" + Clean(parts[2]));
            return Cap("/" + first);
        }

        public static string TrimPath(string? path)
        {
            var p = path ?? "";
            return p.Length <= MaxPathLength ? p : p.Substring(0, MaxPathLength);
        }

        private static string Extension(string segment)
        {
            var dot = segment.LastIndexOf('.');
            var ext = dot >= 0 ? segment.Substring(dot) : "";
            return ext.Length is > 1 and <= 8 ? ext : ".?";
        }

        private static string Clean(string segment)
        {
            var s = segment.ToLowerInvariant();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '&' ? c : '_');
            return sb.ToString();
        }

        private static string Cap(string s) => s.Length <= MaxAreaLength ? s : s.Substring(0, MaxAreaLength);
    }
}
