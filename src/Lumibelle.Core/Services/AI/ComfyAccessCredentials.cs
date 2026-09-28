using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed record ComfyAccessEntry(string Origin, DateTimeOffset UpdatedUtc);
public sealed record ComfyAccessSettings(long Revision, IReadOnlyList<ComfyAccessEntry> Entries);

// This runtime value is never part of AiSettings, a job capture, or a project document.
public sealed class ComfyAccessToken
{
    [JsonIgnore] public string ClientId { get; }
    [JsonIgnore] public string ClientSecret { get; }

    public ComfyAccessToken(string clientId, string clientSecret)
    {
        Validate(clientId, clientSecret);
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public static void Validate(string clientId, string clientSecret)
    {
        static bool Invalid(string? value) => string.IsNullOrEmpty(value) || value.Length > 4096 ||
            value.Any(c => c < '!' || c > '~');
        // Do not assume the older 64-character secret format. Reject whitespace/control
        // characters rather than ever allowing user input to become extra HTTP headers.
        if (Invalid(clientId) || Invalid(clientSecret))
            throw new ComfyAccessException("Enter both the Cloudflare Access Client ID and Client Secret without spaces or line breaks.");
    }

    public override string ToString() => "Cloudflare Access credentials [redacted]";
}

public static class ComfyAccessOrigin
{
    public static string FromServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ComfyAccessException("Cloudflare Access credentials require an HTTPS ComfyUI URL without embedded credentials, a query, or a fragment.");
        return FromSecureEndpoint(uri);
    }

    public static string FromSecureEndpoint(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "wss") || uri.UserInfo.Length != 0)
            throw new ComfyAccessException("Cloudflare Access credentials can only be sent over HTTPS or WSS.");
        var origin = new UriBuilder("https", uri.IdnHost.ToLowerInvariant(), uri.IsDefaultPort ? -1 : uri.Port);
        return origin.Uri.GetLeftPart(UriPartial.Authority);
    }
}

public interface IComfyAccessCredentialStore
{
    Task<ComfyAccessSettings> LoadAsync(CancellationToken ct = default);
    Task<ComfyAccessSettings> SaveAsync(string serverUrl, string clientId, string clientSecret,
        long expectedRevision, CancellationToken ct = default);
    Task<ComfyAccessSettings> RemoveAsync(string serverUrl, long expectedRevision, CancellationToken ct = default);
    Task<ComfyAccessToken?> ResolveAsync(Uri endpoint, CancellationToken ct = default);
}

