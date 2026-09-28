using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed partial class AiTests
{
    [Theory]
    [InlineData("high", false)]
    [InlineData("none", false)]
    [InlineData(null, true)]
    public async Task ProfileOverridesReachOpenRouterThroughTheRealSdkAdapter(string? effort, bool budget)
    {
        var stream = "data: {\"id\":\"chat-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test/model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") }));
        var settings = new FakeAiSettingsStore();
        var profile = new TextModelReference(AiBackend.OpenRouter, "test/model", "Profile", ReasoningEffort: effort)
        { ProfileId = Guid.NewGuid(), Temperature = 0, MaxOutputTokens = 4096, ReasoningMaxTokens = budget ? 1024 : null };
        var assistant = new ScriptAssistant(new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor()), settings);
        var request = new ScriptAssistantRequest(new() { Backend = profile.Backend, Model = profile.Model, Operation = WritingOperation.Discuss },
            new() { ProjectId = Guid.NewGuid() }, [], profile);
        var text = new StringBuilder();
        await foreach (var update in assistant.GenerateAsync(request, TestContext.Current.CancellationToken)) text.Append(update.Text);
        Assert.Equal("Hello", text.ToString());
        using var json = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var body = json.RootElement;
        Assert.Equal("test/model", body.GetProperty("model").GetString());
        Assert.Equal(0, body.GetProperty("temperature").GetSingle());
        Assert.True(body.TryGetProperty("max_completion_tokens", out var output) || body.TryGetProperty("max_tokens", out output));
        Assert.Equal(4096, output.GetInt32());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        var reasoning = body.GetProperty("reasoning");
        if (budget)
        {
            Assert.Equal(1024, reasoning.GetProperty("max_tokens").GetInt32());
            Assert.False(reasoning.TryGetProperty("effort", out _));
            Assert.False(reasoning.TryGetProperty("enabled", out _));
        }
        else if (effort == "none")
        {
            Assert.False(reasoning.GetProperty("enabled").GetBoolean());
            Assert.False(reasoning.TryGetProperty("effort", out _));
            Assert.False(reasoning.TryGetProperty("max_tokens", out _));
            Assert.Single(reasoning.EnumerateObject());
        }
        else
        {
            Assert.Equal(effort, reasoning.GetProperty("effort").GetString());
            Assert.False(reasoning.TryGetProperty("max_tokens", out _));
            Assert.False(reasoning.TryGetProperty("enabled", out _));
        }
        Assert.False(reasoning.TryGetProperty("exclude", out _)); // Hiding reasoning is not disabling it.
        Assert.False(body.TryGetProperty("profileId", out _));
    }

    [Fact]
    public async Task ProfileWithNoOverridesDoesNotSendSamplingOrReasoningFields()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(JsonResponse("""
            {"id":"chat-1","object":"chat.completion","created":1,"model":"test/model",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}]}
            """)));
        var settings = new FakeAiSettingsStore();
        var registry = new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor());
        using var client = await registry.CreateAsync(AiBackend.OpenRouter, "test/model", settings.Value, TestContext.Current.CancellationToken);
        var profile = new TextModelReference(AiBackend.OpenRouter, "test/model", "Defaults") { ProfileId = Guid.NewGuid() };
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")],
            TextGenerationOptions.Create(profile.Backend, settings.Value, selection: profile), TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        foreach (var name in new[] { "temperature", "max_tokens", "max_completion_tokens", "reasoning", "reasoning_effort" })
            Assert.False(body.RootElement.TryGetProperty(name, out _), name);
    }
}
