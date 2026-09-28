using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed partial class AiModelComponentTests
{
    [Fact]
    public void CodexImageSettingsDistinguishAgentSelectionFromTheManagedImageModel()
    {
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, ImageModel = "gpt-5.6-luna" }, DefaultImageWorkflow = ImageWorkflow.CodexImages };
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-images").Click();
        Assert.Equal("Codex agent model", page.Find("label[for='codex-image-model']").TextContent);
        Assert.Equal("Agent reasoning effort", page.Find("label[for='codex-image-effort']").TextContent);
        Assert.Contains("Image model: managed by Codex", page.Find("#codex-image-model-help").TextContent);
        Assert.Equal("codex-image-model-help codex-agent-guide", page.Find("#codex-image-model").GetAttribute("aria-describedby"));
        Assert.Equal("gpt-5.6-luna", page.Find("#codex-image-model").GetAttribute("value"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexAllowanceShowsReportedWindowsWithoutPhantomLimits(bool compact)
    {
        using var context = new BunitContext();
        var mock = new Lumibelle.Testing.MockCodexTransport();
        await using var client = new CodexClient(mock, TimeProvider.System);
        context.Services.AddMudServices();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<ICodexClient>(client);
        var settings = new CodexSettings { Enabled = true };
        var view = context.Render<lumibelle.Components.AI.CodexUsage>(p => p.Add(c => c.Settings, settings).Add(c => c.Compact, compact));
        Assert.Contains("Allowance not checked", view.Markup);
        await client.CheckAsync(settings, Xunit.TestContext.Current.CancellationToken);
        mock.Emit("account/rateLimits/updated", new { rateLimits = new { limitId = "codex", primary = (object?)null, secondary = new { usedPercent = 61, windowDurationMins = 10080 } } });
        view.WaitForAssertion(() => Assert.Contains("39% remaining", view.Markup));
        Assert.Contains("Weekly", view.Markup);
        Assert.DoesNotContain("5-hour", view.Markup);
        Assert.DoesNotContain("Unavailable", view.Markup);
        if (!compact)
        {
            Assert.Single(view.FindAll(".codex-usage-window"));
            Assert.Contains("Reset time not reported", view.Markup);
        }
        mock.Emit("account/rateLimits/updated", new { rateLimits = new { limitId = "codex", primary = new { usedPercent = 0, windowDurationMins = 300 }, secondary = new { usedPercent = 100, windowDurationMins = 10080 } } });
        view.WaitForAssertion(() => Assert.Contains("0% remaining", view.Markup));
        Assert.Contains("5-hour", view.Markup);
        Assert.Contains("100% remaining", view.Markup);
        if (!compact) Assert.Equal(2, view.FindAll(".codex-usage-window").Count);
        mock.Emit("account/rateLimits/updated", new { rateLimits = new { limitId = "codex" } });
        view.WaitForAssertion(() => Assert.Contains("no usage windows reported", view.Markup.ToLowerInvariant()));
        Assert.DoesNotContain("% remaining", view.Markup);
        Assert.Equal(0, mock.Turns);
    }

    [Fact]
    public void CodexConnectionSaveKeepsOtherDraftsAndImageOptions()
    {
        _settings.Value = _settings.Value with { Codex = new() { ImageModel = "image-model", ImageEffort = "high", TextModel = "text-model" } };
        var page = Render<AiSettingsPage>(); page.Find("#ai-tab-connections").Click();
        page.Find("#comfy-url").Change("unsaved-url"); page.Find("#ai-provider-openrouter").Click(); page.Find("#openrouter-key").Change("unsaved-key");
        page.Find("#ai-provider-codex").Click(); page.Find("#codex-executable").Change("C:/tools/codex.exe"); page.Find("#codex-concurrency").Change("3");
        page.Find("#ai-connection-codex form").Submit();
        Assert.Equal(3, _settings.Value.Codex.Concurrency); Assert.Equal("image-model", _settings.Value.Codex.ImageModel);
        Assert.Equal("high", _settings.Value.Codex.ImageEffort); Assert.Equal("text-model", _settings.Value.Codex.TextModel);
        Assert.NotEqual("unsaved-url", _settings.Value.ComfyUrl); Assert.Equal("test-only-key", _settings.Key);
        Assert.Contains("Codex connection saved", page.Find(".settings-feedback").TextContent);
        _settings.SaveError = new WorkspaceConflictException(); page.Find("#codex-concurrency").Change("4"); page.Find("#ai-connection-codex form").Submit();
        Assert.Equal(3, _settings.Value.Codex.Concurrency); Assert.Equal("4", page.Find("#codex-concurrency").GetAttribute("value"));
        Assert.Contains("Another tab", page.Markup);
    }
    [Fact]
    public void CodexEffortIsTemporaryUntilExplicitlySetAsProjectDefault()
    {
        var model = new TextModelReference(AiBackend.Codex, "qa", "Codex QA");
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, TextModel = "qa", TextEffort = "medium" }, DefaultBackend = AiBackend.Codex, StarredTextModels = [model] };
        _providers.Models = [new("qa", "Codex QA", SupportsImages: true, ReasoningEfforts: ["low", "medium", "high"], DefaultEffort: "medium")];
        var id = Guid.NewGuid(); var states = new List<TextModelSelectionState>(); var picker = Picker(id, TextAssistantStudio.PromptEnhancement, states); var dialog = PickerDialog(picker);
        dialog.Find("select[id$='-effort']").Change("high"); Assert.Equal("high", states.Last().Model.ReasoningEffort); Assert.Empty(_preferences.Values);
        _preferences.SaveError = new WorkspaceStoreException("Could not save preference"); Button(dialog, "Set as project default").Click();
        Assert.Equal("high", states.Last().Model.ReasoningEffort); Assert.Contains("Could not save", dialog.Markup);
        _preferences.SaveError = null; Button(dialog, "Set as project default").Click(); Assert.Equal("high", _preferences.Values[id].TextDefault!.ReasoningEffort);
    }
    [Fact]
    public void CodexDefaultEffortSavesOnlyItsFieldAndKeepsFailedDrafts()
    {
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, ImageModel = "image-model", ImageEffort = "low" } };
        _providers.Models = [new("qa", "Codex QA", ReasoningEfforts: ["low", "medium", "high"], DefaultEffort: "medium")];
        var page = Render<AiSettingsPage>();
        page.Find("#ai-tab-connections").Click(); page.Find("#comfy-url").Change("unsaved-server");
        page.Find("#ai-tab-text").Click(); page.Find("#ai-text-provider-codex").Click();
        page.Find("#codex-text-effort").Change("high"); Button(page, "Save default effort").Click();
        Assert.Equal("high", _settings.Value.Codex.TextEffort);
        Assert.Equal("low", _settings.Value.Codex.ImageEffort); Assert.NotEqual("unsaved-server", _settings.Value.ComfyUrl);
        _settings.SaveError = new WorkspaceConflictException();
        page.Find("#codex-text-effort").Change("low"); Button(page, "Save default effort").Click();
        Assert.Equal("high", _settings.Value.Codex.TextEffort); Assert.Equal("low", page.Find("#codex-text-effort").GetAttribute("value"));
        Assert.Contains("Another tab", page.Markup);
        Button(page, "Cancel").Click(); Assert.Equal("high", page.Find("#codex-text-effort").GetAttribute("value"));
        _settings.SaveError = null;
        page.Find("#codex-text-effort").Change(""); Button(page, "Save default effort").Click();
        Assert.Null(_settings.Value.Codex.TextEffort);
    }
    [Fact]
    public void CodexRequestEffortFollowsSettingsAndInvalidChoicesBlockSubmission()
    {
        var model = new TextModelReference(AiBackend.Codex, "qa", "Codex QA");
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, TextModel = "qa", TextEffort = "medium" }, DefaultBackend = AiBackend.Codex, StarredTextModels = [model] };
        _providers.Models = [new("qa", "Codex QA", SupportsImages: true, ReasoningEfforts: ["low", "medium", "high"], DefaultEffort: "medium")];
        var id = Guid.NewGuid(); var states = new List<TextModelSelectionState>(); var picker = Picker(id, TextAssistantStudio.PromptEnhancement, states); var dialog = PickerDialog(picker);
        dialog.Find("select[id$='-effort']").Change("low");
        _settings.Value = _settings.Value with { Codex = _settings.Value.Codex with { TextEffort = "high" } };
        Button(dialog, "Refresh availability").Click(); Assert.Equal("low", states.Last().Model.ReasoningEffort);
        dialog.Find("select[id$='-effort']").Change(""); Assert.Equal("high", states.Last().Model.ReasoningEffort);
        _settings.Value = _settings.Value with { Codex = _settings.Value.Codex with { TextEffort = "max" } };
        Button(dialog, "Refresh availability").Click(); Assert.False(states.Last().Ready); Assert.Contains("supported reasoning effort", dialog.Markup);
        dialog.Find("select[id$='-effort']").Change("low"); Assert.True(states.Last().Ready);
    }
}
