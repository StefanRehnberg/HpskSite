using HpskSite.Models.Competitions;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Läsningar som flera anropare behöver och som bygger på <see cref="ICompetitionStore"/>.
    /// </summary>
    public static class CompetitionStoreExtensions
    {
        /// <summary>
        /// Många tävlingar på en gång, för en lista som tidigare läste <c>IContentService.GetByIds</c>:
        /// de publicerade i ett svep (<see cref="ICompetitionStore.GetMany"/>), det sparade läget bara
        /// för dem som saknas där (opublicerade). Ett id som inte är en tävling, eller ligger i
        /// papperskorgen, saknas i svaret.
        /// </summary>
        public static IReadOnlyDictionary<int, CompetitionInfo> GetManyIncludingUnpublished(
            this ICompetitionStore store, IEnumerable<int> ids)
        {
            var wanted = ids.Where(id => id > 0).Distinct().ToList();
            var result = new Dictionary<int, CompetitionInfo>(store.GetMany(wanted));
            foreach (var id in wanted)
            {
                if (result.ContainsKey(id)) continue;
                var saved = store.GetSaved(id);
                if (saved != null) result[id] = saved;
            }
            return result;
        }
    }
}
