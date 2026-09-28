using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

internal static class ReferenceEditInputs
{
    public static ReferenceEditRequest Capture(ReferenceEditRequest request,
        IReadOnlyList<ReferenceImageSource> sources, int maximum)
    {
        if (sources.Count < 1 || sources.Count > maximum ||
            sources.Any(s => s is null || s.AssetId == Guid.Empty || s.ImageId == Guid.Empty || s.Content is null) ||
            sources[0].AssetId != request.SourceAssetId || sources[0].ImageId != request.SourceImageId ||
            sources.DistinctBy(s => (s.AssetId, s.ImageId)).Count() != sources.Count)
            throw new AiGenerationException($"Choose one to {maximum} distinct images, with the edited base image first.");
        if (request.ReferenceCrops is null || request.ReferenceCrops.Any(c => c is null || c.Reference is null || c.Crop is null))
            throw new AiGenerationException("Reference crops are invalid.");
        if (request.Regions is { } regions)
        {
            var identities = sources.Select(s => new AssetImageReference(s.AssetId, s.ImageId)).ToHashSet();
            if (regions.Any(r => r is null || !identities.Contains(r.Source)) || regions.DistinctBy(r => r.Source).Count() != regions.Count)
                throw new AiGenerationException("Each selection must belong to its exact source image.");
            foreach (var region in regions) RegionalImageEdits.Validate(region);
            var primary = regions.SingleOrDefault(r => r.Source == new AssetImageReference(request.SourceAssetId, request.SourceImageId));
            request = request with { SourceCrop = primary?.Context ?? request.SourceCrop,
                ReferenceCrops = request.ReferenceCrops.Where(c => regions.All(r => r.Source != c.Reference)).Concat(regions.Where(r => r != primary).Select(r => new AssetReferenceCrop(r.Source, r.Context))).ToArray() };
            if (regions.Any(r => r.Source != new AssetImageReference(request.SourceAssetId, request.SourceImageId) && r.Mode != RegionalEditMode.Protect))
                throw new AiGenerationException("Additional references can be protected, but are not composited.");
        }
        if (request.SourceCrop is { } crop) ComfyReferenceImageEditor.ValidateCrop(crop);
        if (request.ReferenceCrops is null) throw new AiGenerationException("Reference crops are invalid.");
        var crops = request.ReferenceCrops.ToArray();
        var additional = sources.Skip(1).Select(s => new AssetImageReference(s.AssetId, s.ImageId)).ToHashSet();
        if (crops.Any(c => c is null || c.Reference is null || c.Crop is null || !additional.Contains(c.Reference)) ||
            crops.DistinctBy(c => c.Reference).Count() != crops.Length)
            throw new AiGenerationException("Each reference crop must belong to a distinct additional image in this edit.");
        foreach (var item in crops) ComfyReferenceImageEditor.ValidateCrop(item.Crop);
        if (request.ReferenceLooks is null || request.ReferenceLooks.Any(r => r is null || r.Reference is null || r.Context is null || r.Context.AssetId != r.Reference.AssetId) ||
            request.ReferenceLooks.Count > 0 && !request.ReferenceLooks.Select(r => r.Reference).SequenceEqual(sources.Select(s => new AssetImageReference(s.AssetId, s.ImageId))))
            throw new AiGenerationException("Reference look context must match the exact ordered inputs.");
        return request with { ReferenceLooks = request.ReferenceLooks.ToArray(), ReferenceCrops = Array.AsReadOnly(crops), Tags = request.Tags.ToArray(), Loras = LoraPolicy.Capture(request.Loras) };
    }

    public static ImageCropRegion? CropFor(ReferenceEditRequest request, ReferenceImageSource source) =>
        source.AssetId == request.SourceAssetId && source.ImageId == request.SourceImageId ? request.SourceCrop :
        request.ReferenceCrops.FirstOrDefault(c => c.Reference == new AssetImageReference(source.AssetId, source.ImageId))?.Crop;
}
