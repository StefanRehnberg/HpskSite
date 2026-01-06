using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services;

/// <summary>
/// Background service that automatically deletes target photos from matches
/// that have been completed for longer than the configured retention period.
/// </summary>
public class TargetPhotoCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<TargetPhotoCleanupService> _logger;
    private readonly IConfiguration _configuration;

    // Run once per day
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(24);

    public TargetPhotoCleanupService(
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment environment,
        ILogger<TargetPhotoCleanupService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TargetPhotoCleanupService started. Will run cleanup every {Interval} hours", CleanupInterval.TotalHours);

        // Wait a bit after startup before first run
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOldPhotosAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown, don't log as error
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during target photo cleanup");
            }

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
        }

        _logger.LogInformation("TargetPhotoCleanupService stopped");
    }

    private async Task CleanupOldPhotosAsync(CancellationToken cancellationToken)
    {
        var retentionDays = _configuration.GetValue("TargetPhotos:RetentionDays", 90);
        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

        _logger.LogInformation("Starting target photo cleanup. Retention: {RetentionDays} days, Cutoff: {CutoffDate:yyyy-MM-dd}",
            retentionDays, cutoffDate);

        using var serviceScope = _scopeFactory.CreateScope();
        var scopeProvider = serviceScope.ServiceProvider.GetRequiredService<IScopeProvider>();

        using var scope = scopeProvider.CreateScope();
        var db = scope.Database;

        // Find matches completed more than RetentionDays ago
        var oldMatches = await db.FetchAsync<OldMatchDto>(
            @"SELECT Id, MatchCode FROM TrainingMatches
              WHERE Status = 'Completed' AND CompletedDate < @0",
            cutoffDate);

        if (!oldMatches.Any())
        {
            _logger.LogDebug("No old completed matches found for cleanup");
            scope.Complete();
            return;
        }

        var photoDir = Path.Combine(_environment.WebRootPath, "media", "target-photos");
        if (!Directory.Exists(photoDir))
        {
            _logger.LogDebug("Target photos directory does not exist, nothing to clean up");
            scope.Complete();
            return;
        }

        var totalDeleted = 0;
        var totalErrors = 0;

        foreach (var match in oldMatches)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var pattern = $"{match.MatchCode}_*.jpg";
            var matchFiles = Directory.GetFiles(photoDir, pattern);

            foreach (var file in matchFiles)
            {
                try
                {
                    File.Delete(file);
                    totalDeleted++;
                    _logger.LogDebug("Deleted target photo: {File}", Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    _logger.LogWarning(ex, "Failed to delete target photo: {File}", file);
                }
            }
        }

        scope.Complete();

        if (totalDeleted > 0 || totalErrors > 0)
        {
            _logger.LogInformation("Target photo cleanup completed. Deleted: {DeletedCount}, Errors: {ErrorCount}, Matches processed: {MatchCount}",
                totalDeleted, totalErrors, oldMatches.Count);
        }
        else
        {
            _logger.LogDebug("Target photo cleanup completed. No files to delete.");
        }
    }

    /// <summary>
    /// Simple DTO for fetching old match data
    /// </summary>
    private class OldMatchDto
    {
        public int Id { get; set; }
        public string MatchCode { get; set; } = string.Empty;
    }
}
