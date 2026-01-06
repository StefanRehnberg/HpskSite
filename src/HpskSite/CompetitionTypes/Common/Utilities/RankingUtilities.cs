namespace HpskSite.CompetitionTypes.Common.Utilities
{
    /// <summary>
    /// Utility functions for ranking and tie-breaking.
    /// Works with any type that has comparable properties.
    /// </summary>
    public static class RankingUtilities
    {
        /// <summary>
        /// Rank items with tie-breaking rules.
        /// Multiple ranking rules are applied in order (primary, then secondary, etc.)
        /// </summary>
        /// <example>
        /// var ranked = RankingUtilities.RankWithTieBreakers(
        ///     participants,
        ///     p => -p.TotalScore,      // Primary: highest score first
        ///     p => -p.InnerTensCount,  // Tiebreaker 1: most X's
        ///     p => p.Name               // Tiebreaker 2: alphabetical by name
        /// );
        /// </example>
        /// <param name="items">Items to rank</param>
        /// <param name="rankingRules">Ranking rules in order of importance (primary first, then tiebreakers)</param>
        /// <returns>List of items with their assigned rank</returns>
        public static List<(T item, int rank)> RankWithTieBreakers<T>(
            IEnumerable<T> items,
            params Func<T, IComparable>[] rankingRules)
        {
            if (rankingRules == null || rankingRules.Length == 0)
                throw new ArgumentException("At least one ranking rule required");

            var list = items.ToList();
            if (!list.Any())
                return new List<(T, int)>();

            // Sort by all rules in order (primary first, then tiebreakers)
            var sorted = list.OrderBy(rankingRules[0]);
            for (int i = 1; i < rankingRules.Length; i++)
            {
                sorted = sorted.ThenBy(rankingRules[i]);
            }

            var sortedList = sorted.ToList();

            // Assign ranks with tie support (same primary score = same rank)
            var ranked = new List<(T, int)>();
            int currentRank = 1;
            IComparable previousValue = null;

            foreach (var item in sortedList)
            {
                var currentValue = rankingRules[0](item);

                // If primary value changed, update rank
                if (previousValue != null && currentValue.CompareTo(previousValue) != 0)
                {
                    currentRank = ranked.Count + 1;
                }

                ranked.Add((item, currentRank));
                previousValue = currentValue;
            }

            return ranked;
        }

        /// <summary>
        /// Group results by a property selector.
        /// </summary>
        /// <example>
        /// var byClass = RankingUtilities.GroupByProperty(
        ///     participants,
        ///     p => p.ShootingClass
        /// );
        /// </example>
        /// <param name="items">Items to group</param>
        /// <param name="groupSelector">Function to select grouping property</param>
        /// <returns>Dictionary with groups and their items</returns>
        public static Dictionary<string, List<T>> GroupByProperty<T>(
            IEnumerable<T> items,
            Func<T, string> groupSelector)
        {
            if (items == null || groupSelector == null)
                return new Dictionary<string, List<T>>();

            return items.GroupBy(groupSelector)
                .ToDictionary(g => g.Key ?? "Unknown", g => g.ToList());
        }

        /// <summary>
        /// Rank items within each group independently.
        /// Useful for class-based rankings where each class has separate rankings.
        /// </summary>
        /// <example>
        /// var classRankings = RankingUtilities.RankByGroup(
        ///     participants,
        ///     p => p.ShootingClass,
        ///     p => -p.TotalScore,      // Primary: highest score
        ///     p => -p.InnerTensCount   // Tiebreaker: most X's
        /// );
        /// // Result: { "Klass1": [(item1, 1), (item2, 2), ...], "Klass2": [...] }
        /// </example>
        /// <param name="items">Items to rank</param>
        /// <param name="groupSelector">Function to select grouping property</param>
        /// <param name="rankingRules">Ranking rules in order of importance</param>
        /// <returns>Dictionary with groups and ranked items within each group</returns>
        public static Dictionary<string, List<(T item, int rank)>> RankByGroup<T>(
            IEnumerable<T> items,
            Func<T, string> groupSelector,
            params Func<T, IComparable>[] rankingRules)
        {
            if (items == null)
                return new Dictionary<string, List<(T, int)>>();

            var grouped = GroupByProperty(items, groupSelector);
            var result = new Dictionary<string, List<(T, int)>>();

            foreach (var group in grouped)
            {
                result[group.Key] = RankWithTieBreakers(group.Value, rankingRules);
            }

            return result;
        }

        /// <summary>
        /// Sort items in descending order of a numeric value.
        /// </summary>
        /// <param name="items">Items to sort</param>
        /// <param name="selector">Function to select value to sort by</param>
        /// <returns>Items sorted descending by selected value</returns>
        public static List<T> SortDescending<T>(
            IEnumerable<T> items,
            Func<T, decimal> selector)
        {
            if (items == null)
                return new List<T>();

            return items.OrderByDescending(selector).ToList();
        }

        /// <summary>
        /// Sort items in ascending order of a numeric value.
        /// </summary>
        /// <param name="items">Items to sort</param>
        /// <param name="selector">Function to select value to sort by</param>
        /// <returns>Items sorted ascending by selected value</returns>
        public static List<T> SortAscending<T>(
            IEnumerable<T> items,
            Func<T, decimal> selector)
        {
            if (items == null)
                return new List<T>();

            return items.OrderBy(selector).ToList();
        }

        /// <summary>
        /// Get top N items from a ranked list.
        /// </summary>
        /// <param name="rankedItems">List of items with ranks</param>
        /// <param name="count">Number of top items to return</param>
        /// <returns>Top N items</returns>
        public static List<T> GetTopN<T>(
            IEnumerable<(T item, int rank)> rankedItems,
            int count)
        {
            if (rankedItems == null || count <= 0)
                return new List<T>();

            return rankedItems
                .Where(x => x.rank <= count)
                .Select(x => x.item)
                .ToList();
        }

        /// <summary>
        /// Get items within a rank range.
        /// </summary>
        /// <param name="rankedItems">List of items with ranks</param>
        /// <param name="startRank">Start rank (inclusive)</param>
        /// <param name="endRank">End rank (inclusive)</param>
        /// <returns>Items within rank range</returns>
        public static List<T> GetRankRange<T>(
            IEnumerable<(T item, int rank)> rankedItems,
            int startRank,
            int endRank)
        {
            if (rankedItems == null || startRank < 1 || endRank < startRank)
                return new List<T>();

            return rankedItems
                .Where(x => x.rank >= startRank && x.rank <= endRank)
                .Select(x => x.item)
                .ToList();
        }

        /// <summary>
        /// Get items with a specific rank (handles ties).
        /// Multiple items can have the same rank if they tied.
        /// </summary>
        /// <param name="rankedItems">List of items with ranks</param>
        /// <param name="rank">Rank to filter by</param>
        /// <returns>All items with specified rank</returns>
        public static List<T> GetRank<T>(
            IEnumerable<(T item, int rank)> rankedItems,
            int rank)
        {
            if (rankedItems == null || rank < 1)
                return new List<T>();

            return rankedItems
                .Where(x => x.rank == rank)
                .Select(x => x.item)
                .ToList();
        }

        /// <summary>
        /// Add medal/placement labels to ranked items.
        /// </summary>
        /// <param name="rankedItems">List of items with ranks</param>
        /// <returns>Dictionary mapping rank to placement label (Gold, Silver, etc.)</returns>
        public static Dictionary<int, string> GetPlacementLabels<T>(
            IEnumerable<(T item, int rank)> rankedItems)
        {
            if (rankedItems == null)
                return new Dictionary<int, string>();

            var labels = new Dictionary<int, string>();
            var uniqueRanks = rankedItems.Select(x => x.rank).Distinct().OrderBy(x => x);

            foreach (var rank in uniqueRanks)
            {
                labels[rank] = GetPlacementLabel(rank);
            }

            return labels;
        }

        /// <summary>
        /// Get placement label for a rank.
        /// </summary>
        /// <param name="rank">Rank position</param>
        /// <returns>Label (e.g., "Gold", "Silver", "Bronze", "4th Place", etc.)</returns>
        public static string GetPlacementLabel(int rank)
        {
            return rank switch
            {
                1 => "🥇 Gold",
                2 => "🥈 Silver",
                3 => "🥉 Bronze",
                4 => "4th Place",
                5 => "5th Place",
                _ => $"{rank}th Place"
            };
        }

        /// <summary>
        /// Check if two items tied for the same rank.
        /// </summary>
        /// <param name="item1">First item with rank</param>
        /// <param name="item2">Second item with rank</param>
        /// <returns>True if items have the same rank</returns>
        public static bool IsTied<T>(
            (T item, int rank) item1,
            (T item, int rank) item2)
        {
            return item1.rank == item2.rank;
        }

        /// <summary>
        /// Group items by rank (returns dict of rank -> items).
        /// Useful for tie handling.
        /// </summary>
        /// <param name="rankedItems">List of items with ranks</param>
        /// <returns>Dictionary mapping rank to all items with that rank</returns>
        public static Dictionary<int, List<T>> GroupByRank<T>(
            IEnumerable<(T item, int rank)> rankedItems)
        {
            if (rankedItems == null)
                return new Dictionary<int, List<T>>();

            return rankedItems
                .GroupBy(x => x.rank)
                .ToDictionary(g => g.Key, g => g.Select(x => x.item).ToList());
        }

        /// <summary>
        /// Renumber ranks sequentially (1, 2, 3...) after tie-breaking.
        /// Useful when you want consecutive numbers instead of 1, 1, 3, 4...
        /// </summary>
        /// <param name="rankedItems">List of items with ranks</param>
        /// <returns>Items with renumbered sequential ranks</returns>
        public static List<(T item, int rank)> RenumberSequential<T>(
            IEnumerable<(T item, int rank)> rankedItems)
        {
            if (rankedItems == null)
                return new List<(T, int)>();

            var result = new List<(T, int)>();
            int newRank = 1;

            foreach (var item in rankedItems.OrderBy(x => x.rank))
            {
                result.Add((item.item, newRank));
                newRank++;
            }

            return result;
        }
    }
}
