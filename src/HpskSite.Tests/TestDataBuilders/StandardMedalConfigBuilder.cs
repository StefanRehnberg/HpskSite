using HpskSite.CompetitionTypes.Precision.Services;

namespace HpskSite.Tests.TestDataBuilders
{
    /// <summary>
    /// Fluent builder for creating StandardMedalConfig test data
    /// </summary>
    public class StandardMedalConfigBuilder
    {
        private int _seriesCount = 6;
        private bool _shouldSplitGroupC = false;

        public StandardMedalConfigBuilder WithSeriesCount(int seriesCount)
        {
            _seriesCount = seriesCount;
            return this;
        }

        public StandardMedalConfigBuilder WithSplitGroupC(bool shouldSplit)
        {
            _shouldSplitGroupC = shouldSplit;
            return this;
        }

        public StandardMedalConfigBuilder ForCompetitionScope(string scope)
        {
            // Determine if Group C should be split based on competition scope
            _shouldSplitGroupC = scope switch
            {
                "Svenskt Mästerskap" => true,
                "Landsdelsmästerskap" => true,
                "Kretsmästerskap" => false,
                "Klubbmästerskap" => false,
                _ => false
            };
            return this;
        }

        public StandardMedalConfig Build()
        {
            return new StandardMedalConfig
            {
                SeriesCount = _seriesCount,
                ShouldSplitGroupC = _shouldSplitGroupC
            };
        }

        /// <summary>
        /// Resets the builder to default values for reuse
        /// </summary>
        public StandardMedalConfigBuilder Reset()
        {
            _seriesCount = 6;
            _shouldSplitGroupC = false;
            return this;
        }
    }
}
