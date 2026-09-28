using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class ClaudeCodeTests
{
    private static readonly ClaudeCodeSettings Settings = new() { Enabled = true, TextModel = "sonnet" };
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static ClaudeCodeRequest Request(IReadOnlyList<AiTextMessage>? messages = null, string model = "sonnet", string? effort = null) =>
        new(Settings, model, effort, Path.Combine(Path.GetTempPath(), "Lumibelle.ClaudeCodeTests", Guid.NewGuid().ToString("N")),
            messages ?? [new("user", [new(Text: "Write a line.")])]);
    private async Task<List<ClaudeCodeUpdate>> Collect(ClaudeCodeClient client, ClaudeCodeRequest? request = null)
    {
        var updates = new List<ClaudeCodeUpdate>();
        await foreach (var update in client.GenerateAsync(request ?? Request(), _ct)) updates.Add(update);
        return updates;
    }

    [Fact]
    public async Task CheckReportsVersionAndSignedInAccountWithoutGenerating()
    {
        var mock = new MockClaudeCodeProcess();
        var check = await new ClaudeCodeClient(mock).CheckAsync(Settings, _ct);
        Assert.True(check.Success, check.Message);
        Assert.Equal("2.1.281", check.Version); Assert.Equal("qa@example.test", check.AccountLabel); Assert.Equal("claude.ai", check.AuthMethod);
        Assert.Equal("firstParty", check.ApiProvider);
        Assert.Equal(["fable", "opus", "sonnet", "haiku"], check.Models.Select(m => m.Id));
        Assert.All(mock.Starts, arguments => Assert.DoesNotContain("-p", arguments));
    }

    [Theory]
    [InlineData(false, "2.1.281", "not signed in")]
    [InlineData(true, "2.1.200", "Update Claude Code")]
    public async Task CheckFailsWhenSignedOutOrOutdated(bool loggedIn, string version, string message)
    {
        var check = await new ClaudeCodeClient(new MockClaudeCodeProcess { LoggedIn = loggedIn, Version = version }).CheckAsync(Settings, _ct);
        Assert.False(check.Success); Assert.Contains(message, check.Message); Assert.Empty(check.Models);
    }

    [Fact]
    public async Task CloudProviderSetupsConnectWithoutAnAnthropicSignIn()
    {
        var check = await new ClaudeCodeClient(new MockClaudeCodeProcess { LoggedIn = false, ApiProvider = "bedrock" }).CheckAsync(Settings, _ct);
        Assert.True(check.Success, check.Message);
        Assert.Equal("bedrock", check.ApiProvider); Assert.Null(check.AuthMethod); Assert.Null(check.AccountLabel);
    }

    [Fact]
    public async Task CheckExplainsDisabledAndMissingInstallations()
    {
        Assert.Contains("Enable Claude Code", (await new ClaudeCodeClient(new MockClaudeCodeProcess()).CheckAsync(Settings with { Enabled = false }, _ct)).Message);
        Assert.Contains("Could not start", (await new ClaudeCodeClient(new MockClaudeCodeProcess { StartFails = true }).CheckAsync(Settings, _ct)).Message);
    }

    [Fact]
    public async Task GenerationRunsAnIsolatedPrintSessionWithTheSelectedModelAndEffort()
    {
        var mock = new MockClaudeCodeProcess();
        await Collect(new ClaudeCodeClient(mock), Request(model: "opus", effort: "high"));
        var arguments = mock.Starts[Assert.Single(mock.Used)];
        foreach (var flag in new[] { "-p", "--no-session-persistence", "--safe-mode", "--strict-mcp-config", "--disable-slash-commands" }) Assert.Contains(flag, arguments);
        Assert.Equal("", arguments[arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("none", arguments[arguments.ToList().IndexOf("--permission-prompts") + 1]);
        Assert.Equal("opus", arguments[arguments.ToList().IndexOf("--model") + 1]);
        Assert.Equal("high", arguments[arguments.ToList().IndexOf("--effort") + 1]);
        Assert.Equal(1, mock.Disposed);
        Assert.DoesNotContain("--effort", ClaudeCodeClient.Arguments("haiku", null));
    }

    [Fact]
    public async Task TextStreamsInOrderAndCompletesWithTheResolvedModel()
    {
        var mock = new MockClaudeCodeProcess { Script = _ => [MockClaudeCodeProcess.Thinking(), MockClaudeCodeProcess.Delta("One "), MockClaudeCodeProcess.Delta("two"), MockClaudeCodeProcess.Result("One two three")] };
        var updates = await Collect(new ClaudeCodeClient(mock));
        Assert.Equal("One two three", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal("claude-sonnet-5", updates.First(u => u.Model is not null).Model);
        Assert.Single(updates, u => u.Activity);
        Assert.True(updates[^1].Complete); Assert.False(updates[^1].Truncated);
    }

    [Fact]
    public async Task MessagesAreSentAsOneUserTurnWithRolesAndImages()
    {
        var mock = new MockClaudeCodeProcess();
        await Collect(new ClaudeCodeClient(mock), Request([
            new("system", [new(Text: "Follow the guide.")]),
            new("user", [new(Text: "Describe this:"), new(Image: [1, 2, 3], MediaType: "image/png"), new(Text: "Briefly.")])]));
        using var json = JsonDocument.Parse(Assert.Single(mock.Inputs));
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("user", json.RootElement.GetProperty("type").GetString());
        var content = message.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(["text", "image", "text"], content.Select(c => c.GetProperty("type").GetString()));
        Assert.Contains("[system]\nFollow the guide.", content[0].GetProperty("text").GetString());
        Assert.EndsWith("[user]\nDescribe this:\n", content[0].GetProperty("text").GetString());
        Assert.Equal("AQID", content[1].GetProperty("source").GetProperty("data").GetString());
        Assert.StartsWith("Briefly.", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsOrIntegrationsInTheSessionStopBeforeAnyText()
    {
        var mock = new MockClaudeCodeProcess { Tools = ["Bash"] };
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(mock)));
        Assert.Contains("tools or integrations", error.Message);
        mock = new MockClaudeCodeProcess { McpServers = [new { name = "remote", status = "connected" }] };
        await Assert.ThrowsAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(mock)));
    }

    [Fact]
    public async Task SignInAndUsageLimitFailuresAreExplained()
    {
        var signedOut = new MockClaudeCodeProcess { Script = _ => [new { type = "assistant", error = "authentication_failed", message = new { content = Array.Empty<object>() } }, MockClaudeCodeProcess.Result("Not logged in · Please run /login", true)] };
        Assert.Contains("not signed in", (await Assert.ThrowsAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(signedOut)))).Message);
        var limited = new MockClaudeCodeProcess { Script = _ => [MockClaudeCodeProcess.Result("You've hit your limit · resets 3pm", true)] };
        var limit = await Assert.ThrowsAsync<ClaudeCodeLimitException>(() => Collect(new ClaudeCodeClient(limited)));
        Assert.Contains("resets 3pm", limit.Message);
        var failed = new MockClaudeCodeProcess { Script = _ => [MockClaudeCodeProcess.Result("Overloaded", true)] };
        Assert.IsNotType<ClaudeCodeLimitException>(await Assert.ThrowsAnyAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(failed))));
    }

    [Fact]
    public async Task InconsistentOrMissingResultsFailWithoutInventingText()
    {
        var differs = new MockClaudeCodeProcess { Script = _ => [MockClaudeCodeProcess.Delta("Alpha"), MockClaudeCodeProcess.Result("Beta")] };
        Assert.Contains("differs", (await Assert.ThrowsAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(differs)))).Message);
        var exited = new MockClaudeCodeProcess { Script = _ => [MockClaudeCodeProcess.Delta("Partial")], ExitCode = 3, Diagnostic = "error: unknown option" };
        var error = await Assert.ThrowsAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(exited)));
        Assert.Contains("exit code 3", error.Message); Assert.Contains("unknown option", error.Message);
        var garbage = new MockClaudeCodeProcess { Script = _ => ["not json"] };
        await Assert.ThrowsAsync<AiGenerationException>(() => Collect(new ClaudeCodeClient(garbage)));
    }

    [Fact]
    public async Task UnavailableModelsAndEffortsAreRejectedBeforeStarting()
    {
        var mock = new MockClaudeCodeProcess(); var client = new ClaudeCodeClient(mock);
        await Assert.ThrowsAsync<AiGenerationException>(() => Collect(client, Request(model: "gpt-5")));
        await Assert.ThrowsAsync<AiGenerationException>(() => Collect(client, Request(model: "haiku", effort: "high")));
        await Assert.ThrowsAsync<AiGenerationException>(() => Collect(client, Request(effort: "ultra")));
        Assert.Empty(mock.Starts);
    }

    [Fact]
    public async Task CancellationDisposesTheRunningProcess()
    {
        var mock = new MockClaudeCodeProcess { Hold = new() };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var client = new ClaudeCodeClient(mock);
        var run = Task.Run(async () => { await foreach (var update in client.GenerateAsync(Request(), cancel.Token)) if (update.Model is not null) cancel.Cancel(); }, _ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, mock.Disposed);
    }

    [Theory]
    [InlineData(true, "low", "low")]
    [InlineData(true, null, null)]
    [InlineData(false, null, "max")]
    public async Task ChatClientUsesExplicitEffortOrTheSettingsDefault(bool explicitEffort, string? selected, string? expected)
    {
        var mock = new MockClaudeCodeProcess { Script = _ => [MockClaudeCodeProcess.Delta("Done"), MockClaudeCodeProcess.Result("Done", stopReason: "max_tokens")] };
        using var chat = new ClaudeCodeChatClient(new ClaudeCodeClient(mock), Settings with { TextEffort = "max" }, "sonnet");
        var options = new ChatOptions();
        if (explicitEffort) options.AdditionalProperties = new() { [TextGenerationOptions.ReasoningEffortKey] = selected };
        var response = await chat.GetResponseAsync([new(ChatRole.User, "Hi")], options, _ct);
        Assert.Equal("Done", response.Text); Assert.Equal(ChatFinishReason.Length, response.FinishReason); Assert.Equal("claude-sonnet-5", response.ModelId);
        var arguments = mock.Starts[Assert.Single(mock.Used)].ToList();
        Assert.Equal(expected, arguments.IndexOf("--effort") is var i and >= 0 ? arguments[i + 1] : null);
        Assert.False(Directory.Exists(mock.Directories[0]));
    }

    [Fact]
    public void PoliciesTreatClaudeCodeAsANamedEffortTextProvider()
    {
        var settings = new AiSettings { ClaudeCode = Settings with { TextEffort = "medium" } };
        var model = new TextModelReference(AiBackend.ClaudeCode, "sonnet", "Sonnet");
        Assert.Equal("medium", TextModelPolicy.WithDefaultEffort(model, settings).ReasoningEffort);
        Assert.Equal("Claude Code · Sonnet", TextModelPolicy.PickerLabel(model, settings, [model], _ => null));
        Assert.Equal("Enable Claude Code in Connections.", TextModelPolicy.Issue(model, settings with { ClaudeCode = new() }, null));
        var check = new AiConnectionCheck(true, "Connected.", ClaudeCodeClient.Models);
        Assert.Null(TextModelPolicy.Issue(model with { ReasoningEffort = "xhigh" }, settings, check));
        Assert.NotNull(TextModelPolicy.Issue(model with { Model = "haiku", ReasoningEffort = "low" }, settings, check));
        Assert.True(TextVisionPolicy.SupportsBackend(AiBackend.ClaudeCode));
        var options = TextGenerationOptions.Create(AiBackend.ClaudeCode, settings, selection: model with { ProfileId = Guid.NewGuid(), Name = "Quick" });
        Assert.Null(options.AdditionalProperties![TextGenerationOptions.ReasoningEffortKey]);
        Assert.Null(options.Temperature); Assert.Null(options.MaxOutputTokens);
    }

    [Fact]
    public void SettingsAndProfilesRejectUnsupportedClaudeCodeValues()
    {
        FileAiSettingsStore.Validate(new AiSettings());
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(new AiSettings { ClaudeCode = Settings with { Concurrency = 5 } }));
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(new AiSettings { ClaudeCode = Settings with { TextEffort = "turbo" } }));
        var profile = new TextModelReference(AiBackend.ClaudeCode, "sonnet", "Careful", ReasoningEffort: "high") { ProfileId = Guid.NewGuid() };
        TextModelProfiles.ValidateReference(profile);
        Assert.Equal("reasoning high", TextModelProfiles.Describe(profile));
        Assert.Equal("model-default reasoning", TextModelProfiles.Describe(profile with { ReasoningEffort = null }));
        Assert.Throws<WorkspaceStoreException>(() => TextModelProfiles.ValidateReference(profile with { Temperature = 0.4f }));
        Assert.Throws<WorkspaceStoreException>(() => TextModelProfiles.ValidateReference(profile with { ReasoningEffort = "none" }));
    }

    [Fact]
    public async Task RegistryCreatesTheChatClientAndReportsTheConnection()
    {
        var registry = new AiProviderRegistry(new TestHttpFactory(new ScriptedHttpHandler((_, _) => throw new InvalidOperationException())),
            new FakeAiSettingsStore(), TestComfy.Monitor(), claude: new ClaudeCodeClient(new MockClaudeCodeProcess()));
        using var created = await registry.CreateAsync(AiBackend.ClaudeCode, "sonnet", new AiSettings { ClaudeCode = Settings }, _ct);
        Assert.IsType<ClaudeCodeChatClient>(created);
        var check = await registry.CheckAsync(AiBackend.ClaudeCode, new AiSettings { ClaudeCode = Settings }, cancellationToken: _ct);
        Assert.True(check.Success); Assert.Equal("2.1.281", check.BackendVersion); Assert.Equal(4, check.Models.Count);
    }
}
