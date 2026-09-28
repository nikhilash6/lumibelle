using lumibelle.Models;

namespace lumibelle.Services.Story;

public interface IAssistantHistoryStore
{
    Task<AssistantHistory> LoadAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<AssistantRun> SaveRunAsync(Guid projectId, AssistantRun run, CancellationToken cancellationToken = default);
}

public class WorkspaceStoreException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class WorkspaceConflictException() : WorkspaceStoreException("Another tab saved newer changes. Download your unsaved copy, or reload the saved version.");
public sealed class ApplicationSession { public Guid Id { get; } = Guid.NewGuid(); }
