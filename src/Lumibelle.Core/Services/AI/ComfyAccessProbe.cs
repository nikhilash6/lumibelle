using System.Net.WebSockets;
using System.Text.Json;

namespace lumibelle.Services.AI;

public sealed record ComfyAccessCheck(bool HttpSucceeded, bool WebSocketSucceeded, string HttpMessage, string WebSocketMessage)
{
    public ComfyAccessFailureKind FailureKind { get; init; }
    public bool NeedsCredentials => ComfyAccessResponses.CanPrompt(FailureKind);
}

public interface IComfyAccessProbe
{
    Task<ComfyAccessCheck> CheckAsync(string serverUrl, CancellationToken ct = default);
}

// A connectivity check only: no prompts, uploads, interrupt, or generation requests.
public sealed class ComfyAccessProbe(IHttpClientFactory clients, IComfyWebSocketFactory sockets) : IComfyAccessProbe
{
    public async Task<ComfyAccessCheck> CheckAsync(string serverUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ComfyAccessException("Enter a valid ComfyUI server URL without embedded credentials, a query, or a fragment.");
        var baseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        using var http = clients.CreateClient("ComfyUI");
        http.BaseAddress = baseAddress;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var response = await http.GetAsync("system_stats", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                // Bound diagnostics to a small JSON document even if the URL points at
                // something other than ComfyUI. Never buffer a video for this check.
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var body = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    if (body.Length + read > 1024 * 1024) throw new ComfyAccessException("ComfyUI's system_stats response was unexpectedly large. Check the server URL.");
                    body.Write(buffer, 0, read);
                }
                using var json = JsonDocument.Parse(body.ToArray());
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("system", out var system) || system.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
                    throw new ComfyAccessException("The response is not a ComfyUI system_stats document. Check the server URL.");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { return new(false, false, "HTTP connection timed out.", "Not checked because HTTP failed."); }
            catch (Exception e) when (e is not OperationCanceledException)
            { return new(false, false, Explain(e, "Could not read the ComfyUI API. Check the URL, network, and TLS certificate."), "Not checked because HTTP failed.") { FailureKind = AccessFailure(e)?.Kind ?? ComfyAccessFailureKind.Unknown }; }
        }

        using var socketTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        socketTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await using var socket = sockets.Create();
            await socket.ConnectAsync(ComfyExecutionMonitor.BuildWebSocketUri(baseAddress, Guid.NewGuid().ToString("N")), socketTimeout.Token);
            if (socket.State != WebSocketState.Open) throw new WebSocketException();
            return new(true, true, "HTTP API check passed.", "WebSocket handshake succeeded; live progress is available.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(true, false, "HTTP API check passed.", "WebSocket handshake timed out. Generation can still use history polling."); }
        catch (Exception e) when (e is not OperationCanceledException)
        { return new(true, false, "HTTP API check passed.", Explain(e, "WebSocket handshake failed. Check proxy support; generation can still use history polling.")) { FailureKind = AccessFailure(e)?.Kind ?? ComfyAccessFailureKind.Unknown }; }
    }

    // ClientWebSocket may wrap handler exceptions. Keep only our fixed, safe
    // diagnosis and classification, never arbitrary server bodies or redirect URLs.
    private static ComfyAccessException? AccessFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is ComfyAccessException access) return access;
        return null;
    }

    private static string Explain(Exception error, string fallback) => AccessFailure(error)?.Message ?? fallback;
}
