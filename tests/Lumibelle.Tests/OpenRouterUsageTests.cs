using System.Net;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public class OpenRouterUsageTests
{
    [Theory]
    [InlineData("0", "0")] [InlineData("0.00000001", "0.00000001")] [InlineData("\"0.0002\"", "0.0002")]
    [InlineData("null", null)] [InlineData("-1", null)] [InlineData("\"oops\"", null)] [InlineData("1e-50", null)]
    public void CostsUseDecimalAndNeverTurnUnknownOrTinyAmountsIntoFree(string input, string? expected)
    {
        using var json = JsonDocument.Parse(input);
        Assert.Equal(expected, OpenRouterUsageParser.Money(json.RootElement)?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    [Fact]
    public async Task SdkAdapterRetainsUsageAfterFinishAndDoesNotDoubleCountCumulativeReports()
    {
        const string stream = """
data: {"id":"generation-test","object":"chat.completion.chunk","created":1,"model":"actual/model","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":"stop"}]}

data: {"id":"generation-test","object":"chat.completion.chunk","created":1,"model":"actual/model","provider":"Provider","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":7,"completion_tokens_details":{"reasoning_tokens":3},"prompt_tokens_details":{"cached_tokens":2},"cost":0.0000123}}

data: {"id":"generation-test","object":"chat.completion.chunk","created":1,"model":"actual/model","provider":"Provider","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":7,"cost":0.0000123}}

data: [DONE]


""";
        using var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream) }));
        var settings = new FakeAiSettingsStore();
        using var client = await new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor()).CreateAsync(AiBackend.OpenRouter, "requested/model", settings.Value, TestContext.Current.CancellationToken);
        OpenRouterRequestUsage? usage = null; var text = "";
        await foreach (var update in ((IProgressReportingChatClient)client).GetStreamingResponseWithProgressAsync([new(ChatRole.User, "Hello")], cancellationToken: TestContext.Current.CancellationToken))
        { text += update.Response?.Text; if (update.OpenRouterUsage is { } report) usage = OpenRouterUsageParser.Merge(usage, report); }
        Assert.Equal("Hello", text); Assert.NotNull(usage); Assert.Equal(0.0000123m, usage.Cost);
        Assert.Equal(5, usage.InputTokens); Assert.Equal(7, usage.OutputTokens); Assert.Equal(3, usage.ReasoningTokens);
        Assert.Equal(2, usage.CachedTokens); Assert.Equal("Provider", usage.Provider); Assert.Equal("actual/model", usage.Model);
        Assert.Single(handler.Requests);
    }
}
