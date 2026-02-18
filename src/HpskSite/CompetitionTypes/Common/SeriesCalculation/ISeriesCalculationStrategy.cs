using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation
{
    public interface ISeriesCalculationStrategy
    {
        string Id { get; }
        string Name { get; }
        string Description { get; }
        List<StrategyParameter> GetParameters();
        SeriesResultData Calculate(SeriesCalculationContext context);
    }
}
