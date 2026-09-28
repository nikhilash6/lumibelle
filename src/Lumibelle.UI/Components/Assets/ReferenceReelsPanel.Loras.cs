using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Assets;

public partial class ReferenceReelsPanel
{
    [Inject] public IComfyLoraCatalog LoraCatalog { get; set; } = null!;
    [Inject] public IProjectAiPreferencesStore LoraPreferences { get; set; } = null!;
    private AiSettings? _loraSettings;
    private LoraVisibility _loraVisibility = new();
    private ComfyLoraCheck? _loraCheck;
    private string? _loraError;
    private bool _loraValid = true;

    protected override async Task OnInitializedAsync() { await LoadPresets(); _presetsReady = true; await LoadLoraOptions(false); }
    private Task RefreshLoras() => LoadLoraOptions(true);
    private async Task LoadLoraOptions(bool checkCatalog)
    {
        try
        {
            var settings = await Settings.LoadAsync(_lifetime.Token);
            var preferences = await LoraPreferences.LoadAsync(ProjectId, _lifetime.Token);
            LoraPolicy.ValidateVisibility(preferences.LoraVisibility);
            var check = checkCatalog ? await LoraCatalog.CheckAsync(settings, _lifetime.Token) : null;
            if (_disposed) return;
            _loraSettings = settings; _loraVisibility = preferences.LoraVisibility; _loraCheck = check; _loraError = null;
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception e) { if (!_disposed) { _loraError = e.Message; _loraCheck = null; } }
    }

    private Task ChangeLoras(IReadOnlyList<LoraSelection> selections) => EditPreset(p =>
        p.Settings.Loras = selections.Count == 0 ? null : LoraPolicy.Capture(selections));

    private Task InsertLoraTrigger(string trigger) => Run(async () =>
    {
        if (_draft is null || _enqueue is not null || string.IsNullOrWhiteSpace(trigger)) return;
        _draft.Instructions = (_draft.Instructions.TrimEnd() + "\n" + trigger).TrimStart();
        await Save();
    });
}
