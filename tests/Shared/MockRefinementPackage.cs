using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Testing;

public static class MockRefinementPackage
{
    public static async Task<H3RefinementPackage> WriteAsync(string directory, VideoSnapshot snapshot, TakeRefinement? refinement, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var width = refinement?.Width ?? snapshot.Width; var height = refinement?.Height ?? snapshot.Height;
        var metadata = new { version = 1, id = Guid.NewGuid(), width, height, frameCount = snapshot.FrameCount, fps = 24,
            context = RefinementPackages.Context(snapshot, refinement), conditioning = new object[] { new object[] { new { tensor = "cond00002" }, new Dictionary<string, object> { ["dict"] = new { } } } } };
        var header = new Dictionary<string, object> { ["__metadata__"] = new { lumibelle = JsonSerializer.Serialize(metadata, AtomicJsonFile.Options) } };
        long offset = 0;
        void Tensor(string name, long[] shape)
        {
            long count = 1; foreach (var n in shape) count *= n;
            var end = offset + count * 4;
            header[name] = new { dtype = "F32", shape, data_offsets = new[] { offset, end } }; offset = end;
        }
        Tensor("video", [1, 24, (snapshot.FrameCount - 5) / 17 * 5 + 2, height / 16, width / 16]);
        Tensor("audio", [1, 32, 2, (long)Math.Round(snapshot.FrameCount / 24d * 40)]); Tensor("cond00002", [1, 1, 1]);
        var json = JsonSerializer.Serialize(header, AtomicJsonFile.Options);
        var data = Encoding.UTF8.GetBytes(json.PadRight(json.Length + (8 - Encoding.UTF8.GetByteCount(json) % 8) % 8));
        var path = Path.Combine(directory, H3RefinementPackage.FileName);
        await using (var file = File.Create(path))
        {
            var prefix = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(prefix, (ulong)data.Length);
            await file.WriteAsync(prefix, ct); await file.WriteAsync(data, ct); file.SetLength(8 + data.Length + offset);
        }
        return await RefinementPackages.InspectAsync(path, snapshot, refinement, ct);
    }
}
