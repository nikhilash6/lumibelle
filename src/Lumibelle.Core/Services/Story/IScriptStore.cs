using lumibelle.Models;

namespace lumibelle.Services.Story;

public interface IScriptStore
{
    Task<ScriptDocument> LoadAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ScriptDocument> SaveAsync(ScriptDocument document, long expectedRevision, string? recoveryReason = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScriptRecovery>> ListRecoveryAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ScriptDocument> RestoreAsync(Guid projectId, Guid recoveryId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ScriptDocument> ApproveAsync(Guid projectId, long expectedRevision, CancellationToken cancellationToken = default);
    Task<ApprovedScriptSnapshot?> LoadApprovedAsync(Guid projectId, Guid? snapshotId = null, CancellationToken cancellationToken = default);
    Task<ScriptSourceSnapshot?> CaptureSourceAsync(Guid projectId, long? expectedRevision = null, CancellationToken cancellationToken = default);
    Task<ScriptSourceSnapshot?> LoadSourceAsync(Guid projectId, Guid snapshotId, CancellationToken cancellationToken = default);
}
