using System.Net.Http.Json;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ComfyH3Video
{
    private static (bool Capture, string? Issue, string[] Models) InspectRefinement(JsonElement root, H3Settings settings, string? standardIssue)
    {
        bool Type(string node, string field, string type) => Input(root, node, field) is { ValueKind: JsonValueKind.Array } input &&
            input.GetArrayLength() > 0 && input[0].ValueKind == JsonValueKind.String && input[0].GetString() == type;
        bool Contract(string node, string[] outputs, params (string Field, string Type)[] fields) => fields.All(f => Type(node, f.Field, f.Type))
            && Options(root, node, "protocol").Contains("lumibelle-h3-v1") && root.GetProperty(node).TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array && output.EnumerateArray().Select(v => v.GetString()).SequenceEqual(outputs);
        var capture = Contract("LumibelleH3CaptureV1", ["LATENT"], ("latent", "LATENT"), ("conditioning", "CONDITIONING"),
            ("context", "STRING"), ("package_id", "STRING"), ("width", "INT"), ("height", "INT"), ("frames", "INT"), ("locked_audio_source", "LATENT"));
        var models = Options(root, "MinimaxH3LatentUpscaler3D", "model_name");
        var issue = !capture || !Contract("LumibelleH3LoadV1", ["LATENT", "LATENT", "CONDITIONING"], ("token", "STRING"), ("package_id", "STRING")) ||
            !Contract("LumibelleH3PrepareV1", ["LATENT", "CONDITIONING", "NOISE"], ("source", "LATENT"), ("video", "LATENT"),
                ("conditioning", "CONDITIONING"), ("width", "INT"), ("height", "INT"), ("frames", "INT"), ("lock_audio", "BOOLEAN"), ("seed", "INT"))
            ? "Install/update the bundled Lumibelle H3 companion nodes and restart ComfyUI. Existing takes remain playable."
            : standardIssue;
        var mode = DynamicOption(Input(root, "MinimaxH3LatentUpscaler3D", "mode"), "target dimensions");
        if (issue is null && (new[] { "latent", "model_name", "mode", "align", "enable_temporal_chunking", "force_unload", "device", "precision" }
            .Any(f => Input(root, "MinimaxH3LatentUpscaler3D", f).ValueKind != JsonValueKind.Array) || mode.ValueKind != JsonValueKind.Object ||
            !mode.TryGetProperty("inputs", out var nested) || !nested.TryGetProperty("required", out var required) ||
            !required.TryGetProperty("width", out _) || !required.TryGetProperty("height", out _) ||
            !Options(root, "MinimaxH3LatentUpscaler3D", "device").Contains("cuda") || !Options(root, "MinimaxH3LatentUpscaler3D", "precision").Contains("fp16")))
            issue = "Install the supported learned MinimaxH3LatentUpscaler3D node. See refinement setup in Video models.";
        if (issue is null && (!models.Contains(settings.LatentUpscaler) || !settings.LatentUpscaler.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)))
            issue = "Select an installed learned 3D H3 upscaler SafeTensors checkpoint in Video models.";
        if (issue is null && (Input(root, "MiniMaxH3SigmaShift", "model").ValueKind != JsonValueKind.Array || !SupportsShift(root, "shift_video", 12) || !SupportsShift(root, "shift_audio", 3)))
            issue = "Refinement requires MiniMaxH3SigmaShift with video shift 12 and audio shift 3. Update ComfyUI.";
        return (capture, issue, models);
    }

    public async Task<string> UploadRefinementAsync(string server, string path, CancellationToken ct)
    {
        using var http = Client(server);
        await using var stream = File.OpenRead(path);
        using var form = new MultipartFormDataContent(); form.Add(new StreamContent(stream), "package", H3RefinementPackage.FileName);
        using var response = await http.PostAsync("lumibelle/refinement/v1/upload", form, ct); response.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var token = result.RootElement.GetProperty("token").GetString();
        if (!Guid.TryParseExact(token, "N", out _)) throw new WorkspaceStoreException("Invalid refinement upload receipt.");
        return token!;
    }

    public static object BuildRefinementWorkflow(VideoSnapshot s, TakeRefinement refinement, long seed, string clientId, string token)
    {
        RefinementPolicy.Validate(refinement, s);
        H3Loras.ValidateSnapshot(s);
        if (!Guid.TryParseExact(token, "N", out _) || !Guid.TryParse(clientId, out _)) throw new WorkspaceStoreException("Invalid refinement transfer identity.");
        object Link(string id, int port = 0) => new object[] { id, port };
        var nodes = new Dictionary<string, object>();
        void Node(string id, string type, object values) => nodes.Add(id, new { class_type = type, inputs = values });
        Node("1", "UNETLoader", new { unet_name = s.Settings.Model, weight_dtype = "default" });
        Node("3", "VAELoader", new { vae_name = s.Settings.VideoVae }); Node("4", "VAELoader", new { vae_name = s.Settings.AudioVae });
        var model = LoraPolicy.AddNodes(nodes, "1", H3Loras.Applied(s));
        Node("18", "MiniMaxH3SigmaShift", new { model = Link(model), shift_video = 12.0, shift_audio = 3.0 });
        Node("21", "LumibelleH3LoadV1", new { protocol = "lumibelle-h3-v1", token, package_id = refinement.SourcePackage.Id.ToString("D") });
        Node("22", "MinimaxH3LatentUpscaler3D", new Dictionary<string, object> { ["latent"] = Link("21", 1), ["model_name"] = refinement.Upscaler,
            ["mode"] = "target dimensions", ["mode.width"] = refinement.Width, ["mode.height"] = refinement.Height, ["align"] = 32,
            ["enable_temporal_chunking"] = true, ["force_unload"] = true, ["device"] = "cuda", ["precision"] = "fp16" });
        Node("23", "LumibelleH3PrepareV1", new { protocol = "lumibelle-h3-v1", source = Link("21"), video = Link("22"), conditioning = Link("21", 2),
            width = refinement.Width, height = refinement.Height, frames = s.FrameCount, lock_audio = refinement.Mode == TakeRefinementMode.Refine, seed });
        Node("6", "BasicGuider", new { model = Link("18"), conditioning = Link("23", 1) });
        Node("8", "KSamplerSelect", new { sampler_name = "res_multistep" });
        Node("9", "BasicScheduler", new { model = Link("18"), scheduler = "simple", steps = refinement.Steps, denoise = refinement.Denoise });
        Node("10", "SamplerCustomAdvanced", new { noise = Link("23", 2), guider = Link("6"), sampler = Link("8"), sigmas = Link("9"), latent_image = Link("23") });
        var capture = new Dictionary<string, object> { ["protocol"] = "lumibelle-h3-v1", ["latent"] = Link("10", 1), ["conditioning"] = Link("23", 1),
            ["context"] = JsonSerializer.Serialize(RefinementPackages.Context(s, refinement), AtomicJsonFile.Options), ["package_id"] = clientId,
            ["width"] = refinement.Width, ["height"] = refinement.Height, ["frames"] = s.FrameCount };
        if (refinement.Mode == TakeRefinementMode.Refine) capture["locked_audio_source"] = Link("21");
        Node("20", "LumibelleH3CaptureV1", capture);
        Node("11", "VAEDecode", new { samples = Link("20"), vae = Link("3") }); Node("12", "VAEDecodeAudio", new { samples = Link("20"), vae = Link("4") });
        Node("13", "CreateVideo", new { images = Link("11"), audio = Link("12"), fps = 24, bit_depth = 8 });
        Node("14", "SaveVideo", new Dictionary<string, object> { ["video"] = Link("13"), ["filename_prefix"] = "lumibelle/" + clientId + "/video", ["format"] = "mp4", ["format.codec"] = "h264" });
        Node("17", "RebatchImages", new { images = Link("11"), batch_size = LosslessFrameArchive.SegmentFrames });
        Node("15", "SaveAnimatedWEBP", new { images = Link("17"), filename_prefix = "lumibelle/" + clientId + "/frames", fps = 24, lossless = true, quality = 80, method = "default" });
        return new { client_id = clientId, prompt = nodes };
    }

    private static async Task<H3RefinementPackage> DownloadPackageAsync(HttpClient http, JsonElement outputs, VideoRun run, string directory, Func<string, Task> progress, CancellationToken ct)
    {
        var entries = outputs.GetProperty("20").GetProperty("refinement");
        if (entries.GetArrayLength() != 1 || entries[0].GetProperty("protocol").GetString() != "lumibelle-h3-v1" ||
            !Guid.TryParseExact(entries[0].GetProperty("token").GetString(), "N", out var id)) throw new WorkspaceStoreException("No valid refinement package was returned.");
        var path = Path.Combine(directory, H3RefinementPackage.FileName);
        if (File.Exists(path))
        {
            try
            {
                var previous = await RefinementPackages.InspectAsync(path, run.Snapshot, run.Refinement, ct);
                if (previous.Id == id) return previous;
            }
            catch (WorkspaceStoreException) { }
            File.Delete(path);
        }
        await progress("Downloading retained video/audio latents and conditioning…");
        using var response = await http.GetAsync("lumibelle/refinement/v1/" + id.ToString("N"), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > RefinementPackages.MaximumBytes) throw new WorkspaceStoreException("The refinement package exceeds 8 GB.");
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(path + ".tmp"))
        {
            var buffer = new byte[1024 * 1024]; int count; long total = 0; long reported = 0;
            while ((count = await source.ReadAsync(buffer, ct)) != 0)
            {
                total += count; if (total > RefinementPackages.MaximumBytes) throw new WorkspaceStoreException("The refinement package exceeds 8 GB.");
                await target.WriteAsync(buffer.AsMemory(0, count), ct);
                if (total - reported >= 16 * 1024 * 1024) { reported = total; await progress($"Transferring refinement data · {total / (1024 * 1024)} MB"); }
            }
            await target.FlushAsync(ct); target.Flush(true);
        }
        var package = await RefinementPackages.InspectAsync(path + ".tmp", run.Snapshot, run.Refinement, ct);
        if (package.Id != id) throw new WorkspaceStoreException("The returned package belongs to a different take.");
        File.Move(path + ".tmp", path, true);
        return package;
    }
}
