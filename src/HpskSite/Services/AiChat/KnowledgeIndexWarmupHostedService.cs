using HpskSite.Services.Hosting;

namespace HpskSite.Services.AiChat
{
    /// <summary>
    /// Bygger AI-chattens sökindex en stund efter start, så att den första frågan inte får vänta
    /// på det. Med en befintlig vektorcache går det på ett ögonblick; utan (första deployen, eller
    /// efter att kunskapsbasen ändrats mycket) tar det några sekunder och kostar ungefär ett öre.
    ///
    /// <para>Ärver <see cref="IsolatedBackgroundService"/> — se CLAUDE.md om varför en vanlig
    /// BackgroundService delar ambient scope med de andra startjobben. Den här rör ingen databas,
    /// men regeln gäller alla hostade tjänster och testas.</para>
    /// </summary>
    public class KnowledgeIndexWarmupHostedService : IsolatedBackgroundService
    {
        private readonly KnowledgeIndexService _index;
        private readonly ILogger<KnowledgeIndexWarmupHostedService> _logger;

        public KnowledgeIndexWarmupHostedService(KnowledgeIndexService index, ILogger<KnowledgeIndexWarmupHostedService> logger)
        {
            _index = index;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            if (!_index.IsSupported) return;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);
                var index = await _index.EnsureIndexAsync(stoppingToken);
                _logger.LogInformation("AI-chattens sökindex klart: {Count} stycken.", index.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Inte allvarligt: den första frågan försöker igen, och faller annars tillbaka
                // på hela kunskapsbasen. Warning, så det syns i prods logg.
                _logger.LogWarning(ex, "AI-chattens sökindex kunde inte förvärmas: {Message}", ex.Message);
            }
        }
    }
}
