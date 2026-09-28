using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public interface IAssetExtractor
{
    IAsyncEnumerable<AssetExtractionUpdate> ExtractAsync(AssetExtractionRequest request,
        CancellationToken cancellationToken = default);
}

public interface IReferenceImageGenerator
{
    Task<ComfyImageConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReferenceGenerationUpdate> GenerateAsync(ReferenceGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IReferenceImageEditor
{
    Task<ComfyImageEditConfiguration> CheckAsync(AiSettings? settings = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, Stream source,
        CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReferenceGenerationUpdate> EditAsync(ReferenceEditRequest request, IReadOnlyList<ReferenceImageSource> sources,
        CancellationToken cancellationToken = default)
    {
        if (sources.Count != 1 || sources[0].AssetId != request.SourceAssetId || sources[0].ImageId != request.SourceImageId)
            throw new AiGenerationException("This image editor requires exactly one matching source image.");
        return EditAsync(request, sources[0].Content, cancellationToken);
    }
}
