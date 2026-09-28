using lumibelle.Services.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lumibelle.Tests;

internal sealed class ModelTestQueueFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Lumibelle.ModelTestQueue", Guid.NewGuid().ToString("N"));
    private AiJobCoordinator? _queue;
    public void Register(IServiceCollection services)
    {
        services.AddHttpClient(); services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAiJobStore>(new FileAiJobStore(_root, TimeProvider.System));
        services.AddSingleton<IAiReviewGate>(new ScriptReviewGate());
        services.AddSingleton<IComfyWebSocketFactory, ClientComfyWebSocketFactory>();
        services.AddSingleton<IComfyExecutionMonitor, ComfyExecutionMonitor>();
        services.AddSingleton<ComfyJobExecution>();
        services.AddSingleton<IModelTestRunner, Lumibelle.Testing.MockModelTestRunner>();
        services.AddSingleton<IAiJobHandler, AiModelTestJobHandler>();
        services.AddSingleton<AiJobCoordinator>();
    }
    public void Start(IServiceProvider services)
    { _queue = services.GetRequiredService<AiJobCoordinator>(); _queue.StartAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    public void Dispose()
    {
        _queue?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

