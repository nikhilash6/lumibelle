using lumibelle.Models;

namespace lumibelle.Services.Story;

// One session per mounted editor. Saves merge revision metadata without replacing newer keystrokes.
public sealed class ScriptEditingSession(IScriptStore store, TimeProvider clock) : IDisposable
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _debounce;
    private long _editVersion;
    private long _savedVersion;
    private string _savedFingerprint = "";
    private bool _disposed;
    public ScriptDocument Document { get; private set; } = null!;
    public bool Dirty => ScriptStructure.Fingerprint(Document.Blocks) != _savedFingerprint;
    public bool Saving { get; private set; }
    public bool Mutating { get; private set; }
    public string? Error { get; private set; }
    public bool Conflict { get; private set; }
    public event Action? Changed;

    public async Task LoadAsync(Guid projectId)
    {
        var document = await store.LoadAsync(projectId);
        Document = document;
        _savedFingerprint = ScriptStructure.Fingerprint(document.Blocks);
        _editVersion = _savedVersion = 0;
        Error = null;
        Conflict = false;
        Changed?.Invoke();
    }

    public void Edit(ScriptDocument document)
    {
        if (_disposed || Mutating) return;
        Document = document;
        _editVersion++;
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = new();
        _ = DebounceAsync(_debounce.Token);
        Changed?.Invoke();
    }

    private async Task DebounceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), clock, ct);
            if (!_disposed && Error is null) await SaveAsync();
        }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> SaveAsync()
    {
        _debounce?.Cancel();
        await _saveGate.WaitAsync();
        try
        {
            if (Conflict) return false;
            while (Dirty && !_disposed)
            {
                Saving = true;
                Error = null;
                Changed?.Invoke();
                var version = _editVersion;
                var saved = await store.SaveAsync(Document.Copy(), Document.Revision);
                Document = Document with { Revision = saved.Revision, UpdatedUtc = saved.UpdatedUtc };
                _savedVersion = version;
                _savedFingerprint = ScriptStructure.Fingerprint(saved.Blocks);
            }
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException)
        {
            SetError(e);
            return false;
        }
        finally { Saving = false; _saveGate.Release(); Changed?.Invoke(); }
    }

    public async Task<bool> MutateAsync(Func<ScriptDocument, ScriptDocument> mutation, string? recoveryReason = null)
    {
        if (!await SaveAsync()) return false;
        await _saveGate.WaitAsync();
        Mutating = true;
        Changed?.Invoke();
        try
        {
            // Read the latest in-memory document after obtaining the gate.
            var next = mutation(Document.Copy());
            Document = await store.SaveAsync(next, Document.Revision, recoveryReason);
            _savedFingerprint = ScriptStructure.Fingerprint(Document.Blocks);
            _savedVersion = _editVersion;
            Error = null;
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException)
        { SetError(e); return false; }
        finally { Mutating = false; _saveGate.Release(); Changed?.Invoke(); }
    }

    public async Task<bool> RestoreAsync(Guid version)
    {
        if (!await SaveAsync()) return false;
        await _saveGate.WaitAsync();
        Mutating = true;
        Changed?.Invoke();
        try
        {
            Document = await store.RestoreAsync(Document.ProjectId, version, Document.Revision);
            _savedFingerprint = ScriptStructure.Fingerprint(Document.Blocks);
            _savedVersion = _editVersion;
            Error = null;
            return true;
        }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { SetError(e); return false; }
        finally { Mutating = false; _saveGate.Release(); Changed?.Invoke(); }
    }

    public async Task<bool> ApproveAsync()
    {
        if (!await SaveAsync()) return false;
        await _saveGate.WaitAsync(); Mutating = true; Changed?.Invoke();
        try { Document = await store.ApproveAsync(Document.ProjectId, Document.Revision); Error = null; return true; }
        catch (Exception e) when (e is WorkspaceStoreException or ProjectStoreException) { SetError(e); return false; }
        finally { Mutating = false; _saveGate.Release(); Changed?.Invoke(); }
    }

    private void SetError(Exception error) { Error = error.Message; Conflict = error is WorkspaceConflictException; }
    public void Dispose() { _disposed = true; _debounce?.Cancel(); _debounce?.Dispose(); Changed = null; }
}
