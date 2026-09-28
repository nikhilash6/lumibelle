using System.Net;
using System.Security.Cryptography;
using System.Text;
using lumibelle.Services;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

internal sealed class ComfyAccessFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "lumibelle-access-test-" + Guid.NewGuid().ToString("N"));
    public string FilePath => Path.Combine(Root, "comfy-access.json");
    public ApplicationPaths Paths { get; }
    public ComfyAccessTestProtector Protector { get; } = new();
    public FileComfyAccessCredentialStore Store { get; }
    public ComfyAccessFixture()
    {
        Directory.CreateDirectory(Root);
        Paths = new(Root);
        Store = new(Paths, Protector, TimeProvider.System);
    }
    public void Dispose()
    {
        Protector.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

// Real encryption in storage tests, without depending on a platform key ring.
internal sealed class ComfyAccessTestProtector : ISecretProtector, IDisposable
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    public bool FailProtect { get; set; }
    public bool FailUnprotect { get; set; }
    public string Protect(string value)
    {
        if (FailProtect) throw new CryptographicException("Test key ring unavailable");
        var plain = Encoding.UTF8.GetBytes(value);
        var result = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16));
        CryptographicOperations.ZeroMemory(plain);
        return Convert.ToBase64String(result);
    }
    public string Unprotect(string value)
    {
        if (FailUnprotect) throw new CryptographicException("Test key ring unavailable");
        var data = Convert.FromBase64String(value);
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        var result = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return result;
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}

internal sealed class ComfyAccessTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return send(request, ct);
    }
}

internal sealed class ComfyAccessTestClients(Func<HttpClient> create) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        Assert.Equal("ComfyUI", name);
        return create();
    }
}

internal static class ComfyAccessTestHttp
{
    public static HttpClient Client(IComfyAccessCredentialStore store,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ComfyAccessHandler(store) { InnerHandler = new ComfyAccessTestHandler(send) }) { Timeout = Timeout.InfiniteTimeSpan };

    public static HttpResponseMessage Json(string json = "{\"system\":{},\"devices\":[]}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static void AssertPair(HttpRequestMessage request, string id, string secret)
    {
        Assert.Equal(id, Assert.Single(request.Headers.GetValues(ComfyAccessTransport.ClientIdHeader)));
        Assert.Equal(secret, Assert.Single(request.Headers.GetValues(ComfyAccessTransport.ClientSecretHeader)));
    }

    public static void AssertNoPair(HttpRequestMessage request)
    {
        Assert.False(request.Headers.Contains(ComfyAccessTransport.ClientIdHeader));
        Assert.False(request.Headers.Contains(ComfyAccessTransport.ClientSecretHeader));
    }

    // A valid upgrade response and a real server-to-client text frame. ClientWebSocket
    // itself performs/validates the handshake and consumes the frame; there is no
    // substitute IComfyWebSocket in these transport tests and no external network.
    public static HttpResponseMessage Upgrade(HttpRequestMessage request)
    {
        var key = Assert.Single(request.Headers.GetValues("Sec-WebSocket-Key"));
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var stream = new MemoryStream();
        stream.Write(new byte[] { 0x81, 0x02, (byte)'{', (byte)'}' });
        stream.Position = 0;
        var response = new HttpResponseMessage(HttpStatusCode.SwitchingProtocols)
        {
            Version = HttpVersion.Version11, Content = new WritableUpgradeContent(stream)
        };
        response.Headers.Connection.Add("Upgrade");
        response.Headers.Upgrade.Add(new("websocket"));
        response.Headers.TryAddWithoutValidation("Sec-WebSocket-Accept", accept);
        return response;
    }
}

// WebSocket.CreateFromStream (used by ClientWebSocket) takes ownership of the
// response stream and writes close frames to it, so the base stream must stay
// writable. StreamContent wraps it in a read-only view; a real network response
// does not. Hand back the writable stream to keep the negotiation faithful.
internal sealed class WritableUpgradeContent(MemoryStream inner) : HttpContent
{
    protected override Stream CreateContentReadStream(CancellationToken cancellationToken) => inner;
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(inner);
    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken) => inner.CopyTo(stream);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => inner.CopyToAsync(stream, cancellationToken);
    protected override bool TryComputeLength(out long length) { length = inner.Length; return true; }
}
