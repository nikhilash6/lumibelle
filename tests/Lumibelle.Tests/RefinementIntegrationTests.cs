using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static JsonNode RefinementCatalog()
    {
        var root = TurboCatalog();
        void Companion(string name, string[] outputs, params (string Field, string Type)[] fields)
        {
            var inputs = new JsonObject { ["protocol"] = new JsonArray(new JsonArray("lumibelle-h3-v1")) };
            foreach (var field in fields) inputs[field.Field] = new JsonArray(field.Type);
            root[name] = new JsonObject { ["input"] = new JsonObject { ["required"] = inputs }, ["output"] = JsonSerializer.SerializeToNode(outputs) };
        }
        Companion("LumibelleH3CaptureV1", ["LATENT"], ("latent", "LATENT"), ("conditioning", "CONDITIONING"), ("context", "STRING"),
            ("package_id", "STRING"), ("width", "INT"), ("height", "INT"), ("frames", "INT"), ("locked_audio_source", "LATENT"));
        Companion("LumibelleH3LoadV1", ["LATENT", "LATENT", "CONDITIONING"], ("token", "STRING"), ("package_id", "STRING"));
        Companion("LumibelleH3PrepareV1", ["LATENT", "CONDITIONING", "NOISE"], ("source", "LATENT"), ("video", "LATENT"),
            ("conditioning", "CONDITIONING"), ("width", "INT"), ("height", "INT"), ("frames", "INT"), ("lock_audio", "BOOLEAN"), ("seed", "INT"));
        root["MinimaxH3LatentUpscaler3D"] = JsonNode.Parse("""
            {"input":{"required":{
              "latent":["LATENT"], "model_name":[["nested/learned.safetensors"]],
              "mode":["DYNAMIC_COMBO",{"options":[{"key":"target dimensions","inputs":{"required":{"width":["INT"],"height":["INT"]}}}]}],
              "align":["INT"], "enable_temporal_chunking":["BOOLEAN"], "force_unload":["BOOLEAN"],
              "device":[["cuda"]], "precision":[["fp16"]]
            }}}
            """);
        return root;
    }

    [Theory]
    [InlineData("none")] [InlineData("companion")] [InlineData("protocol")] [InlineData("type")] [InlineData("output")]
    [InlineData("upscaler")] [InlineData("dimensions")] [InlineData("file")] [InlineData("shift")]
    public void RefinementReadinessChecksExactContractsWithoutDisablingNormalVideo(string problem)
    {
        var root = RefinementCatalog(); var settings = new H3Settings { LatentUpscaler = "nested/learned.safetensors" };
        switch (problem)
        {
            case "companion": root.AsObject().Remove("LumibelleH3CaptureV1"); break;
            case "protocol": root["LumibelleH3LoadV1"]!["input"]!["required"]!["protocol"]![0] = new JsonArray("v2"); break;
            case "type": root["LumibelleH3PrepareV1"]!["input"]!["required"]!["conditioning"]![0] = "IMAGE"; break;
            case "output": root["LumibelleH3CaptureV1"]!["output"]![0] = "IMAGE"; break;
            case "upscaler": root.AsObject().Remove("MinimaxH3LatentUpscaler3D"); break;
            case "dimensions": root["MinimaxH3LatentUpscaler3D"]!["input"]!["required"]!["mode"]![1]!["options"]![0]!["inputs"]!["required"]!.AsObject().Remove("height"); break;
            case "file": settings.LatentUpscaler = "learned.safetensors"; break;
            case "shift": root.AsObject().Remove("MiniMaxH3SigmaShift"); break;
        }
        var check = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.True(check.StandardReady);
        Assert.Equal(problem is not ("companion" or "output"), check.PackageCaptureReady);
        if (problem == "none") Assert.Null(check.RefinementIssue);
        else Assert.False(string.IsNullOrWhiteSpace(check.RefinementIssue));
    }

    [Theory]
    [InlineData(20)] [InlineData(4)] [InlineData(8)]
    public async Task StockComfyAcceptsNormalBatchesButRejectsMissingPromisedCapture(int steps)
    {
        var f = Fixture(); var shot = Ready(); shot.Turbo = steps != 20; shot.TurboSteps = steps == 8 ? 8 : 4;
        using var handler = new ScriptedHttpHandler((r, _) => {
            Assert.Equal("/object_info", r.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TurboCatalog().ToJsonString()) });
        });
        var video = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(39));
        var adapter = new ComfyVideoJobAdapter(video);
        var snapshot = Snapshot(f.Project.Id, shot);
        var request = new AiVideoJobRequest(1, Guid.NewGuid(), snapshot, []);
        await adapter.ValidateAsync(request, _ct);
        var failure = await Assert.ThrowsAsync<WorkspaceStoreException>(() => adapter.ValidateAsync(request with { Snapshot = snapshot with { CaptureRefinementData = true } }, _ct));
        Assert.Contains("companion", failure.Message);
    }

    [Fact]
    public async Task PackageTransferRetriesWithoutDownloadingVideoOrFramesAgain()
    {
        var f = Fixture(); var snapshot = Snapshot(f.Project.Id, Ready()) with { CaptureRefinementData = true };
        var run = new VideoRun { Snapshot = snapshot }; var candidate = new VideoCandidate { Number = 1 };
        var package = await MockRefinementPackage.WriteAsync(Path.Combine(_root, "remote"), snapshot, null, _ct);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "remote", H3RefinementPackage.FileName), _ct);
        candidate.Output = JsonSerializer.SerializeToElement(new { outputs = new Dictionary<string, object> {
            ["14"] = new { images = new[] { new { filename = "video.mp4" } } },
            ["15"] = new { images = new[] { new { filename = "frames-0.webp" }, new { filename = "frames-1.webp" } } },
            ["20"] = new { refinement = new[] { new { protocol = "lumibelle-h3-v1", token = package.Id.ToString("N") } } }
        } });
        var first = await MockFrameArchive.WebpAsync(32, 32, 24, ct: _ct);
        var second = await MockFrameArchive.WebpAsync(32, 32, snapshot.FrameCount - 24, 24, ct: _ct);
        bool fail = true; Dictionary<string, int> requests = [];
        using var handler = new ScriptedHttpHandler((r, ct) => {
            var key = r.RequestUri!.AbsolutePath.Contains("refinement") ? "package" : System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query)["filename"]!;
            requests[key] = requests.GetValueOrDefault(key) + 1;
            return Task.FromResult(key == "package" && fail ? new HttpResponseMessage(HttpStatusCode.InternalServerError) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(key switch { "package" => bytes, "video.mp4" => [1, 2, 3], "frames-0.webp" => first, _ => second }) });
        });
        var generator = new ComfyH3Video(new TestHttpFactory(handler), new BenchmarkComfyMonitor(), f.Assets, f.Assets, f.Shots, new MockMediaTools(snapshot.FrameCount));
        var stage = Path.Combine(await f.Shots.RunDirectoryAsync(f.Project.Id, run.Id, _ct), "candidate-1");
        await Assert.ThrowsAsync<HttpRequestException>(() => generator.DownloadAsync(run, candidate, stage, _ => Task.CompletedTask, _ct));
        Assert.True(File.Exists(Path.Combine(stage, "video.mp4"))); Assert.True(File.Exists(Path.Combine(stage, "archive-0001.webp")));
        Assert.Empty((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        fail = false; var take = await generator.DownloadAsync(run, candidate, stage, _ => Task.CompletedTask, _ct);
        Assert.Equal(package, take.RefinementPackage); Assert.Equal(2, requests["package"]);
        Assert.All(requests.Where(r => r.Key != "package"), r => Assert.Equal(1, r.Value));
        Assert.Equal(3 + take.Frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes) + package.Bytes, take.Bytes);
    }

    [Fact]
    public async Task RefinementLocksEachParentWhileOriginalBatchCanKeepRunning()
    {
        using var f = await QueuedVideoFixture.Create(this, 8);
        var original = await f.Capture(); var running = await f.Claim(original);
        await f.Worker.ExecuteAsync(running, original.Snapshot, _ct); // Keep its job lease active.
        var source = Assert.Single((await f.Shots.LoadAsync(f.Project.Id, _ct)).Takes);
        var request = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id,
            TakeRefinementMode.Refine, source.Width, source.Height, _ct);
        Assert.Equal(source.Id, request.Target.TakeId);
        await f.Jobs.EnqueueAsync(request, _ct);
        var duplicate = await f.CaptureService.CaptureRefinementAsync(Guid.NewGuid(), Guid.NewGuid(), f.Project.Id, source.Id,
            TakeRefinementMode.Rework, source.Width, source.Height, _ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Jobs.EnqueueAsync(duplicate, _ct));
        Assert.NotEqual(original.Target.LockKey(AiJobKind.Video), request.Target.LockKey(AiJobKind.Video));
        Assert.False(JsonSerializer.SerializeToElement(original.Target, AtomicJsonFile.Options).TryGetProperty("takeId", out _));
    }

    [Fact]
    public async Task RefineCopiesEncodedAudioAndVideoWithoutChangingEitherStream()
    {
        Directory.CreateDirectory(_root);
        async Task<string> Ffmpeg(params string[] args)
        {
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) start.ArgumentList.Add(arg);
            using var p = Process.Start(start)!; var errors = p.StandardError.ReadToEndAsync(_ct); var output = p.StandardOutput.ReadToEndAsync(_ct);
            await p.WaitForExitAsync(_ct); Assert.True(p.ExitCode == 0, await errors); return await output;
        }
        var source = Path.Combine(_root, "source.mp4"); var refined = Path.Combine(_root, "refined.mp4"); var merged = Path.Combine(_root, "merged.mp4");
        foreach (var (path, color, tone) in new[] { (source, "red", "440"), (refined, "blue", "880") })
            await Ffmpeg("-f", "lavfi", "-i", $"color=c={color}:s=64x64:r=24", "-f", "lavfi", "-i", $"sine=frequency={tone}:sample_rate=32000",
                "-frames:v", "39", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-shortest", path);
        var tools = new ProductionMediaTools(); await tools.CopyAudioAsync(refined, source, merged, new(), _ct);
        Task<string> Hash(string path, string stream) => Ffmpeg("-i", path, "-map", stream, "-c", "copy", "-f", "hash", "-hash", "sha256", "-");
        Assert.Equal(await Hash(source, "0:a:0"), await Hash(merged, "0:a:0"));
        Assert.NotEqual(await Hash(refined, "0:a:0"), await Hash(merged, "0:a:0"));
        Assert.Equal(await Hash(refined, "0:v:0"), await Hash(merged, "0:v:0"));
        Assert.Equal(await tools.VideoInfoAsync(refined, new(), _ct), await tools.VideoInfoAsync(merged, new(), _ct));
    }
}
