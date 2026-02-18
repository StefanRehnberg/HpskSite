using HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation
{
    public static class SeriesCalculationRegistry
    {
        private static readonly Dictionary<string, ISeriesCalculationStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);

        static SeriesCalculationRegistry()
        {
            Register(new IndividualSumAllStrategy());
            Register(new IndividualBestOfStrategy());
            Register(new IndividualWinsCountStrategy());
            Register(new IndividualFixedPointsStrategy());
            Register(new IndividualDynamicPointsStrategy());
            Register(new ClubTeamBestOfStrategy());
        }

        public static void Register(ISeriesCalculationStrategy strategy) => _strategies[strategy.Id] = strategy;

        public static ISeriesCalculationStrategy? GetById(string id) =>
            !string.IsNullOrEmpty(id) && _strategies.TryGetValue(id, out var s) ? s : null;

        public static List<ISeriesCalculationStrategy> GetAll() => _strategies.Values.ToList();
    }
}
