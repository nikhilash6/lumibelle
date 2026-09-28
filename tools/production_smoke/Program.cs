using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Explicitly invoked live evaluation. Reads source projects; writes only a new output directory.
if (args.Length == 3 && args[0] == "--revalidate")
{
    var sourceDirectory = Path.GetFullPath(args[1]); var resultDirectory = Path.GetFullPath(args[2]);
    if (Directory.Exists(resultDirectory)) throw new IOException("Choose a new output directory to preserve earlier evaluations.");
    var cases = Directory.GetDirectories(sourceDirectory, "shot-*");
    if (cases.Length == 0) throw new IOException("No captured shot evaluations were found.");
    Directory.CreateDirectory(resultDirectory);
    foreach (var input in cases.Order())
    {
        var captured = JsonSerializer.Deserialize<PromptCompositionRequest>(await File.ReadAllTextAsync(Path.Combine(input, "request.json")), AtomicJsonFile.Options)!;
        var raw = await File.ReadAllTextAsync(Path.Combine(input, "response.json"));
        var destination = Path.Combine(resultDirectory, Path.GetFileName(input)); Directory.CreateDirectory(destination);
        await ValidateAsync(raw, captured, destination, CancellationToken.None);
    }
    return;
}
if (args.Length < 4) throw new ArgumentException("Usage: <repo root> <project id> <new output directory> <zero-based shot indices...>");
var root = Path.GetFullPath(args[0]); var projectId = Guid.Parse(args[1]); var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output)) throw new IOException("Choose a new output directory to preserve earlier evaluations.");
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = root, EnvironmentName = "Development" });
builder.Services.Configure<ProjectStorageOptions>(builder.Configuration.GetSection("Projects"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IProjectStore, FileProjectStore>();
builder.Services.AddSingleton<ProjectFiles>();
builder.Services.AddSingleton<IAssetStore, FileAssetStore>();
builder.Services.AddSingleton<IShotStore, FileShotStore>();
builder.Services.AddSingleton<IScriptStore, FileScriptStore>();
using var host = builder.Build();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12)); var ct = timeout.Token;
var files = host.Services.GetRequiredService<ProjectFiles>(); var directory = await files.DirectoryAsync(projectId, ct);
var assets = host.Services.GetRequiredService<IAssetStore>(); var library = await assets.LoadAsync(projectId, ct);
var doc = await host.Services.GetRequiredService<IShotStore>().LoadAsync(projectId, ct);
var project = (await host.Services.GetRequiredService<IProjectStore>().GetAsync(projectId, ct))!;
var settings = (await AtomicJsonFile.ReadAsync<FileAiSettingsStore.StoredSettings>(Path.Combine(root, "App_Data", "ai-settings.json"), ct))!.Settings;
var preferences = await AtomicJsonFile.ReadAsync<ProjectAiPreferences>(Path.Combine(directory, "ai-preferences.json"), ct);
var selection = preferences?.Production ?? preferences?.Shots ?? throw new InvalidOperationException("Choose a Production or Shots model in the project first.");
if (selection.Backend != AiBackend.Codex) throw new InvalidOperationException("This optional live harness uses the configured Codex provider.");
await using var codex = new CodexClient(new CodexTransportFactory(), TimeProvider.System);
using var client = new CodexChatClient(codex, settings.Codex with { TextEffort = selection.ReasoningEffort }, selection.Model);
Directory.CreateDirectory(output);
foreach (var index in args.Skip(3).Select(int.Parse))
{
    var source = doc.Shots[index]; var shot = ShotVideoDefaults.Capture(source, project);
    var approved = await host.Services.GetRequiredService<IScriptStore>().LoadApprovedAsync(projectId, shot.ApprovedScriptId, ct);
    var scene = ScriptStructure.Sections(approved!.Blocks).First(s => s.Id == shot.SceneId);
    var images = await ProductionInputs.CaptureAsync(projectId, shot, assets, ct);
    var request = new PromptCompositionRequest(projectId, Guid.NewGuid(), 1, "live-evaluation", ProductionPolicy.SourceFingerprint(shot), shot,
        ScriptStructure.Markdown(approved.Blocks.Skip(scene.Start).Take(scene.Count)),
        doc.Shots.Skip(Math.Max(0, index - 1)).Take(3).Where(s => s.Id != source.Id).Select(s => s.Title + ": " + s.Description).ToArray(),
        ShotReferences.Resolve(shot, library, doc), ShotLooks.Capture(shot, library), images.Select(i => i.Identity).ToArray(), "", "", "", selection);
    var destination = Path.Combine(output, $"shot-{index:D2}"); Directory.CreateDirectory(destination);
    await File.WriteAllTextAsync(Path.Combine(destination, "request.json"), JsonSerializer.Serialize(request, AtomicJsonFile.Options), ct);
    for (var i = 0; i < images.Count; i++) await File.WriteAllBytesAsync(Path.Combine(destination, $"picture-{i + 1}.png"), images[i].Bytes, ct);
    Console.WriteLine($"Composing {source.Title}: {images.Count} exact reference crops with {selection.Model}.");
    var response = await client.GetResponseAsync(PromptComposer.BuildMessages(request, images.Select(i => i.Bytes).ToArray()), cancellationToken: ct);
    await File.WriteAllTextAsync(Path.Combine(destination, "response.json"), response.Text, ct);
    await ValidateAsync(response.Text, request, destination, ct);
}

static async Task ValidateAsync(string raw, PromptCompositionRequest request, string destination, CancellationToken ct)
{
    try
    {
        var result = PromptComposer.Parse(raw, request);
        await File.WriteAllTextAsync(Path.Combine(destination, "prompt.txt"), result.Prompt, ct);
        await File.WriteAllTextAsync(Path.Combine(destination, "reference-usage.txt"), result.ReferenceUsage, ct);
        Console.WriteLine($"Validated {request.Shot.Title}: {result.Prompt.Length} prompt characters.");
    }
    catch (WorkspaceStoreException e)
    {
        await File.WriteAllTextAsync(Path.Combine(destination, "validation-error.txt"), e.Message, ct);
        Console.WriteLine($"Review needed for {request.Shot.Title}: {e.Message}"); Environment.ExitCode = 1;
    }
}
