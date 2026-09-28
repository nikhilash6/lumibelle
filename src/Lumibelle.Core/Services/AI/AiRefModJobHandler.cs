using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.Production;
using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

public sealed class AiRefModJobHandler(ReelRefModStore store, ComfyRefModClient remote,
    ComfyJobExecution comfy, TimeProvider clock) : IAiJobHandler
{
    public const string Operation = "fantastic-refmod-build/v1";
    public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.RefModBuild];
    public Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context, snapshot), ct);
    public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => RunAsync(context, Read(context, snapshot), ct);
    public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) => comfy.CancelAsync(context, remote.Client, ct);
    private static RefModBuildRequest Read(AiJobContext context, JsonElement snapshot)
    {
        var r = snapshot.Deserialize<RefModBuildRequest>(AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("The captured RefMod request is missing.");
        Validate(r);
        if (r.JobId != context.Job.Id || context.Job.Kind != AiJobKind.RefModBuild || context.Job.Backend != AiBackend.ComfyUI || context.Job.Batch is not null ||
            context.Job.Target != new AiJobTarget(r.ProjectId, r.AssetId, ReelId: r.Media.Id))
            throw new WorkspaceStoreException("The saved RefMod request belongs to another target.");
        return r;
    }
    public static void Validate(RefModBuildRequest r)
    {
        if (r is null || r.Version != 2 || r.Recipe is null || r.Media is null || r.Keyframes is null)
            throw new WorkspaceStoreException("The RefMod build is incomplete or belongs to the older companion experiment. Prepare a new build explicitly.");
        ReelRefMods.Validate(r.Recipe);
        if (r.JobId == Guid.Empty || r.ProjectId == Guid.Empty || r.AssetId == Guid.Empty || r.TimeoutSeconds is < 30 or > 86400 ||
            ReelRefMods.Selection(new() { Media = r.Media, Keyframes = r.Keyframes }) != r.Recipe.Selection || r.Keyframes.Frames.Count != r.Recipe.LatentFrames ||
            AiProviderRegistry.NormalizeComfyUrl(r.ComfyUrl) != r.ComfyUrl)
            throw new WorkspaceStoreException("Invalid captured RefMod build.");
    }
    private async Task<AiJobOutcome> RunAsync(AiJobContext context, RefModBuildRequest request, CancellationToken ct)
    {
        var submitted = (await context.ExecutionAsync(ct)).Submissions.Any(s => s.Operation == Operation);
        if (context.Recovering && !submitted)
            return AiJobOutcome.Attention("The build stopped before submission. Prepare and queue a new build explicitly; no encoding was repeated.", AiJobRecovery.GenerateAgain);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var http = remote.Client(request.ComfyUrl);
        var uploaded = new List<string>();
        if (!submitted)
        {
            await remote.CheckAsync(request.ComfyUrl, request.Recipe.VaeName, linked.Token);
            var input = Path.Combine(await store.BuildDirectoryAsync(request.ProjectId, request.JobId, linked.Token), "inputs");
            for (var i = 0; i < request.Recipe.LatentFrames; i++)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(input, ReelRefModStore.FrameName(i)), linked.Token);
                ReelRefModStore.CheckHash(bytes, request.Recipe.FrameHashes[i]);
                uploaded.Add(await ComfyRefModClient.UploadPictureAsync(http, request.JobId, i, bytes, linked.Token));
            }
        }
        JsonElement? output = null;
        var updates = submitted ? comfy.ObserveAsync(context, Operation, http, ct: linked.Token)
            : comfy.ExecuteAsync(context, Operation, http, id => ComfyRefModClient.BuildWorkflow(request, uploaded, id), ComfyRefModClient.BuildOptions, linked.Token);
        await foreach (var update in updates.WithCancellation(linked.Token))
        {
            await context.ReportAsync(new(update.Progress));
            if (update.Complete && update.Job is { } job) output = job;
        }
        if (output is null) throw new AiJobRecoveryException("Check the accepted RefMod build before submitting again.", AiJobRecovery.CheckStatus);
        try
        {
            var reference = ComfyRefModClient.ReadOutput(output.Value, request);
            if (!await remote.ReferenceAvailableAsync(reference, linked.Token))
                throw new WorkspaceStoreException("The completed server file is missing or does not match the selected Full visual format.");
            await context.ReportAsync(new(new(GenerationPhase.Downloading, "Saving the server-file receipt and source previews…")), true);
            await store.PublishAsync(request, reference, linked.Token);
            await context.SaveResultAsync(new RefModBuildResult(reference));
            return AiJobOutcome.Complete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or WorkspaceStoreException or JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new AiJobRecoveryException("The RefMod build finished, but its receipt could not be checked or saved. Retry output without encoding again. " + e.Message, AiJobRecovery.RetryOutput, e); }
    }
}
