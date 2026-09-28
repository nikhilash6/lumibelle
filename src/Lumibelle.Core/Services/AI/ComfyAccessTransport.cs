using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace lumibelle.Services.AI;

public static class ComfyAccessServices
{
    public static IServiceCollection AddComfyAccess(this IServiceCollection services)
    {
        services.TryAddSingleton<IComfyAccessCredentialStore, FileComfyAccessCredentialStore>();
        services.TryAddSingleton<IComfyWebSocketFactory, AccessComfyWebSocketFactory>();
        services.TryAddSingleton<IComfyAccessProbe, ComfyAccessProbe>();
        services.AddTransient<ComfyAccessHandler>();
        services.AddHttpClient("ComfyUI", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(ComfyAccessTransport.CreatePrimaryHandler)
            .AddHttpMessageHandler<ComfyAccessHandler>()
            .RedactLoggedHeaders(_ => true);
        return services;
    }
}

public static class ComfyAccessTransport
{
    public const string ClientIdHeader = "CF-Access-Client-Id";
    public const string ClientSecretHeader = "CF-Access-Client-Secret";

    // Shared by HTTP and the actual WebSocket handshake. Do not replace this with the
    // default ClientWebSocket.ConnectAsync overload: its redirects are not our policy.
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false
    };
}

public sealed class ComfyAccessHandler(IComfyAccessCredentialStore credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https" or "ws" or "wss") || uri.UserInfo.Length != 0)
            throw new ComfyAccessException("Use an absolute HTTP(S) ComfyUI URL without embedded credentials.");

        // A caller cannot smuggle an old/default token to another origin. Only the
        // credential store, resolved against this exact destination, supplies headers.
        request.Headers.Remove(ComfyAccessTransport.ClientIdHeader);
        request.Headers.Remove(ComfyAccessTransport.ClientSecretHeader);
        ComfyAccessToken? token;
        try { token = await credentials.ResolveAsync(uri, ct); }
        catch (ComfyAccessException e)
        {
            // This is before dispatch, not an uncertain remote acceptance. Do not
            // keep an inner exception that could contain protected-token material.
            throw new ComfyAccessException(e.Message)
            {
                Kind = ComfyAccessFailureKind.CredentialsUnavailable,
                RequestWasNotSent = true
            };
        }
        try
        {
            if (token is not null)
            {
                if (uri.Scheme is not ("https" or "wss"))
                    throw new ComfyAccessException("Cloudflare Access credentials require HTTPS or WSS.");
                request.Headers.Add(ComfyAccessTransport.ClientIdHeader, token.ClientId);
                request.Headers.Add(ComfyAccessTransport.ClientSecretHeader, token.ClientSecret);
            }
            var response = await base.SendAsync(request, ct);
            var problem = ComfyAccessResponses.Classify(response, uri, token is not null);
            if (problem is not null)
            {
                response.Dispose();
                throw problem;
            }
            return response;
        }
        finally
        {
            // Do not leave the pair on Response.RequestMessage or a reusable request.
            request.Headers.Remove(ComfyAccessTransport.ClientIdHeader);
            request.Headers.Remove(ComfyAccessTransport.ClientSecretHeader);
        }
    }
}

public sealed class AccessComfyWebSocketFactory(IHttpClientFactory clients) : IComfyWebSocketFactory
{
    public IComfyWebSocket Create() => new AccessSocket(clients.CreateClient("ComfyUI"));

    private sealed class AccessSocket(HttpMessageInvoker invoker) : IComfyWebSocket
    {
        private readonly ClientWebSocket _socket = new();
        public WebSocketState State => _socket.State;
        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
            _socket.ConnectAsync(uri, invoker, cancellationToken);
        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            _socket.ReceiveAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync()
        {
            _socket.Dispose();
            invoker.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
