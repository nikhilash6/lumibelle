using lumibelle.Models;
using SixLabors.ImageSharp;

namespace lumibelle.Services.Assets;

public static class ImageGeometry
{
    // Both generation inputs and permanent copies use the same outward-rounded rectangle,
    // after EXIF orientation has been applied. Permanent copies never resize the result.
    public static Rectangle CropPixels(int width, int height, ImageCropRegion crop)
    {
        ComfyReferenceImageEditor.ValidateCrop(crop);
        var left = Math.Clamp((int)Math.Floor(crop.X * width), 0, width - 1);
        var top = Math.Clamp((int)Math.Floor(crop.Y * height), 0, height - 1);
        var right = Math.Clamp((int)Math.Ceiling((crop.X + crop.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((crop.Y + crop.Height) * height), top + 1, height);
        return new(left, top, right - left, bottom - top);
    }
}
