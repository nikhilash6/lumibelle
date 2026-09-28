using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Testing;

// Test-only runner; the real queue handler still owns durable results and settings writes.
public sealed class MockModelTestRunner(IAiProviderRegistry providers) : IModelTestRunner
{
    public async Task<AiModelTestJobResult> RunQueuedTestAsync(AiJobContext context, AiModelTestJobRequest request,
        ComfyJobExecution execution, CancellationToken ct)
    {
        if (request.Model.Backend == AiBackend.OpenRouter)
        {
            await context.ReportAsync(new(new(GenerationPhase.Generating, "Running mocked OpenRouter test…")), true);
            return new(null, request.Advanced ? "Mock reply to: " + request.Test.Prompt : "A quiet forest at dawn.",
                OpenRouter: new(context.Job.Id, request.Model.Model, DateTimeOffset.UtcNow, request.Test.MaxOutputTokens, request.Advanced,
                    2, .25, 30, 64, 0, 0, .000001m, request.Model.Model, "Mock provider", "mock-" + context.Job.Id, "stop", true));
        }
        ComfyTextModelVerification? verification = null; string? response = null;
        var updates = request.Advanced
            ? providers.TestComfyTextModelAsync(request.Model.Model, request.Settings, request.Test, ct)
            : providers.VerifyComfyTextModelAsync(request.Model.Model, request.Settings, ct);
        await foreach (var update in updates.WithCancellation(ct))
        {
            if (update.Progress is not null) await context.ReportAsync(new(update.Progress), true);
            verification = update.Verification ?? verification; response = update.Response ?? response;
        }
        return new(verification ?? throw new AiGenerationException("The test returned no verification."), response);
    }
}
