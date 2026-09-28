using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.AI;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lumibelle.Tests;

public sealed partial class LoraTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CancellingCatalogPreflightDoesNotSubmitAJob()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var handler = new ScriptedHttpHandler(async (_, ct) => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); return new(HttpStatusCode.OK); });
        var definition = Definition();
        var service = Service(handler, new() { Value = new() { LoraLibrary = [definition] } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in service.GenerateAsync(new() { Prompt = "Image", AspectRatio = "1:1", Loras = [new(definition.Reference)] }, cancel.Token)) { } });
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/prompt");
    }
    [Theory]
    [InlineData("{}")] [InlineData("not json")]
    [InlineData("{\"LoraLoaderModelOnly\":{\"input\":{\"required\":{\"model\":[],\"lora_name\":[],\"strength_model\":[]}}}}")]
    public async Task MalformedCatalogBecomesAnActionableUnavailableResult(string json)
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(Json(json)));
        var result = await new ComfyLoraCatalog(new TestHttpFactory(handler)).CheckAsync(new(), Ct);
        Assert.False(result.Success); Assert.NotEmpty(result.Message); Assert.Empty(result.Files);
    }
    internal static LoraDefinition Definition(ImageWorkflow workflow = ImageWorkflow.Krea2, string file = "character.safetensors", string name = "Character") =>
        new(new(new AiSettings().ComfyUrl, file, workflow, name), .8f, "character trigger");
    internal static string Catalog(string? missing = null)
    {
        var node = JsonNode.Parse("""{"LoraLoaderModelOnly":{"input":{"required":{"model":["MODEL"],"lora_name":[["character.safetensors","styles/ink.safetensors"]],"strength_model":["FLOAT",{"min":-2,"max":2}]}}}}""")!;
        if (missing == "node") node.AsObject().Clear();
        else if (missing == "file") node["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"] = new JsonArray(new JsonArray("other.safetensors"));
        else if (missing is not null) node["LoraLoaderModelOnly"]!["input"]!["required"]!.AsObject().Remove(missing);
        return node.ToJsonString();
    }
    private static JsonElement Graph(ImageWorkflow workflow, bool editing, IReadOnlyList<LoraSelection> loras)
    {
        var settings = new AiSettings(); var applied = loras.Where(s => s.Enabled && s.Strength != 0).Select(s => new AppliedLora(s.Reference, s.Strength)).ToArray();
        object graph = workflow == ImageWorkflow.Flux2Klein9bKv ? ComfyFluxKleinImages.BuildWorkflow(settings, "Unchanged prompt", 22, 1024, 1024, editing ? ["base", "other"] : [], loras: applied) :
            editing ? ComfyReferenceImageEditor.BuildWorkflow(settings, new ReferenceEditRequest { SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Unchanged prompt", AspectRatio = "1:1", Loras = loras }, new[] { "base", "other" }, 22, 1024, 1024) :
            ComfyReferenceImageGenerator.BuildWorkflow(settings, "Unchanged prompt", 22, 1024, 1024, loras: applied);
        return JsonSerializer.SerializeToElement(graph).GetProperty("prompt");
    }
    [Theory]
    [InlineData(ImageWorkflow.Krea2, false)] [InlineData(ImageWorkflow.Krea2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false)] [InlineData(ImageWorkflow.Flux2Klein9bKv, true)]
    public void GraphsChainOptionalLorasWithoutChangingConditioning(ImageWorkflow workflow, bool editing)
    {
        var first = new LoraSelection(Definition(workflow).Reference, .65f);
        var second = new LoraSelection(Definition(workflow, "styles/ink.safetensors").Reference, -1.25f);
        foreach (var selections in new IReadOnlyList<LoraSelection>[] { [], [first], [second, first], [first with { Enabled = false }, second with { Strength = 0 }] })
        {
            var graph = Graph(workflow, editing, selections); var enabled = selections.Where(s => s.Enabled && s.Strength != 0).ToArray();
            var input = editing && workflow == ImageWorkflow.Krea2 ? "4" : "1";
            for (var i = 0; i < enabled.Length; i++)
            {
                var node = graph.GetProperty($"lora_{i + 1}"); Assert.Equal("LoraLoaderModelOnly", node.GetProperty("class_type").GetString());
                var args = node.GetProperty("inputs"); Assert.Equal(input, args.GetProperty("model")[0].GetString());
                Assert.Equal(enabled[i].Reference.FileName, args.GetProperty("lora_name").GetString()); Assert.Equal(enabled[i].Strength, args.GetProperty("strength_model").GetSingle());
                Assert.False(args.TryGetProperty("clip", out _)); input = $"lora_{i + 1}";
            }
            Assert.Equal(enabled.Length, graph.EnumerateObject().Count(n => n.Name.StartsWith("lora_")));
            var consumer = workflow == ImageWorkflow.Flux2Klein9bKv ? "6" : editing ? "8" : "7";
            Assert.Equal(input, graph.GetProperty(consumer).GetProperty("inputs").GetProperty("model")[0].GetString());
            if (editing && workflow == ImageWorkflow.Krea2)
            {
                Assert.Equal(1, graph.GetProperty("4").GetProperty("inputs").GetProperty("strength_model").GetSingle());
                Assert.Equal("14", graph.GetProperty("9").GetProperty("inputs").GetProperty("image_b")[0].GetString());
                Assert.Equal("14", graph.GetProperty("10").GetProperty("inputs").GetProperty("image_b")[0].GetString());
            }
            var encoder = graph.GetProperty(editing && workflow == ImageWorkflow.Krea2 ? "9" : "4").GetProperty("inputs");
            Assert.Equal("2", encoder.GetProperty("clip")[0].GetString());
            Assert.Equal("Unchanged prompt", encoder.GetProperty(editing && workflow == ImageWorkflow.Krea2 ? "prompt" : "text").GetString());
        }
    }

    [Theory]
    [InlineData(ImageWorkflow.Krea2, false)] [InlineData(ImageWorkflow.Krea2, true)]
    [InlineData(ImageWorkflow.Flux2Klein9bKv, false)] [InlineData(ImageWorkflow.Flux2Klein9bKv, true)]
    public async Task BatchCapturesSelectionsAndSettingsBeforeFirstYield(ImageWorkflow workflow, bool editing)
    {
        var definition = Definition(workflow); var definitions = new List<LoraDefinition> { definition };
        var selections = new List<LoraSelection> { new(definition.Reference, .7f) };
        var settings = new FakeAiSettingsStore { Value = new() { LoraLibrary = definitions } };
        var handler = Handler(workflow, editing); var service = Service(handler, settings);
        using var bytes = new MemoryStream(AssetStoreTests.Png(20, 30));
        var updates = editing ? service.EditAsync(new() { Workflow = workflow, Loras = selections, SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Exact instruction", AspectRatio = "1:1", Count = 2, Seed = 11 }, bytes, Ct) :
            service.GenerateAsync(new() { Workflow = workflow, Loras = selections, Prompt = "Exact instruction", AspectRatio = "1:1", Count = 2, Seed = 11 }, Ct);
        var results = new List<AssetGenerationMetadata>();
        await foreach (var update in updates)
        {
            selections.Clear(); definitions.Clear(); settings.Value = new() { ComfyUrl = "http://changed.invalid" };
            if (update.Metadata is { } metadata) results.Add(metadata);
        }
        Assert.Equal(2, results.Count); Assert.Equal(new long[] { 11, 12 }, results.Select(m => m.Seed));
        Assert.All(results, m => { Assert.Equal("Exact instruction", m.Prompt); Assert.Equal(new AppliedLora(definition.Reference, .7f), Assert.Single(m.Loras)); });
        Assert.Single(handler.Requests, r => r.Path == "/object_info/LoraLoaderModelOnly");
        foreach (var submitted in handler.Requests.Where(r => r.Path == "/prompt"))
        { var graph = JsonNode.Parse(submitted.Body)!["prompt"]!; Assert.Equal("character.safetensors", graph["lora_1"]!["inputs"]!["lora_name"]!.GetValue<string>()); }
    }

    [Theory]
    [InlineData("file")] [InlineData("node")] [InlineData("model")] [InlineData("lora_name")] [InlineData("strength_model")]
    [InlineData("server")] [InlineData("workflow")] [InlineData("registration")] [InlineData("duplicate")] [InlineData("strength")] [InlineData("nan")]
    public async Task InvalidOrUnavailableLoraNeverSubmits(string invalid)
    {
        var definition = Definition(); var selection = new LoraSelection(definition.Reference, invalid == "nan" ? float.NaN : invalid == "strength" ? 3 : 1);
        if (invalid == "server") selection = selection with { Reference = selection.Reference with { ComfyUrl = "http://other.invalid" } };
        if (invalid == "workflow") selection = selection with { Reference = selection.Reference with { Workflow = LoraWorkflow.Flux2Klein9bKv } };
        var handler = Handler(ImageWorkflow.Krea2, false, invalid is "file" or "node" or "model" or "lora_name" or "strength_model" ? invalid : null);
        var settings = new FakeAiSettingsStore { Value = new() { LoraLibrary = invalid == "registration" ? [] : [definition] } };
        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        { await foreach (var _ in Service(handler, settings).GenerateAsync(new() { Prompt = "Image", AspectRatio = "1:1", Loras = invalid == "duplicate" ? [selection, selection] : [selection] }, Ct)) { } });
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/prompt");
    }
    [Fact]
    public async Task DisabledMissingFilesNeedNoLoaderAndProduceNoAppliedMetadata()
    {
        var handler = Handler(ImageWorkflow.Krea2, false, "node");
        await foreach (var update in Service(handler, new()).GenerateAsync(new() { Prompt = "Image", AspectRatio = "1:1", Loras = [new(Definition().Reference, Enabled: false)] }, Ct))
            if (update.Metadata is { } metadata) Assert.Empty(metadata.Loras);
        Assert.DoesNotContain(handler.Requests, r => r.Path == "/object_info/LoraLoaderModelOnly");
    }
    internal static ComfyImageService Service(ScriptedHttpHandler handler, FakeAiSettingsStore settings, FakeProjectAiPreferencesStore? preferences = null)
    {
        var clients = new TestHttpFactory(handler); var monitor = TestComfy.Monitor();
        return new(settings, new(clients, settings, TimeProvider.System, monitor), new(clients, settings, TimeProvider.System, monitor), new(clients, TimeProvider.System, monitor), preferences ?? new(), new FakeAssetStore(Guid.NewGuid()));
    }
    private static ScriptedHttpHandler Handler(ImageWorkflow workflow, bool editing, string? missing = null)
    {
        var job = Guid.NewGuid().ToString(); var output = workflow == ImageWorkflow.Flux2Klein9bKv ? "14" : editing ? "13" : "9";
        return new((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/object_info/LoraLoaderModelOnly" => Json(Catalog(missing)),
            "/object_info" => Json(workflow == ImageWorkflow.Krea2 ? AssetAiTests.EditCatalog() : FluxKleinTests.Catalog()),
            "/prompt" => Json($$"""{"prompt_id":"{{job}}"}"""),
            var path when path == $"/history/{job}" => Json("""{"JOB":{"status":{"completed":true,"status_str":"success"},"outputs":{"OUTPUT":{"images":[{"filename":"take.png","subfolder":"","type":"temp"}]}}}}""".Replace("JOB", job).Replace("OUTPUT", output)),
            "/view" => new(HttpStatusCode.OK) { Content = new ByteArrayContent(AssetStoreTests.Png(4, 3)) },
            _ => new(HttpStatusCode.NotFound)
        }));
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
}

internal sealed class FakeLoraCatalog : IComfyLoraCatalog
{
    public ComfyLoraCheck Value { get; set; } = new(true, "Mock LoRA catalog ready.", ["character.safetensors", "styles/ink.safetensors", "klein/style.safetensors"]);
    public List<AiSettings> Requests { get; } = [];
    public Task<ComfyLoraCheck> CheckAsync(AiSettings settings, CancellationToken cancellationToken = default)
    { Requests.Add(settings); return Task.FromResult(Value); }
}
