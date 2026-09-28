using lumibelle.Services.Shots;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace lumibelle.Components.Pages;

public partial class CutStudio
{
    [Inject] public ICutExporter CutExporter { get; set; } = null!;
    private CancellationTokenSource? _exportCancellation;
    private CutExportResult? _completedExport;
    private string? _exportError, _exportStatus;
    private bool _downloadingExport;

    private void CancelExport() => _exportCancellation?.Cancel();

    private async Task Export()
    {
        if (_disposed || _exporting || _downloadingExport || _doc.Clips.Count == 0 || UnavailableTakes.Length > 0) return;
        var projectId = Id;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _exportCancellation = cancellation;
        _exporting = true;
        _exportError = null;
        _exportStatus = "Saving the cut before exporting…";
        try
        {
            if (!await Save() || _disposed || Id != projectId) return;
            cancellation.Token.ThrowIfCancellationRequested();
            var revision = _doc.Revision;
            _exportStatus = $"Exporting saved revision {revision}. Later edits will not change this export.";
            StateHasChanged();
            var result = await CutExporter.ExportAsync(projectId, revision, cancellation.Token);
            if (_disposed || Id != projectId) return;
            cancellation.Token.ThrowIfCancellationRequested();
            _completedExport = result;
            _exportStatus = $"Revision {revision} is ready. Downloads remain available for up to one hour, while this app is running.";
            // Rendering has finished; a native save dialog is not cancellable via
            // the render token. Keep its busy state separate.
            _exporting = false;
            _exportCancellation = null;
            await DownloadExport();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_disposed && Id == projectId) _exportStatus = "Export cancelled. Your cut is unchanged.";
        }
        catch (Exception e)
        {
            if (!_disposed && Id == projectId) { _exportError = e.Message; _exportStatus = null; }
        }
        finally
        {
            _exporting = false;
            _exportCancellation = null;
            if (!_disposed && Id == projectId && _exportStatus == "Saving the cut before exporting…") _exportStatus = null;
        }
    }

    private async Task DownloadExport()
    {
        if (_disposed || _downloadingExport || _completedExport is not { } result || result.ProjectId != Id) return;
        _downloadingExport = true;
        _exportError = null;
        StateHasChanged();
        try
        {
            // JS only initiates a download of an existing resource. It does not wait
            // for FFmpeg or buffer an entire movie through JS interop/a Blob.
            await JS.InvokeVoidAsync("lumibelleShots.downloadResource", _lifetime.Token,
                "cut.mp4", result.Url);
        }
        catch (Exception e)
        {
            if (!_disposed && Id == result.ProjectId) _exportError = e.Message;
        }
        finally { _downloadingExport = false; }
    }
}
