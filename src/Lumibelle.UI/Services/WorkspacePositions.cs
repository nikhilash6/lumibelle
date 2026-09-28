using System.Text.Json;
using Microsoft.JSInterop;

namespace lumibelle.Services;

// Browsing metadata only. Drafts and document persistence belong to their existing stores.
public sealed class WorkspacePositions(IJSRuntime js)
{
    private readonly Dictionary<(Guid, string), Dictionary<string, JsonElement>> _positions = [];
    public static string Key(Guid project, string studio) => $"lumibelle.position.{project:D}.{studio.ToLowerInvariant()}.v1.data";
    public async Task LoadAsync(Guid project, string studio)
    {
        var key = (project, studio);
        if (_positions.ContainsKey(key)) return;
        Dictionary<string, JsonElement>? data = null;
        try { var json = await js.InvokeAsync<string?>("localStorage.getItem", Key(project, studio)); if (json is not null) data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException or JsonException) { }
        _positions[key] = data ?? [];
    }
    public T Get<T>(Guid project, string studio, string name, T fallback = default!)
    {
        try { return _positions.TryGetValue((project, studio), out var data) && data.TryGetValue(name, out var value) ? value.Deserialize<T>() ?? fallback : fallback; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { return fallback; }
    }
    public async Task SaveAsync<T>(Guid project, string studio, string name, T value)
    {
        if (!_positions.TryGetValue((project, studio), out var data)) return;
        var json = JsonSerializer.SerializeToElement(value);
        if (data.TryGetValue(name, out var previous) && JsonElement.DeepEquals(previous, json)) return;
        data[name] = json;
        try { await js.InvokeVoidAsync("localStorage.setItem", Key(project, studio), JsonSerializer.Serialize(data)); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { }
    }
}
