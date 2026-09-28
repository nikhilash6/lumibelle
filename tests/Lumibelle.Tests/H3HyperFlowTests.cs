using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Production;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    private static double[] HyperFlowNumbers(string text) => text.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    private static JsonElement HyperFlowGraph(VideoSnapshot snapshot, IReadOnlyList<PreparedVideoInput>? inputs = null) =>
        JsonSerializer.SerializeToElement(ComfyH3Video.BuildWorkflow(snapshot, 123, "hyperflow-test", inputs ?? [])).GetProperty("prompt");

    [Fact]
    public void HyperFlowManualGridIsVideoShiftedExactlyOnceWithEightIntervals()
    {
        var raw = HyperFlowNumbers(H3HyperFlow.RawSigmas);
        var video = HyperFlowNumbers(H3HyperFlow.VideoSigmas);
        Assert.Equal(9, raw.Length); Assert.Equal(9, video.Length);
        Assert.Equal(1.0, video[0]); Assert.Equal(0.0, video[^1]);
        for (var i = 0; i < raw.Length; i++)
        {
            Assert.InRange(Math.Abs(video[i] - 12 * raw[i] / (1 + 11 * raw[i])), 0, 5.1e-11);
            if (i > 0) Assert.True(video[i] < video[i - 1]);
            var recoveredBase = video[i] / (12 - 11 * video[i]);
            var audio = 3 * recoveredBase / (1 + 2 * recoveredBase);
            Assert.InRange(Math.Abs(audio - 3 * raw[i] / (1 + 2 * raw[i])), 0, 1e-8);
        }
        Assert.NotEqual(raw[4], video[4]);
        Assert.DoesNotContain("e", H3HyperFlow.VideoSigmas, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, 4)] [InlineData(true, 4)] [InlineData(true, 8)]
    public void HyperFlowPresetTakesPrecedenceOverLegacyTurboFlags(bool turbo, int oldSteps)
    {
        var shot = Ready(); shot.GenerationPreset = H3HyperFlow.Key; shot.Turbo = turbo; shot.TurboSteps = oldSteps;
        var sampling = H3Policy.Sampling(shot, new());
        Assert.Equal(H3HyperFlow.SamplingProfile, sampling.Profile);
        Assert.Equal(8, H3Policy.Quality(shot)); Assert.Equal(8, sampling.Steps);
        Assert.Equal("euler", sampling.Sampler); Assert.Equal("manual", sampling.Scheduler);
        Assert.Equal(12.0, sampling.VideoShift); Assert.Equal(3.0, sampling.AudioShift);
        Assert.Equal(1.0, sampling.LoraStrength); Assert.Equal(H3HyperFlow.DefaultCheckpoint, sampling.Lora);
        Assert.True(H3Presets.Experimental(H3HyperFlow.Key));
    }

    [Fact]
    public void HyperFlowSettingsAreOptionalAndDoNotRewriteLegacyProfiles()
    {
        var settings = new H3Settings();
        var json = JsonSerializer.Serialize(settings, AtomicJsonFile.Options);
        Assert.DoesNotContain("hyperFlowLora", json); Assert.Null(ShotCopy.Of(settings).HyperFlowLora);
        foreach (var key in new[] { "standard", "larry", "pdd", "spectrum", "turbo4", "turbo8" })
        {
            var before = PresetSnapshot(key);
            var changed = before with { Settings = before.Settings with { HyperFlowLora = "not-selected.safetensors" } };
            H3Presets.Validate(changed);
            Assert.Equal(before.Sampling, H3Policy.Sampling(changed.Shot, changed.Settings));
            Assert.True(JsonElement.DeepEquals(HyperFlowGraph(before), HyperFlowGraph(changed)));
            Assert.DoesNotContain(HyperFlowGraph(changed).EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "ManualSigmas");
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void HyperFlowSupportsTheFourConvertedVariantsAndExactCatalogSubfolders(int variant)
    {
        var file = "h3/" + H3HyperFlow.Checkpoints[variant];
        Assert.Null(H3HyperFlow.FileIssue(file));
        Assert.Null(H3HyperFlow.FileIssue(file.Replace('/', '\\')));
        var snapshot = PresetSnapshot(H3HyperFlow.Key);
        snapshot = snapshot with { Settings = snapshot.Settings with { HyperFlowLora = file } };
        snapshot = snapshot with { Preset = H3Presets.Capture(snapshot.Shot, snapshot.Settings), Sampling = H3Policy.Sampling(snapshot.Shot, snapshot.Settings) };
        var graph = HyperFlowGraph(snapshot);
        Assert.Equal(file, graph.GetProperty("16").GetProperty("inputs").GetProperty("lora_name").GetString());
        Assert.Equal(file, snapshot.Preset!.Checkpoint);
        Assert.Equal(H3HyperFlow.VideoSigmas, snapshot.Preset.Inputs.GetProperty("sigmas").GetString());
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")]
    [InlineData("minimax_h3_hyperflow_8step_v1.0.safetensors")]
    [InlineData("other.safetensors")]
    [InlineData("../minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16.safetensors")]
    [InlineData("/minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16.safetensors")]
    [InlineData("C:\\loras\\minimax_h3_hyperflow_8step_v1.0_comfyui_pruned_bf16.safetensors")]
    public void HyperFlowDoesNotTreatRawWeightsOrArbitraryPathsAsSupportedConversions(string? file)
    {
        Assert.NotNull(H3HyperFlow.FileIssue(file));
        // Null is an unset preference (default conversion), not a selected filename.
        if (file is not null)
            Assert.Throws<WorkspaceStoreException>(() => H3Presets.Capture(new Shot { GenerationPreset = H3HyperFlow.Key }, new() { HyperFlowLora = file }));
    }

    [Fact]
    public void HyperFlowCannotBeAddedAsAnOptionalAcceleratorOrOrdinaryTurboLoRA()
    {
        foreach (var file in H3HyperFlow.Checkpoints.Append("minimax_h3_hyperflow_8step_v1.0.safetensors"))
        {
            Assert.True(LoraPolicy.ReservedH3("h3/" + file, new()));
            Assert.Contains("HyperFlow", H3Policy.TurboFileIssue(file, 8));
        }
        var snapshot = WithLoras(PresetSnapshot(H3HyperFlow.Key));
        var original = snapshot.AppliedLoras![0];
        var duplicate = original with { Reference = original.Reference with { FileName = H3HyperFlow.DefaultCheckpoint } };
        H3Loras.ValidateSnapshot(snapshot); // A matching ordinary optional LoRA is a positive control.
        var shot = snapshot.Shot.Copy();
        shot.Loras = [shot.Loras![0] with { Reference = duplicate.Reference }];
        Assert.Throws<WorkspaceStoreException>(() => H3Loras.ValidateSnapshot(snapshot with { Shot = shot, AppliedLoras = [duplicate] }));
    }

    [Theory]
    [InlineData("sigmas")] [InlineData("shift_audio")] [InlineData("sampler")] [InlineData("strength_model")]
    public void HyperFlowRejectsChangedCapturedRecipeRatherThanFallingBack(string field)
    {
        var snapshot = PresetSnapshot(H3HyperFlow.Key);
        var inputs = JsonNode.Parse(snapshot.Preset!.Inputs.GetRawText())!.AsObject();
        switch (field)
        {
            case "sigmas": inputs[field] = H3HyperFlow.RawSigmas; break;
            case "shift_audio": inputs[field] = 6.0; break;
            case "sampler": inputs[field] = "res_multistep"; break;
            default: inputs[field] = .5; break;
        }
        var altered = snapshot with { Preset = snapshot.Preset with { Inputs = JsonSerializer.SerializeToElement(inputs) } };
        Assert.Throws<WorkspaceStoreException>(() => HyperFlowGraph(altered));
        Assert.Throws<WorkspaceStoreException>(() => HyperFlowGraph(snapshot with { Sampling = snapshot.Sampling! with { Scheduler = "simple" } }));
        Assert.Throws<WorkspaceStoreException>(() => HyperFlowGraph(snapshot with { Preset = null }));
    }

    [Fact]
    public void HyperFlowRetainsTheCapturedFileAndScheduleAfterRoundTripAndGlobalChanges()
    {
        var snapshot = PresetSnapshot(H3HyperFlow.Key);
        var captured = ShotCopy.Of(snapshot);
        snapshot.Settings.HyperFlowLora = H3HyperFlow.Checkpoints[1];
        Assert.Equal(H3HyperFlow.DefaultCheckpoint, captured.Preset!.Checkpoint);
        var graph = HyperFlowGraph(captured);
        Assert.Equal(H3HyperFlow.DefaultCheckpoint, graph.GetProperty("16").GetProperty("inputs").GetProperty("lora_name").GetString());
        Assert.Equal(H3HyperFlow.VideoSigmas, graph.GetProperty("9").GetProperty("inputs").GetProperty("sigmas").GetString());
        Assert.Throws<WorkspaceStoreException>(() => HyperFlowGraph(snapshot));
    }

    [Fact]
    public void HyperFlowGraphUsesBasicGuiderEulerAndManualSigmasAcrossOutputChoices()
    {
        foreach (var aspect in new[] { "16:9", "9:16", "1:1" })
        foreach (var resolution in new[] { "preview", "native", "upscaled" })
        foreach (var archive in new[] { false, true })
        {
            var snapshot = PresetSnapshot(H3HyperFlow.Key, archive, aspect, resolution);
            var graph = HyperFlowGraph(snapshot);
            string Type(string node) => graph.GetProperty(node).GetProperty("class_type").GetString()!;
            Assert.Equal("LoraLoaderModelOnly", Type("16")); Assert.Equal("MiniMaxH3SigmaShift", Type("18"));
            Assert.Equal("BasicGuider", Type("6")); Assert.Equal("KSamplerSelect", Type("8")); Assert.Equal("ManualSigmas", Type("9"));
            Assert.Equal("euler", graph.GetProperty("8").GetProperty("inputs").GetProperty("sampler_name").GetString());
            Assert.Equal(H3HyperFlow.VideoSigmas, graph.GetProperty("9").GetProperty("inputs").GetProperty("sigmas").GetString());
            Assert.Equal("9", graph.GetProperty("10").GetProperty("inputs").GetProperty("sigmas")[0].GetString());
            Assert.Equal(12.0, graph.GetProperty("18").GetProperty("inputs").GetProperty("shift_video").GetDouble());
            Assert.Equal(3.0, graph.GetProperty("18").GetProperty("inputs").GetProperty("shift_audio").GetDouble());
            Assert.False(graph.GetProperty("6").GetProperty("inputs").TryGetProperty("cfg", out _));
            Assert.False(graph.GetProperty("6").GetProperty("inputs").TryGetProperty("negative", out _));
            Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "BasicScheduler");
            Assert.Equal(archive, graph.TryGetProperty("15", out _));
            Assert.Equal(resolution == "upscaled", graph.TryGetProperty("41", out _));
            Assert.Equal("VAEDecodeAudio", Type("12"));
        }
    }

    [Fact]
    public void HyperFlowKeepsOptionalLorasBeforeShiftAndAttentionWithoutSpectrumOrSol()
    {
        var snapshot = WithLoras(PresetSnapshot(H3HyperFlow.Key, attention: H3AttentionBackend.Sage));
        var graph = HyperFlowGraph(snapshot);
        string Link(string node) => graph.GetProperty(node).GetProperty("inputs").GetProperty("model")[0].GetString()!;
        Assert.Equal("16", Link("lora_1")); Assert.Equal("lora_1", Link("lora_2"));
        Assert.Equal("lora_2", Link("18")); Assert.Equal("18", Link("30")); Assert.Equal("30", Link("6"));
        Assert.False(graph.TryGetProperty("51", out _)); Assert.False(graph.TryGetProperty("31", out _));
    }

    private static JsonNode HyperFlowCatalog()
    {
        var root = PresetCatalog();
        // Native node contracts: the older fixture predates ManualSigmas.
        root["ManualSigmas"] = JsonNode.Parse("""{"input":{"required":{"sigmas":["STRING",{"default":"1, 0.5","multiline":false}]}},"output":["SIGMAS"],"output_is_list":[false]}""");
        root["LoraLoaderModelOnly"]!["output"] = new JsonArray("MODEL");
        root["LoraLoaderModelOnly"]!["output_is_list"] = new JsonArray(false);
        root["LoraLoaderModelOnly"]!["input"]!["required"]!["strength_model"] = JsonNode.Parse("""["FLOAT",{"default":1.0,"min":-100.0,"max":100.0}]""");
        foreach (var file in H3HyperFlow.Checkpoints)
            root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0]!.AsArray().Add(file);
        root["KSamplerSelect"]!["output"] = new JsonArray("SAMPLER");
        root["KSamplerSelect"]!["output_is_list"] = new JsonArray(false);
        return root;
    }

    [Theory]
    [InlineData("missing-manual")] [InlineData("manual-type")] [InlineData("manual-port")]
    [InlineData("manual-list")] [InlineData("manual-new-required")] [InlineData("missing-euler")]
    [InlineData("missing-shift")] [InlineData("audio-shift-range")] [InlineData("loader-strength")] [InlineData("sampler-port")]
    public void HyperFlowRequiresItsOwnContractsWithoutImposingNewNodesOnStandard(string fault)
    {
        var root = HyperFlowCatalog(); var settings = new H3Settings(); var shot = Ready(); shot.GenerationPreset = H3HyperFlow.Key;
        Assert.True(ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings).Ready(shot));
        switch (fault)
        {
            case "missing-manual": root.AsObject().Remove("ManualSigmas"); break;
            case "manual-type": root["ManualSigmas"]!["input"]!["required"]!["sigmas"] = new JsonArray("FLOAT"); break;
            case "manual-port": root["ManualSigmas"]!["output"] = new JsonArray("STRING"); break;
            case "manual-list": root["ManualSigmas"]!["output_is_list"] = new JsonArray(true); break;
            case "manual-new-required": root["ManualSigmas"]!["input"]!["required"]!["extra"] = new JsonArray("STRING"); break;
            case "missing-euler": root["KSamplerSelect"]!["input"]!["required"]!["sampler_name"] = new JsonArray(new JsonArray("res_multistep")); break;
            case "missing-shift": root.AsObject().Remove("MiniMaxH3SigmaShift"); break;
            case "audio-shift-range": root["MiniMaxH3SigmaShift"]!["input"]!["required"]!["shift_audio"]![1]!["min"] = 4.0; break;
            case "loader-strength": root["LoraLoaderModelOnly"]!["input"]!["required"]!["strength_model"]![1]!["max"] = .5; break;
            case "sampler-port": root["KSamplerSelect"]!["output"] = new JsonArray("STRING"); break;
        }
        var config = ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        Assert.False(config.Ready(shot));
        // A broken common SAMPLER output may also invalidate Standard. HyperFlow-only
        // requirements, however, must not disable the existing Standard path.
        if (fault.StartsWith("manual", StringComparison.Ordinal) || fault is "missing-manual" or "missing-shift" or "audio-shift-range" or "missing-euler")
            Assert.True(config.Ready(Ready()), config.Issue(Ready()));
        Assert.Throws<WorkspaceStoreException>(() => H3Presets.CheckSubmission(PresetSnapshot(H3HyperFlow.Key), config));
    }

    [Fact]
    public void HyperFlowDiscoveryUsesTheExactFileAndExcludesRawAndUnrelatedLoras()
    {
        var root = HyperFlowCatalog(); var files = root["LoraLoaderModelOnly"]!["input"]!["required"]!["lora_name"]![0]!.AsArray();
        files.Add("minimax_h3_hyperflow_8step_v1.0.safetensors"); files.Add("character.safetensors");
        var settings = new H3Settings { HyperFlowLora = "nested/" + H3HyperFlow.DefaultCheckpoint };
        H3Configuration Check() => ComfyH3Video.Inspect(JsonSerializer.SerializeToElement(root), settings);
        var shot = Ready(); shot.GenerationPreset = H3HyperFlow.Key;
        Assert.False(Check().Ready(shot)); Assert.True(Check().Ready(Ready()));
        files.Add(settings.HyperFlowLora);
        Assert.True(Check().Ready(shot), Check().Issue(shot));
        var offered = Check().Presets.Single(p => p.Key == H3HyperFlow.Key).Files;
        Assert.Equal(5, offered.Count); Assert.All(offered, f => Assert.Null(H3HyperFlow.FileIssue(f)));
        settings.HyperFlowLora = "minimax_h3_hyperflow_8step_v1.0.safetensors";
        Assert.False(Check().Ready(shot)); Assert.Contains("two-time", Check().Issue(shot));
    }

    private static (VideoSnapshot Snapshot, IReadOnlyList<PreparedVideoInput> Inputs) HyperFlowReferenceSnapshot(bool refmod)
    {
        var snapshot = PresetSnapshot(H3HyperFlow.Key);
        var shot = snapshot.Shot.Copy();
        shot.Images.Add(new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" });
        shot.Dialogue = [new() { Speaker = "Riley", Language = "English", Text = "Hello." }];
        shot.Voices.Add(new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Riley", Duration = 5 });
        if (refmod)
        {
            var reel = new ShotVideoBinding { Name = "Angles", Visuals = ReelVisuals.RefMod,
                Media = new(Guid.NewGuid(), new('A', 64), 1000, 640, 640, 120, 24, 5, false) };
            reel.Keyframes = new() { Frames = [new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 0, 0) }, new() { Frame = new(reel.Media.Id, reel.Media.Sha256, 24, 1) }] };
            var recipe = ReelRefMods.Recipe(reel, 640, 640, snapshot.Settings.VideoVae, [new string('B', 64), new string('C', 64)]);
            var build = Guid.NewGuid(); reel.RefMod = new(recipe, snapshot.ComfyUrl, ReelRefMods.BuildStem(snapshot.ProjectId, build), build);
            shot.Videos.Add(reel);
        }
        snapshot = snapshot with { Shot = shot, Fingerprint = H3Policy.Fingerprint(shot) };
        var inputs = ReferenceVideos.InputOrder(shot).Select((i, n) => new PreparedVideoInput("reference-" + n,
            i.Kind is VideoInputKind.Audio or VideoInputKind.VideoSoundtrack) { Kind = i.Kind, VideoIndex = i.VideoIndex }).ToArray();
        return (snapshot, inputs);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HyperFlowLeavesNativeOrRefModReferenceAndStandaloneVoiceWiringIntact(bool refmod)
    {
        var (snapshot, inputs) = HyperFlowReferenceSnapshot(refmod); var graph = HyperFlowGraph(snapshot, inputs);
        Assert.Equal(refmod ? 2 : 1, graph.GetProperty("10").GetProperty("inputs").GetProperty("latent_image")[1].GetInt32());
        if (!refmod) Assert.True(graph.GetProperty("5").GetProperty("inputs").TryGetProperty("ref_audios.ref_audio_0", out _));
        else
        {
            Assert.Equal(ComfyRefModClient.EncodeNode, graph.GetProperty("5").GetProperty("class_type").GetString());
            using var media = JsonDocument.Parse(graph.GetProperty("refmedia").GetProperty("inputs").GetProperty("media_state").GetString()!);
            Assert.Single(media.RootElement.EnumerateArray(), m => m.GetProperty("kind").GetString() == "audio");
            Assert.All(media.RootElement.EnumerateArray(), m => Assert.Equal("off", m.GetProperty("audio_mode").GetString()));
            Assert.DoesNotContain(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == ComfyRefModClient.BuildNode);
        }
    }

    [Theory]
    [InlineData(2, false)] [InlineData(4, false)] [InlineData(2, true)]
    public void HyperFlowSharedTakesShareTheScheduleButKeepIndependentSamplerAndSeedNodes(int count, bool refmod)
    {
        var (snapshot, inputs) = HyperFlowReferenceSnapshot(refmod);
        var candidates = AiBatchDefinition.Create(Guid.NewGuid(), count, 456).Candidates;
        var graph = JsonSerializer.SerializeToElement(ComfyMultiTakeWorkflow.Build(candidates,
            c => ComfyH3Video.BuildWorkflow(snapshot, c.Seed, "hyperflow-" + c.Number, inputs), "shared")).GetProperty("prompt");
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "ManualSigmas");
        Assert.Single(graph.EnumerateObject(), n => n.Value.GetProperty("class_type").GetString() == "LoraLoaderModelOnly");
        Assert.Equal(count, graph.EnumerateObject().Count(n => n.Value.GetProperty("class_type").GetString() == "SamplerCustomAdvanced"));
        foreach (var candidate in candidates)
        {
            var noise = graph.GetProperty(ComfyMultiTakeWorkflow.Node(candidate, "7")).GetProperty("inputs");
            var sampler = graph.GetProperty(ComfyMultiTakeWorkflow.Node(candidate, "10")).GetProperty("inputs");
            Assert.Equal(candidate.Seed, noise.GetProperty("noise_seed").GetInt64());
            Assert.Equal("9", sampler.GetProperty("sigmas")[0].GetString());
            Assert.Equal(ComfyMultiTakeWorkflow.Node(candidate, "7"), sampler.GetProperty("noise")[0].GetString());
        }
    }

    [Fact]
    public void HyperFlowReelAndReusableGenerationSetupKeepTheExplicitPreset()
    {
        var draft = new ReferenceReelDraft { AssetId = Guid.NewGuid(), Name = "Voice reference", VoiceMode = ReelVoiceMode.Silent,
            GenerationPreset = H3HyperFlow.Key, Duration = 10, Aspect = "1:1" };
        var shot = ReferenceReels.Inputs(ShotCopy.Of(draft));
        Assert.Equal(H3HyperFlow.Key, H3Presets.Key(shot)); Assert.Equal(8, H3Policy.Sampling(shot, new()).Steps);
        var context = new AssetLookContext(draft.AssetId, "Riley", "", "", null, "", "", "");
        var snapshot = PresetSnapshot(H3HyperFlow.Key) with { Shot = shot, Reel = new(draft, context), Width = 640, Height = 640, FrameCount = H3Policy.Frames(draft.Duration) };
        var graph = HyperFlowGraph(snapshot);
        Assert.Equal("ManualSigmas", graph.GetProperty("9").GetProperty("class_type").GetString());
        Assert.Equal(H3Policy.Frames(10), graph.GetProperty("5").GetProperty("inputs").GetProperty("length").GetInt32());
        var composition = new ProductionComposition { Shot = shot };
        var setup = ShotCopy.Of(GenerationSettings.From(composition));
        var other = new ProductionComposition(); setup.Apply(other);
        Assert.Equal(H3HyperFlow.Key, H3Presets.Key(other.Shot));
    }

    [Fact]
    public Task HyperFlowQueuedOutputRecoveryAndOneMoreRetainTheCapturedPreset() =>
        PresetAndOutputSurviveQueueRecoveryAndAdditionalCandidates(H3HyperFlow.Key, false);
}
