using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class QwenImage21Tests
{
    private static JsonObject Catalog(bool autogrow = true, int references = 16)
    {
        var result = new JsonObject();
        void Node(string name, string[] fields, string[]? outputs = null)
        {
            var inputs = new JsonObject();
            foreach (var field in fields) inputs[field] = new JsonArray(JsonValue.Create("*"));
            result[name] = new JsonObject { ["input"] = new JsonObject { ["required"] = inputs },
                ["output"] = JsonSerializer.SerializeToNode(outputs ?? []) };
        }
        void Combo(string node, string field, params string[] options) => result[node]!["input"]!["required"]![field] = new JsonArray(JsonSerializer.SerializeToNode(options));
        Node("UNETLoader", ["unet_name", "weight_dtype"]); Node("CLIPLoader", ["clip_name", "type", "device"]);
        Node("VAELoader", ["vae_name"]); Node("TextEncodeQwenImage21", ["clip", "prompt", "negative_prompt", "resolution", "vae"], ["CONDITIONING", "CONDITIONING", "LATENT"]);
        Node("KSampler", ["model", "seed", "steps", "cfg", "sampler_name", "scheduler", "denoise", "positive", "negative", "latent_image"]);
        Node("VAEDecode", ["samples", "vae"]); Node("PreviewImage", ["images"]); Node("EmptyLatentImage", ["width", "height", "batch_size"]);
        Node("QwenImage21Cache", ["model", "device", "dtype"]); Node("ETN_LoadImageBase64", ["image"]);
        Node("ImageToMask", ["image", "channel"]); Node("JoinImageWithAlpha", ["image", "alpha"]);
        Combo("UNETLoader", "unet_name", Settings.QwenImage21!.Model);
        Combo("CLIPLoader", "clip_name", Settings.QwenImage21!.TextEncoder);
        Combo("CLIPLoader", "type", "stable_diffusion", "qwen_image");
        Combo("VAELoader", "vae_name", Settings.QwenImage21!.Vae);
        Combo("KSampler", "sampler_name", "euler"); Combo("KSampler", "scheduler", "simple");
        Combo("QwenImage21Cache", "device", "auto", "gpu", "cpu", "off"); Combo("QwenImage21Cache", "dtype", "default", "int8", "int4");
        var encoding = (JsonObject)result["TextEncodeQwenImage21"]!["input"]!["required"]!;
        if (autogrow)
            encoding["images"] = new JsonArray(JsonValue.Create("COMFY_AUTOGROW_V3"), new JsonObject
            {
                ["template"] = new JsonObject { ["min"] = 0,
                    ["names"] = JsonSerializer.SerializeToNode(Enumerable.Range(1, references).Select(i => "image_" + i).ToArray()),
                    ["input"] = new JsonObject { ["required"] = new JsonObject { ["image"] = new JsonArray(JsonValue.Create("IMAGE")) } } }
            });
        else
            for (var i = 1; i <= references; i++) encoding["images.image_" + i] = new JsonArray(JsonValue.Create("IMAGE"));
        return result;
    }
    private static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void CatalogAcceptsNativeAutogrowOrFlattenedImageSockets(bool autogrow)
    {
        var catalog = Element(Catalog(autogrow));
        Assert.True(ComfyQwenImage21.CheckCatalog(catalog, Settings, true).Success);
        Assert.True(ComfyQwenImage21.SupportsReference(catalog, 10));
        Assert.False(ComfyQwenImage21.SupportsReference(catalog, 11));
        Assert.False(ComfyQwenImage21.SupportsReference(catalog, 0));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void MissingTenthSocketIsNotReportedAsTenInputSupport(bool autogrow)
    {
        var result = ComfyQwenImage21.CheckCatalog(Element(Catalog(autogrow, 9)), Settings, true);
        Assert.False(result.Success); Assert.Contains("images.image_10", result.Message);
    }

    [Theory]
    [InlineData("TextEncodeQwenImage21")]
    [InlineData("QwenImage21Cache")]
    [InlineData("ETN_LoadImageBase64")]
    [InlineData("ImageToMask")]
    [InlineData("JoinImageWithAlpha")]
    public void MissingEditNodesProduceAnActionableError(string name)
    {
        var catalog = Catalog(); catalog.Remove(name);
        var result = ComfyQwenImage21.CheckCatalog(Element(catalog), Settings, true);
        Assert.False(result.Success); Assert.Contains(name, result.Message);
    }

    [Fact]
    public void CreateDoesNotRequireEditTransportOrCacheNodes()
    {
        var catalog = Catalog();
        foreach (var name in new[] { "QwenImage21Cache", "ETN_LoadImageBase64", "ImageToMask", "JoinImageWithAlpha" }) catalog.Remove(name);
        Assert.True(ComfyQwenImage21.CheckCatalog(Element(catalog), Settings, false).Success);
        Assert.False(ComfyQwenImage21.CheckCatalog(Element(catalog), Settings, true).Success);
    }

    [Fact]
    public void MissingConfiguredVaeDoesNotSilentlyChooseTheOlderVae()
    {
        var catalog = Catalog();
        catalog["VAELoader"]!["input"]!["required"]!["vae_name"] = new JsonArray(new JsonArray(JsonValue.Create("qwen_image_vae.safetensors")));
        var result = ComfyQwenImage21.CheckCatalog(Element(catalog), Settings, false);
        Assert.False(result.Success); Assert.Contains(Settings.QwenImage21!.Vae, result.Message);
        Assert.Equal("qwen_image_vae.safetensors", Assert.Single(result.Vaes).Id);
    }

    [Fact]
    public void EncoderMustReturnItsNativeLatent()
    {
        var catalog = Catalog(); catalog["TextEncodeQwenImage21"]!["output"] = JsonSerializer.SerializeToNode(new[] { "CONDITIONING", "CONDITIONING" });
        Assert.False(ComfyQwenImage21.CheckCatalog(Element(catalog), Settings, true).Success);
    }

    [Theory]
    [InlineData("auto", "default")] [InlineData("gpu", "int8")]
    [InlineData("cpu", "int4")] [InlineData("off", "default")]
    public void CacheChoicesAreCapturedAndValidated(string device, string dtype)
    {
        var settings = Settings with { QwenImage21 = Settings.QwenImage21! with { CacheDevice = device, CacheDtype = dtype } };
        FileAiSettingsStore.Validate(settings);
        Assert.True(ComfyQwenImage21.CheckCatalog(Element(Catalog()), settings, true).Success);
        var graph = Build(Request(1) with { Settings = settings });
        Assert.Equal(device, Inputs(graph, "6").GetProperty("device").GetString());
        Assert.Equal(dtype, Inputs(graph, "6").GetProperty("dtype").GetString());
    }

    [Fact]
    public void SettingsRejectInvalidCacheAndNormalizeOnlyTheNewModelFields()
    {
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(Settings with { QwenImage21 = new() { CacheDevice = "anywhere" } }));
        Assert.Throws<WorkspaceStoreException>(() => FileAiSettingsStore.Validate(Settings with { QwenImage21 = new() { Vae = "" } }));
        var normalized = QwenImage21Policy.Normalize(new() { Model = " folder/model.safetensors ", TextEncoder = " encoder.safetensors ", Vae = " vae.safetensors " });
        Assert.Equal("folder/model.safetensors", normalized.Model);
        Assert.Equal("encoder.safetensors", normalized.TextEncoder); Assert.Equal("vae.safetensors", normalized.Vae);
        Assert.Equal("krea2_turbo_int8_convrot.safetensors", Settings.ComfyImageModel);
    }

    [Fact]
    public void ModelLoraIsAppliedBeforeTheQwenCache()
    {
        var request = Request(1);
        var lora = new AppliedLora(new LoraReference(Settings.ComfyUrl, "qwen-style.safetensors", LoraWorkflow.QwenImage21, "Qwen style"), .8f);
        var size = QwenImage21Policy.OutputSize(request);
        var graph = Graph(ComfyQwenImage21.BuildWorkflow(Settings, request.Prompt, 42, size.Width, size.Height,
            request.Inputs.Select(i => Convert.ToBase64String(i.Png)).ToArray(), new(), loras: [lora]));
        var model = Inputs(graph, "6").GetProperty("model")[0].GetString()!;
        Assert.Equal("LoraLoaderModelOnly", graph.GetProperty(model).GetProperty("class_type").GetString());
        Assert.Equal("1", Inputs(graph, model).GetProperty("model")[0].GetString());
        Assert.Throws<AiGenerationException>(() => ComfyQwenImage21.BuildWorkflow(Settings, "Create", 1, 1024, 1024, [], new(),
            loras: [lora with { Reference = lora.Reference with { Workflow = LoraWorkflow.Krea2 } }]));
    }
}
