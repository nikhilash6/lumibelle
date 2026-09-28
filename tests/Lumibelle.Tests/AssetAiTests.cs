using System.Net;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using lumibelle.Services.Assets;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png.Chunks;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Lumibelle.Tests;

public sealed class AssetAiTests
{
    [Fact]
    public void ExtractionContextIncludesOnlySelectedWritingAndExistingAssets()
    {
        var included = ScriptBlock.Create(ScriptBlockKind.Scene, "Forest");
        var excluded = ScriptBlock.Create(ScriptBlockKind.Scene, "Home");
        var story = ScriptFixtures.Approved([included, ScriptBlock.Create(ScriptBlockKind.Action, "SELECTED BODY"), excluded, ScriptBlock.Create(ScriptBlockKind.Action, "EXCLUDED BODY")]);
        var library = new AssetLibrary { ProjectId = story.ProjectId, Assets = [Asset("Mira")] };
        var request = new AssetExtractionRequest(story, library, [included.Id], AiBackend.OpenRouter, "test/model");
        var text = string.Join("\n", AssetExtractor.BuildMessages(request).Select(message => message.Text));
        Assert.Contains("SELECTED BODY", text); Assert.DoesNotContain("MAIN STORY", text); Assert.DoesNotContain("EXCLUDED BODY", text);
        Assert.Contains("Mira", text); Assert.Contains(library.Assets[0].Id.ToString(), text);
        var shortEstimate = AssetExtractor.EstimateInputTokens(request);
        Assert.True(AssetExtractor.EstimateInputTokens(request with { SceneIds = [included.Id, excluded.Id] }) > shortEstimate);
    }

    [Fact]
    public void ExtractionParsesReviewableCreateAndMergeProposals()
    {
        var scene = ScriptBlock.Create(ScriptBlockKind.Scene, "Arrival");
        var story = ScriptFixtures.Approved([scene, ScriptBlock.Create(ScriptBlockKind.Action, "Mira arrives.")]);
        var existing = Asset("Mira"); var library = new AssetLibrary { ProjectId = story.ProjectId, Assets = [existing] };
        var request = new AssetExtractionRequest(story, library, [scene.Id], AiBackend.OpenRouter, "model");
        var json = $$"""
        [{"category":"character","name":" Mira ","description":"Short dark hair.","suggestedTags":[" face ","face","full body"],"evidence":[{"label":"Arrival","sceneId":"{{scene.Id}}","excerpt":"Mira arrives."}],"matchedAssetId":"{{existing.Id}}"},
         {"category":"environment","name":"Station","description":"An old station.","suggestedTags":["wide view"],"evidence":[{"label":"Arrival","sceneId":"{{scene.Id}}","excerpt":"the station"}],"matchedAssetId":null}]
        """;
        var proposals = AssetExtractor.Parse(json, request)!;
        Assert.Equal(ExtractionDecision.Merge, proposals[0].Decision); Assert.Equal("Mira", proposals[0].Name);
        Assert.Equal(new[] { "face", "full body" }, proposals[0].SuggestedTags);
        Assert.Equal(ExtractionDecision.Create, proposals[1].Decision);
        Assert.All(proposals.SelectMany(p => p.Evidence), e => Assert.Equal(story.Id, e.ApprovedScriptId));
    }

    [Fact]
    public void ExtractionRejectsMalformedOrInventedReferences()
    {
        var story = ScriptFixtures.Approved([]);
        var request = new AssetExtractionRequest(story, new() { ProjectId = story.ProjectId }, [], AiBackend.OpenRouter, "model");
        Assert.Null(AssetExtractor.Parse("not json", request));
        Assert.Null(AssetExtractor.Parse("[{\"category\":\"character\",\"name\":\"Mira\",\"description\":\"Visible\",\"suggestedTags\":[],\"evidence\":[],\"matchedAssetId\":\"" + Guid.NewGuid() + "\"}]", request));
        Assert.Null(AssetExtractor.Parse("[{\"category\":\"character\",\"name\":\"Mira\",\"description\":\"Visible\",\"suggestedTags\":[null],\"evidence\":[],\"matchedAssetId\":null}]", request));
    }

