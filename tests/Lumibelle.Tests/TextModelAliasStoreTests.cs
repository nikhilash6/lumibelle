using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class TextModelStoreTests
{
    [Fact]
    public async Task AliasesPersistIndependentlyOfStarsAndPreserveModelIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = TextModelPolicy.Key(Local);
        var saved = await Settings.SaveAsync(new() { StarredTextModels = [Local], TextModelAliases = new() { [key] = " Friendly local " } }, cancellationToken: ct);
        Assert.Equal("Friendly local", (await Settings.LoadAsync(ct)).TextModelAliases[key]);
        var unstarred = await Settings.SaveAsync(saved with { StarredTextModels = [] }, cancellationToken: ct);
        Assert.Equal("Friendly local", unstarred.TextModelAliases[key]);
        var restored = await Settings.SaveAsync(unstarred with { StarredTextModels = [Local] }, cancellationToken: ct);
        Assert.Equal(Local, Assert.Single(restored.StarredTextModels));
        Assert.Equal("Friendly local", TextModelPolicy.DisplayName(Local with { ComfyUrl = "HTTP://LOCALHOST:8188/" }, restored));
        Assert.Equal(Local.Name, TextModelPolicy.DisplayName(Local with { ComfyUrl = "http://localhost:8189" }, restored));
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => Settings.SaveAsync(saved, cancellationToken: ct));
        var cleared = await Settings.SaveAsync(restored with { TextModelAliases = new() { [key] = " " } }, cancellationToken: ct);
        Assert.Empty(cleared.TextModelAliases); Assert.Equal(Local.Name, TextModelPolicy.DisplayName(Local, cleared));
        var path = Path.Combine(_root, "App_Data", "ai-settings.json");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        legacy["settings"]!.AsObject().Remove("textModelAliases");
        await File.WriteAllTextAsync(path, legacy.ToJsonString(), ct);
        Assert.Empty((await Settings.LoadAsync(ct)).TextModelAliases);
    }

    [Fact]
    public void DuplicatePickerLabelsIncludeTheProviderIdAndServerWhereNeeded()
    {
        var other = Local with { ComfyUrl = "http://localhost:8189" };
        var choices = new[] { Local, other, Cloud, Cloud with { Model = "another/model" } };
        var settings = new AiSettings { TextModelAliases = choices.ToDictionary(TextModelPolicy.Key, _ => "Writer") };
        var labels = choices.Select(m => TextModelPolicy.PickerLabel(m, settings, choices, _ => null)).ToArray();
        Assert.Equal(4, labels.Distinct().Count());
        Assert.Contains(Local.ComfyUrl!, labels[0]); Assert.Contains(Local.Model, labels[0]);
        Assert.Equal("OpenRouter · Writer · test/model", labels[2]);
    }
}
