using HpskSite.Services.Ranking;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers the Träningsmatch ranking services + the nightly snapshot background job.
    /// See Documentation/TRANINGSMATCH_RANKING_SYSTEM.md.
    /// </summary>
    public class RankingComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddTransient<RankingSnapshotService>();
            builder.Services.AddTransient<RankingService>();
            builder.Services.AddHostedService<RankingSnapshotHostedService>();
        }
    }
}