    [Fact]
    public void KreaWorkflowUsesLocalOfficialDefaultsAndExactSettings()
    {
        var settings = new AiSettings { ComfyImageModel = "krea2_turbo_int8_convrot.safetensors", ComfyImageTextEncoder = "qwen3vl_4b_fp8_scaled.safetensors", ComfyImageVae = "qwen_image_vae.safetensors" };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ComfyReferenceImageGenerator.BuildWorkflow(settings, "A face", 42, 832, 1216)));
        var nodes = json.RootElement.GetProperty("prompt");
        Assert.Equal("UNETLoader", nodes.GetProperty("1").GetProperty("class_type").GetString());
        Assert.Equal("krea2", nodes.GetProperty("2").GetProperty("inputs").GetProperty("type").GetString());
        Assert.Equal("A face", nodes.GetProperty("4").GetProperty("inputs").GetProperty("text").GetString());
        Assert.Equal(832, nodes.GetProperty("6").GetProperty("inputs").GetProperty("width").GetInt32());
        var sampler = nodes.GetProperty("7").GetProperty("inputs");
        Assert.Equal(42, sampler.GetProperty("seed").GetInt64()); Assert.Equal(8, sampler.GetProperty("steps").GetInt32());
        Assert.Equal("euler", sampler.GetProperty("sampler_name").GetString()); Assert.Equal("simple", sampler.GetProperty("scheduler").GetString());
        Assert.Equal("PreviewImage", nodes.GetProperty("9").GetProperty("class_type").GetString());
        Assert.DoesNotContain("TextGenerate", nodes.EnumerateObject().Select(node => node.Value.GetProperty("class_type").GetString()));
    }

    [Fact]
    public void KreaEditWorkflowEmbedsRawSourceAndUsesCurrentIdentityGraph()
    {
        var settings = new AiSettings();
        var assetId = Guid.NewGuid(); var imageId = Guid.NewGuid();
        var request = new ReferenceEditRequest
        {
            SourceAssetId = assetId, SourceImageId = imageId, Prompt = "Change the coat to blue",
            AspectRatio = "16:9", ReferenceBoost = 5.25f, GroundingPixels = 896
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            ComfyReferenceImageEditor.BuildWorkflow(settings, request, "RAW_BASE64", 77, 1344, 768, "edit-client")));
        var root = json.RootElement; var nodes = root.GetProperty("prompt");

        Assert.Equal("edit-client", root.GetProperty("client_id").GetString());
        Assert.Equal("ETN_LoadImageBase64", nodes.GetProperty("5").GetProperty("class_type").GetString());
        Assert.Equal("RAW_BASE64", nodes.GetProperty("5").GetProperty("inputs").GetProperty("image").GetString());
        Assert.DoesNotContain("LoadImage", nodes.EnumerateObject().Select(node => node.Value.GetProperty("class_type").GetString()));
        Assert.Equal(settings.ComfyImageEditLora, nodes.GetProperty("4").GetProperty("inputs").GetProperty("lora_name").GetString());
        Assert.Equal(1f, nodes.GetProperty("4").GetProperty("inputs").GetProperty("strength_model").GetSingle());
        var patch = nodes.GetProperty("8").GetProperty("inputs");
        Assert.Equal("7", patch.GetProperty("target_latent")[0].GetString());
        Assert.Equal("fit", patch.GetProperty("fit_mode").GetString()); Assert.Equal(5.25f, patch.GetProperty("ref_boost").GetSingle());
        Assert.Equal("Change the coat to blue", nodes.GetProperty("9").GetProperty("inputs").GetProperty("prompt").GetString());
        Assert.Equal("", nodes.GetProperty("10").GetProperty("inputs").GetProperty("prompt").GetString());
        Assert.Equal(896, nodes.GetProperty("9").GetProperty("inputs").GetProperty("grounding_px").GetInt32());
        Assert.Equal(1344, nodes.GetProperty("7").GetProperty("inputs").GetProperty("width").GetInt32());
        Assert.Equal(768, nodes.GetProperty("7").GetProperty("inputs").GetProperty("height").GetInt32());
        var sampler = nodes.GetProperty("11").GetProperty("inputs");
        Assert.Equal(77, sampler.GetProperty("seed").GetInt64()); Assert.Equal(10, sampler.GetProperty("steps").GetInt32());
        Assert.Equal(1f, sampler.GetProperty("cfg").GetSingle()); Assert.Equal("euler", sampler.GetProperty("sampler_name").GetString());
        Assert.Equal("simple", sampler.GetProperty("scheduler").GetString()); Assert.Equal(1f, sampler.GetProperty("denoise").GetSingle());
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("webp")]
    public async Task EditSourceIsDecodedAndReencodedAsRawPng(string format)
    {
        var bytes = format switch
        {
            "jpeg" => AssetStoreTests.Jpeg(40, 20),
            "webp" => AssetStoreTests.WebP(40, 20),
            _ => AssetStoreTests.Png(40, 20)
        };
        await using var source = new MemoryStream(bytes);
        var normalized = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source,
            TestContext.Current.CancellationToken);
        var info = Image.Identify(normalized);
        Assert.NotNull(info); Assert.Equal("PNG", info!.Metadata.DecodedImageFormat!.Name);
        Assert.Equal((40, 20), (info.Width, info.Height));
        var rawBase64 = Convert.ToBase64String(normalized);
        Assert.DoesNotContain("data:", rawBase64, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(normalized, Convert.FromBase64String(rawBase64));
    }

    [Fact]
    public async Task EditSourceIsDownscaledToAtMostTwoMegapixels()
    {
        await using var source = new MemoryStream(AssetStoreTests.Png(3000, 1000));
        var normalized = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source,
            TestContext.Current.CancellationToken);
        var info = Image.Identify(normalized)!;
        Assert.True((long)info.Width * info.Height <= ComfyReferenceImageEditor.MaximumSourcePixels);
        Assert.InRange(info.Width / (double)info.Height, 2.99, 3.01);
    }

    [Fact]
    public async Task EditSourceDropsLargeCompressedPngTextMetadata()
    {
        using var image = new Image<Rgba32>(40, 20);
        image.Metadata.GetPngMetadata().TextData.Add(new PngTextData(
            "workflow", new string('x', 2 * 1024 * 1024), string.Empty, string.Empty));
        await using var source = new MemoryStream();
        await image.SaveAsPngAsync(source, TestContext.Current.CancellationToken);
        source.Position = 0;

        var normalized = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source,
            TestContext.Current.CancellationToken);

        using var decoded = Image.Load(normalized);
        Assert.Empty(decoded.Metadata.GetPngMetadata().TextData);
        Assert.Equal((40, 20), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task EditSourceCropIsAppliedBeforeDownscaling()
    {
        await using var source = new MemoryStream(AssetStoreTests.Png(400, 200));
        var crop = new ImageCropRegion { X = .25, Y = 0, Width = .5, Height = 1 };

        var normalized = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source, crop,
            TestContext.Current.CancellationToken);

        var info = Image.Identify(normalized)!;
        Assert.Equal((200, 200), (info.Width, info.Height));
    }

    [Fact]
    public async Task InvalidEditSourceCropFailsBeforeWorkflowSubmission()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), new FakeAiSettingsStore(),
            TimeProvider.System, TestComfy.Monitor());
        await using var source = new MemoryStream(AssetStoreTests.Png(20, 20));
        var request = new ReferenceEditRequest
        {
            SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Change it",
            AspectRatio = "1:1", SourceCrop = new() { X = .75, Y = 0, Width = .5, Height = 1 }
        };

        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var _ in editor.EditAsync(request, source, TestContext.Current.CancellationToken)) { }
        });
        Assert.Contains("outside", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EditSourceAppliesExifOrientationBeforeEncoding()
    {
        using var image = new Image<Rgba32>(40, 20);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        await using var source = new MemoryStream();
        await image.SaveAsJpegAsync(source, TestContext.Current.CancellationToken);
        source.Position = 0;

        var normalized = await ComfyReferenceImageEditor.PrepareSourcePngAsync(source,
            TestContext.Current.CancellationToken);
        var info = Image.Identify(normalized)!;
        Assert.Equal((20, 40), (info.Width, info.Height));
    }

    [Fact]
    public async Task MalformedEditSourceFailsBeforeWorkflowSubmission()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), new FakeAiSettingsStore(),
            TimeProvider.System, TestComfy.Monitor());
        await using var source = new MemoryStream("not an image"u8.ToArray());
        var request = new ReferenceEditRequest
        {
            SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Change it",
            AspectRatio = "1:1"
        };

        var error = await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var _ in editor.EditAsync(request, source, TestContext.Current.CancellationToken)) { }
        });
        Assert.Contains("could not be decoded", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task EditDiscoveryRequiresToolingAndCurrentTargetLatentAndFindsIdentityLoras()
    {
        var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(JsonResponse(EditCatalog(includeBase64: true, includeTarget: true))));
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), new FakeAiSettingsStore(),
            TimeProvider.System, TestComfy.Monitor());
        var ready = await editor.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(ready.Success); Assert.Contains(ready.Loras, model => model.Id == "krea2_identity_edit_v1_2.safetensors");
        Assert.DoesNotContain(ready.Loras, model => model.Id == "other.safetensors");

        handler = new ScriptedHttpHandler((_, _) => Task.FromResult(JsonResponse(EditCatalog(includeBase64: false, includeTarget: true))));
        editor = new(new TestHttpFactory(handler), new FakeAiSettingsStore(), TimeProvider.System, TestComfy.Monitor());
        var missingTooling = await editor.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(missingTooling.Success); Assert.Contains("comfyui-tooling-nodes", missingTooling.Message);

        handler = new ScriptedHttpHandler((_, _) => Task.FromResult(JsonResponse(EditCatalog(includeBase64: true, includeTarget: false))));
        editor = new(new TestHttpFactory(handler), new FakeAiSettingsStore(), TimeProvider.System, TestComfy.Monitor());
        var outdated = await editor.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(outdated.Success); Assert.Contains("target_latent", outdated.Message);

        handler = new ScriptedHttpHandler((_, _) => Task.FromResult(JsonResponse(
            EditCatalog(includeBase64: true, includeTarget: true, includeConfiguredBaseFiles: false))));
        editor = new(new TestHttpFactory(handler), new FakeAiSettingsStore(), TimeProvider.System, TestComfy.Monitor());
        var missingBaseModel = await editor.CheckAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(missingBaseModel.Success); Assert.Contains("unavailable", missingBaseModel.Message);
    }

    [Fact]
    public async Task ComfyGenerationExtractsOnlySubmittedOutputAndDownloadsCandidate()
    {
        var id = Guid.NewGuid().ToString("D");
        var png = AssetStoreTests.Png(3, 2);
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/prompt") return Task.FromResult(JsonResponse($"{{\"prompt_id\":\"{id}\"}}"));
            if (request.RequestUri!.AbsolutePath == $"/history/{id}") return Task.FromResult(JsonResponse($"{{\"other\":{{\"outputs\":{{\"9\":{{\"images\":[]}}}}}},\"{id}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"9\":{{\"images\":[{{\"filename\":\"take.png\",\"subfolder\":\"\",\"type\":\"temp\"}}]}}}}}}}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        });
        var settings = new FakeAiSettingsStore();
        var generator = new ComfyReferenceImageGenerator(new TestHttpFactory(handler), settings, TimeProvider.System, TestComfy.Monitor());
        var updates = new List<ReferenceGenerationUpdate>();
        await foreach (var update in generator.GenerateAsync(new() { Prompt = "Mira portrait", AspectRatio = "2:3", Seed = 100 }, TestContext.Current.CancellationToken)) updates.Add(update);
        Assert.Equal(new[] { "Connecting to ComfyUI…", "Submitting to ComfyUI…", "Queued in ComfyUI…", "Finalizing ComfyUI result…", "Downloading candidate…", "Saving candidate…" }, updates.Select(update => update.Status));
        var complete = updates[^1]; Assert.Equal(png, complete.Image); Assert.Equal(100, complete.Metadata!.Seed); Assert.Equal("Mira portrait", complete.Metadata.Prompt);
        Assert.Contains(handler.Requests, request => request.Path == $"/history/{id}"); Assert.Contains(handler.Requests, request => request.Path == "/view");
    }

    [Fact]
    public async Task ComfyCancellationTargetsOnlySubmittedImageJob()
    {
        var id = Guid.NewGuid().ToString("D"); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/prompt") return JsonResponse($"{{\"prompt_id\":\"{id}\"}}");
            if (request.RequestUri!.AbsolutePath == $"/history/{id}") { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return new(HttpStatusCode.OK);
        });
        var generator = new ComfyReferenceImageGenerator(new TestHttpFactory(handler), new FakeAiSettingsStore(), TimeProvider.System, TestComfy.Monitor());
        await Assert.ThrowsAsync<AiCancellationException>(async () => { await foreach (var _ in generator.GenerateAsync(new() { Prompt = "Mira", AspectRatio = "1:1" }, cancellation.Token)) { } });
        Assert.Contains(handler.Requests, request => request.Path == $"/api/jobs/{id}/cancel");
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("interrupt"));
    }

    [Fact]
    public async Task ComfyEditDownloadsOnlyItsPreviewAndRetainsLineage()
    {
        var id = Guid.NewGuid().ToString("D"); var png = AssetStoreTests.Png(7, 4);
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/object_info") return Task.FromResult(JsonResponse(EditCatalog(true, true)));
            if (request.RequestUri!.AbsolutePath == "/prompt") return Task.FromResult(JsonResponse($"{{\"prompt_id\":\"{id}\"}}"));
            if (request.RequestUri.AbsolutePath == $"/history/{id}") return Task.FromResult(JsonResponse(
                $"{{\"{id}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"13\":{{\"images\":[{{\"filename\":\"edit.png\",\"subfolder\":\"\",\"type\":\"temp\"}}]}}}}}}}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        });
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), new FakeAiSettingsStore(),
            TimeProvider.System, TestComfy.Monitor());
        var request = new ReferenceEditRequest
        {
            SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Blue coat",
            AspectRatio = "16:9", Seed = 20, ReferenceBoost = 4, GroundingPixels = 768,
            SourceCrop = new() { X = .1, Y = .2, Width = .7, Height = .6 }
        };
        var updates = new List<ReferenceGenerationUpdate>();
        await using var source = new MemoryStream(AssetStoreTests.Jpeg(30, 50));
        await foreach (var update in editor.EditAsync(request, source, TestContext.Current.CancellationToken)) updates.Add(update);

        var complete = updates[^1]; Assert.Equal(png, complete.Image); Assert.Equal(20, complete.Metadata!.Seed);
        Assert.Equal(request.SourceImageId, complete.Metadata.Edit!.SourceImageId);
        Assert.Equal(request.SourceCrop, complete.Metadata.Edit.SourceCrop);
        Assert.Equal("krea2_identity_edit_v1_2.safetensors", complete.Metadata.Edit.Lora);
        Assert.DoesNotContain(handler.Requests, value => value.Path.Contains("upload", StringComparison.OrdinalIgnoreCase));
        var submitted = handler.Requests.Single(value => value.Path == "/prompt").Body;
        Assert.Contains("ETN_LoadImageBase64", submitted); Assert.DoesNotContain("data:image", submitted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComfyEditCancellationTargetsOnlyItsSubmittedJob()
    {
        var id = Guid.NewGuid().ToString("D");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new ScriptedHttpHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/object_info") return JsonResponse(EditCatalog(true, true));
            if (request.RequestUri!.AbsolutePath == "/prompt") return JsonResponse($"{{\"prompt_id\":\"{id}\"}}");
            if (request.RequestUri.AbsolutePath == $"/history/{id}")
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new(HttpStatusCode.OK);
        });
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), new FakeAiSettingsStore(),
            TimeProvider.System, TestComfy.Monitor());
        await using var source = new MemoryStream(AssetStoreTests.Png(20, 20));
        var request = new ReferenceEditRequest
        {
            SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Keep the face",
            AspectRatio = "1:1"
        };

        await Assert.ThrowsAsync<AiCancellationException>(async () =>
        {
            await foreach (var _ in editor.EditAsync(request, source, cancellation.Token)) { }
        });
        Assert.Contains(handler.Requests, value => value.Path == $"/api/jobs/{id}/cancel");
        Assert.DoesNotContain(handler.Requests, value => value.Path.Contains("interrupt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ComfyEditKeepsCompletedCandidateWhenLaterCandidateFails()
    {
        var firstId = Guid.NewGuid().ToString("D"); var secondId = Guid.NewGuid().ToString("D"); var submits = 0;
        var handler = new ScriptedHttpHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/object_info") return Task.FromResult(JsonResponse(EditCatalog(true, true)));
            if (request.RequestUri!.AbsolutePath == "/prompt")
                return Task.FromResult(JsonResponse($"{{\"prompt_id\":\"{(++submits == 1 ? firstId : secondId)}\"}}"));
            if (request.RequestUri.AbsolutePath == $"/history/{firstId}") return Task.FromResult(JsonResponse(
                $"{{\"{firstId}\":{{\"status\":{{\"completed\":true,\"status_str\":\"success\"}},\"outputs\":{{\"13\":{{\"images\":[{{\"filename\":\"first.png\",\"subfolder\":\"\",\"type\":\"temp\"}}]}}}}}}}}"));
            if (request.RequestUri.AbsolutePath == $"/history/{secondId}") return Task.FromResult(JsonResponse(
                $"{{\"{secondId}\":{{\"status\":{{\"completed\":true,\"status_str\":\"error\"}},\"outputs\":{{}}}}}}"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(AssetStoreTests.Png(2, 2)) });
        });
        var editor = new ComfyReferenceImageEditor(new TestHttpFactory(handler), new FakeAiSettingsStore(),
            TimeProvider.System, TestComfy.Monitor());
        var request = new ReferenceEditRequest
        {
            SourceAssetId = Guid.NewGuid(), SourceImageId = Guid.NewGuid(), Prompt = "Blue coat",
            AspectRatio = "1:1", Count = 2, Seed = 50
        };
        var completed = new List<ReferenceGenerationUpdate>();
        await using var source = new MemoryStream(AssetStoreTests.Png(2, 3));

        await Assert.ThrowsAsync<AiGenerationException>(async () =>
        {
            await foreach (var update in editor.EditAsync(request, source, TestContext.Current.CancellationToken))
                if (update.Image is not null) completed.Add(update);
        });
        var first = Assert.Single(completed);
        Assert.Equal(1, first.Candidate); Assert.Equal(2, first.Total); Assert.Equal(50, first.Metadata!.Seed);
        Assert.Equal(2, submits);
        var bodies = handler.Requests.Where(value => value.Path == "/prompt").Select(value => value.Body).ToArray();
        Assert.Contains("\"seed\":50", bodies[0]); Assert.Contains("\"seed\":51", bodies[1]);
    }

    private static ReferenceAsset Asset(string name) => new() { Id = Guid.NewGuid(), Name = name, Category = AssetCategory.Character, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    internal static string EditCatalog(bool includeBase64 = true, bool includeTarget = true, bool includeConfiguredBaseFiles = true, string? omitTwoImageInput = null)
    {
        var nodes = new Dictionary<string, object>();
        foreach (var name in new[] { "VAEEncode", "EmptySD3LatentImage", "Krea2EditGroundedEncode", "KSampler", "VAEDecode", "PreviewImage" })
            nodes[name] = new { input = new { required = new { } } };
        nodes["UNETLoader"] = new { input = new { required = new { unet_name = new object[]
            { includeConfiguredBaseFiles ? new[] { "other.safetensors", "krea2_turbo_int8_convrot.safetensors" } : new[] { "other.safetensors" }, new { } } } } };
        nodes["CLIPLoader"] = new { input = new { required = new { clip_name = new object[]
            { includeConfiguredBaseFiles ? new[] { "other.safetensors", "qwen3vl_4b_fp8_scaled.safetensors" } : new[] { "other.safetensors" }, new { } } } } };
        nodes["VAELoader"] = new { input = new { required = new { vae_name = new object[]
            { includeConfiguredBaseFiles ? new[] { "other.safetensors", "qwen_image_vae.safetensors" } : new[] { "other.safetensors" }, new { } } } } };
        nodes["LoraLoaderModelOnly"] = new { input = new { required = new { lora_name = new object[] { new[] { "other.safetensors", "krea2_identity_edit_v1_2.safetensors", "alternate/krea2_identity_edit_custom.safetensors" }, new { } } } } };
        var optional = new Dictionary<string, object>();
        foreach (var input in new[] { "vae", "source_image" }) optional[input] = new object[] { "ANY", new { } };
        foreach (var input in new[] { "source_latent_b", "source_image_b", "ref_boost_a" })
            if (input != omitTwoImageInput) optional[input] = new object[] { "ANY", new { } };
        if (includeTarget) optional["target_latent"] = new object[] { "LATENT", new { } };
        nodes["Krea2EditModelPatch"] = new { input = new { required = new
            {
                model = new object[] { "MODEL", new { } }, source_latent = new object[] { "LATENT", new { } },
                ref_boost = new object[] { "FLOAT", new { } }, fit_mode = new object[] { "STRING", new { } }
            }, optional } };
        var groundedOptional = new Dictionary<string, object> { ["image"] = new object[] { "IMAGE" }, ["grounding_px"] = new object[] { "INT" } };
        if (omitTwoImageInput != "image_b") groundedOptional["image_b"] = new object[] { "IMAGE" };
        nodes["Krea2EditGroundedEncode"] = new { input = new { required = new { clip = new object[] { "CLIP" }, prompt = new object[] { "STRING" } }, optional = groundedOptional } };
        if (includeBase64) nodes["ETN_LoadImageBase64"] = new { input = new { required = new { image = new object[] { "STRING", new { } } } } };
        return JsonSerializer.Serialize(nodes);
    }
}
