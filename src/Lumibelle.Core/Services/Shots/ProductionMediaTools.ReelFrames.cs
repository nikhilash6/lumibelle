using System.Globalization;
using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Story;

namespace lumibelle.Services.Shots;

public sealed partial class ProductionMediaTools
{
    public async Task<IReadOnlyList<double>> ReelFrameTimesAsync(string source, H3Settings settings, CancellationToken ct)
    {
        var output = await Run(settings.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", source], ct);
        using var json = JsonDocument.Parse(output);
        var frames = json.RootElement.GetProperty("frames");
        if (frames.GetArrayLength() is < 1 or > 4000) throw new WorkspaceStoreException("The reel has an unsupported frame count.");
        var times = frames.EnumerateArray().Select(f => double.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!, CultureInfo.InvariantCulture)).ToArray();
        for (var i = 0; i < times.Length; i++)
        {
            if (!double.IsFinite(times[i]) || i > 0 && times[i] < times[i - 1]) throw new WorkspaceStoreException("The reel has invalid frame timestamps.");
        }
        return times;
    }

    public async Task ExtractReelFramesAsync(string source, IReadOnlyList<int> indices, string directory, int maximumEdge, H3Settings settings, CancellationToken ct)
    {
        if (indices.Count is < 1 or > 128 || indices.Any(i => i is < 0 or >= 4000) || !indices.SequenceEqual(indices.Distinct().Order()))
            throw new WorkspaceStoreException("Choose ordered, distinct reel frames.");
        Directory.CreateDirectory(directory);
        // Select original decoded indices. Normalize pixel aspect before rotation: automatic
        // transpose can retain the original SAR, stretching anamorphic portrait frames.
        var select = string.Join('+', indices.Select(i => $"eq(n\\,{i})"));
        using var metadata = JsonDocument.Parse(await Run(settings.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream_side_data=rotation", "-of", "json", source], ct));
        var stream = metadata.RootElement.GetProperty("streams")[0];
        var rotation = stream.TryGetProperty("side_data_list", out var sideData)
            ? sideData.EnumerateArray().Where(s => s.TryGetProperty("rotation", out _)).Select(s => s.GetProperty("rotation").GetDouble()).FirstOrDefault() : 0;
        var angle = (-rotation * Math.PI / 180).ToString(CultureInfo.InvariantCulture);
        var turn = ((int)Math.Round(rotation) % 360 + 360) % 360;
        var orient = turn switch { 90 => ",transpose=cclock", 180 => ",hflip,vflip", 270 => ",transpose=clock", 0 => "", _ => $",rotate={angle}:ow=rotw({angle}):oh=roth({angle})" };
        var ratio = maximumEdge > 0 ? $"min(1,min({maximumEdge}/(iw*sar),{maximumEdge}/ih))" : "1";
        var scale = $"scale=w='max(1,round(iw*sar*{ratio}))':h='max(1,round(ih*{ratio}))',setsar=1";
        await Run(settings.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-threads", "1", "-noautorotate", "-i", source,
            "-map", "0:v:0", "-an", "-vf", $"select='{select}',{scale}{orient}", "-fps_mode", "passthrough", "-frames:v", indices.Count.ToString(CultureInfo.InvariantCulture),
            "-map_metadata", "-1", "-threads", "1", "-c:v", "png", "-start_number", "0", Path.Combine(directory, "%06d.png")], ct);
        for (var i = 0; i < indices.Count; i++)
        {
            var file = new FileInfo(Path.Combine(directory, $"{i:D6}.png"));
            if (!file.Exists || file.Length is <= 0 or > 64 * 1024 * 1024) throw new WorkspaceStoreException("A reel frame could not be extracted.");
        }
    }
}
