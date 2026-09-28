using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed class CodexTests
{
    private static readonly CodexSettings Settings = new() { Enabled = true, ImageModel = "mock-codex" };
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task ReconnectNotificationsKeepTheAcceptedTurnUntilItsTerminalResult(bool image, bool fail)
    {
        var mock = new MockCodexTransport { Hold = true };
        await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null, image);
        var updates = new List<CodexUpdate>();
        async Task Run()
        {
            await foreach (var update in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), [], image), _ct))
            {
                updates.Add(update);
                if (update.TurnId is null || update.Complete) continue;
                var threadId = update.ThreadId!; var turnId = update.TurnId;
                mock.Emit("error", new { threadId, turnId = "another-turn", willRetry = false, error = new { message = "Unrelated error" } });
                mock.Emit("item/agentMessage/delta", new { threadId, turnId, delta = "Before " });
                for (var i = 1; i <= 2; i++)
                    mock.Emit("error", new { threadId, turnId, willRetry = true, error = new { message = $"Reconnecting... {i}/5" } });
                mock.Emit("item/agentMessage/delta", new { threadId, turnId, delta = "after" });
                if (image) mock.Emit("item/completed", new { threadId, turnId, item = new { id = "image", type = "imageGeneration", result = "AQID" } });
                mock.Emit("turn/completed", new { threadId, turn = new { id = turnId, status = fail ? "failed" : "completed", error = new { message = "Retries exhausted" } } });
            }
        }
        if (fail) Assert.Equal("Retries exhausted", (await Assert.ThrowsAsync<AiGenerationException>(Run)).Message);
        else { await Run(); Assert.True(updates[^1].Complete); }
        Assert.Equal("Before after", string.Concat(updates.Select(u => u.Text)));
        Assert.Equal(2, updates.Count(u => u.Progress?.Label.StartsWith("Reconnecting") == true));
        Assert.Equal(image ? 1 : 0, updates.Count(u => u.Image is not null));
        Assert.Equal(1, mock.Turns); Assert.Equal(1, mock.Threads); Assert.Equal(0, mock.Interrupts);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TerminalOrLegacyErrorNotificationsStillFailAndInterrupt(bool legacy)
    {
        var mock = new MockCodexTransport { Hold = true };
        await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        var error = await Assert.ThrowsAsync<AiGenerationException>(async () => {
            await foreach (var update in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), _ct))
                if (update.TurnId is not null)
                    mock.Emit("error", legacy
                        ? new { threadId = update.ThreadId, turnId = update.TurnId, error = new { message = "Request failed" } }
                        : (object)new { threadId = update.ThreadId, turnId = update.TurnId, willRetry = false, error = new { message = "Request failed" } });
        });
        Assert.Equal("Request failed", error.Message); Assert.Equal(1, mock.Turns); Assert.Equal(1, mock.Interrupts);
    }

    [Theory] [InlineData("item/reasoning/textDelta")] [InlineData("item/reasoning/summaryTextDelta")]
    public async Task ReasoningActivityKeepsTextAliveWithoutEnteringTheAnswer(string method)
    {
        var clock = new TimerClock(); var mock = new MockCodexTransport { Hold = true };
        await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        using var watcher = new TextInactivityWatchdog(10, _ct, clock);
        var activity = 0; var text = ""; string? thread = null;
        await foreach (var update in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), watcher.Token))
        {
            watcher.Observe(update.Progress); if (update.Activity) { watcher.Activity(); activity++; }
            text += update.Text;
            if (update.TurnId is not null && !update.Complete) thread = update.ThreadId;
            if (thread is not null && !update.Complete)
            {
                if (activity < 4) { clock.Advance(9); mock.Emit(method, new { threadId = thread, delta = "private reasoning" }); }
                else { mock.Emit("item/completed", new { threadId = thread, item = new { id = "answer", type = "agentMessage", phase = "final_answer", text = "Done" } }); mock.Emit("turn/completed", new { threadId = thread, turn = new { status = "completed" } }); thread = null; }
            }
        }
        Assert.Equal(4, activity); Assert.Equal("Done", text); Assert.False(watcher.Expired);
    }
    [Fact]
    public async Task ImageLifecycleExposesLivePhasesAndFinalTimingWithoutAnInventedEta()
    {
        var mock = new MockCodexTransport { Hold = true }; await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null, true);
        var updates = new List<CodexUpdate>();
        await foreach (var update in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), [], true), _ct))
        {
            updates.Add(update);
            if (update.TurnId is not null && !update.Complete)
            {
                var threadId = update.ThreadId!;
                mock.Emit("item/started", new { threadId, startedAtMs = 1005000, item = new { id = "image", type = "imageGeneration" } });
                mock.Emit("item/completed", new { threadId, completedAtMs = 1028000, item = new { id = "image", type = "imageGeneration", result = "AQID" } });
                mock.Emit("turn/completed", new { threadId, turn = new { status = "completed" } });
            }
        }
        Assert.Equal(new[] { GenerationPhase.Preparing, GenerationPhase.Generating, GenerationPhase.Finalizing }, updates.Where(u => u.Progress is not null).Select(u => u.Progress!.Phase));
        Assert.All(updates.Where(u => u.Progress is not null), u => { Assert.False(u.Progress!.IsDeterminate); Assert.Null(u.Progress.EstimatedRemaining); Assert.NotNull(u.Timing); });
        Assert.NotNull(updates[^1].Timing!.CompletedUtc);
        var call = Assert.Single(updates[^1].Timing!.ImageCalls);
        Assert.Equal(TimeSpan.FromSeconds(23), call.CompletedUtc - call.StartedUtc);
    }

    [Fact]
    public void NativeUsageLimitErrorIsRecognizedWithoutGuessingFromProse()
    {
        var error = JsonSerializer.SerializeToElement(new { error = new { codexErrorInfo = "usageLimitExceeded", message = "Limit" } });
        Assert.IsType<CodexAllowanceException>(CodexClient.Failure(error));
        Assert.IsNotType<CodexAllowanceException>(CodexClient.Failure(JsonSerializer.SerializeToElement(new { message = "A story about usage limits" })));
    }
    [Fact]
    public async Task ReportedAllowanceIsInformationOnlyAndNeverBlocksARequest()
    {
        // Codex reports an exhausted limit by failing the turn, which pauses the lane.
        var mock = new MockCodexTransport { Used = 100 }; await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct);
        Assert.True(check.Usage!.Exhausted);
        Assert.Equal("mock-codex", CodexClient.Capture(check, "mock-codex", null).Model);
        Assert.Equal("mock-codex", CodexClient.Capture(check with { Usage = null }, "mock-codex", null).Model);
        Assert.Equal("mock-codex", CodexClient.Capture(check with { Usage = new(DateTimeOffset.UtcNow, [], "Usage unavailable.") }, "mock-codex", null).Model);
    }
    [Fact]
    public async Task CommentaryStaysOutOfTheStructuredFinalAnswer()
    {
        var mock = new MockCodexTransport { Hold = true }; await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        var text = "";
        await foreach (var update in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), _ct))
        {
            if (update.TurnId is not null && !update.Complete)
            {
                var thread = update.ThreadId!;
                mock.Emit("item/started", new { threadId = thread, item = new { id = "comment", type = "agentMessage", phase = "commentary" } });
                mock.Emit("item/agentMessage/delta", new { threadId = thread, itemId = "comment", delta = "Let me think" });
                mock.Emit("item/completed", new { threadId = thread, item = new { id = "answer", type = "agentMessage", phase = "final_answer", text = "{\"ok\":true}" } });
                mock.Emit("turn/completed", new { threadId = thread, turn = new { status = "completed" } });
            }
            text += update.Text;
        }
        Assert.Equal("{\"ok\":true}", text);
    }
    [Fact]
    public async Task FailedHandshakeIsDisposedAndCanBeCheckedAgain()
    {
        var mock = new MockCodexTransport { FailInitialize = true }; await using var client = new CodexClient(mock, TimeProvider.System);
        Assert.False((await client.CheckAsync(Settings, _ct)).Success); Assert.Equal(1, mock.Stops);
        mock.FailInitialize = false;
        Assert.True((await client.CheckAsync(Settings, _ct)).Success); Assert.Equal(2, mock.Starts);
    }
    [Fact]
    public async Task UncertainCancellationStopsOwnedProcessBeforeReturning()
    {
        var mock = new MockCodexTransport { Hold = true, FailInterrupt = true }; await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var u in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), cancel.Token)) if (u.TurnId is not null) cancel.Cancel();
        });
        Assert.Equal(1, mock.Stops); Assert.False(mock.Alive);
    }
    [Theory]
    [InlineData("lumibelle/disconnected")][InlineData("account/updated")][InlineData("lumibelle/interactiveRequest")]
    public async Task UnexpectedProviderStateFailsWithoutSilentRetry(string notification)
    {
        var mock = new MockCodexTransport { Hold = true }; await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var u in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), _ct))
                if (u.TurnId is not null) mock.Emit(notification, new { });
        });
        Assert.Equal(1, mock.Turns);
    }
    [Fact]
    public async Task MissingImageCapabilityDoesNotEnableGeneration()
    {
        var mock = new MockCodexTransport { ImageCapability = false }; await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct);
        Assert.Throws<AiGenerationException>(() => CodexClient.Capture(check, "mock-codex", null, true));
        Assert.Equal("mock-codex", CodexClient.Capture(check, "mock-codex", null).Model);
        Assert.Equal(0, mock.Turns);
    }
    [Fact]
    public async Task DiscoveryDoesNotGenerateAndReusesOwnedProcess()
    {
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct); await client.CheckAsync(Settings, _ct);
        Assert.True(check.Success); Assert.Equal("0.153.4", check.Version); Assert.True(check.ImageGeneration);
        Assert.Equal("qa@example.test", check.AccountLabel); Assert.NotEqual(check.AccountLabel, check.AccountId);
        Assert.Equal(new[] { "low", "medium", "high" }, Assert.Single(check.Models).ReasoningEfforts);
        Assert.Equal(85, check.Usage!.Limits[0].Primary!.RemainingPercent); Assert.Null(check.Usage.Limits[0].Secondary);
        Assert.Equal(1, mock.Starts); Assert.Equal(0, mock.Threads); Assert.Equal(0, mock.Turns);
    }
    [Fact]
    public async Task IncompatibleVersionIsRejected()
    {
        var mock = new MockCodexTransport { Version = "0.152.0" }; await using var client = new CodexClient(mock, TimeProvider.System);
        Assert.False((await client.CheckAsync(Settings, _ct)).Success); Assert.Equal(0, mock.Threads);
    }
    [Theory]
    [InlineData("apiKey", true, "OpenAI API key")]
    [InlineData("amazonBedrock", true, "Amazon Bedrock")]
    [InlineData(null, false, "Configured model provider")]
    public async Task AnySignInCodexAcceptsIsUsedWithoutAPlanAllowance(string? auth, bool requiresOpenaiAuth, string label)
    {
        var mock = new MockCodexTransport { AuthType = auth, RequiresOpenaiAuth = requiresOpenaiAuth }; await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct);
        Assert.True(check.Success, check.Message); Assert.Equal(label, check.AccountLabel); Assert.False(check.ReportsAllowance); Assert.Null(check.Usage);
        Assert.Empty((await client.ReadUsageAsync(Settings, _ct)).Limits);
        Assert.DoesNotContain(mock.Calls, c => c.Method == "account/rateLimits/read");
        var capture = CodexClient.Capture(check, "mock-codex", null);
        await foreach (var _ in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), [new("user", [new(Text: "Hello")])]), _ct)) { }
        Assert.Equal(1, mock.Turns);
        var thread = mock.Calls.Single(c => c.Method == "thread/start").Params;
        Assert.False(thread.TryGetProperty("modelProvider", out _));
        Assert.False(thread.GetProperty("config").TryGetProperty("forced_login_method", out _));
    }
    [Fact]
    public async Task SignedOutCodexIsRejectedWhenOpenAiAuthIsRequired()
    {
        var mock = new MockCodexTransport { AuthType = null }; await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct);
        Assert.False(check.Success); Assert.Contains("codex login", check.Message);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task EveryRequestUsesEphemeralRestrictedThreadAndNativeOutput(bool images)
    {
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct); var capture = CodexClient.Capture(check, "mock-codex", "high", images);
        var directory = Path.Combine(Path.GetTempPath(), "Lumibelle.CodexTests", Guid.NewGuid().ToString("N"));
        var request = new CodexRequest(Settings, capture, directory, [new("user", [new(Text: "Keep ÖPPET and mouse_token")])], images);
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var updates = new List<CodexUpdate>(); await foreach (var u in client.GenerateAsync(request, _ct)) updates.Add(u);
                Assert.True(updates[^1].Complete); Assert.Contains(updates, u => u.Text == mock.Text);
                Assert.Equal(images ? 1 : 0, updates.Count(u => u.Image is not null));
            }
            Assert.Equal(2, mock.Threads); Assert.Equal(2, mock.Turns); Assert.Equal(1, mock.Starts);
            foreach (var start in mock.Calls.Where(c => c.Method == "thread/start").Select(c => c.Params))
            {
                Assert.True(start.GetProperty("ephemeral").GetBoolean()); Assert.False(start.GetProperty("allowProviderModelFallback").GetBoolean());
                Assert.Equal("never", start.GetProperty("approvalPolicy").GetString()); Assert.Equal("read-only", start.GetProperty("sandbox").GetString());
                var config = start.GetProperty("config"); Assert.Equal(images, config.GetProperty("features.image_generation").GetBoolean());
                Assert.Equal(images, config.GetProperty("features.code_mode").GetBoolean());
                Assert.Equal(images, config.GetProperty("features.code_mode_host").GetBoolean());
                foreach (var key in new[] { "features.shell_tool", "features.unified_exec", "features.apps", "features.plugins", "features.memories", "features.multi_agent" }) Assert.False(config.GetProperty(key).GetBoolean());
                Assert.Empty(config.GetProperty("mcp_servers").EnumerateObject());
            }
            Assert.All(mock.Inputs, t => { Assert.Equal("high", t.GetProperty("effort").GetString()); Assert.Equal("default", t.GetProperty("serviceTierForTurn").GetString()); });
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task AccountChangesAndUnknownModelsCannotSilentlyFallback()
    {
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System);
        var check = await client.CheckAsync(Settings, _ct); var capture = CodexClient.Capture(check, "mock-codex", null);
        Assert.Equal("medium", capture.Effort);
        Assert.Throws<AiGenerationException>(() => CodexClient.Capture(check, "missing", null));
        Assert.Throws<AiGenerationException>(() => CodexClient.Capture(check, "mock-codex", "ultra"));
        mock.Account = "changed@example.test";
        await Assert.ThrowsAsync<AiGenerationException>(async () => { await foreach (var _ in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), _ct)) { } });
        Assert.Equal(0, mock.Threads);
    }
    [Fact]
    public async Task CancellationWaitsForOwnedTurnInterruption()
    {
        var mock = new MockCodexTransport { Hold = true }; await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var u in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), []), cancel.Token)) if (u.TurnId is not null) cancel.Cancel();
        });
        Assert.Equal(1, mock.Interrupts); Assert.Equal(0, mock.Stops);
    }
    [Theory]
    [InlineData(true, false)][InlineData(false, true)]
    public async Task ImageClaimsAndMalformedImagePayloadsAreNotSuccessfulImages(bool missing, bool malformed)
    {
        var mock = new MockCodexTransport { OmitImage = missing, MalformedImage = malformed, Text = "I saved your image." }; await using var client = new CodexClient(mock, TimeProvider.System);
        var capture = CodexClient.Capture(await client.CheckAsync(Settings, _ct), "mock-codex", null, true);
        await Assert.ThrowsAsync<AiGenerationException>(async () => { await foreach (var _ in client.GenerateAsync(new(Settings, capture, Path.GetTempPath(), [], true), _ct)) { } });
    }
    [Fact]
    public async Task QuotaNotificationsUpdateNamedBucketsWithoutInventingMissingValues()
    {
        var mock = new MockCodexTransport(); await using var client = new CodexClient(mock, TimeProvider.System); await client.CheckAsync(Settings, _ct);
        mock.Emit("account/rateLimits/updated", new { rateLimits = new { limitId = "codex", limitName = "Codex", primary = new { usedPercent = 100 } } });
        Assert.True(client.Connection!.Usage!.Exhausted); Assert.Null(client.Connection.Usage.Limits[0].Primary!.ResetsAt);
        var empty = CodexClient.ParseUsage(JsonSerializer.SerializeToElement(new { rateLimits = (object?)null }), DateTimeOffset.UtcNow);
        Assert.Empty(empty.Limits);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void CodexEnhancementProfilesDoNotUseComfyGuidance(bool edit)
    {
        Assert.Equal(edit ? "codex-edit-v1" : "codex-create-v1", PromptProfiles.Id(ImageWorkflow.CodexImages, edit));
        Assert.DoesNotContain("Krea", PromptProfiles.Read(ImageWorkflow.CodexImages, edit));
    }
}
