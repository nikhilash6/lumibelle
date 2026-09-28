using lumibelle.Models;
using lumibelle.Services.AI;

namespace lumibelle.Services.Assets;

public static class ImageWorkflowLimits
{
    public static int MaximumPreparedImageBytes(ImageWorkflow workflow) => workflow == ImageWorkflow.QwenImage21
        ? QwenImage21Policy.MaximumEncodedSourceBytes : ComfyReferenceImageEditor.MaximumEncodedSourceBytes;
    // Used only before the exact workflow has been resolved; validate that workflow afterwards.
    public const int MaximumAnyReferences = 10;
    public static int MaximumReferences(ImageWorkflow workflow) => workflow switch
    {
        ImageWorkflow.Krea2 => ComfyReferenceImageEditor.MaximumReferences,
        ImageWorkflow.Flux2Klein9bKv => ComfyFluxKleinImages.MaximumReferences,
        ImageWorkflow.CodexImages => 8,
        ImageWorkflow.QwenImage21 => 10,
        _ => throw new AiGenerationException("This image workflow is unavailable.")
    };
}
