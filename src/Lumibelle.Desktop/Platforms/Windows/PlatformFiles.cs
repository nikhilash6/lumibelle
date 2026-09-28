using Windows.Storage.Pickers;
namespace Lumibelle.Desktop;
internal static class PlatformFiles
{
    public static async Task SaveAsync(string name, Stream content, ApplicationPaths paths)
    {
        var picker = new FileSavePicker { SuggestedFileName = name };
        var extension = Path.GetExtension(name);
        picker.FileTypeChoices.Add("File", new List<string> { string.IsNullOrEmpty(extension) ? ".txt" : extension });
        var native = (Microsoft.UI.Xaml.Window)Application.Current!.Windows[0].Handler!.PlatformView!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await using var output = await file.OpenStreamForWriteAsync(); output.SetLength(0);
        await content.CopyToAsync(output);
    }

    public static bool CanPickFolder => true;
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var native = (Microsoft.UI.Xaml.Window)Application.Current!.Windows[0].Handler!.PlatformView!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
