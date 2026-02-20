namespace HpskSite.CompetitionTypes.Common.SeriesCalculation
{
    /// <summary>
    /// Shared utility for converting ranked scores into placement points.
    /// Supports two modes:
    /// - Dynamic: 1st = N points (N = participant count), 2nd = N-1, ..., last = 1
    /// - Fixed: Points from a configurable table (e.g., [25,20,16,13,...])
    /// Tied entities share the average of the positions they span (integer division).
    /// </summary>
    public static class PlacementPointsCalculator
    {
        public enum Mode { Off, Dynamic, Fixed }

        private static readonly int[] DefaultPointsTable = { 25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };

        /// <summary>
        /// Rank entries by score (desc) + xCount (desc), then assign points based on mode.
        /// </summary>
        /// <returns>Dictionary mapping EntityId to awarded points.</returns>
        public static Dictionary<int, int> Calculate(
            List<(int EntityId, int Score, int XCount)> entries,
            Mode mode,
            int[]? pointsTable = null)
        {
            var result = new Dictionary<int, int>();
            if (mode == Mode.Off || entries.Count == 0)
                return result;

            var ranked = entries
                .OrderByDescending(e => e.Score)
                .ThenByDescending(e => e.XCount)
                .ToList();

            int participantCount = ranked.Count;
            var table = mode == Mode.Fixed ? (pointsTable ?? DefaultPointsTable) : null;

            int i = 0;
            while (i < ranked.Count)
            {
                int tieStart = i;
                while (i + 1 < ranked.Count
                       && ranked[i + 1].Score == ranked[tieStart].Score
                       && ranked[i + 1].XCount == ranked[tieStart].XCount)
                {
                    i++;
                }

                // Positions tieStart..i share points
                int totalPointsForTie = 0;
                for (int p = tieStart; p <= i; p++)
                {
                    if (mode == Mode.Dynamic)
                    {
                        totalPointsForTie += Math.Max(participantCount - p, 0);
                    }
                    else // Fixed
                    {
                        totalPointsForTie += p < table!.Length ? table[p] : 0;
                    }
                }
                int sharedPoints = totalPointsForTie / (i - tieStart + 1);

                for (int p = tieStart; p <= i; p++)
                {
                    result[ranked[p].EntityId] = sharedPoints;
                }

                i++;
            }

            return result;
        }

        /// <summary>
        /// Parse a mode string ("off", "dynamic", "fixed") into the Mode enum.
        /// Returns Mode.Off for null/unknown values.
        /// </summary>
        public static Mode ParseMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Mode.Off;

            return value.Trim().ToLowerInvariant() switch
            {
                "dynamic" => Mode.Dynamic,
                "fixed" => Mode.Fixed,
                "placement" => Mode.Dynamic, // backward compat with ClubTeamBestOf
                _ => Mode.Off
            };
        }

        /// <summary>
        /// Parse a points table from strategy parameters.
        /// Accepts comma-separated values ("25, 20, 16"), JSON arrays ("[25,20,16]"), or JsonElement arrays.
        /// </summary>
        public static int[] ParsePointsTable(Dictionary<string, object> parameters, string key, int[]? defaultTable = null)
        {
            var fallback = defaultTable ?? DefaultPointsTable;

            if (!parameters.TryGetValue(key, out var ptObj))
                return fallback;

            string? raw = null;
            if (ptObj is string s) raw = s;
            else if (ptObj is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.String)
                raw = el.GetString();
            else if (ptObj is System.Text.Json.JsonElement el2 && el2.ValueKind == System.Text.Json.JsonValueKind.Array)
                raw = el2.GetRawText();

            if (string.IsNullOrWhiteSpace(raw))
                return fallback;

            raw = raw.Trim();

            // Try JSON array first (starts with '[')
            if (raw.StartsWith("["))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<int[]>(raw);
                    if (parsed != null && parsed.Length > 0) return parsed;
                }
                catch { /* fall through to comma-separated */ }
            }

            // Try comma-separated: "25, 20, 16, 13"
            try
            {
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var result = parts.Select(p => int.Parse(p)).ToArray();
                if (result.Length > 0) return result;
            }
            catch { /* fall through to default */ }

            return fallback;
        }

        /// <summary>
        /// Resolve a points table from strategy parameters.
        /// The select parameter (<paramref name="tableKey"/>) can hold a preset comma-separated string
        /// or "custom", in which case the actual table is read from <paramref name="customTableKey"/>.
        /// Also handles legacy JSON array values for backward compatibility.
        /// </summary>
        public static int[] ResolvePointsTable(Dictionary<string, object> parameters, string tableKey, string customTableKey)
        {
            var selected = ParseString(parameters, tableKey, "");

            // "custom" → delegate to the custom text parameter
            if (selected.Equals("custom", StringComparison.OrdinalIgnoreCase))
                return ParsePointsTable(parameters, customTableKey);

            // Otherwise try to parse the selected value directly (preset comma-separated or legacy JSON)
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var parsed = TryParseTable(selected);
                if (parsed != null) return parsed;
            }

            return DefaultPointsTable;
        }

        /// <summary>
        /// Parse a string parameter from the strategy parameter dictionary.
        /// Handles string and JsonElement types.
        /// </summary>
        public static string ParseString(Dictionary<string, object> parameters, string key, string defaultValue)
        {
            if (!parameters.TryGetValue(key, out var obj)) return defaultValue;
            if (obj is string strVal) return strVal;
            if (obj is System.Text.Json.JsonElement jsonEl && jsonEl.ValueKind == System.Text.Json.JsonValueKind.String)
                return jsonEl.GetString() ?? defaultValue;
            return defaultValue;
        }

        /// <summary>
        /// Try to parse a table string (JSON array or comma-separated). Returns null on failure.
        /// </summary>
        private static int[]? TryParseTable(string raw)
        {
            raw = raw.Trim();

            if (raw.StartsWith("["))
            {
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<int[]>(raw);
                    if (parsed != null && parsed.Length > 0) return parsed;
                }
                catch { }
            }

            try
            {
                var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var result = parts.Select(p => int.Parse(p)).ToArray();
                if (result.Length > 0) return result;
            }
            catch { }

            return null;
        }
    }
}
