namespace lumibelle.Services.Assets;

public sealed class ImageTrashCleanupService(IImageTrashStore trash, TimeProvider clock,
    ILogger<ImageTrashCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                foreach (var issue in await trash.CleanupExpiredAsync(stoppingToken))
                    logger.LogWarning("Trash cleanup for {ProjectId}: {Message}", issue.ProjectId, issue.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { logger.LogError(e, "Trash cleanup failed; it will retry on the next pass."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
