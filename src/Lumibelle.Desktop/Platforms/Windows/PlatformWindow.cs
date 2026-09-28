namespace Lumibelle.Desktop;
internal static class PlatformWindow
{
    public static void Attach(Window window, MainPage page)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native) return;
        native.AppWindow.Closing += async (_, args) =>
        {
            if (page.CanClose) return;
            args.Cancel = true;
            if (await page.RequestCloseAsync()) native.Close();
        };
    }
}
