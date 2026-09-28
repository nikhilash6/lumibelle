using lumibelle.Models;
using lumibelle.Services.AI;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class CodexLifetimeTests
{
    private static readonly CodexSettings Settings = new() { Enabled = true };
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUnusedAppServerClosesAfterFiveMinutesAndRestartsOnTheNextUse()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow); var mock = new MockCodexTransport();
        await using var client = new CodexClient(mock, clock);
        await client.CheckAsync(Settings, _ct);
        clock.Advance(CodexClient.IdleTimeout - TimeSpan.FromSeconds(1));
        await client.CloseIfIdleAsync();
        Assert.Equal(0, mock.Stops); Assert.True(mock.Alive);
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.CloseIfIdleAsync();
        Assert.Equal(1, mock.Stops); Assert.False(mock.Alive);
        Assert.True((await client.CheckAsync(Settings, _ct)).Success);
        Assert.Equal(2, mock.Starts);
    }

    [Fact]
    public async Task ARunningRequestKeepsTheAppServerOpenAndItsEndRestartsTheIdleTime()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow); var mock = new MockCodexTransport { Hold = true };
        await using var client = new CodexClient(mock, clock);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        await foreach (var update in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), [new("user", [new(Text: "Hello")])]), _ct))
        {
            if (update.TurnId is null || update.Complete) continue;
            clock.Advance(CodexClient.IdleTimeout * 2);
            await client.CloseIfIdleAsync();
            Assert.Equal(0, mock.Stops);
            mock.Emit("turn/completed", new { threadId = update.ThreadId, turn = new { id = update.TurnId, status = "completed" } });
        }
        await client.CloseIfIdleAsync();
        Assert.Equal(0, mock.Stops);
        clock.Advance(CodexClient.IdleTimeout);
        await client.CloseIfIdleAsync();
        Assert.Equal(1, mock.Stops);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LaunchStartsOnlyAnEnabledCodex(bool enabled)
    {
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        var settings = new FakeAiSettingsStore(); settings.Value = settings.Value with { Codex = Settings with { Enabled = enabled } };
        var launch = new CodexLaunch(client, settings, NullLogger<CodexLaunch>.Instance);
        await launch.StartAsync(_ct); await launch.ExecuteTask!;
        Assert.Equal(enabled ? 1 : 0, mock.Starts);
        Assert.Equal(enabled, client.Connection?.Success == true);
        Assert.Equal(0, mock.Threads);
    }
}
