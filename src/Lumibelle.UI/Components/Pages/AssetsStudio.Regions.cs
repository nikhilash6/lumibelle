using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Components.Pages;

public partial class AssetsStudio
{
    private readonly Dictionary<AssetImageReference, RegionalImageSelection> _regions = [];
    private IReadOnlyList<RegionalImageSelection>? CurrentRegions => IsEditMode && _regions.Count > 0
        ? CurrentImageInputs.Where(_regions.ContainsKey).Select(r => _regions[r]).ToArray() : null;
    private async Task RegionalImageSaved(AssetLibrary library)
    {
        if (library.ProjectId != Id || _disposed) return;
        await _saveGate.WaitAsync();
        try { await ReconcileLibraryLockedAsync(); } finally { _saveGate.Release(); }
        await RefreshImageJobsAsync();
    }
}
