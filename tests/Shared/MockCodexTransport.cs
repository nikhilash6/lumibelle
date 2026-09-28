using System.Text.Json;
using lumibelle.Services.AI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Testing;

// Protocol mock: no CLI process, credentials, network or subscription usage.
public sealed class MockCodexTransport : ICodexTransport, ICodexTransportFactory
{
    public event Action<JsonElement>? Notification;
    public bool Alive { get; private set; } = true;
    public string Version = "0.153.4", Account = "qa@example.test";
    // null reports no account: a configured provider, or signed out when OpenAI auth is required.
    public string? AuthType = "chatgpt";
    public bool RequiresOpenaiAuth = true;
    public double Used = 15;
    public bool ImageCapability = true, ImageInput = true, Hold, OmitImage, MalformedImage, FailTurn, FailInitialize, FailInterrupt;
    // Fails turns the way Codex reports an exhausted allowance.
    public bool QuotaTurn;
    public int Starts, Threads, Turns, Interrupts, Stops;
    public int Delay = 10;
    public string Text = "{\"kind\":\"Prompt\",\"text\":\"A carefully framed illustration, preserving the author's requested details.\"}";
    public string[]? TextChunks;
    public int NativeImageCount = 1;
    public int ImageWidth = 80, ImageHeight = 40;
    public List<(string Method, JsonElement Params)> Calls { get; } = [];
    public List<JsonElement> Inputs { get; } = [];
    private readonly Dictionary<string, bool> _images = [];
    public Task<ICodexTransport> StartAsync(string executable, CancellationToken ct) { Starts++; Alive = true; return Task.FromResult<ICodexTransport>(this); }
    public Task NotifyAsync(string method, object? parameters, CancellationToken ct) { Record(method, parameters); return Task.CompletedTask; }
    public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var p = Record(method, parameters);
        if (method == "initialize" && FailInitialize) throw new AiGenerationException("Initialization failed");
        if (method == "turn/interrupt" && FailInterrupt) throw new AiGenerationException("Interruption uncertain");
        object result = method switch
        {
            "initialize" => new { userAgent = "codex_cli_rs/" + Version },
            "account/read" => new { account = AuthType switch { null => null, "chatgpt" => (object)new { type = AuthType, email = Account, planType = "plus" }, _ => new { type = AuthType } }, requiresOpenaiAuth = RequiresOpenaiAuth },
            "account/rateLimits/read" => new { rateLimitsByLimitId = new { codex = new { limitName = "Codex", primary = new { usedPercent = Used, windowDurationMins = 300, resetsAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() }, secondary = (object?)null } } },
            "modelProvider/capabilities/read" => new { imageGeneration = ImageCapability },
            "model/list" => new { data = new[] { new { id = "mock-codex", model = "mock-codex", displayName = "Codex QA", inputModalities = ImageInput ? new[] { "text", "image" } : ["text"], defaultReasoningEffort = "medium", supportedReasoningEfforts = new[] { new { reasoningEffort = "low" }, new { reasoningEffort = "medium" }, new { reasoningEffort = "high" } } } }, nextCursor = (string?)null },
            "config/read" => new { config = new { mcp_servers = new { unrelated = new { enabled = true } } } },
            "thread/start" => StartThread(p),
            "turn/start" => StartTurn(p),
            "turn/interrupt" => Interrupt(p),
            "thread/unsubscribe" => new { },
            _ => throw new InvalidOperationException("Unexpected protocol method: " + method)
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(result));
    }
    private JsonElement Record(string method, object? p) { var json = JsonSerializer.SerializeToElement(p); lock (Calls) Calls.Add((method, json)); return json; }
    private object StartThread(JsonElement p)
    {
        var id = "thread-" + ++Threads; _images[id] = p.GetProperty("config").GetProperty("features.image_generation").GetBoolean();
        return new { thread = new { id, ephemeral = true, path = (string?)null } };
    }
    private object StartTurn(JsonElement p)
    {
        Turns++; var thread = p.GetProperty("threadId").GetString()!; var turn = "turn-" + Turns; Inputs.Add(p);
        _ = Task.Run(async () =>
        {
            await Task.Delay(Delay);
            if (Hold || !Alive) return;
            if (QuotaTurn)
            {
                Emit("turn/completed", new { threadId = thread, turn = new { id = turn, status = "failed", error = new { message = "You've hit your usage limit.", codexErrorInfo = "usageLimitExceeded" } } });
                return;
            }
            foreach (var text in TextChunks ?? [Text])
                Emit("item/agentMessage/delta", new { threadId = thread, turnId = turn, delta = text });
            if (_images[thread] && !OmitImage)
            {
                for (var i = 0; i < NativeImageCount; i++)
                {
                    Emit("item/started", new { threadId = thread, turnId = turn, item = new { type = "imageGeneration", id = "image-" + i } });
                    using var png = new MemoryStream(); using var image = new Image<Rgb24>(ImageWidth + i, ImageHeight, new Rgb24(200, 40, 80)); image.SaveAsPng(png);
                    Emit("item/completed", new { threadId = thread, turnId = turn, item = new { type = "imageGeneration", id = "image-" + i, status = "completed", result = MalformedImage ? "invalid base64" : Convert.ToBase64String(png.ToArray()), revisedPrompt = "Returned revised prompt" } });
                }
            }
            Emit("turn/completed", new { threadId = thread, turn = new { id = turn, status = FailTurn ? "failed" : "completed", error = FailTurn ? new { message = "Mock provider failure" } : null } });
        });
        return new { turn = new { id = turn, status = "inProgress" } };
    }
    private object Interrupt(JsonElement p) { Interrupts++; Emit("turn/completed", new { threadId = p.GetProperty("threadId").GetString(), turn = new { status = "interrupted" } }); return new { }; }
    public void Emit(string method, object p) => Notification?.Invoke(JsonSerializer.SerializeToElement(new { method, @params = p }));
    public ValueTask DisposeAsync() { Stops++; Alive = false; Emit("lumibelle/disconnected", new { }); return ValueTask.CompletedTask; }
}
