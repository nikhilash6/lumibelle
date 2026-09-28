using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AssetStoreTests
{
    [Fact]
    public async Task AssetOrderIsAtomicAndSurvivesMediaPublicationAndReload()
    {
        var (project, store) = CreateStore(); var first = Asset("First", "notes"); var second = Asset("Second", "notes");
        var initial = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [first, second] }, 0, TestContext.Current.CancellationToken);
        var moved = await store.ReorderAssetsAsync(project.Id, [second.Id, first.Id], initial.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(initial.Revision + 1, moved.Revision);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(initial.Assets[0]), System.Text.Json.JsonSerializer.Serialize(moved.Assets[1]));
        await using var png = new MemoryStream(Png(4, 3));
        var published = await store.AddImageAsync(project.Id, first.Id, png, new("image.png", [], AssetImageOrigin.Imported), moved.Revision, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { second.Id, first.Id }, published.Assets.Select(a => a.Id));
        var restored = await store.ReorderAssetsAsync(project.Id, [first.Id, second.Id], published.Revision, TestContext.Current.CancellationToken);
        Assert.Single(restored.Assets[0].Images);
        Assert.Equal(new[] { first.Id, second.Id }, (await store.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Assets.Select(a => a.Id));
        Assert.Equal(restored.Revision, (await store.ReorderAssetsAsync(project.Id, [first.Id, second.Id], restored.Revision, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public async Task AssetOrderRejectsStaleIncompleteAndDuplicateOrders()
    {
        var (project, store) = CreateStore(); var first = Asset("First", ""); var second = Asset("Second", "");
        var saved = await store.SaveAsync(new() { ProjectId = project.Id, Assets = [first, second] }, 0, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => store.ReorderAssetsAsync(project.Id, [second.Id, first.Id], 0, TestContext.Current.CancellationToken));
        foreach (var order in new Guid[][] { [first.Id], [first.Id, first.Id], [first.Id, Guid.NewGuid()] })
            await Assert.ThrowsAsync<WorkspaceStoreException>(() => store.ReorderAssetsAsync(project.Id, order, saved.Revision, TestContext.Current.CancellationToken));
        Assert.Equal(saved.Revision, (await store.LoadAsync(project.Id, TestContext.Current.CancellationToken)).Revision);
    }

    [Fact]
    public void AssetOrderRebasePreservesLocalMoveAndRejectsCompetingMoves()
    {
        var a = Asset("A", ""); var b = Asset("B", ""); var c = Asset("C", ""); var added = Asset("New", "");
        var baseline = new AssetLibrary { ProjectId = Guid.NewGuid(), Assets = [a, b, c], Revision = 1 };
        var local = baseline with { Assets = [b, a, c] };
        var published = baseline with { Assets = [a with { Description = "new metadata" }, b, c, added], Revision = 2 };
        var merged = AssetLibraryRebase.Merge(baseline, local, published);
        Assert.Equal(new[] { b.Id, a.Id, c.Id, added.Id }, merged.Assets.Select(x => x.Id));
        Assert.Equal("new metadata", merged.Assets[1].Description);
        Assert.Throws<WorkspaceConflictException>(() => AssetLibraryRebase.Merge(baseline, local, published with { Assets = [c, a, b] }));
    }
}
