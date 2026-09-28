using Microsoft.JSInterop;

namespace lumibelle.Services;

public enum ThemePreference { System, Light, Dark }

public sealed class AppearanceState(IJSRuntime js) : IAsyncDisposable
{
    public ThemePreference Preference { get; private set; } = ThemePreference.System;
    public bool IsDark { get; private set; }
    public bool Initialized { get; private set; }
    public event Action? Changed;
    private IJSObjectReference? _module, _subscription;
    private DotNetObjectReference<AppearanceState>? _self;
    private Task? _initialization;
    private bool _disposed;

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        _module = await js.InvokeAsync<IJSObjectReference>("import", UiAssets.Module("appearance-interop.js"));
        if (_disposed) return;
        _self = DotNetObjectReference.Create(this);
        _subscription = await _module.InvokeAsync<IJSObjectReference>("attach", _self);
        AppearanceChanged(await _subscription.InvokeAsync<AppearanceSnapshot>("snapshot"));
        Initialized = true;
        Changed?.Invoke();
    }

    public async Task SetAsync(ThemePreference preference)
    {
        await InitializeAsync();
        if (_disposed || _subscription is null) return;
        AppearanceChanged(await _subscription.InvokeAsync<AppearanceSnapshot>("set", preference.ToString().ToLowerInvariant()));
    }

    [JSInvokable]
    public void AppearanceChanged(AppearanceSnapshot snapshot)
    {
        if (_disposed) return;
        var preference = Enum.TryParse<ThemePreference>(snapshot.Preference, true, out var parsed) ? parsed : ThemePreference.System;
        if (preference == Preference && IsDark == snapshot.Dark) return;
        Preference = preference;
        IsDark = snapshot.Dark;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            if (_initialization is not null) await _initialization;
            if (_subscription is not null) { await _subscription.InvokeVoidAsync("dispose"); await _subscription.DisposeAsync(); }
            if (_module is not null) await _module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
        finally { _self?.Dispose(); }
    }

    public sealed record AppearanceSnapshot(string Preference, bool Dark);
}
