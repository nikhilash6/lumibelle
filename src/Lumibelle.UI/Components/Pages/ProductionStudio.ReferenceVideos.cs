using lumibelle.Models;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private bool _videoPreviewOpen;
    private ShotVideoBinding? _previewVideo;
    private void PreviewVideo(int number)
    {
        if ((Selected is null ? null : lumibelle.Services.Production.ResolvedReferences.For(Selected).Videos.ElementAtOrDefault(number - 1)?.Reel) is not { } video) return;
        _previewVideo = video; _videoPreviewOpen = true;
    }
}
