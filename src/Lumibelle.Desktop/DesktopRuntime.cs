using Microsoft.Extensions.Hosting;
namespace Lumibelle.Desktop;

// MAUI does not start IHostedService automatically. Use its ONE application container.
public sealed class DesktopRuntime(IEnumerable<IHostedService> services, ILogger<DesktopRuntime> logger)
{
    private readonly List<IHostedService> started = [];
    public async Task StartAsync(CancellationToken ct = default)
    {
        try { foreach (var service in services) { await service.StartAsync(ct); started.Add(service); } }
        catch { await StopAsync(); throw; }
    }
    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var service in started.AsEnumerable().Reverse())
            try { await service.StopAsync(timeout.Token); }
            catch (Exception ex) { logger.LogError(ex, "A worker could not finish shutdown; captured jobs will recover on restart."); }
        started.Clear();
    }
}
