using lumibelle.Models;
using lumibelle.Services.Production;

namespace lumibelle.Components.Shots;

public partial class ShotReferenceEditor
{
    private string? _reelVoiceNotice;
    private bool SelectReelVoice(ShotVideoBinding binding)
    {
        if (!AllowReels || ReelAuthoring) return false;
        var result = ReelVoiceDefaults.Apply(_draft, binding, Library);
        _reelVoiceNotice = result.Notice;
        return result.Handled;
    }
}
