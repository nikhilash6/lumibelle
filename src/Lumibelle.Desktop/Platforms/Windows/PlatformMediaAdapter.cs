using Microsoft.AspNetCore.Components.WebView.Maui;
using System.Collections.Concurrent;
namespace Lumibelle.Desktop;

internal sealed class PlatformMediaAdapter(MediaResources media) : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<long, WeakReference<Stream>> streams = new();
    private long nextStream;
    public void Attach(BlazorWebView view) => view.WebResourceRequested += Requested;
    private async void Requested(object? sender, WebViewWebResourceRequestedEventArgs args)
    {
        if (args.PlatformArgs is null || args.Uri?.AbsolutePath is not { } path || !(path.StartsWith("/media/") || path.StartsWith("/downloads/"))) return;
        args.Handled = true;
        using var deferral = args.PlatformArgs.RequestEventArgs.GetDeferral();
        try
        {
            var response = await media.GetAsync(args.Uri.PathAndQuery, args.Method ?? "GET", args.Headers, lifetime.Token);
            if (lifetime.IsCancellationRequested) { await response.DisposeAsync(); return; }
            foreach (var pair in streams) if (!pair.Value.TryGetTarget(out _)) streams.TryRemove(pair.Key, out _);
            streams[Interlocked.Increment(ref nextStream)] = new(response.Content);
            args.SetResponse(response.Status, response.Status.ToString(), response.Headers, response.Content);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { args.SetResponse(500, "Resource unavailable", "text/plain", Stream.Null); }
        finally { deferral.Complete(); }
    }
    public void Dispose() { lifetime.Cancel(); foreach (var stream in streams.Values) if (stream.TryGetTarget(out var target)) target.Dispose(); streams.Clear(); lifetime.Dispose(); }
}
