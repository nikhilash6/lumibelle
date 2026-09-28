using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace lumibelle.Services.Projects;

// Operates on typed, detached documents, never the live library or arbitrary user text.
// A selection is used when enabled with nonzero strength, or recorded as actually applied.
internal sealed class ProjectPackagePrivacy
{
    internal int Removed { get; private set; }
    internal T Clean<T>(T source)
    {
        var node = JsonSerializer.SerializeToNode(source, AtomicJsonFile.Options)!;
        Visit(node, "");
        return node.Deserialize<T>(AtomicJsonFile.Options)!;
    }
    private void Visit(JsonNode? node, string property)
    {
        if (node is JsonArray array) { foreach (var item in array) Visit(item, ""); return; }
        if (node is not JsonObject o) return;
        o.Remove("loraLibrary"); // Never export a catalog, even in a future nested snapshot.
        if (o["loras"] is JsonArray selections) Filter(selections);
        else if (o["loras"] is JsonObject byWorkflow)
        {
            foreach (var entry in byWorkflow.ToArray())
            {
                if (entry.Value is not JsonArray items) throw new WorkspaceStoreException("Unsupported LoRA metadata shape.");
                Filter(items); if (items.Count == 0) byWorkflow.Remove(entry.Key);
            }
        }
        // Captured owner inventories/defaults are not the inputs to the generation.
        // Its recipe and AppliedLoras record the actual choices separately.
        if ((property is "owner" or "asset") && o.ContainsKey("category") && o.ContainsKey("images"))
        {
            if (o["loras"] is JsonObject defaults) Removed += defaults.Sum(e => (e.Value as JsonArray)?.Count ?? 0);
            o["loras"] = new JsonObject(); o["images"] = new JsonArray(); o["defaultVoiceId"] = null;
            o["preferredIdentityReferences"] = new JsonArray();
            foreach (var look in o["looks"]?.AsArray() ?? new JsonArray())
                if (look is JsonObject l) l["preferredAppearanceReferences"] = new JsonArray();
        }
        if (o.ContainsKey("sourceImageId") && Number(o["loraStrength"], out var v) && v == 0)
        { if (o["lora"]?.GetValue<string>() is { Length: > 0 }) Removed++; o["lora"] = ""; }
        if (o["loraVisibility"] is JsonObject)
            o["loraVisibility"] = new JsonObject { ["onlyTags"] = new JsonArray(), ["hiddenTags"] = new JsonArray() };
        foreach (var entry in o.ToArray()) Visit(entry.Value, entry.Key);
        if (o.ContainsKey("shot") && o.ContainsKey("settings") && o.ContainsKey("frameCount") && o.ContainsKey("fingerprint"))
        {
            var shot = o["shot"]!.Deserialize<Shot>(AtomicJsonFile.Options)!;
            if (H3Presets.Key(shot) is "standard" or "spectrum" && o["sampling"] is JsonObject sampling) {
                if (sampling["lora"]?.GetValue<string>() is { Length: > 0 }) Removed++;
                sampling["lora"] = null; sampling["loraStrength"] = 0.0;
            }
            var snapshot = o.Deserialize<VideoSnapshot>(AtomicJsonFile.Options)!;
            var key = H3Presets.Key(snapshot.Shot); var settings = snapshot.Settings;
            void RemovedFile(bool unused, string? file) { if (unused && !string.IsNullOrEmpty(file)) Removed++; }
            var turbo4 = key is "turbo4" or H3Presets.TurboLight;
            RemovedFile(!turbo4, settings.TurboLora); RemovedFile(key != "turbo8", settings.Turbo8StepLora);
            RemovedFile(key != "larry", settings.LarryLora); RemovedFile(key != "pdd", settings.PddCheckpoint);
            RemovedFile(key != H3HyperFlow.Key, settings.HyperFlowLora);
            settings.TurboLora = turbo4 ? settings.TurboLora : "";
            settings.Turbo8StepLora = key == "turbo8" ? settings.Turbo8StepLora : "";
            settings.LarryLora = key == "larry" ? settings.LarryLora : null;
            settings.PddCheckpoint = key == "pdd" ? settings.PddCheckpoint : null;
            settings.HyperFlowLora = key == H3HyperFlow.Key ? settings.HyperFlowLora : null;
            // Machine-local executable paths are configuration, not portable provenance.
            settings.Ffmpeg = "ffmpeg"; settings.Ffprobe = "ffprobe";
            settings.LatentUpscaler = snapshot.PreviewUpscale?.Checkpoint ?? "";
            o["settings"] = JsonSerializer.SerializeToNode(settings, AtomicJsonFile.Options);
            // No applied weight/strength or authored text changed. Recompute the portable
            // recipe fingerprint when removal of inactive selections changes serialization.
            o["fingerprint"] = snapshot.Reel is { } reel ? VideoResolutions.Fingerprint(reel.Recipe) : H3Policy.Fingerprint(snapshot.Shot);
        }
    }
    private static bool Number(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<double>(out number)) return true;
        if (value.TryGetValue<float>(out var f)) { number = f; return true; }
        if (value.TryGetValue<int>(out var i)) { number = i; return true; }
        if (value.TryGetValue<long>(out var l)) { number = l; return true; }
        return false;
    }
    private void Filter(JsonArray items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is not JsonObject item || !Number(item["strength"], out var strength) || !double.IsFinite(strength))
                throw new WorkspaceStoreException("Invalid LoRA metadata; export cannot safely determine its use.");
            var enabled = item["enabled"]?.GetValue<bool>() ?? true;
            if (!enabled || strength == 0) { items.RemoveAt(i); Removed++; }
        }
    }
}
