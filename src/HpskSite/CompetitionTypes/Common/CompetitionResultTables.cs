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
    }
}
