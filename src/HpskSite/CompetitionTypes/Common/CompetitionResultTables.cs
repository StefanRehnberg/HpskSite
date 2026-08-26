namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Competition type → its result table. THE one place this mapping lives.
    ///
    /// It existed in three copies before 2026-08-25, one of them carrying the comment "keep the two
    /// in sync" — which is the smell, not the safeguard. A drifted copy is how the Fältskytte series
    /// bug happened: the fallback silently answered `PrecisionResultEntry` for a discipline whose rows
    /// live somewhere else, so the query returned nothing and the page rendered "inga resultat än".
    /// </summary>
    public static class CompetitionResultTables
    {
        private static readonly Dictionary<string, string> ByType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Precision"] = "PrecisionResultEntry",
            ["Milsnabb"] = "MilsnabbResultEntry",
            ["Duell"] = "DuellResultEntry",
            ["NationellHelmatch"] = "NationellHelmatchResultEntry",
            ["MagnumPrecision"] = "MagnumPrecisionResultEntry",
            ["Springskytte"] = "SpringskytteResultEntry",
            ["Faltskytte"] = "FaltskytteResultEntry",
            ["MagnumFalt"] = "FaltskytteResultEntry",
            ["Standardpistol"] = "StandardpistolResultEntry",
            ["Sportpistol"] = "SportpistolResultEntry",
        };

        /// <summary>
        /// The precision-family fallback for an empty or unknown type is deliberate — legacy nodes
        /// carry no competitionType and are Precision. ⚠️ It also means a typo in a NAMED discipline
        /// silently reads the wrong table, so prefer <see cref="TryFor"/> where a wrong answer is
        /// worse than no answer (anything that DELETES rows).
        /// </summary>
        public static string For(string? typeId) =>
            ByType.TryGetValue((typeId ?? "").Trim(), out var t) ? t : "PrecisionResultEntry";

        /// <summary>Resolves only a recognised discipline; no silent fallback.</summary>
        public static bool TryFor(string? typeId, out string tableName)
        {
            tableName = "";
            var key = (typeId ?? "").Trim();
            if (key.Length == 0) return false;
            return ByType.TryGetValue(key, out tableName!);
        }

        /// <summary>
        /// Disciplines that own their own result controller, table AND row shape, and whose rows the
        /// SHARED precision-family result endpoints must therefore never address.
        /// </summary>
        private static readonly HashSet<string> OwnResultController =
            new(StringComparer.OrdinalIgnoreCase) { "Faltskytte", "MagnumFalt" };

        /// <summary>
        /// The map for the SHARED result endpoints — <c>CompetitionResults</c>' save / delete /
        /// class-change / series-read, and the class change in <c>PrecisionStartList</c>. Identical to
        /// <see cref="For"/> for every discipline those endpoints legitimately serve, including the
        /// empty/unknown → Precision fallback that legacy nodes depend on.
        ///
        /// ⚠️ It THROWS for Fältskytte/MagnumFält, and that is the whole point of it existing.
        /// Those endpoints used to carry their own copy of this map whose `_ => "PrecisionResultEntry"`
        /// fallback answered *Precision* for a fält competition — so a fält id reaching them read an
        /// empty table, MERGEd a precision-shaped row into it, or DELETEd from it: wrong, and silent.
        /// Simply pointing them at <see cref="For"/> instead would be worse, not better: the DELETE
        /// and the class-change UPDATE would start addressing REAL Fältskytte rows with a
        /// class-scoped WHERE, i.e. quietly destroying another discipline's results. A fält
        /// competition arriving here is a caller error either way, so say so instead of guessing —
        /// every call site is inside a try/catch that already reports a failure to the operator.
        /// Fältskytte's own controller is unaffected; it never routes through here.
        /// </summary>
        public static string ForSharedResultEndpoint(string? typeId)
        {
            var key = (typeId ?? "").Trim();
            if (OwnResultController.Contains(key))
            {
                throw new InvalidOperationException(
                    $"Tävlingstypen '{key}' har egna resultatytor och egen resultattabell " +
                    $"({For(key)}). De delade resultatvägarna för precisionsfamiljen får inte " +
                    "läsa eller skriva dess rader — anropet gick till fel endpoint.");
            }
            return For(key);
        }
    }
}
