using lumibelle.Models;
using lumibelle.Services.AI;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed partial class AiJobCoordinatorTests
{
    [Fact]
    public async Task CodexSharesConcurrencyAndPausesOnAReportedUsageLimitUntilResumed()
    {
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, Concurrency = 2 } };
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        await client.CheckAsync(_settings.Value.Codex, _ct);
        var a = await Store.EnqueueAsync(Request(AiBackend.Codex), _ct);
        var b = await Store.EnqueueAsync(Request(AiBackend.Codex, AiJobKind.AssetExtraction), _ct);
        var waiting = await Store.EnqueueAsync(Request(AiBackend.Codex), _ct);
        var handler = new FakeHandler();
        var queue = new AiJobCoordinator(Store, _settings, [handler], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance, client);
        _coordinators.Add(queue); await queue.StartAsync(_ct);
        var first = await handler.Next(_ct); var second = await handler.Next(_ct);
        Assert.Equal(2, handler.Calls.Count);
        first.Done.SetException(new CodexAllowanceException("Allowance exhausted"));
        await State(first.Context.Job.Id, j => j.State == AiJobState.NeedsAttention);
        Assert.Contains(AiBackend.Codex, (await Store.ReadAsync(_ct)).Paused);
        await second.Complete(); await State(second.Context.Job.Id, j => j.State == AiJobState.Completed);
        Assert.Equal(AiJobState.Waiting, (await Store.ReadAsync(_ct)).Jobs.Single(j => j.Id == waiting.Id).State);
        // Resuming confirms Codex is connected; a stale allowance reading does not block it.
        mock.AuthType = null;
        await Assert.ThrowsAsync<AiGenerationException>(() => queue.SetPausedAsync(AiBackend.Codex, false, _ct));
        mock.AuthType = "chatgpt"; mock.Used = 100;
        await queue.SetPausedAsync(AiBackend.Codex, false, _ct);
        var third = await handler.Next(_ct); Assert.Equal(waiting.Id, third.Context.Job.Id);
        await third.Complete(); await State(waiting.Id, j => j.State == AiJobState.Completed);
    }
}
