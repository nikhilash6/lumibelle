using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using Lumibelle.Testing;

// Uses the production Codex handler and persistence; the App Server transport alone is mocked.
public sealed class CodexImageHandler(IAssetStore assets, IReferenceImageGenerator generator, IReferenceImageEditor editor, ICodexClient codex) : IAiBatchJobHandler
{
    private readonly MockImageJobHandler _comfy = new(assets, generator, editor);
    private readonly AiImageJobHandler _codex = new(assets, null!, null!, null!, TimeProvider.System, codex);
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ImageCreate, AiJobKind.ImageEdit];
    private IAiBatchJobHandler Handler(AiJobHeader job) => job.Backend == AiBackend.Codex ? _codex : _comfy;
    public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Handler(context.Job).ExecuteAsync(context, snapshot, ct);
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Handler(context.Job).RecoverAsync(context, snapshot, ct);
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => Handler(context.Job).CancelRemoteAsync(context, snapshot, ct);
    public Task ValidateExtensionAsync(AiJobHeader root, JsonElement snapshot, CancellationToken ct) => Handler(root).ValidateExtensionAsync(root, snapshot, ct);
}
