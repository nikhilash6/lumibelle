using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// Starts a spare Claude Code process for the default model at launch, so the first
// request does not wait for CLI startup. Unused spares close after five minutes.
public sealed class ClaudeCodeLaunch(IClaudeCodeClient claude, IAiSettingsStore settings, ILogger<ClaudeCodeLaunch> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var configured = (await settings.LoadAsync(stoppingToken)).ClaudeCode;
            if (configured.Enabled && configured.TextModel.Length > 0) claude.Prestart(configured, configured.TextModel, configured.TextEffort);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (WorkspaceStoreException e) { logger.LogInformation("Claude Code was not prestarted at launch: {Message}", e.Message); }
    }
}
