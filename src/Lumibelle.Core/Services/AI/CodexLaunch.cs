using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// Starts an enabled Codex App Server at launch so the first request does not wait
// for it. The client closes it again after CodexClient.IdleTimeout without use.
public sealed class CodexLaunch(ICodexClient codex, IAiSettingsStore settings, ILogger<CodexLaunch> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var configured = (await settings.LoadAsync(stoppingToken)).Codex;
            if (configured.Enabled) await codex.CheckAsync(configured, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception e) when (e is AiGenerationException or WorkspaceStoreException) { logger.LogInformation("Codex was not started at launch: {Message}", e.Message); }
    }
}
