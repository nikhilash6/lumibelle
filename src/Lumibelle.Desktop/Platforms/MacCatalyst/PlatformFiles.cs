using Foundation;
using UIKit;
namespace Lumibelle.Desktop;
internal static class PlatformFiles
{
    public static async Task SaveAsync(string name, Stream content, ApplicationPaths paths)
    {
        var directory = Path.Combine(paths.Temporary, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        try
        {
            await using (var output = File.Create(path)) await content.CopyToAsync(output);
            using var picker = new UIDocumentPickerViewController([NSUrl.FromFilename(path)], true);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            picker.DidPickDocumentAtUrls += (_, _) => done.TrySetResult();
            picker.WasCancelled += (_, _) => done.TrySetResult();
            var window = (UIWindow)Application.Current!.Windows[0].Handler!.PlatformView!;
            var presenter = window.RootViewController!; while (presenter.PresentedViewController is { } next) presenter = next;
            presenter.PresentViewController(picker, true, null); await done.Task;
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    // A document picker grants only temporary, security-scoped folder access; type the path instead.
    public static bool CanPickFolder => false;
    public static Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}
