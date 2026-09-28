using lumibelle.Services;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class ProjectRouteTests : IDisposable
{
    private readonly ApplicationPaths _paths = new(Path.Combine(Path.GetTempPath(), "Lumibelle.Tests", Guid.NewGuid().ToString()));
    private FileProjectStore Store => new(_paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance);
    private FileProjectRoutes Routes => new(_paths, Store);
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("The Lantern Festival", "the-lantern-festival")]
    [InlineData("  Lumière / 光! ", "lumiere-光")]
    [InlineData("🌲?!", "project")]
    [InlineData("00000000-0000-0000-0000-000000000001", "project-00000000-0000-0000-0000-000000000001")]
    public void NamesBecomeReadableKeys(string name, string expected) => Assert.Equal(expected, FileProjectRoutes.Slug(name));
    [Fact]
    public async Task RenameReservesAliasesAndKeepsIdsAndDeepLinks()
    {
        var p = await Store.CreateAsync(new("First name"), Ct); var routes = Routes;
        Assert.Equal(p.Id, (await routes.ResolveAsync(p.Id.ToString(), Ct))!.ProjectId);
        p = await Store.UpdateAsync(p, new("New name"), Ct);
        Assert.Equal("new-name", (await routes.ResolveAsync("first-name", Ct))!.Slug);
        Assert.Equal($"/projects/new-name/shots?shotId=x#prompt", routes.Canonicalize($"/projects/{p.Id}/shots?shotId=x#prompt"));
        Assert.Equal("/projects/new-name/shots?shotId=x#prompt", routes.Canonicalize($"/projects/{p.Id}/production?shotId=x#prompt"));
        var other = await Store.CreateAsync(new("First name"), Ct);
        Assert.Equal("first-name-2", (await routes.ResolveAsync(other.Id.ToString(), Ct))!.Slug);
        p = await Store.UpdateAsync(p, new("First name"), Ct);
        Assert.Equal("first-name", (await Routes.ResolveAsync("new-name", Ct))!.Slug);
        Assert.True(File.Exists(Path.Combine(_paths.Projects, p.Id.ToString(), "project.json")));
    }
    [Fact]
    public async Task ConcurrentAllocationsAreUniqueAndReopenUnchanged()
    {
        var projects = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store.CreateAsync(new("Shared name"), Ct)));
        var resolutions = await Task.WhenAll(projects.Select(async p => (await Routes.ResolveAsync(p.Id.ToString(), Ct))!.Slug));
        Assert.Equal(8, resolutions.Distinct().Count());
        foreach (var project in projects) Assert.Equal(project.Id, (await Routes.ResolveAsync(project.Id.ToString(), Ct))!.ProjectId);
        Assert.Null(await Routes.ResolveAsync("missing", Ct));
    }
    [Fact]
    public async Task InterruptedRenameIsReconciledWithoutReusingAnAlias()
    {
        var p = await Store.CreateAsync(new("Original"), Ct); await Routes.RefreshAsync(Ct);
        var path = Path.Combine(_paths.Projects, p.Id.ToString(), "project.json");
        await AtomicJsonFile.WriteAsync(path, p with { Name = "Recovered rename" }, Ct);
        Assert.Equal("recovered-rename", (await Routes.ResolveAsync("original", Ct))!.Slug);
        var original = await Store.CreateAsync(new("Original"), Ct);
        Assert.Equal("original-2", (await Routes.ResolveAsync(original.Id.ToString(), Ct))!.Slug);
    }
    [Fact]
    public async Task CorruptIndexFailsWithoutOverwritingAliases()
    {
        await Store.CreateAsync(new("Project"), Ct); await Routes.RefreshAsync(Ct);
        var path = Path.Combine(_paths.Projects, "project-routes.json"); await File.WriteAllTextAsync(path, "broken", Ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => Routes.RefreshAsync(Ct));
        Assert.Equal("broken", await File.ReadAllTextAsync(path, Ct));
    }
    [Fact]
    public async Task RemovedProjectsKeepTheirNamesReserved()
    {
        var p = await Store.CreateAsync(new("Reserved"), Ct); await Routes.RefreshAsync(Ct);
        Directory.Delete(Path.Combine(_paths.Projects, p.Id.ToString()), true);
        var next = await Store.CreateAsync(new("Reserved"), Ct);
        Assert.Null(await Routes.ResolveAsync("reserved", Ct));
        Assert.Equal("reserved-2", (await Routes.ResolveAsync(next.Id.ToString(), Ct))!.Slug);
    }
    public void Dispose() { if (Directory.Exists(_paths.Data)) Directory.Delete(_paths.Data, true); }
}
