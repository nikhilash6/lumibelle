using System.Runtime.CompilerServices;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public sealed class ComfyFluxKleinImages(IHttpClientFactory clients, TimeProvider clock, IComfyExecutionMonitor monitor)
{
    public const int MaximumReferences = 8;
    internal const string OutputNode = "14";
    internal static readonly ComfyExecutionOptions ExecutionOptions = new(
        new Dictionary<string, ComfyNodeStage>
        {
            ["1"] = new(GenerationPhase.Preparing, "Opening FLUX.2 Klein 9B KV…"),
            ["2"] = new(GenerationPhase.Preparing, "Opening the Qwen3 8B encoder…"),
            ["3"] = new(GenerationPhase.Preparing, "Opening the FLUX.2 VAE…"),
            ["4"] = new(GenerationPhase.Preparing, "Encoding the image prompt…"),
            ["12"] = new(GenerationPhase.Preparing, "Preparing Klein KV…", "Generating image", "steps", "sampling"),
            ["13"] = new(GenerationPhase.Finalizing, "Decoding the image…"),
            [OutputNode] = new(GenerationPhase.Finalizing, "Preparing the image output…")
        },
        "ComfyUI rejected the Klein KV workflow. Refresh image models in AI settings.",
        "ComfyUI could not execute the Klein KV workflow. Check model compatibility and GPU memory.",
        "Image generation timed out before ComfyUI accepted the job.", "Image generation timed out.",
        "ComfyUI returned an unreadable image response.", "The connection to ComfyUI failed during image generation.");

    public async Task<ComfyImageConfiguration> CheckAsync(AiSettings settings, bool editing, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var http = Client(settings);
            using var response = await http.GetAsync("object_info", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return CheckCatalog(json.RootElement, settings, editing);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(false, "The Klein KV connection check timed out.", [], [], []); }
        catch (HttpRequestException)
        { return new(false, "Couldn’t reach ComfyUI. Check its address and connection.", [], [], []); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        { return new(false, "ComfyUI returned an unexpected node catalog.", [], [], []); }
    }

    internal static ComfyImageConfiguration CheckCatalog(JsonElement catalog, AiSettings settings, bool editing)
    {
        var models = Options(catalog, "UNETLoader", "unet_name").Where(IsKleinKv).Select(name => new AiModel(name, name)).ToArray();
        var encoders = Options(catalog, "CLIPLoader", "clip_name").Where(IsEncoder).Select(name => new AiModel(name, name)).ToArray();
        var vaes = Options(catalog, "VAELoader", "vae_name").Where(IsVae).Select(name => new AiModel(name, name)).ToArray();
        // Check the inputs we actually submit, including optional reference latents and newer node contracts.
        var graph = JsonSerializer.SerializeToElement(BuildWorkflow(settings, "check", 1, 1024, 1024, editing ? ["check"] : []));
        var missing = new List<string>();
        foreach (var node in graph.GetProperty("prompt").EnumerateObject())
        {
            var type = node.Value.GetProperty("class_type").GetString()!;
            if (!catalog.TryGetProperty(type, out var info) || !info.TryGetProperty("input", out var inputs)) { missing.Add(type); continue; }
            foreach (var input in node.Value.GetProperty("inputs").EnumerateObject())
                if (!HasInput(inputs, input.Name)) missing.Add($"{type}.{input.Name}");
        }
        if (missing.Count > 0) return new(false, $"Klein KV needs these ComfyUI nodes or inputs: {string.Join(", ", missing.Distinct())}. Update ComfyUI; editing also needs comfyui-tooling-nodes.", models, encoders, vaes);
        if (!Options(catalog, "CLIPLoader", "type").Contains("flux2"))
            return new(false, "Update ComfyUI: CLIPLoader must support the flux2 encoder type.", models, encoders, vaes);
        var absent = new List<string>();
        if (!models.Any(model => model.Id == settings.FluxKleinModel)) absent.Add(settings.FluxKleinModel);
        if (!encoders.Any(model => model.Id == settings.FluxKleinTextEncoder)) absent.Add(settings.FluxKleinTextEncoder);
        if (!vaes.Any(model => model.Id == settings.FluxKleinVae)) absent.Add(settings.FluxKleinVae);
        return new(absent.Count == 0, absent.Count == 0
            ? $"FLUX.2 Klein 9B KV {(editing ? "multi-reference editing" : "image creation")} is ready."
            : $"Configured Klein KV files are missing or incompatible: {string.Join(", ", absent)}. Refresh and select the installed files.", models, encoders, vaes);
    }

    public IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request, AiSettings settings,
        CancellationToken cancellationToken = default) => RunAsync(request.Prompt, request.AspectRatio, request.Count, request.Seed, null, [], request.Loras, settings, request.Resolution, cancellationToken);

    public IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources,
        AiSettings settings, CancellationToken cancellationToken = default) =>
        RunAsync(request.Prompt, request.AspectRatio, request.Count, request.Seed, request, sources, request.Loras, settings, request.Resolution, cancellationToken);

    private async IAsyncEnumerable<ReferenceGenerationUpdate> RunAsync(string prompt, string aspect, int count, long? fixedSeed,
        ReferenceEditRequest? edit, IReadOnlyList<ReferenceImageSource> sources, IReadOnlyList<LoraSelection> selections, AiSettings settings, int resolution,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        sources = sources.ToArray();
        selections = LoraPolicy.Capture(selections);
        settings = settings with { LoraLibrary = settings.LoraLibrary.ToArray() };
        if (edit?.Regions?.Count > 0) throw new AiGenerationException("Regional edits require the queued review workflow.");
        if (edit is not null) edit = ReferenceEditInputs.Capture(edit, sources, MaximumReferences);
        FileAiSettingsStore.Validate(settings);
        if (string.IsNullOrWhiteSpace(prompt)) throw new AiGenerationException("Describe the image or edit first.");
        if (count is < 1 or > 4) throw new AiGenerationException("Generate between one and four candidates.");
        if (fixedSeed is < 0 || fixedSeed > long.MaxValue - count + 1) throw new AiGenerationException("Seed must be a nonnegative whole number with room for each candidate.");
        if (!ImageAspectPolicy.IsSupported(aspect, edit is not null)) throw new AiGenerationException("Choose a supported aspect ratio.");
        if (!IsKleinKv(settings.FluxKleinModel) || !IsEncoder(settings.FluxKleinTextEncoder) || !IsVae(settings.FluxKleinVae))
            throw new AiGenerationException("Choose the Klein 9B KV model, Qwen3 8B encoder, and FLUX.2 VAE in AI settings.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ImageTimeoutSeconds));
        // A stale UI check must never submit a missing model or silently choose a different one.
        var check = await CheckAsync(settings, edit is not null, timeout.Token);
        if (!check.Success) throw new AiGenerationException(check.Message);
        var loras = await LoraPolicy.PrepareAsync(selections, settings, ImageWorkflow.Flux2Klein9bKv, clients, timeout.Token);
        var encoded = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            yield return new($"Preparing reference image {i + 1} of {sources.Count}…", 1, count,
                Progress: new(GenerationPhase.Preparing, $"Preparing reference image {i + 1} of {sources.Count}…"));
            var bytes = await ComfyReferenceImageEditor.PrepareSourcePngAsync(sources[i].Content,
                edit is null ? null : ReferenceEditInputs.CropFor(edit, sources[i]), timeout.Token);
            if (bytes.Length > ComfyReferenceImageEditor.MaximumEncodedSourceBytes) throw new AiGenerationException("A prepared reference image is too large.");
            encoded.Add(Convert.ToBase64String(bytes));
        }
        var size = ImageAspectPolicy.Size(aspect, encoded.Count == 0 ? null : Convert.FromBase64String(encoded[0]), resolution);
        // IDs and crop are copied before yielding any candidate. Caller-owned image streams stay open until the run finishes.
        var lineage = edit is null ? null : new AssetEditMetadata
        {
            SourceAssetId = edit.SourceAssetId, SourceImageId = edit.SourceImageId, SourceCrop = edit.SourceCrop,
            ReferenceCrops = edit.ReferenceCrops,
            References = sources.Select(source => new AssetImageReference(source.AssetId, source.ImageId)).ToArray(),
            LoraStrength = 0, ReferenceBoost = 0, GroundingPixels = 0, FitMode = "reference-latent"
        };
        using var http = Client(settings);
        for (var candidate = 1; candidate <= count; candidate++)
        {
            if (cancellationToken.IsCancellationRequested) throw new AiCancellationException("Generation cancelled.", cancellationToken);
            if (timeout.IsCancellationRequested) throw new AiGenerationException("Image generation timed out before the next candidate started.");
            var seed = fixedSeed is null ? Random.Shared.NextInt64(1, long.MaxValue) : fixedSeed.Value + candidate - 1;
            var started = clock.GetUtcNow();
            ComfyReferenceImageGenerator.ComfyOutput? output = null;
            GenerationProgress? progress = null;
            await foreach (var update in monitor.ExecuteAsync(http,
                clientId => BuildWorkflow(settings, prompt.Trim(), seed, size.Width, size.Height, encoded, clientId, loras),
                ExecutionOptions, timeout.Token, cancellationToken))
            {
                progress = update.Progress;
                yield return new(progress.Label, candidate, count, Progress: progress);
                if (update.Complete && update.Job is { } job) output = ComfyReferenceImageGenerator.ReadOutput(job, OutputNode);
            }
            if (output is null) throw new AiGenerationException("ComfyUI finished without a Klein image output.");
            progress = new(GenerationPhase.Downloading, "Downloading candidate…", Elapsed: clock.GetUtcNow() - started,
                LiveUpdatesAvailable: progress?.LiveUpdatesAvailable ?? false);
            yield return new(progress.Label, candidate, count, Progress: progress);
            var image = await ComfyReferenceImageGenerator.DownloadHandledAsync(http, output, timeout, cancellationToken);
            var metadata = new AssetGenerationMetadata
            {
                Workflow = ImageWorkflow.Flux2Klein9bKv, Prompt = prompt.Trim(), Seed = seed, AspectRatio = aspect, Resolution = resolution,
                DiffusionModel = settings.FluxKleinModel, TextEncoder = settings.FluxKleinTextEncoder, Vae = settings.FluxKleinVae,
                Steps = 4, Edit = lineage, Loras = loras
            };
            yield return new("Saving candidate…", candidate, count, image, output.FileName, metadata,
                new(GenerationPhase.Saving, "Saving candidate…", Elapsed: clock.GetUtcNow() - started, LiveUpdatesAvailable: progress.LiveUpdatesAvailable));
        }
    }

    // Flattened from Comfy-Org/workflow_templates: image_flux2_klein_9b_kv_image_edit.json.
    // Sources use in-memory tooling nodes; the author's chosen canvas replaces GetImageSize.
    public static object BuildWorkflow(AiSettings settings, string prompt, long seed, int width, int height,
        IReadOnlyList<string> references, string? clientId = null, IReadOnlyList<AppliedLora>? loras = null)
    {
        static object[] Link(string id) => [id, 0];
        static object Node(string type, object inputs) => new { class_type = type, inputs };
        var nodes = new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = settings.FluxKleinModel, weight_dtype = "default" }),
            ["2"] = Node("CLIPLoader", new { clip_name = settings.FluxKleinTextEncoder, type = "flux2", device = "default" }),
            ["3"] = Node("VAELoader", new { vae_name = settings.FluxKleinVae }),
            ["4"] = Node("CLIPTextEncode", new { text = prompt, clip = Link("2") }),
            ["5"] = Node("ConditioningZeroOut", new { conditioning = Link("4") }),
            ["6"] = Node("FluxKVCache", new { model = Link("1") }),
            ["8"] = Node("RandomNoise", new { noise_seed = seed }),
            ["9"] = Node("KSamplerSelect", new { sampler_name = "euler" }),
            ["10"] = Node("Flux2Scheduler", new { steps = 4, width, height }),
            ["11"] = Node("EmptyFlux2LatentImage", new { width, height, batch_size = 1 }),
            ["12"] = Node("SamplerCustomAdvanced", new { noise = Link("8"), guider = Link("7"), sampler = Link("9"), sigmas = Link("10"), latent_image = Link("11") }),
            ["13"] = Node("VAEDecode", new { samples = Link("12"), vae = Link("3") }),
            [OutputNode] = Node("PreviewImage", new { images = Link("13") })
        };
        nodes["6"] = Node("FluxKVCache", new { model = Link(LoraPolicy.AddNodes(nodes, "1", loras ?? [])) });
        var positive = "4"; var negative = "5";
        for (var i = 0; i < references.Count; i++)
        {
            var id = 100 + i * 5;
            var load = id.ToString(); var scale = (id + 1).ToString(); var encode = (id + 2).ToString();
            var pos = (id + 3).ToString(); var neg = (id + 4).ToString();
            nodes[load] = Node("ETN_LoadImageBase64", new { image = references[i] });
            nodes[scale] = Node("ImageScaleToTotalPixels", new { image = Link(load), upscale_method = "lanczos", megapixels = 1.0, resolution_steps = 1 });
            nodes[encode] = Node("VAEEncode", new { pixels = Link(scale), vae = Link("3") });
            nodes[pos] = Node("ReferenceLatent", new { conditioning = Link(positive), latent = Link(encode) });
            nodes[neg] = Node("ReferenceLatent", new { conditioning = Link(negative), latent = Link(encode) });
            positive = pos; negative = neg;
        }
        nodes["7"] = Node("CFGGuider", new { model = Link("6"), positive = Link(positive), negative = Link(negative), cfg = 1.0 });
        return new { client_id = clientId ?? Guid.NewGuid().ToString("D"), prompt = nodes };
    }

    private HttpClient Client(AiSettings settings)
    {
        var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = new Uri(settings.ComfyUrl.TrimEnd('/') + "/"); http.Timeout = Timeout.InfiniteTimeSpan;
        return http;
    }
    private static bool HasInput(JsonElement inputs, string name) => new[] { "required", "optional" }
        .Any(group => inputs.TryGetProperty(group, out var values) && values.TryGetProperty(name, out _));
    private static IReadOnlyList<string> Options(JsonElement catalog, string node, string input)
    {
        if (!catalog.TryGetProperty(node, out var info) || !info.TryGetProperty("input", out var inputs) ||
            !inputs.TryGetProperty("required", out var required) || !required.TryGetProperty(input, out var spec) || spec.ValueKind != JsonValueKind.Array || spec.GetArrayLength() == 0) return [];
        var choices = spec[0];
        if (choices.ValueKind != JsonValueKind.Array && spec.GetArrayLength() > 1 && spec[1].ValueKind == JsonValueKind.Object && spec[1].TryGetProperty("options", out var options)) choices = options;
        return choices.ValueKind == JsonValueKind.Array ? choices.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray() : [];
    }
    private static string Basename(string name) => name.Replace('\\', '/').Split('/')[^1];
    private static bool IsKleinKv(string name) => Basename(name).StartsWith("flux-2-klein-9b-kv", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
    private static bool IsEncoder(string name) => Basename(name).StartsWith("qwen_3_8b", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
    private static bool IsVae(string name) => Basename(name).StartsWith("flux2-vae", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase);
}
