using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public static class ImageAspectPolicy
{
    public const string FromImage1 = "From Image 1";

    public static bool IsSupported(string aspect, bool editing) =>
        ComfyReferenceImageGenerator.Sizes.ContainsKey(aspect) || editing && aspect == FromImage1;

    public static bool IsResolutionSupported(int resolution) => resolution is 1024 or 1440 or 2048;

    // Resolution is a pixel budget; source proportions include the submitted crop.
    public static (int Width, int Height) Size(string aspect, int sourceWidth, int sourceHeight, int resolution = 1024)
    {
        if (!IsResolutionSupported(resolution)) throw new AiGenerationException("Choose a supported image resolution.");
        if (aspect == FromImage1) return QwenImage21Policy.Size(sourceWidth, sourceHeight, resolution);
        if (!ComfyReferenceImageGenerator.Sizes.TryGetValue(aspect, out var size)) throw new AiGenerationException("Choose a supported aspect ratio.");
        return resolution == 1024 ? size : QwenImage21Policy.Size(size.Width, size.Height, resolution);
    }

    public static (int Width, int Height) Size(string aspect, byte[]? source = null, int resolution = 1024)
    {
        if (aspect != FromImage1) return Size(aspect, 0, 0, resolution);
        if (source is null) throw new AiGenerationException("Choose Image 1 before following its aspect ratio.");
        var size = QwenImage21Policy.PngSize(source);
        return Size(aspect, size.Width, size.Height, resolution);
    }
}
