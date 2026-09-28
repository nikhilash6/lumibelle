using lumibelle.Models;
using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;

namespace lumibelle.Components.Pages;

public partial class ProductionStudio
{
    private readonly SemaphoreSlim _outputDefaultsGate = new(1, 1);
    private int _pendingOutputDefaults;
    private string PresetResolution => ActiveGlobalSetup?.Settings is { } settings
        ? settings.UpscalePreview ? "upscaled" : VideoResolutions.Key(settings.Resolution ?? (settings.NativeResolution ? VideoResolution.Native : VideoResolution.Preview))
        : "preview";

    private void ChangeOutputResolution(ChangeEventArgs e)
    {
        var key = Text(e);
        if (key != "upscaled" && !VideoResolutions.TryParse(key, out _)) return;
        EditComposition(c => {
            c.OutputOverrides ??= new();
            c.OutputOverrides.Resolution = key == "upscaled" ? VideoResolution.Preview : VideoResolutions.TryParse(key, out var resolution) ? resolution : VideoResolution.Preview;
            c.OutputOverrides.UpscalePreview = key == "upscaled";
            c.OutputOverrides.Apply(c);
        });
    }

    private void ChangeOutputTakes(int count) => EditComposition(c => {
        c.OutputOverrides ??= new(); c.OutputOverrides.TakeCount = count; c.OutputOverrides.Apply(c);
    });

    private void ResetOutput() => EditComposition(c => {
        c.OutputOverrides = null; ActiveGlobalSetup?.Settings.Apply(c);
    });

    private async Task SaveOutputDefaults()
    {
        EditComposition(c => c.OutputOverrides = null);
        await Save();
    }

    private Task ChangeDefaultResolution(ChangeEventArgs e) => EditOutputDefaults(settings => {
        var key = Text(e);
        if (key == "upscaled") { settings.Resolution = null; settings.NativeResolution = false; settings.UpscalePreview = true; }
        else if (VideoResolutions.TryParse(key, out var resolution)) {
            settings.Resolution = resolution is VideoResolution.Preview or VideoResolution.Native ? null : resolution; settings.NativeResolution = resolution == VideoResolution.Native; settings.UpscalePreview = false;
        }
    });

    private Task ChangeDefaultTakes(int count) => EditOutputDefaults(settings => settings.TakeCount = count);

    private async Task EditOutputDefaults(Action<GenerationSettings> edit)
    {
        var compositionId = Current?.Id;
        _pendingOutputDefaults++;
        _setupBusy = true;
        await _outputDefaultsGate.WaitAsync();
        try {
            if (compositionId != Current?.Id) return;
            if (_promptEditor is not null) await _promptEditor.FlushAsync();
            if (!await Save() || ActiveGlobalSetup is not { } preset) return;
            var settings = ShotCopy.Of(preset.Settings); edit(settings);
            _globalSetups = await GenerationSetups.SaveAsync(preset with { Settings = settings }, preset.Version, _lifetime.Token);
            _production = await Production.LoadAsync(Id, _lifetime.Token);
            _savedComposition = Current?.Copy(); _undo.Clear();
        } catch (Exception e) { _error = e.Message; }
        finally { _setupBusy = --_pendingOutputDefaults > 0; _outputDefaultsGate.Release(); }
    }
}
