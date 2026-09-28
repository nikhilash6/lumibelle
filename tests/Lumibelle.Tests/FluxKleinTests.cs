using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;

namespace Lumibelle.Tests;

public sealed class FluxKleinTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void OfficialGraphUsesKvFourStepFluxSamplingAndOrderedReferenceConditioning(int count)
    {
        var settings = new AiSettings();
        var graph = JsonSerializer.SerializeToElement(ComfyFluxKleinImages.BuildWorkflow(settings, "Use image 1", 77, 1344, 768,
            Enumerable.Range(1, count).Select(i => $"IMAGE_{i}").ToArray(), "client"));
        var nodes = graph.GetProperty("prompt");
        JsonElement Inputs(string id) => nodes.GetProperty(id).GetProperty("inputs");
        Assert.Equal("client", graph.GetProperty("client_id").GetString());
        Assert.Equal(settings.FluxKleinModel, Inputs("1").GetProperty("unet_name").GetString());
        Assert.Equal(settings.FluxKleinTextEncoder, Inputs("2").GetProperty("clip_name").GetString());
        Assert.Equal("flux2", Inputs("2").GetProperty("type").GetString());
        Assert.Equal(settings.FluxKleinVae, Inputs("3").GetProperty("vae_name").GetString());
        Assert.Equal("FluxKVCache", nodes.GetProperty("6").GetProperty("class_type").GetString());
        Assert.Equal(1, Inputs("7").GetProperty("cfg").GetDouble());
        Assert.Equal(77, Inputs("8").GetProperty("noise_seed").GetInt64());
        Assert.Equal("euler", Inputs("9").GetProperty("sampler_name").GetString());
        Assert.Equal(4, Inputs("10").GetProperty("steps").GetInt32());
        Assert.Equal(1344, Inputs("10").GetProperty("width").GetInt32());
        Assert.Equal(768, Inputs("11").GetProperty("height").GetInt32());
        Assert.Equal("EmptyFlux2LatentImage", nodes.GetProperty("11").GetProperty("class_type").GetString());
        Assert.Equal("SamplerCustomAdvanced", nodes.GetProperty("12").GetProperty("class_type").GetString());
        Assert.Equal("12", Inputs("13").GetProperty("samples")[0].GetString());
        Assert.Equal("PreviewImage", nodes.GetProperty("14").GetProperty("class_type").GetString());
        var types = nodes.EnumerateObject().Select(node => node.Value.GetProperty("class_type").GetString()).ToArray();
        Assert.DoesNotContain("LoraLoaderModelOnly", types); Assert.DoesNotContain("Krea2EditModelPatch", types);
        Assert.Equal(count * 2, types.Count(type => type == "ReferenceLatent"));
        for (var i = 0; i < count; i++)
        {
            var id = 100 + i * 5;
            Assert.Equal($"IMAGE_{i + 1}", Inputs(id.ToString()).GetProperty("image").GetString());
            Assert.Equal(1, Inputs((id + 1).ToString()).GetProperty("megapixels").GetDouble());
            Assert.Equal("lanczos", Inputs((id + 1).ToString()).GetProperty("upscale_method").GetString());
            Assert.Equal((id + 2).ToString(), Inputs((id + 3).ToString()).GetProperty("latent")[0].GetString());
            Assert.Equal(i == 0 ? "4" : (id - 2).ToString(), Inputs((id + 3).ToString()).GetProperty("conditioning")[0].GetString());
            Assert.Equal(i == 0 ? "5" : (id - 1).ToString(), Inputs((id + 4).ToString()).GetProperty("conditioning")[0].GetString());
        }
        Assert.Equal(count == 0 ? "4" : (103 + (count - 1) * 5).ToString(), Inputs("7").GetProperty("positive")[0].GetString());
        Assert.Equal(count == 0 ? "5" : (104 + (count - 1) * 5).ToString(), Inputs("7").GetProperty("negative")[0].GetString());
    }

    [Fact]
    public void DiscoveryChecksExactFilesAndInputsAndDoesNotRequireKreaOrEditNodesForCreation()
    {
        var settings = new AiSettings();
        using var json = JsonDocument.Parse(Catalog());
        var ready = ComfyFluxKleinImages.CheckCatalog(json.RootElement, settings, true);
        Assert.True(ready.Success, ready.Message);
        Assert.Single(ready.DiffusionModels); Assert.Single(ready.TextEncoders); Assert.Single(ready.Vaes);
        var missing = ComfyFluxKleinImages.CheckCatalog(json.RootElement, settings with { FluxKleinModel = "missing.safetensors" }, true);
        Assert.False(missing.Success); Assert.Contains("missing.safetensors", missing.Message);
        using var noTooling = JsonDocument.Parse(Catalog(omit: "ETN_LoadImageBase64"));
        Assert.True(ComfyFluxKleinImages.CheckCatalog(noTooling.RootElement, settings, false).Success);
        Assert.False(ComfyFluxKleinImages.CheckCatalog(noTooling.RootElement, settings, true).Success);
        using var noKv = JsonDocument.Parse(Catalog(omit: "FluxKVCache"));
        Assert.Contains("FluxKVCache", ComfyFluxKleinImages.CheckCatalog(noKv.RootElement, settings, false).Message);
        using var noLatent = JsonDocument.Parse(Catalog(omit: "ReferenceLatent.latent"));
        Assert.Contains("ReferenceLatent.latent", ComfyFluxKleinImages.CheckCatalog(noLatent.RootElement, settings, true).Message);
    }

    [Fact]
    public async Task RoutedMultiReferenceRunCapturesModelSourceOrderCropAndSettingsAcrossCandidates()
    {
        var store = new FakeAiSettingsStore(); // Explicit per-run Klein overrides the Krea default.
        var job = Guid.NewGuid().ToString();
        var handler = Handler(job);
        var service = Service(handler, store);
        var assetId = Guid.NewGuid(); var sourceId = Guid.NewGuid(); var otherAsset = Guid.NewGuid(); var otherImage = Guid.NewGuid();
        await using var first = new MemoryStream(AssetStoreTests.Png(40, 60));
        await using var second = new MemoryStream(AssetStoreTests.Jpeg(30, 20));
        var request = new ReferenceEditRequest { Workflow = ImageWorkflow.Flux2Klein9bKv, SourceAssetId = assetId, SourceImageId = sourceId,
            Prompt = "Use the person in image 1 with image 2's outfit", AspectRatio = "16:9", Count = 2, Seed = 50,
            SourceCrop = new() { X = .1, Y = .1, Width = .8, Height = .8 },
            ReferenceCrops = [new(new(otherAsset, otherImage), new() { Width = .5, Height = .5 })] };
        var results = new List<ReferenceGenerationUpdate>();
        await foreach (var update in service.EditAsync(request, [new(assetId, sourceId, first), new(otherAsset, otherImage, second)], Ct))
        {
            if (update.Image is null) continue;
            results.Add(update);
            store.Value = store.Value with { FluxKleinModel = "changed.safetensors", ComfyUrl = "http://elsewhere.invalid" };
        }
        Assert.Equal(2, results.Count);
        Assert.Equal(new long[] { 50, 51 }, results.Select(result => result.Metadata!.Seed));
        foreach (var result in results)
        {
            var meta = result.Metadata!;
            Assert.Equal(ImageWorkflow.Flux2Klein9bKv, meta.Workflow); Assert.Equal(4, meta.Steps);
            Assert.Equal(new AiSettings().FluxKleinModel, meta.DiffusionModel);
            Assert.Equal(request.SourceCrop, meta.Edit!.SourceCrop);
            Assert.Equal(request.ReferenceCrops, meta.Edit.ReferenceCrops);
            Assert.Equal(new[] { new AssetImageReference(assetId, sourceId), new(otherAsset, otherImage) }, meta.Edit.References);
            Assert.Empty(meta.Edit.Lora); Assert.Equal("reference-latent", meta.Edit.FitMode);
            Assert.Equal(AssetStoreTests.Png(3, 2), result.Image);
        }
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "/prompt"));
        var submitted = handler.Requests.First(r => r.Path == "/prompt").Body;
        using var graph = JsonDocument.Parse(submitted);
        var bytes = Convert.FromBase64String(graph.RootElement.GetProperty("prompt").GetProperty("100").GetProperty("inputs").GetProperty("image").GetString()!);
        using var prepared = SixLabors.ImageSharp.Image.Load(bytes);
        Assert.Equal(32, prepared.Width); Assert.Equal(48, prepared.Height);
        var otherBytes = Convert.FromBase64String(graph.RootElement.GetProperty("prompt").GetProperty("105").GetProperty("inputs").GetProperty("image").GetString()!);
        using var otherPrepared = SixLabors.ImageSharp.Image.Load(otherBytes);
        Assert.Equal((15, 10), (otherPrepared.Width, otherPrepared.Height));
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("upload") || r.Path.Contains("interrupt"));
    }

    [Fact]
    public async Task MissingConfiguredModelCannotSubmitAndNoFallbackOccurs()
    {
        var handler = Handler(Guid.NewGuid().ToString());
        var service = Service(handler, new() { Value = new() { DefaultImageWorkflow = ImageWorkflow.Flux2Klein9bKv, FluxKleinModel = "folder/flux-2-klein-9b-kv.safetensors" } });
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        { await foreach (var _ in service.GenerateAsync(new() { Prompt = "Mouse", AspectRatio = "1:1" }, Ct)) { } });
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/prompt");
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2, 3, false)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, 2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, 9, false)]
    public async Task InvalidReferenceSetsCannotSubmit(ImageWorkflow workflow, int count, bool duplicate)
    {
        var handler = Handler(Guid.NewGuid().ToString()); var service = Service(handler, new());
        await using var stream = new MemoryStream(AssetStoreTests.Png(2, 2));
        var asset = Guid.NewGuid(); var image = Guid.NewGuid();
        var sources = Enumerable.Range(0, count).Select(i => new ReferenceImageSource(asset, i == 0 || duplicate ? image : Guid.NewGuid(), stream)).ToArray();
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        { await foreach (var _ in service.EditAsync(new() { Workflow = workflow, SourceAssetId = asset, SourceImageId = image, Prompt = "Edit", AspectRatio = "1:1" }, sources, Ct)) { } });
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationTargetsOnlyKleinJob()
    {
        var job = Guid.NewGuid().ToString(); using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/object_info") return Json(Catalog());
            if (request.RequestUri.AbsolutePath == "/prompt") return Json($"{{\"prompt_id\":\"{job}\"}}");
            if (request.RequestUri.AbsolutePath == $"/history/{job}") { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return new(HttpStatusCode.OK);
        });
        var service = Service(handler, new());
        await Assert.ThrowsAsync<AiCancellationException>(async () =>
        { await foreach (var _ in service.GenerateAsync(new() { Workflow = ImageWorkflow.Flux2Klein9bKv, Prompt = "Mouse", AspectRatio = "1:1" }, cancel.Token)) { } });
        Assert.Contains(handler.Requests, r => r.Path == $"/api/jobs/{job}/cancel");
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("interrupt"));
    }

    [Fact]
    public async Task CompletedCandidatesRemainAvailableWhenNextJobFails()
    {
        var submits = 0; var job = Guid.NewGuid().ToString();
        var handler = new ScriptedHttpHandler((request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/object_info") return Task.FromResult(Json(Catalog()));
            if (request.RequestUri.AbsolutePath == "/prompt")
                return Task.FromResult(++submits == 2 ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Json($"{{\"prompt_id\":\"{job}\"}}"));
            return HandlerResponse(request, job);
        });
        var completed = new List<ReferenceGenerationUpdate>();
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var result in Service(handler, new()).GenerateAsync(new() { Workflow = ImageWorkflow.Flux2Klein9bKv, Prompt = "Mouse", AspectRatio = "1:1", Count = 2 }, Ct))
                if (result.Image is not null) completed.Add(result);
        });
        Assert.Single(completed); Assert.Equal(2, submits);
    }

    private static ComfyImageService Service(ScriptedHttpHandler handler, FakeAiSettingsStore store)
    {
        var clients = new TestHttpFactory(handler); var monitor = TestComfy.Monitor();
        return new(store, new(clients, store, TimeProvider.System, monitor), new(clients, store, TimeProvider.System, monitor), new(clients, TimeProvider.System, monitor), new FakeProjectAiPreferencesStore(), new FakeAssetStore(Guid.NewGuid()));
    }
    private static ScriptedHttpHandler Handler(string job) => new((request, _) => HandlerResponse(request, job));
    private static Task<HttpResponseMessage> HandlerResponse(HttpRequestMessage request, string job) => Task.FromResult(request.RequestUri!.AbsolutePath switch
    {
        "/object_info" => Json(Catalog()),
        "/prompt" => Json($"{{\"prompt_id\":\"{job}\"}}"),
        var path when path == $"/history/{job}" => Json("""{"JOB":{"status":{"completed":true,"status_str":"success"},"outputs":{"99":{"images":[{"filename":"wrong.png"}]},"14":{"images":[{"filename":"klein.png","subfolder":"","type":"temp"}]}}}}""".Replace("JOB", job)),
        "/view" when request.RequestUri.Query.Contains("klein.png") => new(HttpStatusCode.OK) { Content = new ByteArrayContent(AssetStoreTests.Png(3, 2)) },
        _ => new(HttpStatusCode.NotFound)
    });
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // Fixed node contracts from ComfyUI object_info; deliberately independent of BuildWorkflow.
    internal static string Catalog(string? omit = null)
    {
        var contracts = new Dictionary<string, string>
        {
            ["UNETLoader"] = "unet_name weight_dtype", ["CLIPLoader"] = "clip_name type device", ["VAELoader"] = "vae_name",
            ["CLIPTextEncode"] = "text clip", ["ConditioningZeroOut"] = "conditioning", ["FluxKVCache"] = "model",
            ["RandomNoise"] = "noise_seed", ["KSamplerSelect"] = "sampler_name", ["Flux2Scheduler"] = "steps width height",
            ["EmptyFlux2LatentImage"] = "width height batch_size", ["CFGGuider"] = "model positive negative cfg",
            ["SamplerCustomAdvanced"] = "noise guider sampler sigmas latent_image", ["VAEDecode"] = "samples vae", ["PreviewImage"] = "images",
            ["ETN_LoadImageBase64"] = "image", ["ImageScaleToTotalPixels"] = "image upscale_method megapixels resolution_steps",
            ["VAEEncode"] = "pixels vae", ["ReferenceLatent"] = "conditioning"
        };
        var nodes = new Dictionary<string, object>(); var settings = new AiSettings();
        foreach (var (node, names) in contracts)
        {
            if (node == omit) continue;
            var required = names.Split(' ').ToDictionary(name => name, name => (object)new object[] { "ANY", new { } });
            if (node == "UNETLoader") required["unet_name"] = new object[] { new[] { settings.FluxKleinModel, "flux-2-klein-base-9b.safetensors", "flux-2-klein-4b.safetensors", "flux-2-klein-9b.safetensors" } };
            if (node == "CLIPLoader")
            {
                required["clip_name"] = new object[] { "COMBO", new { options = new[] { settings.FluxKleinTextEncoder, "qwen_3_4b.safetensors" } } };
                required["type"] = new object[] { new[] { "flux2", "krea2" } };
            }
            if (node == "VAELoader") required["vae_name"] = new object[] { new[] { settings.FluxKleinVae, "qwen_image_vae.safetensors" } };
            var optional = new Dictionary<string, object>();
            if (node == "ReferenceLatent" && omit != "ReferenceLatent.latent") optional["latent"] = new object[] { "LATENT", new { } };
            nodes[node] = new { input = new { required, optional } };
        }
        return JsonSerializer.Serialize(nodes);
    }
}
