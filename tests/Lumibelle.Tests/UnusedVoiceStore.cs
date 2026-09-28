using lumibelle.Models;
using lumibelle.Services.Assets;
namespace Lumibelle.Tests;
internal sealed class UnusedVoiceStore : IVoiceStore
{
    public Task<AssetLibrary> AddVoiceAsync(Guid p, Guid a, Stream i, string f, string n, double s, double d, H3Settings settings, long r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetLibrary> UpdateVoiceAsync(Guid p, VoiceReference v, long r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetLibrary> DiscardVoiceAsync(Guid p, Guid v, long r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetLibrary> RestoreVoicesAsync(Guid p, IReadOnlyCollection<Guid> ids, long r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetLibrary> PurgeVoicesAsync(Guid p, IReadOnlyCollection<Guid> ids, long r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AssetMedia?> OpenVoiceAsync(Guid p, Guid v, bool trash = false, CancellationToken ct = default) => Task.FromResult<AssetMedia?>(null);
}
