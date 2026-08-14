using System.Globalization;
using System.Text;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Guesses which pistol.nu member a free-text roster name was meant to be.
    ///
    /// <para><b>Why:</b> a staffing plan is typed from a paper list, so most people start as free text — on
    /// the SM plan this was 32 of 41. A free-text row delivers nothing: no personal schedule, no push, no
    /// club tally for the surplus split. Linking them one at a time through a search box is 32 searches;
    /// the organiser will not do it. So we guess, and let them confirm a whole screenful at once.</para>
    ///
    /// <para><b>Never auto-links.</b> Every candidate is a suggestion a human accepts. Silently binding
    /// "Tommy" to the wrong Tommy would hand a stranger someone else's shift and, worse, would be invisible.</para>
    /// </summary>
    public class PersonMatchService
    {
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<PersonMatchService> _logger;

        public PersonMatchService(IMemberService memberService, ClubService clubService,
            IScopeProvider scopeProvider, ILogger<PersonMatchService> logger)
        {
            _memberService = memberService;
            _clubService = clubService;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>Every distinct free-text person on the competition, with the members they might be.</summary>
        public List<PersonMatchRow> Suggest(int competitionId, int maxCandidates = 4)
        {
            var result = new List<PersonMatchRow>();
            List<StaffAssignment> rows;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                rows = scope.Database.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND MemberId IS NULL", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PersonMatch: read failed for competition {CompetitionId}", competitionId);
                return result;
            }
            if (rows.Count == 0) return result;

            var members = LoadMembers();

            foreach (var grp in rows
                .Where(r => !string.IsNullOrWhiteSpace(r.DisplayName))
                .GroupBy(r => r.DisplayName.Trim().ToLowerInvariant())
                .OrderBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var name = grp.First().DisplayName.Trim();
                var row = new PersonMatchRow
                {
                    Key = "n:" + grp.Key,
                    Name = name,
                    RowCount = grp.Count(),
                    Email = grp.Select(r => r.Email).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
                    // Two names in one cell ("Bert J / Hans R") is a note, not a person — say so instead
                    // of confidently matching half of it.
                    LooksLikeTwoPeople = name.Contains('/') || name.Contains(" o ") || name.Contains(" och "),
                };

                if (!row.LooksLikeTwoPeople)
                {
                    var scored = members
                        .Select(m => new { m, r = Match(name, m.Name) })
                        .Where(x => x.r.Score > 0)
                        .OrderByDescending(x => x.r.Score)
                        .ThenBy(x => x.m.Name, StringComparer.CurrentCulture)
                        .ToList();

                    row.Candidates = scored.Take(maxCandidates)
                        .Select(x => new PersonMatchCandidate
                        {
                            MemberId = x.m.Id,
                            Name = x.m.Name,
                            ClubName = x.m.Club,
                            Score = x.r.Score,
                            Reason = x.r.Reason,
                        })
                        .ToList();

                    // Confident = one clear winner. Two people called Johansson must never pre-select one.
                    // An EXACT name match is confident even with a near-miss beside it — "Johan Hansson"
                    // spelled identically is not made doubtful by a "Johan Jansson" also being on file;
                    // only a SECOND exact match makes it genuinely ambiguous.
                    var exact = scored.Count(x => x.r.Score >= 100);
                    row.Confident = row.Candidates.Count > 0
                        && (exact == 1
                            || (row.Candidates[0].Score >= 90
                                && (row.Candidates.Count == 1 || row.Candidates[0].Score - row.Candidates[1].Score >= 20)));
                }
                result.Add(row);
            }

            return result
                .OrderByDescending(r => r.Confident)
                .ThenByDescending(r => r.Candidates.Count > 0)
                .ThenBy(r => r.Name, StringComparer.CurrentCulture)
                .ToList();
        }

        private record MemberLite(int Id, string Name, string? Club);

        private List<MemberLite> LoadMembers()
        {
            var list = new List<MemberLite>();
            try
            {
                var clubNames = new Dictionary<int, string?>();
                foreach (var m in _memberService.GetAll(0, int.MaxValue, out _))
                {
                    if (!m.IsApproved) continue;
                    var first = m.GetValue<string>("firstName");
                    var last = m.GetValue<string>("lastName");
                    var name = $"{first} {last}".Trim();
                    if (string.IsNullOrWhiteSpace(name)) name = m.Name ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string? club = null;
                    // primaryClubId is stored as a STRING on the member type.
                    var raw = m.GetValue<string>("primaryClubId");
                    if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out var cid) && cid > 0)
                    {
                        if (!clubNames.TryGetValue(cid, out club))
                        {
                            club = _clubService.GetClubNameById(cid);
                            clubNames[cid] = club;
                        }
                    }
                    list.Add(new MemberLite(m.Id, name, club));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PersonMatch: member load failed");
            }
            return list;
        }

        // ---------------------------------------------------------------- scoring

        /// <summary>
        /// 0 = no match. Higher is better. The reason comes from the RULE THAT FIRED, not from the score —
        /// deriving it from the number labelled "same surname, same initial" as a spelling variant, which
        /// tells the organiser something untrue about why we are suggesting this person.
        /// Tuned against the real failure modes in a hand-typed plan: a misspelling ("Hanrik Stensson"),
        /// an abbreviation ("Hugo R"), and a first name alone ("Tommy").
        /// </summary>
        internal static (int Score, string Reason) Match(string typed, string member)
        {
            var a = Norm(typed);
            var b = Norm(member);
            if (a.Length == 0 || b.Length == 0) return (0, "");
            if (a == b) return (100, "Namnet stämmer exakt");

            var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (at.Length == 0 || bt.Length == 0) return (0, "");

            // Same tokens, any order ("Reschke Hans" = "Hans Reschke").
            if (at.Length == bt.Length && at.OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(bt.OrderBy(x => x, StringComparer.Ordinal)))
                return (95, "Samma namn, omvänd ordning");

            var aLast = at[^1];
            var bLast = bt[^1];
            var aFirst = at[0];
            var bFirst = bt[0];

            // "Hugo R" → surname abbreviated to an initial.
            if (aFirst == bFirst && aLast.Length == 1 && bLast.StartsWith(aLast, StringComparison.Ordinal))
                return (88, "Efternamnet förkortat till initial");
            if (bFirst == aFirst && bLast.Length == 1 && aLast.StartsWith(bLast, StringComparison.Ordinal))
                return (88, "Efternamnet förkortat till initial");

            if (aLast == bLast && aFirst == bFirst) return (92, "Samma för- och efternamn");

            // Misspelling somewhere in the full string ("Hanrik" → "Henrik").
            var d = Levenshtein(a, b);
            if (d == 1) return (90, "En bokstavs skillnad");
            if (d == 2) return (78, "Två bokstävers skillnad");

            if (aLast == bLast)
            {
                if (aFirst.Length > 0 && bFirst.Length > 0 && aFirst[0] == bFirst[0])
                    return (72, "Samma efternamn, samma initial");
                return (45, "Samma efternamn");
            }

            // A single token that IS someone's first name ("Tommy", "Monika") — deliberately weak, because
            // it is exactly the case where guessing wrong is easiest.
            if (at.Length == 1 && bFirst == aFirst) return (40, "Bara förnamnet angivet");
            if (at.Length == 1 && bLast == aFirst) return (38, "Matchar ett efternamn");

            if (d <= 3 && Math.Abs(a.Length - b.Length) <= 3) return (30, "Svag likhet");
            return (0, "");
        }

        /// <summary>Lowercase, strip punctuation, map å/ä/ö explicitly BEFORE decomposition (otherwise
        /// "ö" silently becomes "o" and Söder collides with Soder).</summary>
        internal static string Norm(string s)
        {
            var lowered = (s ?? "").Trim().ToLowerInvariant()
                .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
                .Replace("é", "e").Replace("ü", "u").Replace("ø", "o").Replace("æ", "ae");
            var n = lowered.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(n.Length);
            foreach (var ch in n)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        internal static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) prev[j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                (prev, cur) = (cur, prev);
            }
            return prev[b.Length];
        }
    }
}
