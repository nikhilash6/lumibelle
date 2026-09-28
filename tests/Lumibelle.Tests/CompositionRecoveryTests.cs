using System.Reflection;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class AiJobStoreTests
{
    [Fact]
    public async Task CompositionRetryHandlesStateChangesAfterTheButtonWasRendered()
    {
        var store = Store; var queued = await store.EnqueueAsync(Request(), _ct);
        await store.UpdateAsync(queued.Id, j => j with { State = AiJobState.NeedsAttention, Recovery = AiJobRecovery.CheckStatus }, _ct);
        using var queue = new AiJobCoordinator(store, new FakeAiSettingsStore(), [], _clock, NullLogger<AiJobCoordinator>.Instance);
        await queue.RefreshAsync(_ct);
        Assert.True(queue.View.Jobs.Single().CanRetryCaptured);
        // The displayed queue is stale; a different operation has classified the result.
        await store.UpdateAsync(queued.Id, j => j with { Recovery = AiJobRecovery.GenerateAgain }, _ct);
        var studio = new ProductionStudio { AiJobs = queue };
        var retry = typeof(ProductionStudio).GetMethod("RetryComposition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Guid, Task>>(studio);
        await retry(queued.Id);
        var error = (string?)typeof(ProductionStudio).GetField("_compositionError", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(studio);
        Assert.Contains("cannot be resumed", error);
        Assert.False((bool)typeof(ProductionStudio).GetField("_compositionBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(studio)!);
        Assert.Equal(AiJobState.NeedsAttention, (await store.ReadAsync(_ct)).Jobs.Single().State);
    }
}
