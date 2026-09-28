using System.Diagnostics;
using System.Text.Json;
using lumibelle.Services;
using lumibelle.Services.Assets;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length != 2 || !Guid.TryParse(args[1], out var projectId))
{
    Console.Error.WriteLine("Usage: storage_benchmark <data-directory> <project-id>");
    return 1;
}
var paths = new ApplicationPaths(args[0]);
var projects = new FileProjectStore(paths, TimeProvider.System, NullLogger<FileProjectStore>.Instance);
var files = new ProjectFiles(paths, projects);
var assets = new FileAssetStore(files, TimeProvider.System);
var shots = new FileShotStore(files, TimeProvider.System);
var library = await assets.LoadAsync(projectId);
var document = await shots.LoadAsync(projectId);
var images = library.Assets.SelectMany(a => a.Images.Select(i => (Asset: a.Id, Image: i.Id))).Take(40).ToArray();
var takes = document.Takes.Take(40).ToArray();
var measurements = new List<object>();
await Measure("Load asset library", () => assets.LoadAsync(projectId));
await Measure("Load shots", () => shots.LoadAsync(projectId));
await Measure($"Open {images.Length} images sequentially", async () =>
{
    foreach (var (asset, image) in images)
    { await using var media = await assets.OpenImageAsync(projectId, asset, image) ?? throw new IOException("A sampled image is unavailable."); }
});
await Measure($"Open {takes.Length} take videos sequentially", async () =>
{
    foreach (var take in takes)
    { await using var media = await shots.OpenAsync(projectId, take.Id, lumibelle.Models.ShotTrashKind.Take) ?? throw new IOException("A sampled take is unavailable."); }
});
Console.WriteLine(JsonSerializer.Serialize(new { assetCount = library.Assets.Count,
    imageCount = library.Assets.Sum(a => a.Images.Count), shotCount = document.Shots.Count,
    takeCount = document.Takes.Count, measurements }, new JsonSerializerOptions { WriteIndented = true }));
return 0;

async Task Measure(string name, Func<Task> action)
{
    await action(); // JIT / OS file cache warm-up; never extracts frames or writes project content.
    var times = new List<double>();
    for (var i = 0; i < 5; i++)
    {
        var timer = Stopwatch.StartNew();
        await action();
        times.Add(timer.Elapsed.TotalMilliseconds);
    }
    measurements.Add(new { name, medianMilliseconds = times.Order().ElementAt(2), samples = times });
}
