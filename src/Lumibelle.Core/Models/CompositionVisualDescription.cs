namespace lumibelle.Models;

/// <summary>Immutable text evidence captured for a numbered generation reference, not an attachment.</summary>
public sealed record CompositionVisualDescription(string Label, Guid BindingId, string Name, string Text,
    string Source, ImageCropRegion? Crop = null);
