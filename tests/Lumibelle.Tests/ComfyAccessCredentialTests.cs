using System.Text.Json;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class ComfyAccessCredentialTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task EmptyStoreDoesNotCreateCredentialsOrFiles()
    {
        using var f = new ComfyAccessFixture();
        var empty = await f.Store.LoadAsync(Ct);
        Assert.Equal(0, empty.Revision); Assert.Empty(empty.Entries);
        Assert.Null(await f.Store.ResolveAsync(new("https://comfy.example/system_stats"), Ct));
        Assert.Null(await f.Store.ResolveAsync(new("http://localhost:8188/prompt"), Ct));
        Assert.False(File.Exists(f.FilePath));
    }

    [Fact]
    public async Task BothValuesAreProtectedAndRuntimeTokensCannotEnterSerializedCaptures()
    {
        using var f = new ComfyAccessFixture();
        const string id = "test-client-id.access", secret = "test-only-client-secret";
        var saved = await f.Store.SaveAsync("https://comfy.example", id, secret, 0, Ct);
        var disk = await File.ReadAllTextAsync(f.FilePath, Ct);
        Assert.DoesNotContain(id, disk); Assert.DoesNotContain(secret, disk);
        Assert.DoesNotContain(id, JsonSerializer.Serialize(saved));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(saved));
        var reopened = new FileComfyAccessCredentialStore(f.Paths, f.Protector, TimeProvider.System);
        var token = await reopened.ResolveAsync(new("wss://comfy.example/ws?clientId=example"), Ct);
        Assert.NotNull(token); Assert.Equal(id, token.ClientId); Assert.Equal(secret, token.ClientSecret);
        Assert.Equal("{}", JsonSerializer.Serialize(token));
        Assert.DoesNotContain(id, token.ToString()); Assert.DoesNotContain(secret, token.ToString());
    }

    [Theory]
    [InlineData("https://COMFY.example:443/prefix/", "https://comfy.example")]
    [InlineData("https://comfy.example:8443/prefix", "https://comfy.example:8443")]
    [InlineData("https://comfy.example/", "https://comfy.example")]
    public void CanonicalOriginKeepsThePortButNotThePath(string url, string expected) =>
        Assert.Equal(expected, ComfyAccessOrigin.FromServerUrl(url));

    [Theory]
    [InlineData("http://comfy.example")]
    [InlineData("wss://comfy.example")]
    [InlineData("https://user:password@comfy.example")]
    [InlineData("https://comfy.example/?secret=bad")]
    [InlineData("https://comfy.example/#secret")]
    [InlineData("/relative")]
    [InlineData("file:///tmp/model")]
    public async Task RejectsUnsafeConfigurationWithoutSaving(string url)
    {
        using var f = new ComfyAccessFixture();
        await Assert.ThrowsAsync<ComfyAccessException>(() => f.Store.SaveAsync(url, "id", "secret", 0, Ct));
        Assert.False(File.Exists(f.FilePath));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("id", "")]
    [InlineData("id\r\nX-Evil:yes", "secret")]
    [InlineData("id", "secret\nInjected")]
    [InlineData("id", "a b")]
    [InlineData("id", "a\tkey")]
    [InlineData("id", " a-key")]
    public void RejectsIncompleteOrHeaderUnsafeTokens(string id, string secret) =>
        Assert.Throws<ComfyAccessException>(() => new ComfyAccessToken(id, secret));

    [Fact]
    public void AcceptsBothOpaqueModernAndLegacySecrets()
    {
        _ = new ComfyAccessToken("test.access", new string('a', 64));
        _ = new ComfyAccessToken("test.access", "cfast_" + new string('B', 48));
    }

    [Theory]
    [InlineData("https://comfy.example/view?filename=result.mp4", true)]
    [InlineData("wss://comfy.example/ws?clientId=one", true)]
    [InlineData("https://comfy.example:443/object_info", true)]
    [InlineData("https://comfy.example:8443/prompt", false)]
    [InlineData("http://comfy.example/prompt", false)]
    [InlineData("ws://comfy.example/ws", false)]
    [InlineData("https://other.example/prompt", false)]
    [InlineData("https://comfy.example.evil.test/prompt", false)]
    public async Task LookupUsesExactSecureOrigin(string endpoint, bool expected)
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://comfy.example/prefix", "id", "secret", 0, Ct);
        Assert.Equal(expected, await f.Store.ResolveAsync(new(endpoint), Ct) is not null);
    }

    [Fact]
    public async Task RotationAndRemovalPreserveOtherOriginsAndRejectStaleWrites()
    {
        using var f = new ComfyAccessFixture();
        var a = await f.Store.SaveAsync("https://a.example", "a", "old-a", 0, Ct);
        var b = await f.Store.SaveAsync("https://b.example", "b", "secret-b", a.Revision, Ct);
        await Assert.ThrowsAsync<ComfyAccessException>(() => f.Store.SaveAsync("https://a.example", "a", "stale", a.Revision, Ct));
        await Assert.ThrowsAsync<ComfyAccessException>(() => f.Store.RemoveAsync("https://b.example", a.Revision, Ct));
        var rotated = await f.Store.SaveAsync("https://a.example", "new-a", "new-secret-a", b.Revision, Ct);
        Assert.Equal("new-secret-a", (await f.Store.ResolveAsync(new("https://a.example/queue"), Ct))!.ClientSecret);
        Assert.Equal("secret-b", (await f.Store.ResolveAsync(new("https://b.example/queue"), Ct))!.ClientSecret);
        var removed = await f.Store.RemoveAsync("https://a.example", rotated.Revision, Ct);
        Assert.Single(removed.Entries); Assert.Equal("https://b.example", removed.Entries[0].Origin);
        Assert.Null(await f.Store.ResolveAsync(new("wss://a.example/ws"), Ct));
    }

    [Fact]
    public async Task TamperingWithTheOuterOriginCannotRetargetTheEncryptedPair()
    {
        using var f = new ComfyAccessFixture();
        await f.Store.SaveAsync("https://a.example", "id", "secret", 0, Ct);
        var json = await File.ReadAllTextAsync(f.FilePath, Ct);
        await File.WriteAllTextAsync(f.FilePath, json.Replace("https://a.example", "https://b.example", StringComparison.Ordinal), Ct);
        var error = await Assert.ThrowsAsync<ComfyAccessException>(() => f.Store.ResolveAsync(new("https://b.example/view"), Ct));
        Assert.Contains("Replace", error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task KeyRingFailureDoesNotEraseTheOldRecordAndReplacementCanRepairIt()
    {
        using var f = new ComfyAccessFixture();
        var saved = await f.Store.SaveAsync("https://comfy.example", "id", "old", 0, Ct);
        var before = await File.ReadAllBytesAsync(f.FilePath, Ct);
        f.Protector.FailProtect = true;
        await Assert.ThrowsAsync<ComfyAccessException>(() => f.Store.SaveAsync("https://comfy.example", "new-id", "new", saved.Revision, Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.FilePath, Ct));
        f.Protector.FailProtect = false; f.Protector.FailUnprotect = true;
        Assert.Single((await f.Store.LoadAsync(Ct)).Entries);
        await Assert.ThrowsAsync<ComfyAccessException>(() => f.Store.ResolveAsync(new("https://comfy.example/prompt"), Ct));
        await f.Store.SaveAsync("https://comfy.example", "new-id", "new", saved.Revision, Ct);
        f.Protector.FailUnprotect = false;
        Assert.Equal("new", (await f.Store.ResolveAsync(new("https://comfy.example/prompt"), Ct))!.ClientSecret);
    }

    [Fact]
    public async Task CorruptFileIsNotSilentlyOverwritten()
    {
        using var f = new ComfyAccessFixture();
        await File.WriteAllTextAsync(f.FilePath, "{broken", Ct);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct));
        Assert.Equal("{broken", await File.ReadAllTextAsync(f.FilePath, Ct));
    }

    [Fact]
    public async Task CancellationBeforeWriteLeavesNoFile()
    {
        using var f = new ComfyAccessFixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.SaveAsync("https://comfy.example", "id", "secret", 0, cancelled.Token));
        Assert.False(File.Exists(f.FilePath));
    }
}
