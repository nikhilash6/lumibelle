using System.Text;
namespace Lumibelle.Desktop;
public sealed class DesktopHostActions(MediaResources media, ApplicationPaths paths) : IHostActions
{
    public bool IsDesktop => true;
    public async Task SaveTextAsync(string name, string text)
    { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)); await MainThread.InvokeOnMainThreadAsync(() => PlatformFiles.SaveAsync(Path.GetFileName(name), stream, paths)); }
    public async Task SaveResourceAsync(string name, string url)
    {
        await using var response = await media.GetAsync(url);
        if (response.Status != 200) throw new IOException("The file is no longer available.");
        await MainThread.InvokeOnMainThreadAsync(() => PlatformFiles.SaveAsync(Path.GetFileName(name), response.Content, paths));
    }
    public bool CanPickFolder => PlatformFiles.CanPickFolder;
    public Task<string?> PickFolderAsync() => MainThread.InvokeOnMainThreadAsync(PlatformFiles.PickFolderAsync);
    public async Task OpenExternalAsync(string url)
    { if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") await Launcher.Default.OpenAsync(uri); }
}
