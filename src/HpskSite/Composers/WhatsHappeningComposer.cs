using HpskSite.Services;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Composers
{
    /// <summary>
    /// Registers <see cref="WhatsHappeningService"/> for the "Det här händer" home/region feed.
    /// </summary>
    public class WhatsHappeningComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<WhatsHappeningService>();
            // Scoped (not Singleton): it consumes IShooterStatisticsService, which is scoped.
            builder.Services.AddScoped<HomeHubService>();
            // Board-member hub section (consumes scoped board services).
            builder.Services.AddScoped<BoardHubService>();
            // Shooting-range compliance reminders on the hub.
            builder.Services.AddScoped<RangeHubService>();
        }
    }
}
