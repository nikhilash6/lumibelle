using lumibelle.Models;
using lumibelle.Services.AI;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class AiJobCoordinatorTests
{
    [Fact]
    public async Task ClaudeCodeUsesItsOwnConcurrencyAndPausesAtItsUsageLimitUntilResumed()
    {
        _settings.Value = _settings.Value with { ClaudeCode = new() { Enabled = true, Concurrency = 2 } };
        var mock = new MockClaudeCodeProcess(); await using var client = new ClaudeCodeClient(mock);
        await Store.EnqueueAsync(Request(AiBackend.ClaudeCode), _ct);
        await Store.EnqueueAsync(Request(AiBackend.ClaudeCode, AiJobKind.AssetExtraction), _ct);
        var waiting = await Store.EnqueueAsync(Request(AiBackend.ClaudeCode), _ct);
        var handler = new FakeHandler();
        var queue = new AiJobCoordinator(Store, _settings, [handler], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance, claude: client);
        _coordinators.Add(queue); await queue.StartAsync(_ct);
        var first = await handler.Next(_ct); var second = await handler.Next(_ct);
        Assert.Equal(2, handler.Calls.Count);
        first.Done.SetException(new ClaudeCodeLimitException("Claude usage limit reached."));
        await State(first.Context.Job.Id, j => j.State == AiJobState.NeedsAttention);
        Assert.Contains(AiBackend.ClaudeCode, (await Store.ReadAsync(_ct)).Paused);
        await second.Complete(); await State(second.Context.Job.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == waiting.Id).State);
        mock.LoggedIn = false;
        await Assert.ThrowsAsync<AiGenerationException>(() => queue.SetPausedAsync(AiBackend.ClaudeCode, false, _ct));
        mock.LoggedIn = true;
        await queue.SetPausedAsync(AiBackend.ClaudeCode, false, _ct);
        var third = await handler.Next(_ct); Assert.Equal(waiting.Id, third.Context.Job.Id);
        await third.Complete(); await State(waiting.Id, j => j.State == AiJobState.Completed);
    }
}