public sealed class FileComfyAccessCredentialStore(ApplicationPaths paths, ISecretProtector protector, TimeProvider clock)
    : IComfyAccessCredentialStore
{
    private readonly string _path = Path.Combine(paths.Data, "comfy-access.json");

    public async Task<ComfyAccessSettings> LoadAsync(CancellationToken ct = default) => Summary(await ReadAsync(ct));

    public async Task<ComfyAccessSettings> SaveAsync(string serverUrl, string clientId, string clientSecret,
        long expectedRevision, CancellationToken ct = default)
    {
        var origin = ComfyAccessOrigin.FromServerUrl(serverUrl);
        ComfyAccessToken.Validate(clientId, clientSecret);
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var current = await ReadAsync(ct);
        CheckRevision(current, expectedRevision);
        string encrypted;
        try
        {
            // Authenticate the origin inside the encrypted payload too: editing an origin
            // in the outer JSON must never retarget a saved credential to a different host.
            encrypted = protector.Protect(JsonSerializer.Serialize(new Payload(1, origin, clientId, clientSecret)));
        }
        catch (CryptographicException)
        {
            throw new ComfyAccessException("Could not protect the Access credentials with this account's key ring. Nothing was saved.");
        }
        var entries = current.Entries.Where(e => e.Origin != origin).ToList();
        entries.Add(new(origin, clock.GetUtcNow(), encrypted));
        if (entries.Count > 100) throw new ComfyAccessException("Remove an unused Access connection before adding another.");
        var saved = new Stored(1, checked(current.Revision + 1), entries.OrderBy(e => e.Origin, StringComparer.Ordinal).ToList());
        await AtomicJsonFile.WriteAsync(_path, saved, ct);
        return Summary(saved);
    }

    public async Task<ComfyAccessSettings> RemoveAsync(string serverUrl, long expectedRevision, CancellationToken ct = default)
    {
        var origin = ComfyAccessOrigin.FromServerUrl(serverUrl);
        using var gate = await ProjectFiles.LockAsync(_path, ct);
        var current = await ReadAsync(ct);
        CheckRevision(current, expectedRevision);
        var entries = current.Entries.Where(e => e.Origin != origin).ToList();
        if (entries.Count == current.Entries.Count) return Summary(current);
        var saved = new Stored(1, checked(current.Revision + 1), entries);
        await AtomicJsonFile.WriteAsync(_path, saved, ct);
        return Summary(saved);
    }

    public async Task<ComfyAccessToken?> ResolveAsync(Uri endpoint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!endpoint.IsAbsoluteUri || endpoint.UserInfo.Length != 0)
            throw new ComfyAccessException("Use an absolute ComfyUI URL without embedded credentials.");
        if (endpoint.Scheme is not ("https" or "wss")) return null;
        var origin = ComfyAccessOrigin.FromSecureEndpoint(endpoint);
        var stored = (await ReadAsync(ct)).Entries.SingleOrDefault(e => e.Origin == origin);
        if (stored is null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(protector.Unprotect(stored.ProtectedToken));
            if (payload is null || payload.Version != 1 || payload.Origin != origin)
                throw new CryptographicException();
            return new(payload.ClientId, payload.ClientSecret);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException or ComfyAccessException)
        {
            // No inner exception or provider response: neither may echo secret material.
            throw new ComfyAccessException("This account cannot open the saved Cloudflare Access credentials for this origin. Replace them in ComfyUI Access settings.");
        }
    }

    private async Task<Stored> ReadAsync(CancellationToken ct)
    {
        Stored? stored;
        try { stored = await AtomicJsonFile.ReadAsync<Stored>(_path, ct); }
        catch (WorkspaceStoreException e) when (e.InnerException is DirectoryNotFoundException && !File.Exists(Path.GetDirectoryName(_path)))
        { stored = null; }
        catch (WorkspaceStoreException)
        { throw new ComfyAccessException("Could not read the Access credential file. Restore a valid file or check its permissions; it has not been replaced."); }
        if (stored is null) return new(1, 0, []);
        if (stored.SchemaVersion != 1 || stored.Revision < 0 || stored.Entries is null || stored.Entries.Count > 100 ||
            stored.Entries.Any(e => e is null || string.IsNullOrEmpty(e.ProtectedToken) || e.ProtectedToken.Length > 65536) ||
            stored.Entries.Select(e => e.Origin).Distinct(StringComparer.Ordinal).Count() != stored.Entries.Count)
            throw new ComfyAccessException("The Access credential file is invalid or uses an unsupported format. It has not been replaced.");
        foreach (var entry in stored.Entries)
            if (ComfyAccessOrigin.FromServerUrl(entry.Origin) != entry.Origin)
                throw new ComfyAccessException("The Access credential file contains an invalid origin. It has not been replaced.");
        return stored;
    }

    private static void CheckRevision(Stored current, long revision)
    {
        if (current.Revision != revision)
            throw new ComfyAccessException("Access credentials changed in another window. Reload before saving or removing credentials.");
    }

    private static ComfyAccessSettings Summary(Stored value) => new(value.Revision,
        value.Entries.Select(e => new ComfyAccessEntry(e.Origin, e.UpdatedUtc)).ToArray());

    private sealed record Stored(int SchemaVersion, long Revision, List<ProtectedEntry> Entries);
    private sealed record ProtectedEntry(string Origin, DateTimeOffset UpdatedUtc, string ProtectedToken);
    private sealed record Payload(int Version, string Origin, string ClientId, string ClientSecret)
    {
        public override string ToString() => "Cloudflare Access payload [redacted]";
    }
}
