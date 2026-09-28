using lumibelle.Services.Story;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace lumibelle.Services.Assets;

public readonly record struct ImageInfo(string ContentType, string Extension, int Width, int Height);

public static class ImageInspector
{
    public static ImageInfo Inspect(ReadOnlySpan<byte> data)
    {
        try
        {
            using var image = Image.Load(data);
            var format = image.Metadata.DecodedImageFormat?.Name;
            // Crop coordinates are applied after AutoOrient when an image is edited, so the
            // persisted dimensions must describe that same visual orientation.
            image.Mutate(context => context.AutoOrient());
            return format?.ToUpperInvariant() switch
            {
                "PNG" => Valid("image/png", ".png", image.Width, image.Height),
                "JPEG" => Valid("image/jpeg", ".jpg", image.Width, image.Height),
                "WEBP" => Valid("image/webp", ".webp", image.Width, image.Height),
                _ => throw Invalid()
            };
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static ImageInfo Valid(string contentType, string extension, int width, int height) => width is > 0 and <= 32768 && height is > 0 and <= 32768
        ? new(contentType, extension, width, height) : throw Invalid();
    private static WorkspaceStoreException Invalid() => new("Choose a readable PNG, JPEG, or WebP image no larger than 25 MB.");
}
