using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public static class RefinementPackages
{
    public const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    public static object Context(VideoSnapshot snapshot, TakeRefinement? refinement) => new { snapshot, refinement };
    public static async Task<H3RefinementPackage> InspectAsync(string path, VideoSnapshot snapshot, TakeRefinement? refinement, CancellationToken ct)
    {
        try
        {
            await using var file = File.OpenRead(path);
            if (file.Length is < 10 or > MaximumBytes) throw new InvalidDataException("Invalid package length.");
            var prefix = new byte[8]; await file.ReadExactlyAsync(prefix, ct);
            var length = BinaryPrimitives.ReadUInt64LittleEndian(prefix);
            if (length is < 2 or > 16 * 1024 * 1024 || (long)length + 8 >= file.Length) throw new InvalidDataException("Invalid package header.");
            var buffer = new byte[(int)length]; await file.ReadExactlyAsync(buffer, ct);
            using var header = JsonDocument.Parse(buffer);
            var root = header.RootElement;
            using var metadata = JsonDocument.Parse(root.GetProperty("__metadata__").GetProperty("lumibelle").GetString()!);
            var m = metadata.RootElement;
            var w = refinement?.Width ?? snapshot.Width; var h = refinement?.Height ?? snapshot.Height;
            if (m.GetProperty("version").GetInt32() != 1 || m.GetProperty("fps").GetInt32() != 24 ||
                m.GetProperty("width").GetInt32() != w || m.GetProperty("height").GetInt32() != h || m.GetProperty("frameCount").GetInt32() != snapshot.FrameCount ||
                !Guid.TryParse(m.GetProperty("id").GetString(), out var id) || id == Guid.Empty ||
                !JsonElement.DeepEquals(m.GetProperty("context"), JsonSerializer.SerializeToElement(Context(snapshot, refinement), AtomicJsonFile.Options)))
                throw new InvalidDataException("Package context does not match the captured take.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            List<(long Start, long End)> ranges = [];
            foreach (var entry in root.EnumerateObject().Where(e => e.Name != "__metadata__"))
            {
                if (!names.Add(entry.Name)) throw new InvalidDataException("Duplicate tensor.");
                var dtype = entry.Value.GetProperty("dtype").GetString();
                var size = dtype switch { "F64" or "I64" => 8, "F32" or "I32" => 4, "F16" or "BF16" or "I16" => 2, "I8" or "U8" or "BOOL" => 1, _ => throw new InvalidDataException("Unsupported tensor dtype.") };
                var shape = entry.Value.GetProperty("shape").EnumerateArray().Select(e => e.GetInt64()).ToArray();
                if (shape.Length > 8 || shape.Any(n => n < 1)) throw new InvalidDataException("Invalid tensor shape.");
                long bytes = size; foreach (var n in shape) bytes = checked(bytes * n);
                var offsets = entry.Value.GetProperty("data_offsets").EnumerateArray().Select(e => e.GetInt64()).ToArray();
                if (offsets.Length != 2 || offsets[0] < 0 || offsets[1] - offsets[0] != bytes || offsets[1] > file.Length - (long)length - 8)
                    throw new InvalidDataException("Invalid tensor byte range.");
                ranges.Add((offsets[0], offsets[1]));
                if (entry.Name is "video" or "audio")
                {
                    long[] expected = entry.Name == "video" ? [1, 24, (snapshot.FrameCount - 5) / 17 * 5 + 2, h / 16, w / 16]
                        : [1, 32, 2, (long)Math.Round(snapshot.FrameCount / 24d * 40)];
                    if (!shape.SequenceEqual(expected) || dtype is not ("F16" or "BF16" or "F32" or "F64")) throw new InvalidDataException("Joint latent shape or dtype mismatch.");
                }
            }
            var ordered = ranges.OrderBy(r => r.Start).ToArray(); long end = 0;
            foreach (var range in ordered) { if (range.Start != end) throw new InvalidDataException("Overlapping or missing tensor bytes."); end = range.End; }
            if (end != file.Length - (long)length - 8 || !names.Contains("video") || !names.Contains("audio")) throw new InvalidDataException("Incomplete joint latent package.");
            HashSet<string> used = ["video", "audio"];
            void Tree(JsonElement value, int depth)
            {
                if (depth > 32) throw new InvalidDataException("Conditioning tree too deep.");
                if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) Tree(item, depth + 1); }
                else if (value.ValueKind == JsonValueKind.Object)
                {
                    if (value.EnumerateObject().Count() != 1) throw new InvalidDataException("Invalid conditioning tag.");
                    if (value.TryGetProperty("tensor", out var tensor))
                    {
                        var name = tensor.GetString()!;
                        if (name is "video" or "audio" || !names.Contains(name)) throw new InvalidDataException("Unknown conditioning tensor.");
                        used.Add(name);
                    }
                    else if (value.TryGetProperty("dict", out var dictionary)) { foreach (var item in dictionary.EnumerateObject()) Tree(item.Value, depth + 1); }
                    else throw new InvalidDataException("Unsupported conditioning object.");
                }
                else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
                    throw new InvalidDataException("Non-finite conditioning metadata.");
            }
            var conditioning = m.GetProperty("conditioning");
            if (conditioning.ValueKind != JsonValueKind.Array || conditioning.GetArrayLength() == 0) throw new InvalidDataException("Missing conditioning.");
            Tree(conditioning, 0);
            if (!used.SetEquals(names)) throw new InvalidDataException("Unreferenced conditioning tensor.");
            file.Position = 0;
            return new(id, file.Length, Convert.ToHexString(await SHA256.HashDataAsync(file, ct)), w, h, snapshot.FrameCount);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or InvalidDataException or EndOfStreamException)
        { throw new WorkspaceStoreException("The refinement package is corrupt, incomplete, or incompatible. Retry transfer; no video was regenerated.", e); }
    }
    public static async Task VerifyFileAsync(string path, long bytes, string hash, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length != bytes || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)) != hash)
            throw new WorkspaceStoreException("A captured refinement file is missing or changed. It cannot be silently replaced.");
    }
}
