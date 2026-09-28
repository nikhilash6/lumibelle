using lumibelle.Models;
using lumibelle.Services.AI;
using Lumibelle.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class ClaudeCodeStandbyTests
{
    private static readonly ClaudeCodeSettings Settings = new() { Enabled = true, TextModel = "sonnet", TextEffort = "low" };
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private async Task Run(ClaudeCodeClient client, string model = "sonnet", string? effort = "low")
    {
        var directory = Path.Combine(Path.GetTempPath(), "Lumibelle.ClaudeCodeStandbyTests", Guid.NewGuid().ToString("N"));
        await foreach (var _ in client.GenerateAsync(new(Settings, model, effort, directory, [new("user", [new(Text: "Hi")])]), _ct)) { }
    }

    [Fact]
    public async Task EachRequestLeavesASpareThatTheNextMatchingRequestUses()
    {
        var mock = new MockClaudeCodeProcess(); await using var client = new ClaudeCodeClient(mock);
        await Run(client);
        Assert.Equal(2, mock.Starts.Count); Assert.Equal([0], mock.Used);
        await Run(client);
        Assert.Equal(3, mock.Starts.Count); Assert.Equal([0, 1], mock.Used);
        // A different effort is a different command line, so it cannot use that spare.
        await Run(client, effort: "high");
        Assert.Equal(3, mock.Used[^1]);
        Assert.Equal("high", mock.Starts[3][mock.Starts[3].ToList().IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task SparesCloseAfterFiveMinutesUnusedAndExitedSparesAreNotUsed()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow); var mock = new MockClaudeCodeProcess();
        await using var client = new ClaudeCodeClient(mock, clock);
        client.Prestart(Settings, "sonnet", "low");
        var directory = Assert.Single(mock.Directories);
        Assert.True(Directory.Exists(directory));
        clock.Advance(ClaudeCodeClient.StandbyLifetime - TimeSpan.FromSeconds(1));
        await client.ExpireAsync();
        Assert.Equal(0, mock.Disposed);
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.ExpireAsync();
        Assert.Equal(1, mock.Disposed); Assert.False(Directory.Exists(directory));
        client.Prestart(Settings, "sonnet", "low");
        mock.AllExited = true;
        await Run(client);
        mock.AllExited = false;
        Assert.Equal(2, mock.Used.Single());
    }

    [Fact]
    public async Task PrestartIgnoresDisabledUnknownOrDuplicateChoicesAndStopClearsThePool()
    {
        var mock = new MockClaudeCodeProcess(); await using var client = new ClaudeCodeClient(mock);
        client.Prestart(Settings with { Enabled = false }, "sonnet", null);
        client.Prestart(Settings, "gpt-5", null);
        client.Prestart(Settings, "haiku", "high");
        Assert.Empty(mock.Starts);
        client.Prestart(Settings, "sonnet", "low"); client.Prestart(Settings, "sonnet", "low");
        Assert.Single(mock.Starts);
        await client.StopAsync();
        Assert.Equal(1, mock.Disposed);
        await Run(client);
        Assert.Equal(1, mock.Used.Single());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LaunchPrestartsTheDefaultModelOnlyWhenEnabled(bool enabled)
    {
        var mock = new MockClaudeCodeProcess(); await using var client = new ClaudeCodeClient(mock);
        var settings = new FakeAiSettingsStore(); settings.Value = settings.Value with { ClaudeCode = Settings with { Enabled = enabled } };
        var launch = new ClaudeCodeLaunch(client, settings, NullLogger<ClaudeCodeLaunch>.Instance);
        await launch.StartAsync(_ct); await launch.ExecuteTask!;
        Assert.Equal(enabled ? 1 : 0, mock.Starts.Count);
        if (enabled) Assert.Equal("low", mock.Starts[0][mock.Starts[0].ToList().IndexOf("--effort") + 1]);
    }
}

public sealed class ClaudeCodeEnvironmentTests
{
    [Fact]
    public void ANestedSessionsOwnVariablesAreRemovedButProviderSettingsPass()
    {
        var environment = new Dictionary<string, string?>
        {
            ["CLAUDECODE"] = "1", ["CLAUDE_PID"] = "42", ["CLAUDE_AGENT_SDK_VERSION"] = "1.0", ["CLAUDE_CODE_SESSION_ID"] = "s",
            ["CLAUDE_CODE_MESSAGING_TOKEN"] = "t", ["CLAUDE_CODE_USE_BEDROCK"] = "1", ["CLAUDE_CODE_OAUTH_TOKEN"] = "o",
            ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:4100", ["ANTHROPIC_API_KEY"] = "k", ["PATH"] = "p"
        };
        ClaudeCodeProcessFactory.RemoveSessionEnvironment(environment);
        Assert.Equal(["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_USE_BEDROCK", "PATH"], environment.Keys.Order());
        var remote = new Dictionary<string, string?> { ["ANTHROPIC_BASE_URL"] = "https://gateway.example.test" };
        ClaudeCodeProcessFactory.RemoveSessionEnvironment(remote);
        Assert.True(remote.ContainsKey("ANTHROPIC_BASE_URL"));
    }
}
