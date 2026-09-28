using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using lumibelle.Models;
using lumibelle.Services.Assets;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    private static readonly DialogOptions SetupOptions = new() { MaxWidth = MaxWidth.Large, FullWidth = true, CloseOnEscapeKey = false, BackdropClick = false };
    private readonly string _promptHistoryScope = $"reel:{Guid.NewGuid():N}";
    private bool _setupDialogOpen, _closingSetupDialog, _restoreSetupFocus, _focusSetupTab;
    private string _setupTab = "Prompt", _setupOriginTab = "Prompt";
    private ElementReference _setupPromptButton, _setupSettingsButton, _setupPromptTab, _setupSettingsTab;
    private string SaveStatus => _saving ? "Saving…" : _saveFailed ? "Save failed" : _draft?.Revision > 0 ? "Saved" : "New recipe";
    private string SetupPromptStatus => ActiveComposition is not null ? "Writing…" : !HasPromptPair ? "Missing"
        : _draft!.CheckedInputs != ReferenceReels.InputsFingerprint(_draft) ? "Review prompt" : "Ready";
    private string VoiceModeLabel => _draft?.VoiceMode switch { ReelVoiceMode.NewVoice => "New voice", ReelVoiceMode.ExistingRecording => "Existing recording", _ => "Silent" };

    private async Task OpenSetupDialog(string tab)
    {
        await _reset;
        if (_disposed || _draft is null) return;
        if (_presetBusy || _busy) return;
        await Run(RefreshPresets);
        _setupTab = _setupOriginTab = tab;
        _setupDialogOpen = true;
    }

    private Task SetupDialogVisibility(bool visible) => visible ? Task.CompletedTask : CloseSetupDialog();
    private async Task CloseSetupDialog()
    {
        if (!_setupDialogOpen || _closingSetupDialog || _busy) return;
        _closingSetupDialog = true;
        try
        {
            if (!await FlushForClose()) return;
            _setupDialogOpen = false; _restoreSetupFocus = true;
            _prompt = null; _assist = null;
        }
        finally { _closingSetupDialog = false; }
    }

    private async Task SetSetupTab(string tab)
    {
        if (_setupTab == tab) return;
        if (!await FlushForClose()) return;
        _setupTab = tab;
    }

    private async Task SetupTabKeyDown(KeyboardEventArgs e)
    {
        if (e.Key is not ("ArrowLeft" or "ArrowRight" or "Home" or "End")) return;
        await SetSetupTab(e.Key == "Home" ? "Prompt" : e.Key == "End" ? "Settings" : _setupTab == "Prompt" ? "Settings" : "Prompt");
        _focusSetupTab = true;
    }

    private async Task RestoreSetupFocus()
    {
        if (_focusSetupTab && _setupDialogOpen)
        {
            _focusSetupTab = false;
            await (_setupTab == "Prompt" ? _setupPromptTab : _setupSettingsTab).FocusAsync();
        }
        if (_restoreSetupFocus)
        {
            _restoreSetupFocus = false;
            await RevealTools.InvokeAsync();
            await (_setupOriginTab == "Prompt" ? _setupPromptButton : _setupSettingsButton).FocusAsync();
        }
    }
}
