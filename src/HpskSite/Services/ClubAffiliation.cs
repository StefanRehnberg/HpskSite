namespace HpskSite.Services
{
    /// <summary>
    /// Medlemmens klubbar i egenskaperna (<c>primaryClubId</c> + <c>memberClubIDs</c>) som rena funktioner.
    ///
    /// <para><b>Egenskaperna och <c>ClubMembership</c> ska säga samma sak</b> (genomgången 2026-10-13, A5):
    /// en klubb står i egenskaperna ⇔ medlemskapet i tabellen är inte avslutat (Utträdd/Avliden).
    /// <see cref="ClubMembershipSyncHandler"/> för egenskaperna till tabellen; klubbadmins dialog, som
    /// skriver tabellen, för tillbaka ändringen till egenskaperna med de här funktionerna.</para>
    /// </summary>
    public static class ClubAffiliation
    {
        public static readonly string[] EndedStatuses = { "Utträdd", "Avliden" };

        public static bool IsEnded(string? status)
            => EndedStatuses.Contains((status ?? "").Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Klubben tillagd. Står den redan där ändras ingenting. Saknas huvudklubb blir den huvudklubb,
        /// annars läggs den sist bland övriga.
        /// </summary>
        public static (string? Primary, string Others) With(string? primary, string? others, int clubId)
        {
            var p = (primary ?? "").Trim();
            var list = Split(others);
            var id = clubId.ToString();
            if (p == id || list.Contains(id)) return (NullIfEmpty(p), string.Join(",", list));
            if (p.Length == 0) return (id, string.Join(",", list));
            list.Add(id);
            return (p, string.Join(",", list));
        }

        /// <summary>
        /// Klubben borttagen. Var den huvudklubb blir medlemmens första andra klubb huvudklubb (och
        /// lämnar listan över övriga); har medlemmen ingen annan klubb blir huvudklubben tom.
        /// Stefans beslut 2026-10-13 — en medlem med andra klubbar ska inte stå utan huvudklubb.
        /// </summary>
        public static (string? Primary, string Others) Without(string? primary, string? others, int clubId)
        {
            var p = (primary ?? "").Trim();
            var id = clubId.ToString();
            var list = Split(others).Where(c => c != id).ToList();
            if (p != id) return (NullIfEmpty(p), string.Join(",", list));
            if (list.Count == 0) return (null, "");
            var promoted = list[0];
            return (promoted, string.Join(",", list.Skip(1)));
        }

        private static List<string> Split(string? csv)
            => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    }
}
