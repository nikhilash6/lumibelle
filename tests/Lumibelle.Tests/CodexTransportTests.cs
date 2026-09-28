using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class CodexTransportTests
{
    [Fact]
    public void StartupAllowsPerThreadImageToolsWithoutEnablingUnrelatedTools()
    {
        var start = CodexTransportFactory.StartInfo("codex.exe");
        var arguments = start.ArgumentList.ToArray();
        foreach (var feature in CodexClient.ImageToolFeatures)
            Assert.DoesNotContain(arguments, arg => arg.StartsWith(feature + "=", StringComparison.Ordinal));
        foreach (var feature in new[] { "shell_tool", "unified_exec", "plugins", "apps", "browser_use", "computer_use", "memories", "multi_agent" })
            Assert.Contains("features." + feature + "=false", arguments);
        Assert.Contains("mcp_servers={}", arguments);
        Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow);
        // Sign-in and provider come from the user's own Codex setup, not from overrides.
        Assert.DoesNotContain(arguments, arg => arg.StartsWith("forced_login_method=", StringComparison.Ordinal) || arg.StartsWith("model_provider=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FramingCorrelatesInterleavedRepliesAndRejectsInteractiveRequests()
    {
        var inbound = new Pipe(); var outbound = new Pipe();
        using var reader = new StreamReader(outbound.Reader.AsStream());
        using var writer = new StreamWriter(inbound.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        await using var transport = new CodexTransport(new(inbound.Reader.AsStream()), new(outbound.Writer.AsStream(), new UTF8Encoding(false)),
            async () => await inbound.Writer.CompleteAsync());
        var ct = TestContext.Current.CancellationToken;
        var first = transport.CallAsync("first", new { text = "ÖPPET\nsecond line" }, ct);
        var second = transport.CallAsync("second", new { }, ct);
        using var request1 = JsonDocument.Parse((await reader.ReadLineAsync(ct))!);
        using var request2 = JsonDocument.Parse((await reader.ReadLineAsync(ct))!);
        Assert.Equal("ÖPPET\nsecond line", request1.RootElement.GetProperty("params").GetProperty("text").GetString());
        var id1 = request1.RootElement.GetProperty("id").GetInt64(); var id2 = request2.RootElement.GetProperty("id").GetInt64();
        var notification = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Notification += n => { if (n.GetProperty("method").GetString() == "lumibelle/interactiveRequest") notification.TrySetResult(n); };
        await writer.WriteAsync("{\"id\":"); await writer.FlushAsync(ct);
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        await writer.WriteLineAsync($"{id2},\"result\":{{\"value\":2}}}}");
        Assert.Equal(2, (await second).GetProperty("value").GetInt32());
        await writer.WriteLineAsync("{\"id\":\"approval-1\",\"method\":\"item/commandExecution/requestApproval\",\"params\":{\"threadId\":\"thread\"}}");
        using var rejection = JsonDocument.Parse((await reader.ReadLineAsync(ct))!);
        Assert.Equal("approval-1", rejection.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32601, rejection.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        await notification.Task.WaitAsync(ct);
        await writer.WriteLineAsync($"{{\"id\":{id1},\"error\":{{\"message\":\"Mock failure\"}}}}");
        Assert.Contains("Mock failure", (await Assert.ThrowsAsync<AiGenerationException>(() => first)).Message);
        var pending = transport.CallAsync("pending", new { }, ct);
        await reader.ReadLineAsync(ct); await inbound.Writer.CompleteAsync();
        await Assert.ThrowsAsync<AiGenerationException>(() => pending);
        Assert.False(transport.Alive);
        await Assert.ThrowsAsync<AiGenerationException>(() => transport.CallAsync("late", new { }, ct));
    }
}
