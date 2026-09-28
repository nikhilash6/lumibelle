using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ComfyTextSettingsTests
{
    internal static readonly TextModelReference Model = new(AiBackend.ComfyUI, "exact/model.safetensors", "Display name", "http://comfy.test:8188");
    [Fact]
    public void SettingsUseExactServerIdentityAndPreserveLegacyFallbacks()
    {
        var settings = JsonSerializer.Deserialize<AiSettings>("{\"maxOutputTokens\":1024,\"temperature\":0.4}", AtomicJsonFile.Options)!;
        Assert.Equal(new(1024, .4f), ComfyTextSettings.Resolve(Model, settings));
        var key = TextModelPolicy.Key(Model);
        settings.ComfyTextModels[key] = new(8192, .8f);
        settings = settings with { TextModelAliases = new() { [key] = "Alias" }, StarredTextModels = [] };
        Assert.Equal(8192, ComfyTextSettings.Resolve(Model with { Name = "Renamed", ComfyUrl = "HTTP://COMFY.TEST:8188/" }, settings).MaxOutputTokens);
        Assert.Equal(1024, ComfyTextSettings.Resolve(Model with { ComfyUrl = "http://other:8188" }, settings).MaxOutputTokens);
        Assert.Equal(1024, ComfyTextSettings.Resolve(Model with { Model = "other/model" }, settings).MaxOutputTokens);
        FileAiSettingsStore.Validate(settings);
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(settings with { ComfyTextModels = new() { [key] = new(32769, .5f) } }));
    }
}
