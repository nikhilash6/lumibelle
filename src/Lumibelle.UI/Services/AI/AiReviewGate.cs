using lumibelle.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace lumibelle.Services.AI;

public interface IAiReviewGate
{
    Task<Guid> TabIdAsync();
    Task<bool> TryOpenAsync(AiJobHeader job, ElementReference origin, bool automatic);
    Task CloseAsync(Guid id);
}

// Browser-local ownership prevents two completions from reserving a dialog during
// the same render interval. It is intentionally not a global server dialog lock.
public sealed class AiReviewGate(IJSRuntime js) : IAiReviewGate, IAsyncDisposable
{
    private Task<IJSObjectReference>? _module;
    private Task<IJSObjectReference> Module => _module ??= js.InvokeAsync<IJSObjectReference>("import", lumibelle.UiAssets.Module("ai-jobs.js")).AsTask();
    public async Task<Guid> TabIdAsync() => Guid.Parse(await (await Module).InvokeAsync<string>("tabId"));
    public async Task<bool> TryOpenAsync(AiJobHeader job, ElementReference origin, bool automatic)
    {
        try { return await (await Module).InvokeAsync<bool>("tryReview", job.Id, job.OriginTabId, origin, automatic); }
        catch (JSDisconnectedException) { return false; }
    }
    public async Task CloseAsync(Guid id)
    {
        if (_module is null) return;
        try { await (await _module).InvokeVoidAsync("closeReview", id); }
        catch (JSDisconnectedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await (await _module).DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
