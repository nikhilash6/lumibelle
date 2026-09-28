using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiSettingsComponentTests
{
    [Fact]
    public void QueueConcurrencyHasIndependentDraftCancelAndFailureRetry()
    {
        var page = Render<AiSettingsPage>(); var saved = _settings.Value;
        page.Find("#ai-tab-connections").Click(); page.Find("#comfy-url").Change("http://unsaved.test:8188");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-defaults").Click(); page.Find("#timeout").Change("500");
        Assert.Empty(page.FindAll("#ai-panel-text #openrouter-concurrency"));
        page.Find("#ai-tab-connections").Click(); page.Find("#ai-provider-openrouter").Click();
        page.Find("#openrouter-concurrency").Change("3");
        Assert.Equal(1, _settings.Value.OpenRouterConcurrency);
        page.FindAll("button").Where(b => b.Closest("[hidden]") is null).Single(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.Equal("1", page.Find("#openrouter-concurrency").GetAttribute("value"));
        page.Find("#openrouter-concurrency").Change("4"); _settings.SaveError = new WorkspaceStoreException("Disk unavailable");
        page.FindAll("form").Single(f => f.Closest("[hidden]") is null).Submit(); Assert.Contains("Disk unavailable", page.Markup);
        Assert.Equal("4", page.Find("#openrouter-concurrency").GetAttribute("value")); Assert.Equal(1, _settings.Value.OpenRouterConcurrency);
        _settings.SaveError = null; page.FindAll("form").Single(f => f.Closest("[hidden]") is null).Submit();
        Assert.Equal(4, _settings.Value.OpenRouterConcurrency); Assert.Equal(saved.ComfyUrl, _settings.Value.ComfyUrl);
        Assert.Equal(saved.TimeoutSeconds, _settings.Value.TimeoutSeconds);
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-defaults").Click(); Assert.Equal("500", page.Find("#timeout").GetAttribute("value"));
        page.Find("#text-timeout-form").Submit();
        Assert.Equal(500, _settings.Value.TimeoutSeconds); Assert.Equal(4, _settings.Value.OpenRouterConcurrency);
    }

    [Fact]
    public void TextTimeoutSaveAndCancelPreserveOpenRouterDrafts()
    {
        var page = Render<AiSettingsPage>();
        page.Find("#ai-provider-openrouter").Click();
        page.Find("#openrouter-key").Change("draft-key"); page.Find("#openrouter-concurrency").Change("6");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-defaults").Click(); page.Find("#timeout").Change("500");
        page.Find("#text-timeout-form").Submit();
        Assert.Equal(500, _settings.Value.TimeoutSeconds); Assert.Equal(1, _settings.Value.OpenRouterConcurrency);
        Assert.NotEqual("draft-key", _settings.Key);
        page.Find("#text-timeout-form").QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Cancel").Click();
        page.Find("#ai-tab-connections").Click();
        Assert.Equal("6", page.Find("#openrouter-concurrency").GetAttribute("value"));
        Assert.Equal("draft-key", page.Find("#openrouter-key").GetAttribute("value"));
        page.Find("#connection-openrouter-form").Submit();
        Assert.Equal(6, _settings.Value.OpenRouterConcurrency); Assert.Equal("draft-key", _settings.Key);
        Assert.Equal(500, _settings.Value.TimeoutSeconds);
    }

    [Fact]
    public void OpenRouterConflictRetainsKeyAndConcurrencyDrafts()
    {
        var page = Render<AiSettingsPage>(); page.Find("#ai-provider-openrouter").Click();
        page.Find("#openrouter-key").Change("replacement-key"); page.Find("#openrouter-concurrency").Change("5");
        _settings.SaveError = new WorkspaceConflictException();
        page.Find("#connection-openrouter-form").Submit();
        Assert.Contains("Reload saved settings", page.Markup);
        Assert.Equal("5", page.Find("#openrouter-concurrency").GetAttribute("value"));
        Assert.Equal("replacement-key", page.Find("#openrouter-key").GetAttribute("value"));
        Assert.Equal(1, _settings.Value.OpenRouterConcurrency);
    }
}
public sealed partial class AiSettingsStoreTests
{
    [Theory] [InlineData(1)] [InlineData(8)]
    public async Task QueueConcurrencyPersistsWithCredentialsAndLegacyDefault(int concurrency)
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var saved = await Store.SaveAsync(new AiSettings { OpenRouterConcurrency = concurrency }, "protected-key", cancellationToken: ct);
        Assert.Equal(concurrency, (await Store.LoadAsync(ct)).OpenRouterConcurrency);
        Assert.Equal("protected-key", await Store.ReadOpenRouterKeyAsync(ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { OpenRouterConcurrency = 0 }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Store.SaveAsync(saved with { OpenRouterConcurrency = 9 }, cancellationToken: ct));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Store.SaveAsync(saved with { Revision = 0 }, cancellationToken: ct));
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        json["settings"]!.AsObject().Remove("openRouterConcurrency"); await File.WriteAllTextAsync(path, json.ToJsonString(), ct);
        Assert.Equal(1, (await Store.LoadAsync(ct)).OpenRouterConcurrency);
    }
}
