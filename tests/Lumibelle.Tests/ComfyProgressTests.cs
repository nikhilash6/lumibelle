using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class ComfyProgressTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecutionErrorsShowProviderDetailsFromSocketAndHistory(bool socketFailure)
    {
        var id = Guid.NewGuid().ToString("D");
        var detail = new { prompt_id = id, node_id = "10", node_type = "SamplerCustomAdvanced", exception_type = "RuntimeError", exception_message = "aimdo memory compile error\n" };
        var socket = new ScriptedComfyWebSocket(socketFailure
            ? FakeFrame.Text(JsonSerializer.Serialize(new { type = "execution_error", data = detail })) : FakeFrame.Close());
        var history = JsonSerializer.Serialize(new Dictionary<string, object> { [id] = new {
            status = new { status_str = "error", completed = false, messages = new object[] { new object[] { "execution_error", detail } } }
        } });
        using var http = new HttpClient(new ScriptedHttpHandler((request, _) => Task.FromResult(JsonResponse(
            request.RequestUri!.AbsolutePath == "/prompt" ? JsonSerializer.Serialize(new { prompt_id = id }) : socketFailure ? "{}" : history))))
            { BaseAddress = new Uri("http://comfy/") };
        var failure = await Assert.ThrowsAsync<AiGenerationException>(async () => {
            await foreach (var update in new ComfyExecutionMonitor(new ScriptedComfyWebSocketFactory(socket), TimeProvider.System)
                .ExecuteAsync(http, clientId => new { client_id = clientId }, Options(), TestContext.Current.CancellationToken, TestContext.Current.CancellationToken)) { }
        });
        Assert.Equal("ComfyUI failed at SamplerCustomAdvanced (node 10): RuntimeError: aimdo memory compile error", failure.Message);
    }

    [Fact]
    public void FailureDetailsAreBoundedAndMissingDetailsUseTheNeutralFallback()
    {
        var history = JsonSerializer.SerializeToElement(new { status = new { messages = new object[] { new object[] {
            "execution_error", new { exception_message = new string('x', 5000), current_inputs = "Do not expose inputs", traceback = "Do not expose stack" }
        } } } });
        var message = ComfyExecutionMonitor.ExecutionFailureMessage(Options(), history);
        Assert.True(message.Length < 1100); Assert.EndsWith("…", message);
        Assert.DoesNotContain("Do not expose", message);
        Assert.Equal(Options().ExecutionErrorMessage, ComfyExecutionMonitor.ExecutionFailureMessage(Options(), JsonSerializer.SerializeToElement(new { })));
    }

    [Theory]
    [InlineData("http://localhost:8188/", "ws://localhost:8188/ws?clientId=client-1")]
    [InlineData("https://example.test/comfy", "wss://example.test/comfy/ws?clientId=client-1")]
    public void WebSocketUriTracksConfiguredSchemeAndBasePath(string address, string expected)
    {
        Assert.Equal(expected, ComfyExecutionMonitor.BuildWebSocketUri(new Uri(address), "client-1").AbsoluteUri);
    }

    [Fact]
    public void SocketParserFiltersJobsAndUnderstandsBothProgressFormats()
    {
        var id = Guid.NewGuid().ToString("D");
        Assert.Null(ComfyExecutionMonitor.ParseSocketMessage("{\"type\":\"progress\",\"data\":{\"prompt_id\":\"other\",\"node\":\"7\",\"value\":1,\"max\":8}}", id));

        var legacy = ComfyExecutionMonitor.ParseSocketMessage($"{{\"type\":\"progress\",\"data\":{{\"prompt_id\":\"{id}\",\"node\":\"7\",\"value\":3,\"max\":8}}}}", id)!;
        Assert.Equal("7", legacy.NodeId); Assert.Equal(3, legacy.Current); Assert.Equal(8, legacy.Maximum);

        var state = ComfyExecutionMonitor.ParseSocketMessage($"{{\"type\":\"progress_state\",\"data\":{{\"prompt_id\":\"{id}\",\"nodes\":{{\"2\":{{\"state\":\"running\",\"value\":1726,\"max\":2048}}}}}}}}", id)!;
        Assert.Equal("progress", state.Type); Assert.Equal("2", state.NodeId); Assert.Equal(1726, state.Current); Assert.Equal(2048, state.Maximum);

        Assert.Equal("execution_success", ComfyExecutionMonitor.ParseSocketMessage($"{{\"type\":\"execution_success\",\"data\":{{\"prompt_id\":\"{id}\"}}}}", id)!.Type);
        Assert.Equal("execution_error", ComfyExecutionMonitor.ParseSocketMessage($"{{\"type\":\"execution_error\",\"data\":{{\"prompt_id\":\"{id}\"}}}}", id)!.Type);
        Assert.Equal("execution_interrupted", ComfyExecutionMonitor.ParseSocketMessage($"{{\"type\":\"execution_interrupted\",\"data\":{{\"prompt_id\":\"{id}\"}}}}", id)!.Type);
    }

    [Fact]
    public void ProgressTrackerEstimatesOnlyAfterAnObservedRate()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
        var tracker = new GenerationProgressTracker(clock);
        var first = tracker.SetProgress(GenerationPhase.Generating, "Generating text", 100, 1000, "tokens", "2");
        Assert.Null(first.EstimatedRemaining);

        clock.Advance(TimeSpan.FromSeconds(2));
        var later = tracker.SetProgress(GenerationPhase.Generating, "Generating text", 200, 1000, "tokens", "2");
        Assert.Null(later.EstimatedRemaining); // A token budget is not a real output total.
        Assert.Equal(TimeSpan.FromSeconds(2), later.Elapsed);
        tracker.MarkEmitted();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(tracker.ShouldEmit(TimeSpan.FromMilliseconds(250)));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(tracker.ShouldEmit(TimeSpan.FromMilliseconds(250)));

        var final = tracker.SetStage(GenerationPhase.Finalizing, "Finalizing…", "3");
        Assert.False(final.IsDeterminate);
        Assert.Null(final.EstimatedRemaining);
    }

    [Fact]
    public async Task MonitorReadsFragmentedAndBinaryFramesAndUsesTheSameClientId()
    {
        var id = Guid.NewGuid().ToString("D");
        var socket = new ScriptedComfyWebSocket([
            FakeFrame.Binary([1, 2, 3]),
            .. FakeFrame.Text($"{{\"type\":\"execution_start\",\"data\":{{\"prompt_id\":\"{id}\"}}}}", splitAt: 18),
            FakeFrame.Text($"{{\"type\":\"executing\",\"data\":{{\"prompt_id\":\"{id}\",\"node\":\"7\"}}}}"),
            FakeFrame.Text($"{{\"type\":\"progress\",\"data\":{{\"prompt_id\":\"{id}\",\"node\":\"7\",\"value\":3,\"max\":8}}}}")]);
        var factory = new ScriptedComfyWebSocketFactory(socket);
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/prompt", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var submittedClient = body.RootElement.GetProperty("client_id").GetString();
                Assert.Equal(factory.ClientId, submittedClient);
                return JsonResponse($"{{\"prompt_id\":\"{id}\"}}");
            }
            await Task.Delay(200, ct);
            return JsonResponse($"{{\"{id}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{}}}}}}");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://comfy/base/") };
        var monitor = new ComfyExecutionMonitor(factory, TimeProvider.System);
        var updates = new List<ComfyExecutionUpdate>();
        await foreach (var update in monitor.ExecuteAsync(http, clientId => new { client_id = clientId, prompt = new { } },
            Options(), TestContext.Current.CancellationToken, TestContext.Current.CancellationToken)) updates.Add(update);

        Assert.Equal("ws://comfy/base/ws", factory.ConnectedUri!.GetLeftPart(UriPartial.Path));
        var progress = Assert.Single(updates, update => update.Progress.IsDeterminate);
        Assert.Equal("Generating image", progress.Progress.Label);
        Assert.Equal(3, progress.Progress.Current);
        Assert.Equal(8, progress.Progress.Maximum);
        Assert.Equal("steps", progress.Progress.Unit);
        Assert.Contains(updates, update => update.Progress.Label == "Starting ComfyUI workflow…");
        Assert.Contains(updates, update => update.Progress.Label == "Preparing Krea 2…");
        Assert.True(updates[^1].Complete);
    }

    [Fact]
    public async Task SocketLossFallsBackToHistoryWithoutLosingCompletion()
    {
        var id = Guid.NewGuid().ToString("D");
        var socket = new ScriptedComfyWebSocket(
            FakeFrame.Text($"{{\"type\":\"execution_start\",\"data\":{{\"prompt_id\":\"{id}\"}}}}"),
            FakeFrame.Close());
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/prompt") return JsonResponse($"{{\"prompt_id\":\"{id}\"}}");
            await Task.Delay(200, ct);
            return JsonResponse($"{{\"{id}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{}}}}}}");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://comfy/") };
        var updates = new List<ComfyExecutionUpdate>();
        await foreach (var update in new ComfyExecutionMonitor(new ScriptedComfyWebSocketFactory(socket), TimeProvider.System)
            .ExecuteAsync(http, clientId => new { client_id = clientId, prompt = new { } }, Options(),
                TestContext.Current.CancellationToken, TestContext.Current.CancellationToken)) updates.Add(update);

        Assert.Contains(updates, update => !update.Progress.LiveUpdatesAvailable);
        Assert.True(updates[^1].Complete);
    }

    private static ComfyExecutionOptions Options() => new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["7"] = new(GenerationPhase.Preparing, "Preparing Krea 2…", "Generating image", "steps")
        }, "rejected", "execution failed", "submit timeout", "timeout", "unreadable", "connection failed");

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now += value;
}

