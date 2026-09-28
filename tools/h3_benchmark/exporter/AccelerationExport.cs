using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

internal static class AccelerationExport
{
    public static async Task Run(string path)
    {
        var root = Path.GetFullPath(path);
        var settings = JsonSerializer.Deserialize<H3Settings>(File.ReadAllText(Path.Combine(root, "h3-settings.json")), AtomicJsonFile.Options)!;
        var output = Path.Combine(root, "requests");
        if (Directory.Exists(output)) throw new IOException("Refusing to overwrite existing requests.");
        Directory.CreateDirectory(output);
        foreach (var scene in new[] { "dialogue", "motion" })
        foreach (var seed in new long[] { 20260911, 20260912 })
        foreach (var dimensions in new[] { (Frames: 141, Native: false), (Frames: 141, Native: true), (Frames: 243, Native: false) })
        foreach (var variant in new[] { "standard", "turbo4", "turbo8", "sol" })
        {
            var name = $"{scene}-{variant}-{dimensions.Frames}-{(dimensions.Native ? "native" : "upscaled")}-{seed}";
            var shot = new Shot { Title = "Neutral acceleration comparison", Description = scene, Aspect = "16:9",
                Duration = dimensions.Frames / 24d - 1e-9, ApprovedScriptId = Guid.NewGuid(), SceneId = Guid.NewGuid(),
                NativeResolution = dimensions.Native, UpscalePreview = !dimensions.Native,
                Turbo = variant.StartsWith("turbo"), TurboSteps = variant == "turbo8" ? 8 : 4,
                Images = [new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Face" },
                          new() { AssetId = Guid.NewGuid(), MediaId = Guid.NewGuid(), Name = "Outfit" }],
                Voices = scene == "dialogue" ? [new() { AssetId = Guid.NewGuid(), VoiceId = Guid.NewGuid(), Speaker = "Subject 1", Duration = 4.248 }] : [] };
            if (scene == "dialogue") shot.Dialogue.Add(new() { Speaker = "Subject 1", Language = "English", Text = "The package is here. We can leave now." });
            var seconds = (dimensions.Frames / 24d).ToString("0.000", CultureInfo.InvariantCulture);
            var action = scene == "dialogue"
                ? "A locked medium close-up. She turns toward the camera and says, \"The package is here. We can leave now.\" Quiet room ambience, no music or cuts."
                : "A full-body shot. She walks briskly across a room, turns, and raises one arm to wave. Audible footsteps, no speech, music, or cuts.";
            var voice = scene == "dialogue" ? " Her voice matches <Audio 1>." : "";
            var prompt = $"""
                subject_definitions:
                <Subject 1> is the adult woman from <Picture 1>, wearing the outfit from <Picture 2>.{voice}
                summary:
                [reference generation] A woman in a quiet, uncluttered room.
                retention_analysis:
                Preserve <Subject 1>'s face, short auburn hair and glasses from <Picture 1>. Preserve the white, black and gold armor and boots from <Picture 2>.
                detailed_description:
                [Shot 1] One continuous camera take lasting {seconds} seconds. {action}
                overall_soundscape:
                {(scene == "dialogue" ? "Clear spoken words from <Subject 1>, matching <Audio 1>. Quiet room ambience." : "Audible footsteps and quiet room ambience. No speech.")}
                non_diegetic_music:
                No music.
                """;
            var current = settings with { Performance = settings.Performance with { Attention = H3AttentionBackend.ServerDefault, SolAttention = variant == "sol" } };
            var size = H3Policy.Size("16:9", dimensions.Native);
            var snapshot = new VideoSnapshot(Guid.NewGuid(), 0, shot, prompt, H3Policy.Fingerprint(shot), "http://isolated.invalid", current,
                size.Width, size.Height, dimensions.Frames, H3Policy.Profile) {
                Sampling = H3Policy.Sampling(shot, current), Performance = H3Performance.Capture(current.Performance),
                PreviewUpscale = dimensions.Native ? null : H3PreviewUpscaling.Capture(H3UpscalerImplementation.Plus, current.LatentUpscaler, "16:9") };
            List<PreparedVideoInput> inputs = [new("inputs/face.png", false), new("inputs/outfit.png", false)];
            if (scene == "dialogue") inputs.Add(new("inputs/voice.wav", true));
            var graph = ComfyH3Video.BuildWorkflow(snapshot, seed, Guid.NewGuid().ToString("D"), inputs);
            var finalSize = H3PreviewUpscaling.OutputSize(snapshot);
            await using var file = new FileStream(Path.Combine(output, name + ".json"), FileMode.CreateNew, FileAccess.Write);
            await JsonSerializer.SerializeAsync(file, new { name, scene, configuration = variant, seed, snapshot, workflow = graph,
                expected = new { width = finalSize.Width, height = finalSize.Height, frames = dimensions.Frames, fps = 24 },
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ComfyH3Video).Assembly.Location))) }, AtomicJsonFile.Options);
        }
        Console.WriteLine("Exported 48 application-built control graphs.");
    }
}
