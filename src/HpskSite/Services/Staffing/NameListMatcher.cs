using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HpskSite.Services.Staffing
{
    /// <summary>En person i poolen (anmälda och funktionärer) som en pappersrad kan matchas mot. <see cref="StartTimes"/> är "HH:mm".</summary>
    /// <param name="Where">Var första starten är, t.ex. "Patrull 16" — så att två som heter lika i samma klubb och klass kan skiljas åt (persontest 12: bara klockslaget fanns, och Birgitta fick gissa).</param>
    public sealed record MatchPerson(int MemberId, string Name, string? Club, string? Class, IReadOnlyList<string> StartTimes, string? Where = null);

    public enum NameMatchStatus
    {
        /// <summary>En enda person, samma namn (stavningen får skilja i versaler, mellanrum, å/ä/ö mot a/o, ordningen förnamn–efternamn, mellannamn).</summary>
        Matched,
        /// <summary>Flera personer med samma namn och raden räcker inte för att välja — människan väljer.</summary>
        Ambiguous,
        /// <summary>Ingen exakt, men en som nästan stavas så (ett eller två tecken fel) — föreslås, markeras inte.</summary>
        Suggested,
        NotFound,
        /// <summary>Samma person står redan tidigare i listan.</summary>
        Duplicate,
    }

    public sealed record NameMatch(string Line, string Query, NameMatchStatus Status, MatchPerson? Chosen, IReadOnlyList<MatchPerson> Options);

    /// <summary>
    /// Planera dagen: en pappersrad ("Erik Pettersson | C2 | patrull 16 11:30") blir en person. Ren funktion — ingen databas — så att den går att bevisa.
    /// Regler, i ordning: <b>exakt namn</b> (versaler och mellanrum spelar ingen roll) → <b>vikt namn</b> (å/ä/ö = a/a/o, ordningen fri, mellannamn tillåtna) →
    /// <b>nästan</b> (högst 1–2 tecken fel; bara ett förslag, aldrig markerad). Flera med samma namn avgörs av resten av raden (klass, starttid, klubb) när den
    /// pekar ut en ensam; annars <see cref="NameMatchStatus.Ambiguous"/>. Hellre en rad som människan får titta på än fel person.
    /// </summary>
    public static class NameListMatcher
    {
        public const int MaxLines = 200;

        public static List<NameMatch> Match(IReadOnlyList<string> lines, IReadOnlyList<MatchPerson> pool)
        {
            var result = new List<NameMatch>();
            var taken = new HashSet<int>();
            foreach (var raw in lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(MaxLines))
            {
                var (query, hints) = Parse(raw);
                if (query.Length == 0) continue;
                var m = MatchOne(raw.Trim(), query, hints, pool);
                if (m.Chosen != null && m.Status == NameMatchStatus.Matched && !taken.Add(m.Chosen.MemberId))
                    m = m with { Status = NameMatchStatus.Duplicate };
                result.Add(m);
            }
            return result;
        }

        /// <summary>Namndelen och resten (tips). Avskiljare: |, tabb, ; och parentes. "Efternamn, Förnamn" vänds.</summary>
        internal static (string Name, string Hints) Parse(string line)
        {
            var s = Regex.Replace(line.Trim(), @"^\s*(\d+[\.\)]|[-•*])\s+", "");
            var parts = Regex.Split(s, @"[|\t;(]");
            var name = parts[0].Trim();
            var hints = string.Join(" ", parts.Skip(1));
            var comma = name.Split(',');
            if (comma.Length == 2 && comma[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 1 && comma[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 1)
                name = comma[1].Trim() + " " + comma[0].Trim();
            else if (comma.Length > 1) { hints += " " + string.Join(" ", comma.Skip(1)); name = comma[0].Trim(); }
            return (Regex.Replace(name, @"\s+", " "), hints);
        }

        private static NameMatch MatchOne(string line, string query, string hints, IReadOnlyList<MatchPerson> pool)
        {
            var q = Norm(query);
            var qFold = Fold(query);
            var exact = pool.Where(p => Norm(p.Name) == q).ToList();
            var level = exact;
            if (level.Count == 0) level = pool.Where(p => Fold(p.Name) == qFold || SameTokens(p.Name, query)).ToList();
            if (level.Count == 0)
            {
                var near = pool.Select(p => (p, d: Distance(Fold(p.Name), qFold))).Where(x => x.d <= (qFold.Length > 12 ? 2 : 1)).OrderBy(x => x.d).ToList();
                if (near.Count > 0 && (near.Count == 1 || near[0].d < near[1].d)) return new NameMatch(line, query, NameMatchStatus.Suggested, near[0].p, new[] { near[0].p });
                return new NameMatch(line, query, NameMatchStatus.NotFound, null, near.Take(5).Select(x => x.p).ToList());
            }
            if (level.Count == 1) return new NameMatch(line, query, NameMatchStatus.Matched, level[0], level);

            // Flera med samma namn: raden avgör om den pekar ut en ensam.
            var scored = level.Select(p => (p, s: Score(p, hints))).OrderByDescending(x => x.s).ToList();
            if (scored[0].s > 0 && scored[0].s > scored[1].s) return new NameMatch(line, query, NameMatchStatus.Matched, scored[0].p, scored.Select(x => x.p).ToList());
            return new NameMatch(line, query, NameMatchStatus.Ambiguous, null, scored.Select(x => x.p).ToList());
        }

        /// <summary>Hur många tips som pekar på personen: klass, starttid, klubbens ord.</summary>
        private static int Score(MatchPerson p, string hints)
        {
            if (string.IsNullOrWhiteSpace(hints)) return 0;
            var score = 0;
            var tokens = Regex.Split(hints.ToLowerInvariant(), @"[^\p{L}\p{N}:]+").Where(t => t.Length > 0).ToList();
            if (!string.IsNullOrEmpty(p.Class) && tokens.Contains(p.Class.ToLowerInvariant())) score++;
            foreach (var t in p.StartTimes)
            {
                var short1 = t.TrimStart('0');
                if (tokens.Contains(t) || tokens.Contains(short1)) { score++; break; }
            }
            if (!string.IsNullOrWhiteSpace(p.Club))
                foreach (var w in Fold(p.Club).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4))
                    if (tokens.Any(t => Fold(t) == w || (w.Length >= 5 && Fold(t).StartsWith(w[..5])))) { score++; break; }
            return score;
        }

        private static bool SameTokens(string a, string b)
        {
            var ta = Fold(a).Split(' ', StringSplitOptions.RemoveEmptyEntries); var tb = Fold(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tb.Length < 2 || ta.Length < 2) return false;
            // samma ord i annan ordning, eller pappret har förnamn + efternamn och registret har ett mellannamn till
            return ta.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(tb.OrderBy(x => x, StringComparer.Ordinal))
                || (tb.Length == 2 && ta.Length > 2 && ta[0] == tb[0] && ta[^1] == tb[1]);
        }

        private static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

        /// <summary>Gemener, å/ä → a, ö → o, övriga accenter bort, skiljetecken → mellanrum.</summary>
        internal static string Fold(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        internal static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
                for (var j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
