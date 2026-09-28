using System.Text.Json;

namespace lumibelle.Models;

// Null on earlier snapshots: those requests retain their original graph and archives.
public sealed record H3PresetProfile(string Version, string Key, string? Checkpoint, JsonElement Inputs);
public sealed record H3OutputPolicy(bool SaveLosslessFrames);
public sealed record H3PresetSetup(string Key, string? Issue, IReadOnlyList<string> Files);