internal sealed class ScriptedComfyWebSocketFactory(ScriptedComfyWebSocket socket) : IComfyWebSocketFactory
{
    public Uri? ConnectedUri { get; private set; }
    public string? ClientId => ConnectedUri?.Query["?clientId=".Length..];
    public IComfyWebSocket Create()
    {
        socket.OnConnect = uri => ConnectedUri = uri;
        return socket;
    }
}

internal sealed class ScriptedComfyWebSocket(params FakeFrame[] frames) : IComfyWebSocket
{
    private readonly Queue<FakeFrame> _frames = new(frames);
    public Action<Uri>? OnConnect { get; set; }
    public TaskCompletionSource AllFramesRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WebSocketState State { get; private set; } = WebSocketState.None;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        OnConnect?.Invoke(uri);
        State = WebSocketState.Open;
        return Task.CompletedTask;
    }

    public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        if (_frames.Count == 0)
        {
            AllFramesRead.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        var frame = _frames.Dequeue();
        frame.Bytes.CopyTo(buffer.AsSpan());
        if (_frames.Count == 0) AllFramesRead.TrySetResult();
        if (frame.Type == WebSocketMessageType.Close) State = WebSocketState.CloseReceived;
        return new WebSocketReceiveResult(frame.Bytes.Length, frame.Type, frame.EndOfMessage);
    }

    public ValueTask DisposeAsync()
    {
        State = WebSocketState.Closed;
        return ValueTask.CompletedTask;
    }
}

internal sealed record FakeFrame(byte[] Bytes, WebSocketMessageType Type, bool EndOfMessage)
{
    public static FakeFrame Binary(byte[] bytes) => new(bytes, WebSocketMessageType.Binary, true);
    public static FakeFrame Close() => new([], WebSocketMessageType.Close, true);
    public static FakeFrame Text(string value) => new(Encoding.UTF8.GetBytes(value), WebSocketMessageType.Text, true);
    public static FakeFrame[] Text(string value, int splitAt)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [new(bytes[..splitAt], WebSocketMessageType.Text, false), new(bytes[splitAt..], WebSocketMessageType.Text, true)];
    }
}
