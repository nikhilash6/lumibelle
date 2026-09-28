using Microsoft.JSInterop;
namespace lumibelle;
public interface IHostActions
{
    bool IsDesktop { get; }
    Task SaveTextAsync(string name, string text);
    Task SaveResourceAsync(string name, string url);
    Task OpenExternalAsync(string url);
    // A native folder chooser, where the host has one. Browsers can only type a path.
    bool CanPickFolder => false;
    Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}
public sealed class BrowserHostActions(IJSRuntime js) : IHostActions
{
    public bool IsDesktop => false;
    public async Task SaveTextAsync(string name, string text)
    { await using var module = await js.InvokeAsync<IJSObjectReference>("import", UiAssets.Module("writing.js")); await module.InvokeVoidAsync("downloadText", name, text); }
    public async Task SaveResourceAsync(string name, string url)
    { await using var module = await js.InvokeAsync<IJSObjectReference>("import", UiAssets.Module("host-bridge.js")); await module.InvokeVoidAsync("download", name, url); }
    public async Task OpenExternalAsync(string url) { await js.InvokeVoidAsync("open", url, "_blank", "noopener"); }
}
