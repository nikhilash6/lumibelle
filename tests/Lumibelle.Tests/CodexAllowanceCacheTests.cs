using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public class CodexAllowanceCacheTests
{
    [Fact]
    public async Task DisplayChecksAreDeduplicatedStaleOnFailureAndInvalidatedForAnotherAccount()
    {
        var clock = new Clock(); var client = new Client(clock); var service = new CodexAllowanceService(client, clock);
        var settings = new CodexSettings { Enabled = true };
        var ct = TestContext.Current.CancellationToken;
        var initial = await service.ReadAsync(settings, ct: ct);
        await service.ReadAsync(settings, ct: ct); Assert.Equal(1, client.Reads);
        clock.Now += TimeSpan.FromMinutes(1); client.Fail = true;
        var stale = await service.ReadAsync(settings, ct: ct);
        Assert.NotNull(stale.Error); Assert.Equal(initial.CheckedUtc, stale.CheckedUtc); Assert.Equal(initial.Limits, stale.Limits);
        client.Connection = null;
        var invalidated = await service.ReadAsync(settings, ct: ct);
        Assert.Empty(invalidated.Limits); Assert.NotNull(invalidated.Error);
        client.Fail = false; clock.Now += TimeSpan.FromMinutes(1);
        await service.ReadAsync(settings with { ExecutablePath = "another-cli" }, ct: ct);
        Assert.Equal(4, client.Reads);
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Client(Clock clock) : ICodexClient
    {
        public int Reads; public bool Fail;
        public event Action? Changed { add { } remove { } }
        public CodexConnection? Connection { get; set; }
        public Task<CodexConnection> CheckAsync(CodexSettings settings, CancellationToken ct = default)
        {
            Reads++; if (Fail) throw new AiGenerationException("Unavailable");
            Connection = new(true, "Connected", "1", "account", "Account", false, [], Usage());
            return Task.FromResult(Connection);
        }
        public Task<CodexUsage> ReadUsageAsync(CodexSettings settings, CancellationToken ct = default)
        { Reads++; if (Fail) throw new AiGenerationException("Unavailable"); return Task.FromResult(Usage()); }
        private CodexUsage Usage() => new(clock.Now, [new("codex", "Codex", new(20, 300, null), null)]);
        public IAsyncEnumerable<CodexUpdate> GenerateAsync(CodexRequest request, CancellationToken ct = default) => throw new InvalidOperationException("Capacity cannot generate");
        public Task StopAsync() => throw new InvalidOperationException("Capacity cannot stop work");
    }
}
